using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using MoonBaseAlpha.Terrain;

namespace MoonBaseAlpha.Cave
{
    /// <summary>
    /// Implicit description of the Marius Hills Pit and the start of the tunnel network (frame coordinates).
    /// Air = above the surface OR inside the cave. f(p) &gt; 0 in air, &lt; 0 in rock.
    ///
    /// Pit (LROC Pits Atlas): elliptical, inner shaft 55 x 49 m, 40 m deep below the plain, with a
    /// funnel ~10 m deep (modelled in <see cref="HeightField"/>), overhangs near floor level, boulder floor
    /// sloping down into the tube mouth on the down-rille side. A flat landing spot under the crane.
    /// The tunnels themselves come from <see cref="TunnelNetwork"/> primitives.
    /// </summary>
    public struct CaveField
    {
        public HeightField hf;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<TunnelPrim> prims;
        public int primCount;
        public float rim;              // frame Y of the plain
        public float pitFloor;         // frame Y
        public float mouthFloorY;      // frame Y of the tube floor at the shaft wall
        public float2 landingSpot;     // flat pad under the crane (frame xz)
        // graded ramp from the landing pad down the debris mound into the tube (u = distance along the pit axis)
        public float rampU0, rampU1;   // ramp start (pad edge) / end (meets the tube floor, inside the pit box)
        public float rampEndY;         // tube floor height at rampU1
        public const float RampBlend = 4f;   // m of rounded transition at each end

        /// <summary>Graded floor height along the pit axis: pad height before rampU0, constant grade, rounded ends.</summary>
        public float RampY(float u)
        {
            float padY = pitFloor + 1.5f, L = rampU1 - rampU0, e = RampBlend;
            float g = (padY - rampEndY) / (L - e);
            float x = math.clamp(u - rampU0, 0, L);
            float F = x < e ? x * x / (2 * e) : x < L - e ? x - e / 2 : L - e - (L - x) * (L - x) / (2 * e);
            return padY - g * F;
        }

        /// <summary>Max grade of the ramp (deg).</summary>
        public float RampGradeDeg => math.degrees(math.atan((pitFloor + 1.5f - rampEndY) / (rampU1 - rampU0 - RampBlend)));

        static float SMin(float a, float b, float k)
        {
            float h = math.saturate(0.5f + 0.5f * (b - a) / k);
            return math.lerp(b, a, h) - k * h * (1 - h);
        }

        /// <summary>Distance-ish to the pit shaft air volume (negative inside).</summary>
        public float ShaftSdf(float3 p)
        {
            float2 xz = p.xz;
            float2 d = xz - hf.pitCentre;
            float u = math.dot(d, hf.pitAxis);
            float v = d.x * hf.pitAxis.y - d.y * hf.pitAxis.x;
            float depth = rim - p.y;
            float flare = 1f + 0.18f * math.smoothstep(28f, 37f, depth);
            float wobble = 1f + 0.06f * noise.snoise(new float2(math.atan2(v, u) * 2.2f, depth * 0.08f));
            float a = hf.innerA * flare * wobble, b = hf.innerB * flare * wobble;
            float re = math.sqrt(u * u / (a * a) + v * v / (b * b));
            float shaftD = (re - 1f) * 0.5f * (a + b);
            // debris mound: the pad on top, then a graded ramp (<= 12 deg) the full width of the floor - no trench
            float floorY = RampY(u);
            float rubble = 1.2f * math.max(0, noise.snoise(xz * 0.21f)) + 0.6f * noise.snoise(xz * 0.6f);
            // radar-surveyed landing zone, levelled for the crane platform + ramps (radius ~11 m) ...
            float pad = math.smoothstep(19f, 15f, math.length(xz - landingSpot));
            // ... and a cleared lane from it down the talus to the tube mouth
            float2 mouthXZ = hf.pitCentre + hf.pitAxis * rampU1;
            float2 lane = mouthXZ - landingSpot;
            float lt = math.saturate(math.dot(xz - landingSpot, lane) / math.dot(lane, lane));
            float laneD = math.length(xz - (landingSpot + lane * lt));
            float clear = math.max(pad, math.smoothstep(9f, 6f, laneD));
            floorY += 0.6f * rubble * (1 - clear);
            if (pad > 0) floorY = math.lerp(floorY, pitFloor + 1.5f, pad);
            float shaft = math.max(shaftD, floorY - p.y);
            return math.max(shaft, p.y - (rim + 30f));
        }

        public float CaveSdf(float3 p)
        {
            float shaft = ShaftSdf(p);
            float net = primCount > 0 ? TunnelSdf.Eval(prims, primCount, p, out _, out _) : 1e9f;
            float cave = SMin(shaft, net, 4f);
            // the ramp continues into the tube as a fill of fallen debris until it meets the tube floor
            {
                float2 d = p.xz - hf.pitCentre;
                float u = math.dot(d, hf.pitAxis), v = d.x * hf.pitAxis.y - d.y * hf.pitAxis.x;
                if (u > rampU0 && u < rampU1 + 1f && math.abs(v) < 45f && p.y > mouthFloorY - 8f)
                    cave = math.max(cave, RampY(u) - p.y);
            }
            float floorness = math.saturate((p.y - (pitFloor + 5f)) / 3f);   // rough walls, smooth prepared floor
            float rough = 0.55f * noise.snoise(p * 0.17f) + 0.22f * noise.snoise(p * 0.55f + 17.3f);
            return cave + rough * floorness * math.step(shaft, net);
        }

        /// <summary>f &gt; 0 in air, &lt; 0 in rock.</summary>
        public float Density(float3 p, float surfaceY) => math.max(p.y - surfaceY, -CaveSdf(p));
    }
}
