using System;
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace MoonBaseAlpha.Terrain
{
    /// <summary>
    /// The lunar surface height function, Y(x, z) in frame coordinates (curvature included).
    /// = real elevation data (LROC NAC 4 m where available, SLDEM2015 59 m elsewhere, bicubic)
    /// + the Marius Hills Pit funnel (modelled from the LROC Pits Atlas, the DTM has no data in the pit)
    /// + deterministic sub-resolution detail: small craters (power-law size distribution), fractal
    ///   roughness and boulders. Detail is band-limited by <c>minFeature</c> so coarse LODs don't alias.
    /// Burst-compatible value type; copy it into jobs freely (arrays are read-only and shared).
    /// </summary>
    public struct HeightField : IDisposable
    {
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<ushort> near;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<ushort> far;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<byte> mask;
        public int nearN, farN;
        public double nearSpacing, farSpacing, nearHalf, farHalf;
        public float nearOff, nearScale, farOff, farScale;

        // Pit (frame xz), major axis along the rille.
        public float2 pitCentre;
        public float2 pitAxis;           // unit vector, major axis (down-rille)
        public float funnelA, funnelB;   // funnel semi-axes (m)
        public float innerA, innerB;     // inner shaft semi-axes (m)
        public float rimHeight;          // frame Y of the plain around the pit
        public float funnelDepth;        // ~10 m

        // Region replaced by the cave mesh (frame xz min/max); terrain detail is calmed near its edges.
        public float4 caveBox;
        public byte hasCaveBox;
        /// <summary>World-seed-derived salt for craters / boulders / roughness (the LRO data never changes).</summary>
        public uint detailSeed;
        /// <summary>Levelled work pad prepared by the crew (x, z, radius, height); radius 0 = none.</summary>
        public float4 pad0;

        public bool IsCreated => near.IsCreated;

        public static HeightField Load(TerrainMeta meta, Allocator alloc = Allocator.Persistent)
        {
            var hf = new HeightField
            {
                nearN = meta.near.n, farN = meta.far.n,
                nearSpacing = meta.near.spacing, farSpacing = meta.far.spacing,
                nearOff = (float)meta.near.heightOffset, nearScale = (float)meta.near.heightScale,
                farOff = (float)meta.far.heightOffset, farScale = (float)meta.far.heightScale,
            };
            hf.nearHalf = (hf.nearN - 1) * hf.nearSpacing * 0.5;
            hf.farHalf = (hf.farN - 1) * hf.farSpacing * 0.5;
            hf.near = ReadU16(Path.Combine(TerrainMeta.Folder, meta.near.file), hf.nearN * hf.nearN, alloc);
            hf.far = ReadU16(Path.Combine(TerrainMeta.Folder, meta.far.file), hf.farN * hf.farN, alloc);
            var mbytes = File.ReadAllBytes(Path.Combine(TerrainMeta.Folder, meta.near.maskFile));
            hf.mask = new NativeArray<byte>(mbytes, alloc);

            // Pit geometry: LROC Pits Atlas - funnel 79 x 92 m, inner 49 x 55 m, 40 m deep, funnel ~10 m deep.
            hf.pitCentre = float2.zero; // frame origin is the atlas pit centre
            float az = math.radians(meta.pit != null ? meta.pit.rilleAzimuthDown : 296f);
            hf.pitAxis = new float2(math.sin(az), math.cos(az));   // azimuth from north, x east
            hf.funnelA = 46f; hf.funnelB = 39.5f;
            hf.innerA = 27.5f; hf.innerB = 24.5f;
            hf.funnelDepth = 10f;
            // Plain height around the pit: median-ish of the data on a ring outside the faceted DTM area.
            float sum = 0; int cnt = 0;
            for (int i = 0; i < 72; i++)
            {
                float a = i * math.PI * 2 / 72;
                for (float r = 85; r <= 110; r += 5)
                { sum += hf.DataHeight(r * math.sin(a), r * math.cos(a)); cnt++; }
            }
            hf.rimHeight = sum / cnt;
            return hf;
        }

        static NativeArray<ushort> ReadU16(string path, int count, Allocator alloc)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != count * 2) throw new InvalidDataException($"{path}: expected {count * 2} bytes, got {bytes.Length}");
            var arr = new NativeArray<ushort>(count, alloc, NativeArrayOptions.UninitializedMemory);
            arr.Reinterpret<byte>(2).CopyFrom(bytes);
            return arr;
        }

        public void Dispose()
        {
            if (near.IsCreated) near.Dispose();
            if (far.IsCreated) far.Dispose();
            if (mask.IsCreated) mask.Dispose();
        }

        // ------------------------------------------------------------------ data

        static float CR(float p0, float p1, float p2, float p3, float t)
        {
            // Catmull-Rom
            return p1 + 0.5f * t * (p2 - p0 + t * (2f * p0 - 5f * p1 + 4f * p2 - p3 + t * (3f * (p1 - p2) + p3 - p0)));
        }

        static float Bicubic(in NativeArray<ushort> g, int n, double gx, double gz, float off, float scale)
        {
            int ix = (int)math.floor(gx), iz = (int)math.floor(gz);
            float tx = (float)(gx - ix), tz = (float)(gz - iz);
            float r0 = 0, r1 = 0, r2 = 0, r3 = 0;
            for (int j = -1; j <= 2; j++)
            {
                int zz = math.clamp(iz + j, 0, n - 1) * n;
                float a = g[zz + math.clamp(ix - 1, 0, n - 1)];
                float b = g[zz + math.clamp(ix, 0, n - 1)];
                float c = g[zz + math.clamp(ix + 1, 0, n - 1)];
                float d = g[zz + math.clamp(ix + 2, 0, n - 1)];
                float v = CR(a, b, c, d, tx);
                if (j == -1) r0 = v; else if (j == 0) r1 = v; else if (j == 1) r2 = v; else r3 = v;
            }
            return off + scale * CR(r0, r1, r2, r3, tz);
        }

        /// <summary>Elevation data only (no pit model, no detail).</summary>
        public float DataHeight(double x, double z)
        {
            double fx = math.abs(x), fz = math.abs(z);
            float hFar = Bicubic(far, farN, (x + farHalf) / farSpacing, (z + farHalf) / farSpacing, farOff, farScale);
            double edge = nearHalf - 2 * nearSpacing;
            double m = math.max(fx, fz);
            if (m >= edge) return hFar;
            float hNear = Bicubic(near, nearN, (x + nearHalf) / nearSpacing, (z + nearHalf) / nearSpacing, nearOff, nearScale);
            float w = math.smoothstep((float)(edge - 600), (float)edge, (float)m);
            return math.lerp(hNear, hFar, w);
        }

        /// <summary>0..1, how much of the height at this point is real 4 m NAC data.</summary>
        public float NacWeight(double x, double z)
        {
            double gx = (x + nearHalf) / nearSpacing, gz = (z + nearHalf) / nearSpacing;
            if (gx < 0 || gz < 0 || gx >= nearN - 1 || gz >= nearN - 1) return 0;
            int ix = (int)gx, iz = (int)gz;
            float tx = (float)(gx - ix), tz = (float)(gz - iz);
            int i = iz * nearN + ix;
            float a = math.lerp(mask[i], mask[i + 1], tx);
            float b = math.lerp(mask[i + nearN], mask[i + nearN + 1], tx);
            return math.lerp(a, b, tz) / 255f;
        }

        // ------------------------------------------------------------------ pit

        /// <summary>Elliptical normalised radius in funnel units (1 = funnel rim).</summary>
        public float PitRho(float2 p)
        {
            float2 d = p - pitCentre;
            float u = math.dot(d, pitAxis);
            float v = d.x * pitAxis.y - d.y * pitAxis.x;
            return math.sqrt(u * u / (funnelA * funnelA) + v * v / (funnelB * funnelB));
        }

        // ------------------------------------------------------------------ detail

        static float Rand(uint h) => (h & 0xFFFFFF) / 16777216f;

        /// <summary>Sum of craters from one size level (cell size S), 3x3 neighbourhood.</summary>
        static float CraterLevel(float2 p, float S, float prob, uint seed, ref float rockMul)
        {
            float2 cellF = math.floor(p / S);
            int2 cell = (int2)cellF;
            float h = 0;
            for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                int2 c = cell + new int2(i, j);
                uint hh = math.hash(new int3(c, (int)seed));
                if (Rand(hh) > prob) continue;
                uint h2 = math.hash(new uint2(hh, 0x9E3779B9u));
                uint h3 = math.hash(new uint2(h2, 0x85EBCA6Bu));
                float2 centre = (new float2(c.x, c.y) + new float2(0.1f + 0.8f * Rand(h2), 0.1f + 0.8f * Rand(h3))) * S;
                float D = S * (0.25f + 0.55f * Rand(math.hash(new uint2(h3, 7u))));
                float R = 0.5f * D;
                float r = math.length(p - centre) / R;
                if (r >= 2.2f) continue;
                float fresh = Rand(math.hash(new uint2(h3, 11u)));
                fresh *= fresh;                                    // most small craters are degraded
                if (S >= 16f) fresh *= fresh;                      // and almost all 10-25 m ones (old, infilled)
                float depth = D * (0.04f + 0.16f * fresh);
                float rim = D * (0.008f + 0.035f * fresh);
                float bowl = r < 1f ? -depth * (1 - r * r) * (1 - r * r) : 0f;
                float rr = (r - 1f) / (0.25f + 0.25f * (1 - fresh));
                h += bowl + rim * math.exp(-rr * rr);
                // fresh craters have blocky rims
                if (fresh > 0.4f && r > 0.7f && r < 1.6f) rockMul += 3f * fresh;
            }
            return h;
        }

        static float Fbm(float2 p, float minFeature, float largeScaleWeight, float midScaleWeight)
        {
            // amplitude ~ 0.035 * lambda^0.9 (lunar surface roughness, Hurst ~0.9)
            float h = 0;
            float lambda = 256f;
            for (int o = 0; o < 10; o++, lambda *= 0.5f)
            {
                if (lambda < minFeature * 1.5f) break;
                float amp = 0.035f * math.pow(lambda, 0.9f);
                float w = lambda >= 32f ? largeScaleWeight : (lambda >= 8f ? midScaleWeight : 1f);
                if (w <= 0.001f) continue;
                h += w * amp * noise.snoise(p / lambda + new float2(o * 17.13f, o * 31.71f));
            }
            return h;
        }

        static float Rocks(float2 p, float minFeature, float density, int seed)
        {
            const float S = 3f;
            float2 cellF = math.floor(p / S);
            int2 cell = (int2)cellF;
            float h = 0;
            for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                int2 c = cell + new int2(i, j);
                uint hh = math.hash(new int3(c, 424242 ^ seed));
                if (Rand(hh) > 0.012f * density) continue;        // mare plains: low rock abundance (Diviner)
                uint h2 = math.hash(new uint2(hh, 3u));
                uint h3 = math.hash(new uint2(h2, 5u));
                float2 centre = (new float2(c.x, c.y) + new float2(Rand(h2), Rand(h3))) * S;
                float u = Rand(math.hash(new uint2(h3, 9u)));
                float rad = 0.15f + 0.75f * u * u * u * u;      // mostly cobbles; rare boulders up to ~0.9 m
                if (rad * 2f < minFeature) continue;
                float r = math.length(p - centre) / rad;
                if (r >= 1f) continue;
                h += rad * 0.7f * math.sqrt(1 - r * r);
            }
            return h;
        }

        /// <summary>
        /// Full surface height in frame coordinates. <paramref name="minFeature"/> is the smallest
        /// wavelength to include (use the mesh spacing for render LODs, 0 for physics).
        /// </summary>
        public float Height(double x, double z, float minFeature = 0f)
        {
            float data = DataHeight(x, z);
            float2 p = new float2((float)x, (float)z);
            float w = NacWeight(x, z);

            // Pit funnel model (DTM is unreliable inside ~60 m of the pit).
            float rho = PitRho(p);
            float pitW = math.smoothstep(1.7f, 1.25f, rho);
            float funnel = rimHeight - funnelDepth * math.smoothstep(1.0f, 0.6f, rho);
            float baseH = math.lerp(data, funnel, pitW);

            // Detail
            float calm = math.smoothstep(0.9f, 1.6f, rho);  // no craters in the funnel
            float edgeCalm = 1f;
            if (hasCaveBox != 0)
            {
                float dx = math.min(math.abs((float)x - caveBox.x), math.abs((float)x - caveBox.z));
                float dz = math.min(math.abs((float)z - caveBox.y), math.abs((float)z - caveBox.w));
                bool insideX = x > caveBox.x - 3 && x < caveBox.z + 3;
                bool insideZ = z > caveBox.y - 3 && z < caveBox.w + 3;
                if ((insideX && dz < 3f) || (insideZ && dx < 3f)) edgeCalm = 0f;
            }

            float rockMul = 1f + 2f * math.smoothstep(3.0f, 1.2f, rho);   // rubble near the pit
            float d = 0;
            if (calm > 0)
            {
                float nonNac = 1 - w;
                // small craters everywhere, larger ones only where the data is too coarse to have them
                uint ds = detailSeed;
                if (minFeature < 2f && edgeCalm > 0) d += CraterLevel(p, 4f, 0.45f, 11u ^ ds, ref rockMul) * edgeCalm;
                if (minFeature < 4f) d += CraterLevel(p, 8f, 0.45f, 12u ^ ds, ref rockMul);
                if (minFeature < 8f) d += CraterLevel(p, 16f, 0.45f, 13u ^ ds, ref rockMul);
                if (minFeature < 16f) d += CraterLevel(p, 32f, 0.40f * (0.35f + 0.65f * nonNac), 14u ^ ds, ref rockMul);
                if (minFeature < 32f && nonNac > 0.01f) d += nonNac * CraterLevel(p, 64f, 0.40f, 15u ^ ds, ref rockMul);
                if (minFeature < 64f && nonNac > 0.01f) d += nonNac * CraterLevel(p, 128f, 0.35f, 16u ^ ds, ref rockMul);
                if (minFeature < 128f && nonNac > 0.01f) d += nonNac * CraterLevel(p, 256f, 0.30f, 17u ^ ds, ref rockMul);
                d *= calm;
            }
            float large = 1 - w;                 // NAC already resolves >= ~16 m relief
            float mid = 1 - 0.6f * w;
            d += Fbm(p + (float)(detailSeed % 9973u) * 0.731f, minFeature, large, mid) * (0.35f + 0.65f * calm);
            if (minFeature < 1.5f && edgeCalm > 0)
                d += Rocks(p, minFeature, rockMul * (rho < 1.0f ? 0.3f : 1f), (int)(detailSeed & 0x7FFFFFFF)) * edgeCalm;
            float h = baseH + d;
            if (pad0.z > 0)
            {
                float pd = math.length(p - pad0.xy);
                h = math.lerp(h, pad0.w, math.smoothstep(pad0.z + 4f, pad0.z, pd));
            }
            return h;
        }
    }
}
