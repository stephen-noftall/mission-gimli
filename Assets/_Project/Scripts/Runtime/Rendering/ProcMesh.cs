using System.Collections.Generic;
using UnityEngine;

namespace MoonBaseAlpha.Rendering
{
    /// <summary>Small procedural geometry kit: swept tubes, lathes, paraboloids, rounded boxes, helices.</summary>
    public static class ProcMesh
    {
        /// <summary>Tube of radius r swept along a polyline; corners are rounded with the given bend radius.</summary>
        public static Mesh SweepTube(IList<Vector3> path, float r, int sides = 10, float bendRadius = 0f, bool caps = true)
        {
            var pts = bendRadius > 0 ? RoundCorners(path, bendRadius, 6) : new List<Vector3>(path);
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            Vector3 prevNormal = Vector3.zero;
            float len = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 dir = i == 0 ? pts[1] - pts[0] : i == pts.Count - 1 ? pts[i] - pts[i - 1] : (pts[i + 1] - pts[i - 1]);
                dir.Normalize();
                if (i > 0) len += Vector3.Distance(pts[i], pts[i - 1]);
                // parallel transport frame
                Vector3 nrm = i == 0 ? Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right).normalized
                                     : Vector3.ProjectOnPlane(prevNormal, dir).normalized;
                prevNormal = nrm;
                Vector3 bin = Vector3.Cross(dir, nrm);
                for (int s = 0; s <= sides; s++)
                {
                    float a = s * Mathf.PI * 2 / sides;
                    Vector3 o = nrm * Mathf.Cos(a) + bin * Mathf.Sin(a);
                    v.Add(pts[i] + o * r); n.Add(o); uv.Add(new Vector2(s / (float)sides, len));
                }
            }
            int ring = sides + 1;
            for (int i = 0; i < pts.Count - 1; i++)
            for (int s = 0; s < sides; s++)
            {
                int a = i * ring + s, b = a + ring;
                t.Add(a); t.Add(b); t.Add(a + 1); t.Add(a + 1); t.Add(b); t.Add(b + 1);
            }
            if (caps)
            {
                for (int e = 0; e < 2; e++)
                {
                    int ri = e == 0 ? 0 : pts.Count - 1;
                    Vector3 dir = e == 0 ? (pts[0] - pts[1]).normalized : (pts[ri] - pts[ri - 1]).normalized;
                    int c = v.Count; v.Add(pts[ri]); n.Add(dir); uv.Add(Vector2.zero);
                    int start = v.Count;
                    for (int s = 0; s <= sides; s++) { v.Add(v[ri * ring + s]); n.Add(dir); uv.Add(Vector2.zero); }
                    for (int s = 0; s < sides; s++)
                    {
                        if (e == 0) { t.Add(c); t.Add(start + s); t.Add(start + s + 1); }
                        else { t.Add(c); t.Add(start + s + 1); t.Add(start + s); }
                    }
                }
            }
            return Build("Tube", v, n, uv, t);
        }

        static List<Vector3> RoundCorners(IList<Vector3> p, float rad, int seg)
        {
            var o = new List<Vector3> { p[0] };
            for (int i = 1; i < p.Count - 1; i++)
            {
                Vector3 a = p[i - 1], b = p[i], c = p[i + 1];
                float d = Mathf.Min(rad, Vector3.Distance(a, b) * 0.45f, Vector3.Distance(b, c) * 0.45f);
                Vector3 p0 = b + (a - b).normalized * d, p1 = b + (c - b).normalized * d;
                for (int s = 0; s <= seg; s++)
                {
                    float tt = s / (float)seg;
                    o.Add(Vector3.Lerp(Vector3.Lerp(p0, b, tt), Vector3.Lerp(b, p1, tt), tt));   // quadratic Bezier
                }
            }
            o.Add(p[p.Count - 1]);
            return o;
        }

        /// <summary>Surface of revolution about +Y from a profile of (radius, y) points.</summary>
        public static Mesh Lathe(IList<Vector2> profile, int segments = 32, bool smooth = true)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            for (int s = 0; s <= segments; s++)
            {
                float a = s * Mathf.PI * 2 / segments;
                float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                for (int i = 0; i < profile.Count; i++)
                {
                    var p = profile[i];
                    v.Add(new Vector3(p.x * ca, p.y, p.x * sa));
                    Vector2 d = i == 0 ? profile[1] - profile[0] : i == profile.Count - 1 ? profile[i] - profile[i - 1] : profile[i + 1] - profile[i - 1];
                    var pn = new Vector2(d.y, -d.x).normalized;          // outward in profile space
                    n.Add(new Vector3(pn.x * ca, pn.y, pn.x * sa));
                    uv.Add(new Vector2(s / (float)segments, i / (float)(profile.Count - 1)));
                }
            }
            int m = profile.Count;
            for (int s = 0; s < segments; s++)
            for (int i = 0; i < m - 1; i++)
            {
                int a = s * m + i, b = a + m;
                t.Add(a); t.Add(a + 1); t.Add(b); t.Add(b); t.Add(a + 1); t.Add(b + 1);
            }
            var mesh = Build("Lathe", v, n, uv, t);
            if (!smooth) mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>Wheel tyre: open cylinder band (axis X) with UVs wrapping around the circumference.</summary>
        public static Mesh TyreBand(float radius, float width, int segments = 48, float crown = 0.02f)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            const int rows = 6;
            for (int s = 0; s <= segments; s++)
            {
                float a = s * Mathf.PI * 2 / segments;
                for (int j = 0; j <= rows; j++)
                {
                    float x = -width / 2 + width * j / rows;
                    float xr = x / (width / 2);
                    float r = radius - crown * xr * xr;
                    var dir = new Vector3(0, Mathf.Cos(a), Mathf.Sin(a));
                    v.Add(new Vector3(x, 0, 0) + dir * r);
                    n.Add((dir + new Vector3(-xr * crown * 4f / width, 0, 0)).normalized);
                    uv.Add(new Vector2(s / (float)segments * 24f, j / (float)rows * 2f));
                }
            }
            int m = rows + 1;
            for (int s = 0; s < segments; s++)
            for (int j = 0; j < rows; j++)
            {
                int a = s * m + j, b = a + m;
                t.Add(a); t.Add(b); t.Add(a + 1); t.Add(a + 1); t.Add(b); t.Add(b + 1);
            }
            return Build("Tyre", v, n, uv, t);
        }

        /// <summary>Parabolic dish (open, facing +Y), radius R, depth D.</summary>
        public static Mesh Paraboloid(float R, float D, int seg = 24, int rings = 6)
        {
            var prof = new List<Vector2>();
            for (int i = 0; i <= rings; i++) { float r = R * i / rings; prof.Add(new Vector2(r, D * (r / R) * (r / R))); }
            prof[0] = new Vector2(0.0001f, 0);
            var m = Lathe(prof, seg);
            // make it double-sided by duplicating with flipped winding
            return DoubleSided(m);
        }

        /// <summary>Helix tube along +Y.</summary>
        public static Mesh Helix(float radius, float pitch, float turns, float wire, int segPerTurn = 16)
        {
            var p = new List<Vector3>();
            int n = Mathf.CeilToInt(turns * segPerTurn);
            for (int i = 0; i <= n; i++)
            {
                float a = i * Mathf.PI * 2 / segPerTurn;
                p.Add(new Vector3(Mathf.Cos(a) * radius, pitch * a / (Mathf.PI * 2), Mathf.Sin(a) * radius));
            }
            return SweepTube(p, wire, 6, 0, false);
        }

        /// <summary>Box with bevelled edges (cheap: a box plus chamfer via normals averaged).</summary>
        public static Mesh RoundedBox(Vector3 size, float bevel)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var src = go.GetComponent<MeshFilter>().sharedMesh;
            var m = Object.Instantiate(src);
            Util.Kill(go);
            var v = m.vertices;
            for (int i = 0; i < v.Length; i++) v[i] = Vector3.Scale(v[i], size);
            m.vertices = v;
            m.RecalculateBounds();
            return m;
        }

        public static Mesh DoubleSided(Mesh m)
        {
            var v = new List<Vector3>(m.vertices); var n = new List<Vector3>(m.normals); var uv = new List<Vector2>(m.uv);
            while (uv.Count < v.Count) uv.Add(Vector2.zero);
            var t = new List<int>(m.triangles);
            int c = v.Count;
            for (int i = 0; i < c; i++) { v.Add(v[i]); n.Add(-n[i]); uv.Add(uv[i]); }
            int tc = t.Count;
            for (int i = 0; i < tc; i += 3) { t.Add(t[i] + c); t.Add(t[i + 2] + c); t.Add(t[i + 1] + c); }
            return Build(m.name + "2S", v, n, uv, t);
        }

        static Mesh Build(string name, List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t)
        {
            // make every face's winding agree with its vertex normals (front faces point outwards)
            for (int i = 0; i + 2 < t.Count; i += 3)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                var fn = Vector3.Cross(b - a, c - a);
                if (fn.sqrMagnitude < 1e-14f) continue;
                if (Vector3.Dot(fn, n[t[i]] + n[t[i + 1]] + n[t[i + 2]]) < 0) { int tmp = t[i + 1]; t[i + 1] = t[i + 2]; t[i + 2] = tmp; }
            }
            var m = new Mesh { name = name };
            if (v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            m.RecalculateTangents();
            return m;
        }
    }

    /// <summary>Procedural textures (generated once at startup, no image assets needed).</summary>
    public static class ProcTex
    {
        static float Hash(int x, int y) { uint h = (uint)(x * 374761393 + y * 668265263); h = (h ^ (h >> 13)) * 1274126177u; return (h & 0xFFFFFF) / 16777216f; }
        static float VNoise(float x, float y, int period)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            int x0 = ((ix % period) + period) % period, y0 = ((iy % period) + period) % period;
            int x1 = (x0 + 1) % period, y1 = (y0 + 1) % period;
            return Mathf.Lerp(Mathf.Lerp(Hash(x0, y0), Hash(x1, y0), fx), Mathf.Lerp(Hash(x0, y1), Hash(x1, y1), fx), fy);
        }
        static float Fbm(float x, float y, int period, int oct)
        {
            float s = 0, a = 0.5f;
            for (int o = 0; o < oct; o++) { s += a * VNoise(x, y, period); x *= 2; y *= 2; period *= 2; a *= 0.5f; }
            return s;
        }

        /// <summary>Normal map from a height function (tileable).</summary>
        static Texture2D NormalFromHeight(string name, int size, System.Func<int, int, float> h, float strength)
        {
            var hs = new float[size * size];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) hs[y * size + x] = h(x, y);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { name = name, wrapMode = TextureWrapMode.Repeat };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float l = hs[y * size + (x - 1 + size) % size], r = hs[y * size + (x + 1) % size];
                float d = hs[((y - 1 + size) % size) * size + x], u = hs[((y + 1) % size) * size + x];
                var n = new Vector3((l - r) * strength, (d - u) * strength, 1).normalized;
                px[y * size + x] = new Color32((byte)((n.x * 0.5f + 0.5f) * 255), (byte)((n.y * 0.5f + 0.5f) * 255), (byte)((n.z * 0.5f + 0.5f) * 255), 255);
            }
            tex.SetPixels32(px); tex.Apply(true, false);
            return tex;
        }

        /// <summary>Crinkled multilayer insulation (gold/silver foil) normal map.</summary>
        public static Texture2D FoilNormal() => NormalFromHeight("FoilNormal", 256,
            (x, y) => Fbm(x / 16f, y / 16f, 16, 4) + 0.6f * Mathf.Abs(Fbm(x / 6f + 9, y / 6f, 43, 2) - 0.5f), 6f);

        /// <summary>Brushed aluminium normal map (fine streaks along U).</summary>
        public static Texture2D BrushedNormal() => NormalFromHeight("BrushedNormal", 128,
            (x, y) => VNoise(x / 32f, y * 1.5f, 128) * 0.5f + VNoise(x / 8f, y * 0.8f + 3, 64) * 0.2f, 1.5f);

        /// <summary>Woven nylon webbing: albedo with alpha (gaps between straps).</summary>
        public static Texture2D Webbing(Color strap)
        {
            const int S = 128;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true) { name = "Webbing", wrapMode = TextureWrapMode.Repeat };
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                int cx = x % 32, cy = y % 32;
                bool vert = cx >= 2 && cx < 28, hori = cy >= 2 && cy < 28;
                bool over = ((x / 32 + y / 32) & 1) == 0;
                float shade = 0.8f + 0.2f * Mathf.Sin((over ? cy : cx) * 0.6f);
                float a = (vert || hori) ? 1f : 0f;
                var c = strap * shade * (over == vert ? 1f : 0.85f);
                c.a = a;
                px[y * S + x] = c;
            }
            tex.SetPixels(px); tex.Apply(true, false);
            return tex;
        }

        /// <summary>
        /// Apollo wire-mesh tyre: zinc-coated piano wire woven into a diamond lattice (alpha-cut).
        /// UV u wraps the circumference (x24 in the mesh), v across the width (x2).
        /// </summary>
        public static Texture2D WireMesh()
        {
            const int S = 128;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true) { name = "WireMesh", wrapMode = TextureWrapMode.Repeat };
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = x / (float)S, v = y / (float)S;
                float d1 = Mathf.Abs(Mathf.Repeat(u * 4 + v * 4, 1f) - 0.5f);
                float d2 = Mathf.Abs(Mathf.Repeat(u * 4 - v * 4, 1f) - 0.5f);
                float wire = Mathf.Max(Mathf.InverseLerp(0.43f, 0.48f, d1), Mathf.InverseLerp(0.43f, 0.48f, d2));
                float g = 0.62f + 0.15f * Hash(x / 3, y / 3);
                px[y * S + x] = new Color(g, g, g * 1.02f, wire > 0.5f ? 1f : 0f);
            }
            tex.SetPixels(px); tex.Apply(true, false);
            return tex;
        }

        /// <summary>Fabric weave normal map for the suit's outer layer (Ortho-fabric).</summary>
        public static Texture2D FabricNormal() => NormalFromHeight("FabricNormal", 128,
            (x, y) => 0.5f + 0.25f * Mathf.Sin(x * Mathf.PI * 2 / 8f) * Mathf.Sin(y * Mathf.PI * 2 / 8f) + 0.15f * VNoise(x / 4f, y / 4f, 32), 2.5f);

        /// <summary>Instrument panel: dark face with gauges/labels (emissive mask in alpha).</summary>
        public static Texture2D Panel()
        {
            const int W = 256, H = 128;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = "Panel" };
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var c = new Color(0.06f, 0.06f, 0.065f, 0f);
                // gauges
                for (int g = 0; g < 4; g++)
                {
                    float cx = 32 + g * 64, cy = 80, dx = x - cx, dy = y - cy;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    if (r < 24 && r > 21) c = new Color(0.7f, 0.7f, 0.7f, 0.2f);
                    if (r < 21) c = new Color(0.02f, 0.02f, 0.02f, 0f);
                    if (r < 20 && Mathf.Abs(dy - dx * 0.4f) < 1.2f && dx > 0) c = new Color(0.9f, 0.9f, 0.85f, 0.6f);
                }
                // label strips
                if (y > 20 && y < 30 && (x % 64) > 10 && (x % 64) < 54) c = new Color(0.8f, 0.8f, 0.8f, 0.1f);
                if (y > 38 && y < 46 && (x % 32) > 8 && (x % 32) < 24) c = new Color(0.9f, 0.4f, 0.1f, 0.8f);
                px[y * W + x] = c;
            }
            tex.SetPixels(px); tex.Apply(true, false);
            return tex;
        }

        /// <summary>
        /// Hub-motor cover (round 8): anodised disc with a panel line, 8-bolt circle, centre cap, a fill valve, an
        /// "MR-4 FL" stencil and regolith dust thickening towards the rim (heavier on one side, so rotation reads).
        /// Alpha = disc (cutout).
        /// </summary>
        public static Texture2D HubCap(string label, int seed)
        {
            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true) { name = "HubCap" + label, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[S * S];
            var rng = new System.Random(seed);
            float Noise(float x, float y) => Mathf.PerlinNoise(x + seed * 7.1f, y + seed * 3.3f);
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f) / S * 2 - 1, v = (y + 0.5f) / S * 2 - 1;
                float r = Mathf.Sqrt(u * u + v * v), a = Mathf.Atan2(v, u);
                if (r > 0.98f) { px[y * S + x] = new Color32(0, 0, 0, 0); continue; }
                float baseV = 0.62f + 0.05f * (Noise(u * 3, v * 3) - 0.5f);
                var c = new Color(baseV, baseV, baseV * 1.01f);
                if (Mathf.Abs(r - 0.72f) < 0.012f) c *= 0.45f;                                  // panel line
                if (r > 0.93f) c *= 0.8f;                                                      // rolled edge
                for (int b = 0; b < 8; b++)                                                    // bolt circle
                {
                    float ba = b * Mathf.PI / 4f, bx = 0.55f * Mathf.Cos(ba), by = 0.55f * Mathf.Sin(ba);
                    float d = Mathf.Sqrt((u - bx) * (u - bx) + (v - by) * (v - by));
                    if (d < 0.045f) c = Color.Lerp(new Color(0.85f, 0.85f, 0.88f), new Color(0.25f, 0.25f, 0.27f), d / 0.045f);
                }
                if (r < 0.2f) c = Color.Lerp(new Color(0.3f, 0.3f, 0.32f), c, Mathf.SmoothStep(0.17f, 0.2f, r));   // centre cap
                if (Mathf.Abs(r - 0.2f) < 0.01f) c *= 0.5f;
                { float vx = 0.82f * Mathf.Cos(1.1f), vy = 0.82f * Mathf.Sin(1.1f); if ((u - vx) * (u - vx) + (v - vy) * (v - vy) < 0.035f * 0.035f) c = new Color(0.75f, 0.6f, 0.2f); }  // valve
                // dust: towards the rim, heavier on one side (reads as rotation), speckled
                float side = 0.5f + 0.5f * Mathf.Cos(a - 2.4f);
                float dust = Mathf.Clamp01((r - 0.45f) / 0.5f) * (0.35f + 0.65f * side) * (0.6f + 0.8f * Noise(u * 9, v * 9));
                c = Color.Lerp(c, new Color(0.42f, 0.4f, 0.37f), Mathf.Clamp01(dust) * 0.75f);
                px[y * S + x] = c;
            }
            // stencil, upper half, dark grey
            int sc = 4, w = PixelFont.Width(label, sc);
            PixelFont.Draw(px, S, S, S / 2 - w / 2, (int)(S * 0.66f), label, new Color32(40, 40, 44, 255), sc);
            tex.SetPixels32(px); tex.Apply(true, false);
            return tex;
        }

        // ---------------------------------------------------------------- suit (round 8)

        static Texture2D NewTex(string name, int w, int h, bool clamp = false) =>
            new Texture2D(w, h, TextureFormat.RGBA32, true) { name = name, wrapMode = clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat };

        /// <summary>Suit fabric (limbs, capsule UVs: u around, v along): seams with stitching, faint quilting.</summary>
        public static Texture2D SuitSeams()
        {
            const int S = 256;
            var tex = NewTex("SuitSeams", S, S); var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = x / (float)S, v = y / (float)S;
                float c = 0.97f - 0.03f * Mathf.PerlinNoise(u * 12f, v * 12f);
                // circumferential seams (bands) and one long seam down the inside
                float band = Mathf.Min(Mathf.Abs(Frac(v * 4f) - 0.5f), 0.5f);
                bool seamH = Mathf.Abs(Frac(v * 4f + 0.5f) - 0.5f) < 0.012f;
                bool seamV = Mathf.Abs(u - 0.5f) < 0.006f;
                if (seamH || seamV) c *= 0.72f;
                // stitching: small dashes beside the seams
                bool stitchH = Mathf.Abs(Frac(v * 4f + 0.5f) - 0.5f) is > 0.02f and < 0.026f && (x / 3) % 2 == 0;
                bool stitchV = Mathf.Abs(u - 0.5f) is > 0.012f and < 0.016f && (y / 3) % 2 == 0;
                if (stitchH || stitchV) c *= 0.85f;
                c *= 1f - 0.03f * (1f - band * 2f);
                byte b = (byte)(Mathf.Clamp01(c) * 255);
                px[y * S + x] = new Color32(b, b, (byte)(b * 0.985f), 255);
            }
            tex.SetPixels32(px); tex.Apply(true, false);
            return tex;
        }

        static float Frac(float x) => x - Mathf.Floor(x);

        /// <summary>Hard upper torso (sphere UVs): latitude panel lines, a crew-coloured band all round the chest, fasteners.</summary>
        public static Texture2D HardTorso(Color crew)
        {
            const int S = 256;
            var tex = NewTex("HardTorso", S, S); var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = x / (float)S, v = y / (float)S;
                var c = new Color(0.95f, 0.95f, 0.93f);
                if (Mathf.Abs(v - 0.38f) < 0.004f || Mathf.Abs(v - 0.74f) < 0.004f || Mathf.Abs(Frac(u * 6f) - 0.5f) < 0.004f && v > 0.38f && v < 0.74f) c *= 0.7f;
                if (v > 0.58f && v < 0.62f) c = crew;                                              // crew stripe
                if (Mathf.Abs(v - 0.66f) < 0.006f && (x % 21) < 3) c *= 0.6f;                      // fasteners
                px[y * S + x] = c;
            }
            tex.SetPixels32(px); tex.Apply(true, false);
            return tex;
        }

        /// <summary>PLSS backpack faces (cube UVs per face): panel border, access panel, screws, vent grille, label.</summary>
        public static Texture2D PlssPanels()
        {
            const int S = 256;
            var tex = NewTex("PlssPanels", S, S, true); var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = x / (float)S, v = y / (float)S;
                var c = new Color(0.93f, 0.93f, 0.91f);
                float edge = Mathf.Min(Mathf.Min(u, 1 - u), Mathf.Min(v, 1 - v));
                if (edge < 0.035f && edge > 0.02f) c *= 0.7f;                                     // panel border line
                if (u > 0.15f && u < 0.85f && v > 0.5f && v < 0.85f && (Mathf.Abs(u - 0.15f) < 0.006f || Mathf.Abs(u - 0.85f) < 0.006f || Mathf.Abs(v - 0.5f) < 0.006f || Mathf.Abs(v - 0.85f) < 0.006f)) c *= 0.65f;
                foreach (var (sx, sy) in new[] { (0.08f, 0.08f), (0.92f, 0.08f), (0.08f, 0.92f), (0.92f, 0.92f) })
                    if ((u - sx) * (u - sx) + (v - sy) * (v - sy) < 0.015f * 0.015f) c = new Color(0.45f, 0.45f, 0.47f);
                if (u > 0.25f && u < 0.75f && v > 0.12f && v < 0.3f && (y / 4) % 2 == 0) c *= 0.55f;   // vent grille
                px[y * S + x] = c;
            }
            PixelFont.Draw(px, S, S, S / 2 - PixelFont.Width("PLSS", 5) / 2, (int)(S * 0.6f), "PLSS", new Color32(70, 70, 75, 255), 5);
            tex.SetPixels32(px); tex.Apply(true, false);
            return tex;
        }

        /// <summary>Round mission patch (alpha = disc): dark blue, grey Moon with a pit, gold border, "MBA".</summary>
        public static Texture2D MissionPatch()
        {
            const int S = 128;
            var tex = NewTex("MissionPatch", S, S, true); var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f) / S * 2 - 1, v = (y + 0.5f) / S * 2 - 1, r = Mathf.Sqrt(u * u + v * v);
                Color c = r > 0.98f ? new Color(0, 0, 0, 0) : r > 0.86f ? new Color(0.85f, 0.65f, 0.2f) : new Color(0.06f, 0.1f, 0.28f);
                float mr = Mathf.Sqrt((u + 0.1f) * (u + 0.1f) + (v + 0.25f) * (v + 0.25f));
                if (r <= 0.86f && mr < 0.42f) c = Color.Lerp(new Color(0.72f, 0.72f, 0.7f), new Color(0.5f, 0.5f, 0.5f), Mathf.PerlinNoise(u * 6, v * 6));
                if (r <= 0.86f && Mathf.Sqrt((u + 0.05f) * (u + 0.05f) + (v + 0.2f) * (v + 0.2f)) < 0.07f) c = new Color(0.05f, 0.05f, 0.05f);   // the pit
                if (r <= 0.86f && (x * 7 + y * 13) % 97 == 0 && mr > 0.45f) c = Color.white;                                                  // stars
                px[y * S + x] = c;
            }
            PixelFont.Draw(px, S, S, S / 2 - PixelFont.Width("MBA", 4) / 2, (int)(S * 0.68f), "MBA", new Color32(240, 240, 240, 255), 4);
            tex.SetPixels32(px); tex.Apply(true, false);
            return tex;
        }

        /// <summary>Arm tab: crew colour with the crew number.</summary>
        public static Texture2D CrewTab(Color crew, int number)
        {
            const int W = 48, H = 32;
            var tex = NewTex("CrewTab" + number, W, H, true); var px = new Color32[W * H];
            for (int i = 0; i < px.Length; i++) px[i] = crew;
            for (int x = 0; x < W; x++) { px[x] = px[(H - 1) * W + x] = new Color32(230, 230, 230, 255); }
            string s = number.ToString();
            PixelFont.Draw(px, W, H, W / 2 - PixelFont.Width(s, 4) / 2, H / 2 - 10, s, new Color32(245, 245, 245, 255), 4);
            tex.SetPixels32(px); tex.Apply(true, false);
            return tex;
        }

        /// <summary>Soft round particle sprite.</summary>
        // ---------------------------------------------------------------- rover surfaces (round 12, P1)

        static readonly Color Regolith = new(0.42f, 0.4f, 0.37f);

        /// <summary>
        /// Fibreglass fender (u across the fender, v along the arc, both arc ends low down): moulded panel lines with a
        /// rivet row, a stencil, scuffs and scratches, regolith dust building up towards the low ends (and speckled).
        /// </summary>
        public static Texture2D Fibreglass(string label, int seed)
        {
            const int S = 256;
            var tex = NewTex("Fibreglass" + label, S, S, true);
            var px = new Color32[S * S];
            var rnd = new System.Random(seed);
            float N(float x, float y) => Mathf.PerlinNoise(x + seed * 5.3f, y + seed * 2.9f);
            var basec = new Color(0.86f, 0.85f, 0.81f);
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f) / S, v = (y + 0.5f) / S;
                var c = basec * (0.97f + 0.06f * (N(u * 6, v * 6) - 0.5f));
                if (Mathf.Abs(u - 0.08f) < 0.006f || Mathf.Abs(u - 0.92f) < 0.006f) c *= 0.7f;                // moulded edge lines
                if (Mathf.Abs(v - 0.5f) < 0.004f) c *= 0.72f;                                                    // panel joint
                for (int r = 0; r < 7; r++) { float rx = 0.15f + r * 0.117f; if ((u - rx) * (u - rx) + (v - 0.53f) * (v - 0.53f) < 0.006f * 0.006f) c = new Color(0.55f, 0.55f, 0.57f); }   // rivets
                float low = Mathf.Max(Mathf.SmoothStep(0.3f, 0f, v), Mathf.SmoothStep(0.7f, 1f, v));            // the low ends
                float dust = low * (0.55f + 0.6f * N(u * 14, v * 14)) + 0.12f * N(u * 40, v * 40);
                c = Color.Lerp(c, Regolith, Mathf.Clamp01(dust) * 0.8f);
                px[y * S + x] = c;
            }
            // scuffs: short dark strokes, more near the low ends
            for (int k = 0; k < 60; k++)
            {
                float v0 = (float)rnd.NextDouble(); if (rnd.NextDouble() < 0.5) v0 = v0 < 0.5f ? v0 * 0.35f : 1f - (1f - v0) * 0.35f;
                int x0 = rnd.Next(S), y0 = (int)(v0 * S), len = 4 + rnd.Next(18); float ang = (float)rnd.NextDouble() * Mathf.PI;
                for (int t = 0; t < len; t++)
                {
                    int x = x0 + (int)(Mathf.Cos(ang) * t), y = y0 + (int)(Mathf.Sin(ang) * t);
                    if (x < 0 || y < 0 || x >= S || y >= S) continue;
                    var c = (Color)px[y * S + x]; px[y * S + x] = Color.Lerp(c, new Color(0.3f, 0.29f, 0.27f), 0.45f);
                }
            }
            int sc = 3, w = PixelFont.Width(label, sc);
            PixelFont.Draw(px, S, S, S / 2 - w / 2, (int)(S * 0.62f), label, new Color32(60, 62, 70, 255), sc);
            tex.SetPixels32(px); tex.Apply(true, false);
            return tex;
        }

        /// <summary>
        /// Round 13 (T2): the avionics bay's hinged dust covers (as on the Apollo LRV: they shaded / closed over the
        /// radiators). Off-white beta-cloth over a frame: a fine weave, a sewn hem with stitch dashes, the hinge line
        /// with knuckles along one edge, a pull tab and a stencil, regolith dust heavier towards the lower edge.
        /// </summary>
        public static Texture2D DustCover(int seed)
        {
            const int S = 256;
            var tex = NewTex("DustCoverFabric" + seed, S, S, true);
            var px = new Color32[S * S];
            float N(float x, float y) => Mathf.PerlinNoise(x + seed * 3.1f, y + seed * 7.7f);
            var cloth = new Color(0.84f, 0.84f, 0.81f);
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f) / S, v = (y + 0.5f) / S;
                float weave = ((x + y) % 4 < 2 ? 1f : 0.985f) * ((x - y + 1024) % 6 < 3 ? 1f : 0.99f);
                var c = cloth * weave * (0.97f + 0.06f * (N(u * 7, v * 7) - 0.5f));
                float edge = Mathf.Min(Mathf.Min(u, 1 - u), Mathf.Min(v, 1 - v));
                if (edge < 0.055f) c *= 0.93f;                                                       // the folded hem
                if (Mathf.Abs(edge - 0.07f) < 0.004f && ((x + y) / 5) % 2 == 0) c *= 0.62f;          // stitch line
                if (v > 0.9f && v < 0.94f) c = Color.Lerp(c, new Color(0.6f, 0.61f, 0.63f), 0.85f);   // hinge rod
                if (v > 0.885f && v < 0.955f && ((int)(u * 8)) % 2 == 0 && u > 0.08f && u < 0.92f)
                    c = Color.Lerp(c, new Color(0.5f, 0.51f, 0.53f), 0.8f);                           // hinge knuckles
                if (Mathf.Abs(u - 0.5f) < 0.05f && v < 0.07f) c = new Color(0.78f, 0.38f, 0.1f);    // pull tab
                float dust = Mathf.SmoothStep(0.45f, 0f, v) * (0.5f + 0.7f * N(u * 12, v * 12)) + 0.1f * N(u * 40, v * 40);
                c = Color.Lerp(c, Regolith, Mathf.Clamp01(dust) * 0.7f);
                c.a = 1f;
                px[y * S + x] = c;
            }
            var ink = new Color32(70, 72, 80, 255);
            foreach (var (txt, sc, yy) in new[] { ("DUST COVER", 3, 0.58f), ("LIFT AT TAB", 2, 0.48f), ("DO NOT STEP", 2, 0.4f) })
            {
                int w = PixelFont.Width(txt, sc);
                PixelFont.Draw(px, S, S, S / 2 - w / 2, (int)(S * yy), txt, ink, sc);
            }
            tex.SetPixels32(px); tex.Apply(true, false);
            return tex;
        }

        /// <summary>
        /// Painted / anodised hardware for boxes and tubes (UV 0..1 per face): a tint with fine mottling, edge wear and
        /// grime towards the face edges, a few scratches, a dust film. wear = how much the edges show bare metal.
        /// </summary>
        public static Texture2D Paint(string name, Color tint, int seed, float wear, float grime)
        {
            const int S = 128;
            var tex = NewTex(name, S, S);
            var px = new Color32[S * S];
            var rnd = new System.Random(seed);
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f) / S, v = (y + 0.5f) / S;
                float edge = Mathf.Min(Mathf.Min(u, 1 - u), Mathf.Min(v, 1 - v));
                float n = Fbm(u * 8, v * 8, 8, 3);
                var c = tint * (0.94f + 0.12f * (n - 0.5f));
                float e = Mathf.SmoothStep(0.06f, 0f, edge) * (0.5f + n);
                c = Color.Lerp(c, new Color(0.62f, 0.62f, 0.64f), Mathf.Clamp01(e * wear));
                c = Color.Lerp(c, Regolith * 0.8f, Mathf.Clamp01(grime * (0.4f * Mathf.SmoothStep(0.12f, 0f, edge) + 0.3f * Fbm(u * 16 + 3, v * 16 + 7, 16, 2))));
                c.a = 1f;
                px[y * S + x] = c;
            }
            for (int k = 0; k < 14; k++)
            {
                int x0 = rnd.Next(S), y0 = rnd.Next(S), len = 3 + rnd.Next(14); float ang = (float)rnd.NextDouble() * Mathf.PI;
                for (int t = 0; t < len; t++)
                {
                    int x = (x0 + (int)(Mathf.Cos(ang) * t) + S) % S, y = (y0 + (int)(Mathf.Sin(ang) * t) + S) % S;
                    px[y * S + x] = Color.Lerp(px[y * S + x], new Color(0.6f, 0.6f, 0.62f), 0.4f * wear + 0.15f);
                }
            }
            tex.SetPixels32(px); tex.Apply(true, false);
            return tex;
        }

        /// <summary>
        /// Steel plate (crane deck sides / underside, round 12 P4): weld seams, bolt rows along the edges, scuffs and
        /// regolith dust settling low. Tiles; one plate = the whole texture.
        /// </summary>
        public static Texture2D SteelPlate(int seed)
        {
            const int S = 256;
            var tex = NewTex("SteelPlate", S, S);
            var px = new Color32[S * S];
            var rnd = new System.Random(seed);
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f) / S, v = (y + 0.5f) / S;
                float n = Fbm(u * 6, v * 6, 6, 4);
                var c = new Color(0.5f, 0.51f, 0.53f) * (0.9f + 0.2f * (n - 0.5f));
                float seamU = Mathf.Min(u, 1 - u), seamV = Mathf.Min(v, 1 - v);
                float seam = Mathf.Min(seamU, seamV);
                if (seam < 0.012f) c = new Color(0.36f, 0.35f, 0.33f) * (0.85f + 0.3f * Hash(x, y));            // weld bead
                else if (seam < 0.02f) c *= 0.8f;                                                                  // heat tint
                for (int k = 0; k < 8; k++)
                {
                    float bx = (k + 0.5f) / 8f;
                    foreach (float by in new[] { 0.06f, 0.94f })
                        if ((u - bx) * (u - bx) + (v - by) * (v - by) < 0.012f * 0.012f) c = new Color(0.68f, 0.68f, 0.7f);
                }
                float dust = Mathf.SmoothStep(0.45f, 0f, v) * (0.4f + 0.6f * Fbm(u * 12 + 5, v * 12, 12, 2));
                c = Color.Lerp(c, Regolith, Mathf.Clamp01(dust) * 0.7f);
                c.a = 1f;
                px[y * S + x] = c;
            }
            for (int k = 0; k < 40; k++)
            {
                int x0 = rnd.Next(S), y0 = rnd.Next(S), len = 4 + rnd.Next(30); float ang = (float)rnd.NextDouble() * Mathf.PI;
                for (int t = 0; t < len; t++)
                {
                    int x = (x0 + (int)(Mathf.Cos(ang) * t) + S) % S, y = (y0 + (int)(Mathf.Sin(ang) * t) + S) % S;
                    px[y * S + x] = Color.Lerp(px[y * S + x], new Color(0.7f, 0.7f, 0.72f), 0.35f);
                }
            }
            tex.SetPixels32(px); tex.Apply(true, false);
            return tex;
        }

        public static Texture2D SoftDot()
        {
            const int S = 32;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true) { name = "SoftDot", wrapMode = TextureWrapMode.Clamp };
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float dx = (x + 0.5f) / S * 2 - 1, dy = (y + 0.5f) / S * 2 - 1;
                float a = Mathf.Clamp01(1 - Mathf.Sqrt(dx * dx + dy * dy));
                px[y * S + x] = new Color(1, 1, 1, a * a);
            }
            tex.SetPixels(px); tex.Apply(true, false);
            return tex;
        }
    }
}
