using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace MoonBaseAlpha.Cave
{
    /// <summary>One tunnel element (Burst-friendly).</summary>
    public struct TunnelPrim
    {
        public const int Tube = 0, Chamber = 1, Shaft = 2;
        public int type;
        public float3 a, b;          // tube: endpoints (x, floorY, z); chamber: a = centre (x, floorY, z); shaft: a = top floor, b = bottom
        public float hw, hv;         // half-width, half-height of the cross-section (m)
        public float hwEnd;          // tube: half-width at b (tapering spurs / mouth widening)
        public float dropT, drop;    // lava-fall step position (0..1) and size (m)
        public float dropW;          // half-width of the step (fraction of the length; 0 = legacy 0.04)
        public float moundT, moundH, moundR;   // breakdown pile on the floor
        public uint seed;
        public byte deadEnd;         // tube end b is closed (rounded taper)
        public byte kind;            // 0 tube / chamber, 1 level ramp, 2 junction ramp (talus / collapse ramp at a crossing), 3 ramp connector
        public int level;
    }

    /// <summary>
    /// Endless, layered, seeded lava-tube network under the Marius Hills.
    ///
    /// Layout: per level, a lattice of cells (Cell m) in (u, v) where +u points down-rille (the direction
    /// the lava flowed, WNW at the pit) and v is across. Each cell has one junction node (jittered).
    ///  - Backbone: every node links downhill to one of the 3 nodes in the next row -> from anywhere there is
    ///    always a way onward, and the network never ends.
    ///  - Branches: extra downhill / lateral links (loops); dead-end spurs (tight, must reverse out).
    ///  - Levels: stacked tube systems ~LevelSpacing apart (older flows below younger). Ramps descend to
    ///    the next level; some junction floors have collapsed into the level below (shaft + talus cone).
    ///  - Grade: floors drop ~1.04% along u (measured from LRO data along the rille above).
    ///  - Sizes follow the published estimates: main tubes 40-120 m wide, chambers up to ~300 m, branches
    ///    15-30 m, spurs 12-20 m; width:height ~3:1 for wide tubes.
    /// Everything derives from the world seed + cell coordinates, so any place regenerates identically.
    /// </summary>
    public class TunnelNetwork
    {
        public const float Cell = 320f;
        public const float LevelSpacing = 75f;
        public const float Grade = 0.0104f;   // 10.4 m/km
        /// <summary>Design grades (deg): level ramps, crossing ramps / lava falls / breakdown piles. Rover: ~30 deg on talus.</summary>
        public const float MaxLevelRampDeg = 18f, JunctionRampDeg = 12f;
        /// <summary>Over/under crossings need this much rock between the lower tube's roof and the upper floor.</summary>
        public const float MinRoof = 6f;
        readonly Dictionary<int3, TunnelPrim[]> baseCache = new(), cellCache = new();

        public readonly uint seed;
        public readonly float2 origin;        // pit centre (frame xz)
        public readonly float2 dirU, dirV;    // down-rille / across
        public readonly float level0Floor;    // floor Y of level 0 at u = 0 (frame)
        public readonly float3 mouthFloor;    // floor point at the pit shaft wall where the network starts
        public readonly float rimY;
        public int maxLevels = 6;
        /// <summary>
        /// The lava-flow layout (round 11, default in the game): when set, the queries below answer from it and the
        /// lattice generator above is parked (still used by its own tests).
        /// </summary>
        public FlowNetwork Flow { get; private set; }
        public bool IsFlow => Flow != null;
        public void UseFlowLayout(string code, FlowTubes.Settings settings = null) { Flow = new FlowNetwork(this, code, settings); maxLevels = Flow.L.flows.Count; }
        /// <summary>Surface height (frame Y) at xz - used to keep level-0 ceilings under at least ~12 m of rock.</summary>
        public System.Func<float2, float> surfaceAt;

        public TunnelNetwork(uint seed, float2 pitCentre, float2 downRille, float rimY, float mouthFloorY, float shaftRadius)
        {
            this.seed = seed;
            origin = pitCentre;
            dirU = math.normalize(downRille);
            dirV = new float2(-dirU.y, dirU.x);
            level0Floor = mouthFloorY;
            this.rimY = rimY;
            var m = origin + dirU * shaftRadius;
            mouthFloor = new float3(m.x, mouthFloorY, m.y);
        }

        // ------------------------------------------------------------------ hashing

        uint H(int level, int i, int j, uint salt) => math.hash(new int4(level, i, j, (int)(salt ^ seed)));
        static float R01(uint h) => (h & 0xFFFFFF) / 16777216f;
        float Rand(int level, int i, int j, uint salt) => R01(H(level, i, j, salt));

        public float2 ToXZ(float u, float v) => origin + dirU * u + dirV * v;
        public float2 ToUV(float2 xz) { var d = xz - origin; return new float2(math.dot(d, dirU), math.dot(d, dirV)); }

        /// <summary>The tube level whose nominal floor is nearest p (deep levels sink faster, so depth / 75 m drifts).</summary>
        public int LevelAt(float3 p)
        {
            if (Flow != null) return Flow.FlowAt(p);
            float u = ToUV(p.xz).x; int best = 0; float bd = float.MaxValue;
            for (int L = 0; L < maxLevels; L++) { float d = math.abs(LevelFloor(L, u) - p.y); if (d < bd) { bd = d; best = L; } }
            return best;
        }

        /// <summary>Floor height of a level at down-rille distance u (before local variation).</summary>
        public float LevelFloor(int level, float u) => Flow != null ? Flow.FlowFloorY(math.clamp(level, 0, maxLevels - 1), ToXZ(u, 0)) : level0Floor - level * LevelSpacing - Grade * u
                                                      - (level >= 3 ? (level - 2) * 0.004f * math.max(0, u) : 0f);   // deep levels sink faster (speculative zone)

        // ------------------------------------------------------------------ nodes

        public bool NodeExists(int level, int i, int j) => i >= 0 && level >= 0 && level < maxLevels;

        public float3 NodePos(int level, int i, int j)
        {
            if (Flow != null)
            {
                // (0, 0, 0): a point ~130 m down the trunk from the mouth (the old first junction, used by the pit ramp);
                // else node j of the flow graph
                if (level == 0 && i == 0 && j == 0) { var F = Flow.WayPts[Flow.FirstDownstreamEdge]; float acc = 0;
                    for (int k = 1; k < F.Length; k++) { acc += math.distance(F[k - 1].xz, F[k].xz); if (acc >= 130f) return F[k]; } return F[F.Length - 1]; }
                return Flow.NodeFrame[math.clamp(j, 0, Flow.NodeFrame.Count - 1)];
            }
            float u, v;
            if (level == 0 && i == 0 && j == 0) { u = Cell * 0.42f; v = 0f; }          // first junction ahead of the pit mouth
            else
            {
                u = (i + 0.5f) * Cell + (Rand(level, i, j, 1) - 0.5f) * 0.55f * Cell;
                v = j * Cell + (Rand(level, i, j, 2) - 0.5f) * 0.55f * Cell;
            }
            var xz = ToXZ(u, v);
            float y = LevelFloor(level, u) + (Rand(level, i, j, 3) - 0.5f) * 10f;
            return new float3(xz.x, y, xz.y);
        }

        float MainHalfWidth(int level, int i, int j, uint salt)
        {
            // lognormal around 30 m half-width (60 m wide), clamped to 20..60 (40..120 m wide)
            float g = (Rand(level, i, j, salt) + Rand(level, i, j, salt + 101) + Rand(level, i, j, salt + 202) - 1.5f) * 1.2f;
            return math.clamp(30f * math.exp(g * 0.45f), 20f, 60f);
        }
        float BranchHalfWidth(int level, int i, int j, uint salt) => math.lerp(8f, 15f, Rand(level, i, j, salt));
        float SpurHalfWidth(int level, int i, int j, uint salt) => math.lerp(6f, 10f, Rand(level, i, j, salt));

        /// <summary>Half-height for a half-width: natural arched tubes, ~1.3:1 (narrow) to ~1.9:1 (wide).</summary>
        public static float HeightFor(float hw) => Tunnel.HeightFor(hw);

        /// <summary>Backbone successor column offset (-1, 0, +1) for node (level, i, j).</summary>
        public int BackboneDj(int level, int i, int j)
        {
            float r = Rand(level, i, j, 10);
            return r < 0.25f ? -1 : r < 0.75f ? 0 : 1;
        }

        public bool HasRamp(int level, int i, int j) => level < maxLevels - 1 && i >= 1 && Rand(level, i, j, 30) < 0.2f;

        /// <summary>
        /// Node on level+1 that the ramp from (level, i, j) leads to: the next row if that is gentle enough
        /// (&lt;= MaxLevelRampDeg), else one or two rows further (a longer, gentler ramp). Drivable both ways.
        /// </summary>
        public int2 RampTarget(int level, int i, int j)
        {
            var n = NodePos(level, i, j);
            for (int di = 1; di <= 3; di++)
            {
                var m = NodePos(level + 1, i + di, j);
                float h = math.distance(n.xz, m.xz) - 2f * RampFlat;   // the sloped middle (flat run-outs at both ends)
                if (h > 0 && math.degrees(math.atan2(n.y - m.y, h)) <= MaxLevelRampDeg) return new int2(i + di, j);
            }
            return new int2(i + 3, j);
        }

        /// <summary>Level ramps run flat for this long at each end (m): tubes that cross near an end meet them at their own floor height.</summary>
        public const float RampFlat = 30f;

        /// <summary>Floor parameter of a level ramp: flat run-outs at both ends (dropW holds the flat fraction), straight in between.</summary>
        public static float RampT(in TunnelPrim t, float tt) => t.kind == 1 && t.dropW > 0f ? math.saturate((tt - t.dropW) / math.max(1e-3f, 1f - 2f * t.dropW)) : tt;
        /// <summary>Half-width of node (level, i, j)'s junction chamber (same numbers as <see cref="BaseCellPrims"/>).</summary>
        public float ChamberHalfWidth(int level, int i, int j)
        {
            float rc = Rand(level, i, j, 50);
            return rc < 0.015f ? math.lerp(160f, 220f, Rand(level, i, j, 51)) : rc < 0.11f ? math.lerp(60f, 150f, Rand(level, i, j, 51)) : MainHalfWidth(level, i, j, 20) * 1.15f;
        }

        /// <summary>
        /// Which side (+1 / -1 along dirV) the level ramp of node (level, i, j) runs on: away from the backbone, the
        /// branch, the lateral link and collapse shafts at either end. Levels are stacked (nodes of all levels sit
        /// nearly above each other), so a ramp in the node-to-node corridor would run under its own level's backbone
        /// (a thin, holed floor) and break into the lower level's tube from above (a cliff): ramps get their own
        /// corridor beside it.
        /// </summary>
        public int RampSide(int level, int i, int j)
        {
            int dj = BackboneDj(level, i, j);
            int dj2 = Rand(level, i, j, 21) < 0.25f ? (dj == 0 ? (Rand(level, i, j, 22) < 0.5f ? -1 : 1) : 0) : 99;
            bool lateral = Rand(level, i, j, 24) < 0.3f;
            var n = NodePos(level, i, j);
            var rt = RampTarget(level, i, j);
            var m = NodePos(level + 1, rt.x, rt.y);
            float Score(int s)
            {
                float c = 0;
                if (dj == s) c += 4;
                if (dj2 == s) c += 2;
                if (lateral && s == 1) c += 1;
                var side = dirV * s;
                if (HasCollapse(level, i, j) && math.dot(math.normalizesafe(CollapsePos(level, i, j).xz - n.xz), side) > 0.2f) c += 6;
                if (HasCollapse(level, rt.x, rt.y) && math.dot(math.normalizesafe(CollapsePos(level, rt.x, rt.y).xz - m.xz), side) > 0.2f) c += 6;   // talus cone below
                if (HasCollapse(level + 1, rt.x, rt.y) && math.dot(math.normalizesafe(CollapsePos(level + 1, rt.x, rt.y).xz - m.xz), side) > 0.2f) c += 6;
                int djm = BackboneDj(level + 1, rt.x, rt.y);
                if (djm == s) c += 1;
                // the lower level under the ramp's start: its backbone / branch / lateral heading into this side
                // would cross the ramp half-way down (a step the junction ramps can't fix)
                int djl = BackboneDj(level + 1, i, j);
                if (djl == s) c += 4;
                if (Rand(level + 1, i, j, 21) < 0.25f && (djl == 0 ? (Rand(level + 1, i, j, 22) < 0.5f ? -1 : 1) : 0) == s) c += 2;
                if (s == 1 && Rand(level + 1, i, j, 24) < 0.3f) c += 1;
                // tubes arriving at the lower end node from this side
                for (int d = -1; d <= 1; d++)
                    if (d == -s && BackboneDj(level + 1, rt.x - 1, rt.y - d) == d) c += 2;
                return c;
            }
            float a = Score(-1), b = Score(1);
            return a < b ? -1 : b < a ? 1 : (Rand(level, i, j, 32) < 0.5f ? -1 : 1);
        }

        /// <summary>
        /// The level ramp of node (level, i, j) as a path: node n -> flat connector -> ramp start a (beside n, clear of the
        /// node's chamber and tubes) -> straight ramp down -> ramp end b (beside the lower node, at its floor) -> flat
        /// connector -> lower node m. Valid when <see cref="HasRamp"/>.
        /// </summary>
        public void RampPath(int level, int i, int j, out float3 n, out float3 a, out float3 b, out float3 m, out float hw)
        {
            n = NodePos(level, i, j);
            var rt = RampTarget(level, i, j);
            m = NodePos(level + 1, rt.x, rt.y);
            hw = BranchHalfWidth(level, i, j, 31) + 2f;
            var key = new int3(level, i, j);
            if (!rampCache.TryGetValue(key, out var c))
            {
                c = ChooseRampCorridor(level, i, j, n, m, hw);
                if (rampCache.Count > 100000) rampCache.Clear();
                rampCache[key] = c;
            }
            a = c.a; b = c.b;
        }

        readonly Dictionary<int3, (float3 a, float3 b, float score)> rampCache = new();
        readonly Dictionary<int3, TunnelPrim[]> plainCache = new();

        /// <summary>Conflict score of the chosen corridor (0 = clean; tests).</summary>
        public float RampConflict(int level, int i, int j) { RampPath(level, i, j, out _, out _, out _, out _, out _); return rampCache[new int3(level, i, j)].score; }

        /// <summary>A cell's primitives without its level ramp (what a ramp corridor must keep clear of).</summary>
        TunnelPrim[] PlainCellPrims(int level, int i, int j)
        {
            var key = new int3(level, i, j);
            if (plainCache.TryGetValue(key, out var arr)) return arr;
            var tmp = new List<TunnelPrim>(10);
            BaseCellPrims(level, i, j, tmp, false);
            arr = tmp.ToArray();
            if (plainCache.Count > 120000) plainCache.Clear();
            plainCache[key] = arr;
            return arr;
        }

        /// <summary>
        /// Pick the ramp's corridor: both sides of the junction, three offsets each (clear of the end chambers first,
        /// then 45 / 90 m further out). Each candidate path (connector, ramp with its run-outs, connector) is tested
        /// against the tubes, chambers and shafts of both levels around it: a sample counts as a conflict where the ramp
        /// floor would hang over another void (a cliff), sit under another floor with a thin roof (holes above), or
        /// cross a shaft. The cleanest candidate wins (ties: the side heuristic, then the shorter offset).
        /// </summary>
        (float3 a, float3 b, float score) ChooseRampCorridor(int level, int i, int j, float3 n, float3 m, float hw)
        {
            var rt = RampTarget(level, i, j);
            float clear = math.max(math.max(ChamberHalfWidth(level, i, j), ChamberHalfWidth(level + 1, rt.x, rt.y)),
                                   1.2f * math.max(MainHalfWidth(level, i, j, 20), MainHalfWidth(level + 1, rt.x, rt.y, 20)));
            float off0 = math.min(clear, 120f) + hw + 6f;
            int pref = RampSide(level, i, j);
            var prims = new List<TunnelPrim>(256);
            for (int L = level; L <= level + 1; L++)
            for (int ii = i - 2; ii <= rt.x + 1; ii++)
            for (int jj = j - 3; jj <= j + 3; jj++)
                if (NodeExists(L, ii, jj)) prims.AddRange(PlainCellPrims(L, ii, jj));
            float rampHv = HeightFor(hw);
            (float3, float3, float) best = default; float bestScore = float.MaxValue;
            var near = new List<TunnelPrim>(64);
            foreach (int s in new[] { pref, -pref })
            for (int k = 0; k < 3; k++)
            {
                float off = off0 + 45f * k;
                var o2 = dirV * (s * off); var o = new float3(o2.x, 0, o2.y);
                var a = n + o; var b = m + o;
                // prims near this candidate
                var lo = math.min(math.min(n, m), math.min(a, b)) - new float3(80, 0, 80);
                var hi = math.max(math.max(n, m), math.max(a, b)) + new float3(80, 0, 80);
                near.Clear();
                foreach (var p in prims)
                {
                    float r = math.max(p.hw, p.hwEnd) + 10f;
                    var pl = p.type == TunnelPrim.Tube ? math.min(p.a, p.b) - r : p.a - r;
                    var ph = p.type == TunnelPrim.Tube ? math.max(p.a, p.b) + r : p.a + r;
                    if (pl.x <= hi.x && ph.x >= lo.x && pl.z <= hi.z && ph.z >= lo.z) near.Add(p);
                }
                float len = math.distance(a.xz, b.xz);
                float f = math.min(0.3f, RampFlat / math.max(1f, len));
                int conflicts = 0;
                void Test(float3 q, float y)
                {
                    float yc = y + 1.55f * rampHv;
                    foreach (var p in near)
                    {
                        if (p.type == TunnelPrim.Shaft)
                        {
                            if (math.distance(q.xz, p.a.xz) < p.hw * 1.2f + hw && y < p.a.y + 2f && y > p.b.y - 2f) { conflicts++; return; }
                            continue;
                        }
                        float pf, pc, w;
                        if (p.type == TunnelPrim.Chamber)
                        {
                            if (math.distance(q.xz, p.a.xz) > p.hw * 1.12f + hw) continue;
                            pf = p.a.y; pc = pf + 1.45f * p.hv;
                        }
                        else
                        {
                            float t = SegParam(q.xz, p.a.xz, p.b.xz);
                            w = math.lerp(p.hw, p.hwEnd, t) * 1.18f + hw;
                            if (math.distance(q.xz, math.lerp(p.a.xz, p.b.xz, t)) > w) continue;
                            pf = FloorAt(p, t); pc = pf + 1.55f * p.hv;
                        }
                        if (pf < y - 2.5f && pc > y - 6f) { conflicts++; return; }        // a void under the ramp floor: cliff / thin floor
                        if (pf > y + 2.5f && pf < yc + 6f) { conflicts++; return; }       // a floor just above the ramp's vault: holes
                    }
                }
                foreach (var (p0, p1, sloped) in new[] { (n, a, false), (a, b, true), (b, m, false) })
                {
                    float sl = math.distance(p0.xz, p1.xz);
                    int ns = math.max(2, (int)(sl / 8f));
                    for (int q = 0; q <= ns; q++)
                    {
                        float t = q / (float)ns;
                        var pt = math.lerp(p0, p1, t);
                        float y = sloped ? math.lerp(a.y, b.y, math.saturate((t - f) / math.max(1e-3f, 1f - 2f * f))) : p0.y;
                        Test(pt, y);
                    }
                }
                float score = conflicts + 0.3f * k + (s == pref ? 0f : 0.2f);
                if (score < bestScore) { bestScore = score; best = (a, b, conflicts); }
            }
            return best;
        }

        /// <summary>
        /// Collapsed junction floor (shaft into a chamber one level down). The hash is only weakly decorrelated
        /// across levels, so raw collapses stack into one open column through every level; a cell whose floor
        /// collapsed on the level above keeps its own floor (the talus cone lands on it).
        /// </summary>
        public bool HasCollapse(int level, int i, int j) =>
            level >= 0 && level < maxLevels - 1 && i >= 2 && Rand(level, i, j, 40) < 0.12f && !HasCollapse(level - 1, i, j);

        /// <summary>
        /// Nearest collapse shaft on the floor level of <paramref name="p"/> (within 8 m vertically): its top centre,
        /// radius and drop to the floor below. False if none within <paramref name="range"/> of its edge.
        /// </summary>
        public bool CollapseNear(float3 p, float range, out float3 top, out float radius, out float drop)
        {
            if (Flow != null) return Flow.CollapseNear(p, range, out top, out radius, out drop);
            top = default; radius = 0; drop = 0;
            var uv = ToUV(p.xz);
            int ci = (int)math.floor(uv.x / Cell), cj = (int)math.round(uv.y / Cell);
            float best = range;
            bool found = false;
            for (int level = 0; level < maxLevels - 1; level++)
            for (int i = ci - 1; i <= ci + 1; i++)
            for (int j = cj - 1; j <= cj + 1; j++)
            {
                if (!HasCollapse(level, i, j)) continue;
                var t = CollapsePos(level, i, j);
                if (math.abs(t.y - p.y) > 8f) continue;
                float rs = math.lerp(9f, 16f, Rand(level, i, j, 41));
                float edge = math.distance(t.xz, p.xz) - rs;
                if (edge < best) { best = edge; top = t; radius = rs; drop = t.y - LevelFloor(level + 1, ToUV(t.xz).x); found = true; }
            }
            return found;
        }

        /// <summary>Collapse shaft position: off to the side of the junction so the main floor stays intact.</summary>
        public float3 CollapsePos(int level, int i, int j)
        {
            var n = NodePos(level, i, j);
            float ang = Rand(level, i, j, 45) * math.PI * 2f;
            float dist = 0.6f * MainHalfWidth(level, i, j, 20) + 6f;
            return n + new float3(math.cos(ang) * dist, 0, math.sin(ang) * dist);
        }

        public TunnelPrim ClampRoofPublic(TunnelPrim p) => ClampRoof(p);

        TunnelPrim ClampRoof(TunnelPrim p)
        {
            if (p.level != 0 || surfaceAt == null) return p;
            float3 c = p.type == TunnelPrim.Tube ? (p.a + p.b) * 0.5f : p.a;
            float surf = math.min(surfaceAt(c.xz), p.type == TunnelPrim.Tube ? math.min(surfaceAt(p.a.xz), surfaceAt(p.b.xz)) : surfaceAt(c.xz));
            float floorY = p.type == TunnelPrim.Tube ? math.max(p.a.y, p.b.y) : p.a.y;
            float k = p.type == TunnelPrim.Tube ? 1.55f : 1.45f;   // ceiling = floor + k * hv
            float maxHv = (surf - 12f - floorY) / k;
            if (p.hv > maxHv) p.hv = math.max(5f, maxHv);
            return p;
        }

        // ------------------------------------------------------------------ primitives

        TunnelPrim Tube(int level, float3 a, float3 b, float hw, float hwEnd, uint s, bool deadEnd)
        {
            var p = new TunnelPrim
            {
                type = TunnelPrim.Tube, a = a, b = b, hw = hw, hwEnd = hwEnd, hv = HeightFor(math.max(hw, hwEnd)),
                seed = s, deadEnd = (byte)(deadEnd ? 1 : 0), level = level,
            };
            float r = R01(math.hash(new uint2(s, 7)));
            float len = math.distance(a.xz, b.xz);
            if (r < 0.18f)
            {
                // a lava fall, worn into a rubble slope the rover can drive: the step spreads over ~7x its height
                // (smoothstep peak slope 1.5 x mean -> <= ~12 deg + floor rubble); short tubes get a smaller step
                p.drop = math.min(3f + 4f * R01(math.hash(new uint2(s, 9))), len * 0.6f / 7f);
                p.dropW = math.max(0.04f, 3.5f * p.drop / math.max(len, 1f));
                float lo = p.dropW + 0.05f, hi = math.max(lo, 1f - p.dropW - 0.05f);
                p.dropT = math.clamp(0.3f + 0.4f * R01(math.hash(new uint2(s, 8))), lo, hi);
                if (p.drop < 1f) { p.drop = 0; p.dropW = 0; }
            }
            if (R01(math.hash(new uint2(s, 11))) < 0.35f)
            {
                p.moundT = 0.2f + 0.6f * R01(math.hash(new uint2(s, 12)));
                p.moundR = 10f + 15f * R01(math.hash(new uint2(s, 14)));
                // breakdown pile: peak slope 0.86 H/R -> H <= 0.3 R keeps it under ~15 deg
                p.moundH = math.min(2f + 6f * R01(math.hash(new uint2(s, 13))), 0.3f * p.moundR);
            }
            return p;
        }

        /// <summary>
        /// All primitives belonging to cell (level, i, j): its junction, outgoing links, spur, ramp, collapse, plus the
        /// talus ramps of the tube crossings this cell owns (see <see cref="AddJunctionRamps"/>). Cached.
        /// </summary>
        public void CellPrims(int level, int i, int j, List<TunnelPrim> outList)
        {
            if (!NodeExists(level, i, j)) return;
            var key = new int3(level, i, j);
            if (!cellCache.TryGetValue(key, out var arr))
            {
                var tmp = new List<TunnelPrim>(12);
                tmp.AddRange(BaseCached(level, i, j));
                AddJunctionRamps(level, i, j, tmp);
                arr = tmp.ToArray();
                if (cellCache.Count > 60000) cellCache.Clear();
                cellCache[key] = arr;
            }
            outList.AddRange(arr);
        }

        TunnelPrim[] BaseCached(int level, int i, int j)
        {
            var key = new int3(level, i, j);
            if (baseCache.TryGetValue(key, out var arr)) return arr;
            var tmp = new List<TunnelPrim>(10);
            BaseCellPrims(level, i, j, tmp);
            arr = tmp.ToArray();
            if (baseCache.Count > 120000) baseCache.Clear();
            baseCache[key] = arr;
            return arr;
        }

        /// <summary>The cell's own primitives before crossings are resolved (deterministic from the seed).</summary>
        public void BaseCellPrims(int level, int i, int j, List<TunnelPrim> outList) => BaseCellPrims(level, i, j, outList, true);

        void BaseCellPrims(int level, int i, int j, List<TunnelPrim> outList, bool withRamp)
        {
            if (!NodeExists(level, i, j)) return;
            int start = outList.Count;
            var n = NodePos(level, i, j);
            uint cs = H(level, i, j, 99);

            // backbone
            int dj = BackboneDj(level, i, j);
            float hwMain = MainHalfWidth(level, i, j, 20);
            float hwNext = MainHalfWidth(level, i + 1, j + dj, 20);
            outList.Add(Tube(level, n, NodePos(level, i + 1, j + dj), hwMain, hwNext, cs ^ 0x1111u, false));

            // junction / chamber: rare giant voids (320-440 m), chambers 120-300 m, else a junction a bit wider than the tube
            float rc = Rand(level, i, j, 50);
            float chamberHw = ChamberHalfWidth(level, i, j);
            outList.Add(new TunnelPrim { type = TunnelPrim.Chamber, a = n, hw = chamberHw, hv = math.min(HeightFor(chamberHw) * (rc < 0.11f ? 0.8f : 1f), 45f), seed = cs ^ 0x2222u, level = level });

            // extra downhill branch
            if (Rand(level, i, j, 21) < 0.25f)
            {
                int dj2 = dj == 0 ? (Rand(level, i, j, 22) < 0.5f ? -1 : 1) : 0;
                float hb = BranchHalfWidth(level, i, j, 23);
                outList.Add(Tube(level, n, NodePos(level, i + 1, j + dj2), hb, hb, cs ^ 0x3333u, false));
            }
            // lateral link (loops)
            if (Rand(level, i, j, 24) < 0.3f)
            {
                float hb = BranchHalfWidth(level, i, j, 25);
                outList.Add(Tube(level, n, NodePos(level, i, j + 1), hb, hb, cs ^ 0x4444u, false));
            }
            // dead-end spur
            if (Rand(level, i, j, 26) < 0.5f)
            {
                float ang = Rand(level, i, j, 27) * math.PI * 2f;
                float len = math.lerp(60f, 200f, Rand(level, i, j, 28));
                float2 d = new float2(math.cos(ang), math.sin(ang));
                var e = n + new float3(d.x * len, -Grade * len - 2f, d.y * len);
                float hs = SpurHalfWidth(level, i, j, 29);
                outList.Add(Tube(level, n, e, hs, hs * 0.5f, cs ^ 0x5555u, true));
            }
            // ramp down to the next level (drivable, <= 18 deg) in its own corridor beside the stacked tubes:
            // flat connector out of the junction, the ramp, flat connector into the lower junction
            if (withRamp && HasRamp(level, i, j))
            {
                RampPath(level, i, j, out var rn, out var ra, out var rb, out var rm, out float hb);
                var c1 = Tube(level, rn, ra, hb, hb, cs ^ 0x6161u, false);
                c1.drop = 0; c1.dropW = 0; c1.moundH = 0; c1.kind = 3;
                var rp = Tube(level, ra, rb, hb, hb, cs ^ 0x6666u, false);
                rp.drop = 0; rp.moundH = 0; rp.kind = 1;
                rp.dropW = math.min(0.3f, RampFlat / math.max(1f, math.distance(ra.xz, rb.xz)));
                var c2 = Tube(level + 1, rb, rm, hb, hb, cs ^ 0x6262u, false);
                c2.drop = 0; c2.dropW = 0; c2.moundH = 0; c2.kind = 3;
                outList.Add(c1); outList.Add(rp); outList.Add(c2);
            }
            // collapsed floor: shaft into a chamber of the level below (+ talus cone there)
            if (HasCollapse(level, i, j))
            {
                float rs = math.lerp(9f, 16f, Rand(level, i, j, 41));
                var top = CollapsePos(level, i, j);
                var below = new float3(top.x, LevelFloor(level + 1, ToUV(top.xz).x), top.z);
                outList.Add(new TunnelPrim { type = TunnelPrim.Shaft, a = top, b = below, hw = rs, seed = cs ^ 0x7777u, level = level });
            }
            // the chamber under a collapse in the level above
            if (level >= 1 && HasCollapse(level - 1, i, j))
            {
                var up = CollapsePos(level - 1, i, j);
                var c = new float3(up.x, LevelFloor(level, ToUV(up.xz).x), up.z);
                float hwc = math.lerp(30f, 55f, Rand(level, i, j, 42));
                outList.Add(new TunnelPrim { type = TunnelPrim.Chamber, a = c, hw = hwc, hv = HeightFor(hwc), seed = cs ^ 0x8888u, level = level,
                                             moundH = math.lerp(8f, 18f, Rand(level, i, j, 43)), moundR = hwc * 0.7f });
                outList.Add(Tube(level, c, n, BranchHalfWidth(level, i, j, 44) + 3f, hwMain * 0.8f, cs ^ 0x9999u, false));
            }
            // pit mouth -> first junction
            if (level == 0 && i == 0 && j == 0)
                outList.Add(Tube(0, mouthFloor, n, 11f, hwMain, 0xC0FFEEu ^ seed, false));
            if (level == 0 && surfaceAt != null)
                for (int k = start; k < outList.Count; k++) outList[k] = ClampRoof(outList[k]);
        }

        /// <summary>Collect every primitive whose bounds come within <paramref name="margin"/> of the box.</summary>
        public void Collect(float3 min, float3 max, float margin, List<TunnelPrim> result)
        {
            if (Flow != null) { Flow.Collect(min, max, margin, result); return; }
            result.Clear();
            var tmp = new List<TunnelPrim>(16);
            // which (u, v) cells can contribute: links reach one row forward and one column sideways,
            // spurs up to 200 m, chambers up to 220 m: scan a generous window
            float2 c0 = ToUV(min.xz), c1 = ToUV(new float2(max.x, min.z)), c2 = ToUV(new float2(min.x, max.z)), c3 = ToUV(max.xz);
            float2 uvMin = math.min(math.min(c0, c1), math.min(c2, c3)) - (margin + 260f);
            uvMin.x -= 3f * Cell;   // level ramps start up to 3 rows back
            float2 uvMax = math.max(math.max(c0, c1), math.max(c2, c3)) + (margin + 260f);
            int i0 = (int)math.floor(uvMin.x / Cell) - 1, i1 = (int)math.floor(uvMax.x / Cell) + 1;
            int j0 = (int)math.floor(uvMin.y / Cell) - 1, j1 = (int)math.floor(uvMax.y / Cell) + 1;
            for (int level = 0; level < maxLevels; level++)
            {
                // quick vertical reject per level
                float uMid = math.clamp((uvMin.x + uvMax.x) * 0.5f, 0, 1e9f);
                float yl = LevelFloor(level, uMid);
                float span = Grade * (uvMax.x - uvMin.x) + 130f;
                if (yl - span - LevelSpacing > max.y + margin || yl + span + 60f < min.y - margin) continue;
                for (int i = math.max(0, i0); i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    tmp.Clear();
                    CellPrims(level, i, j, tmp);
                    foreach (var p in tmp) if (Overlaps(p, min - margin, max + margin)) result.Add(p);
                }
            }
        }

        public static bool OverlapsBox(in TunnelPrim p, float3 min, float3 max, float margin) => Overlaps(p, min - margin, max + margin);

        /// <summary>Canonical primitive order (independent of how the list was collected).</summary>
        public static int Compare(TunnelPrim x, TunnelPrim y)
        {
            int c = x.level.CompareTo(y.level); if (c != 0) return c;
            c = x.type.CompareTo(y.type); if (c != 0) return c;
            c = x.seed.CompareTo(y.seed); if (c != 0) return c;
            c = x.a.x.CompareTo(y.a.x); if (c != 0) return c;
            c = x.a.z.CompareTo(y.a.z); if (c != 0) return c;
            return x.b.x.CompareTo(y.b.x);
        }

        static bool Overlaps(in TunnelPrim p, float3 min, float3 max)
        {
            float3 lo, hi;
            float r = math.max(p.hw, p.hwEnd) + 4f;
            switch (p.type)
            {
                case TunnelPrim.Chamber:
                    lo = p.a - new float3(p.hw + 4, p.hv * 0.4f + p.moundH + 4, p.hw + 4);
                    hi = p.a + new float3(p.hw + 4, p.hv * 2.2f + 6, p.hw + 4);
                    break;
                case TunnelPrim.Shaft:
                    lo = math.min(p.a, p.b) - new float3(p.hw + 4, 4, p.hw + 4);
                    hi = math.max(p.a, p.b) + new float3(p.hw + 4, 4, p.hw + 4);
                    break;
                default:
                    lo = math.min(p.a, p.b) - new float3(r, 6 + p.drop + p.moundH, r);
                    hi = math.max(p.a, p.b) + new float3(r, 2f * HeightFor(r) + 8f, r);
                    break;
            }
            return math.all(lo <= max) && math.all(hi >= min);
        }

        // ------------------------------------------------------------------ crossings -> junctions

        /// <summary>Two primitives (tube/chamber) whose air volumes meet at different floor heights.</summary>
        public struct Crossing
        {
            public TunnelPrim hi, lo;      // higher / lower floor
            public float3 cHi, cLo;        // closest points on their axes (floor height)
            public float drop;             // floor height difference (m)
            public float gap;              // rock between the lower roof and the upper floor (m; < 0 = air volumes overlap)
            public bool Junction => gap < MinRoof;   // overlap or a thin roof -> collapsed into a junction
        }

        /// <summary>Floor height of a tube at parameter t (axis, incl. a lava-fall step; no noise / pile).</summary>
        public static float FloorAt(in TunnelPrim t, float tt)
        {
            float y = math.lerp(t.a.y, t.b.y, RampT(t, tt));
            if (t.drop > 0) { float w = t.dropW > 0 ? t.dropW : 0.04f; y -= t.drop * (math.smoothstep(t.dropT - w, t.dropT + w, tt) - tt); }
            return y;
        }

        static float SegParam(float2 p, float2 a, float2 b)
        {
            var ab = b - a; float l2 = math.lengthsq(ab);
            return l2 < 1e-6f ? 0 : math.saturate(math.dot(p - a, ab) / l2);
        }

        /// <summary>Closest points between segments a0a1 and b0b1 (xz): parameters s (on a) and t (on b).</summary>
        static void ClosestSegSeg(float2 a0, float2 a1, float2 b0, float2 b1, out float s, out float t)
        {
            float2 d1 = a1 - a0, d2 = b1 - b0, r = a0 - b0;
            float aa = math.dot(d1, d1), e = math.dot(d2, d2), f = math.dot(d2, r);
            float c = math.dot(d1, r), bb = math.dot(d1, d2), den = aa * e - bb * bb;
            s = den > 1e-6f ? math.saturate((bb * f - c * e) / den) : 0f;
            t = e > 1e-6f ? (bb * s + f) / e : 0f;
            if (t < 0) { t = 0; s = aa > 1e-6f ? math.saturate(-c / aa) : 0; }
            else if (t > 1) { t = 1; s = aa > 1e-6f ? math.saturate((bb - c) / aa) : 0; }
        }

        /// <summary>Do X and Y meet with a floor step (or cross with a thin roof)? Tubes and chambers only.</summary>
        public static bool TryCrossing(in TunnelPrim X, in TunnelPrim Y, out Crossing c)
        {
            c = default;
            if (X.type == TunnelPrim.Shaft || Y.type == TunnelPrim.Shaft) return false;
            if (X.type == TunnelPrim.Chamber && Y.type == TunnelPrim.Chamber) return false;
            float3 px, py; float wx, wy, hx, hy, fx, fy;
            if (X.type == TunnelPrim.Tube && Y.type == TunnelPrim.Tube)
            {
                ClosestSegSeg(X.a.xz, X.b.xz, Y.a.xz, Y.b.xz, out float s, out float t);
                px = math.lerp(X.a, X.b, s); py = math.lerp(Y.a, Y.b, t);
                fx = FloorAt(X, s); fy = FloorAt(Y, t);
                wx = math.lerp(X.hw, X.hwEnd, s); wy = math.lerp(Y.hw, Y.hwEnd, t);
                hx = 1.55f * X.hv; hy = 1.55f * Y.hv;
            }
            else
            {
                bool xTube = X.type == TunnelPrim.Tube;
                var T = xTube ? X : Y; var C = xTube ? Y : X;
                float t = SegParam(C.a.xz, T.a.xz, T.b.xz);
                var pt = math.lerp(T.a, T.b, t);
                float ft = FloorAt(T, t), wt = math.lerp(T.hw, T.hwEnd, t);
                if (xTube) { px = pt; py = C.a; fx = ft; fy = C.a.y; wx = wt; wy = C.hw; hx = 1.55f * T.hv; hy = 1.45f * C.hv; }
                else { px = C.a; py = pt; fx = C.a.y; fy = ft; wx = C.hw; wy = wt; hx = 1.45f * C.hv; hy = 1.55f * T.hv; }
            }
            float dist = math.distance(px.xz, py.xz);
            if (dist > wx + wy + 1f) return false;
            float d = fx - fy;
            if (math.abs(d) < 2f) return false;           // same level: the floors merge smoothly
            bool xHi = d > 0;
            c.hi = xHi ? X : Y; c.lo = xHi ? Y : X;
            c.cHi = xHi ? new float3(px.x, fx, px.z) : new float3(py.x, fy, py.z);
            c.cLo = xHi ? new float3(py.x, fy, py.z) : new float3(px.x, fx, px.z);
            c.drop = math.abs(d);
            c.gap = c.drop - (xHi ? hy : hx);
            return true;
        }

        /// <summary>
        /// Talus ramps for a junction crossing: trenches cut into the higher floor (the roof between them collapsed and
        /// the rubble slid into a slope), one each way, descending at &lt;= JunctionRampDeg to the lower floor. Along the
        /// higher tube; if the higher one is a chamber, across the lower tube. Over/under crossings with a thick roof
        /// (gap &gt;= MinRoof) are left alone.
        /// </summary>
        public static void RampsFor(in Crossing c, uint seed, List<TunnelPrim> outList)
        {
            // a level ramp (<= 18 deg) over a lower tube: a 12 deg trench can't descend faster than the ramp itself, so its
            // start ran away up the whole ramp and cut a deep slot into the junction floor above (round 9 finding).
            // No trench; the crossing is left as it is (the mission route checks its links and avoids such steps).
            if (c.hi.kind == 1) return;
            float len = math.max(c.drop / math.tan(math.radians(JunctionRampDeg)), 15f);
            float2 dir;
            if (c.hi.type == TunnelPrim.Tube) dir = math.normalizesafe(c.hi.b.xz - c.hi.a.xz, new float2(1, 0));
            else { var ld = math.normalizesafe(c.lo.b.xz - c.lo.a.xz, new float2(1, 0)); dir = new float2(-ld.y, ld.x); }
            float wHi = c.hi.type == TunnelPrim.Tube ? math.min(c.hi.hw, c.hi.hwEnd) : c.hi.hw;
            float wLo = c.lo.type == TunnelPrim.Tube ? math.min(c.lo.hw, c.lo.hwEnd) : c.lo.hw;
            float hw = math.clamp(math.min(wHi, wLo) * 0.7f, 5f, 9f);
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                // the ramp reaches the lower floor where it enters the lower tube's footprint (its edge, not its axis:
                // inside the footprint the floor is the lower one anyway, so ending at the axis left a step at the edge)
                var rampDir = -sgn * dir;                                   // horizontal, from S towards the crossing
                float inset;
                if (c.lo.type == TunnelPrim.Tube)
                {
                    var loDir = math.normalizesafe(c.lo.b.xz - c.lo.a.xz, new float2(1, 0));
                    float sinT = math.abs(rampDir.x * loDir.y - rampDir.y * loDir.x);
                    inset = math.min(wLo * 0.85f / math.max(sinT, 0.3f), 60f);
                }
                else inset = wLo * 0.85f;
                // keep the crossing's offset across dir (stay inside the higher tube), slide along dir to the entry point
                float o = math.dot(c.cLo.xz - c.cHi.xz, dir);
                var exz = (c.hi.type == TunnelPrim.Tube ? c.cHi.xz + dir * o : c.cLo.xz) - rampDir * inset;
                // real floor heights at both ends (either tube may slope: level ramps, lava falls), then the length
                float yE = FloorOf(c.lo, exz);
                float yS = c.cHi.y; var sxz = exz;
                for (int it = 0; it < 8; it++)
                {
                    float L = math.max((yS - yE) / math.tan(math.radians(JunctionRampDeg)), 15f);
                    sxz = exz - rampDir * L;
                    float y2 = FloorOf(c.hi, sxz);
                    if (math.abs(y2 - yS) < 0.05f) { yS = y2; break; }
                    yS = y2;
                }
                if (yS - yE < 1f) continue;   // no step on this side
                if (!RampFits(c.hi, sxz)) continue;   // the trench would start outside its own tube (runaway): no room
                var E = new float3(exz.x, yE, exz.y);
                var S = new float3(sxz.x, yS, sxz.y);
                outList.Add(new TunnelPrim
                {
                    type = TunnelPrim.Tube, a = S, b = E, hw = hw, hwEnd = hw, hv = HeightFor(hw),
                    seed = seed ^ (uint)(sgn + 7) * 0x9E3779B9u, level = c.hi.level, kind = 2,
                });
            }
        }

        /// <summary>Does a junction trench starting at sxz stay within the higher prim (tube: its length +- its half-width)?</summary>
        public static bool RampFits(in TunnelPrim hi, float2 sxz)
        {
            if (hi.type == TunnelPrim.Chamber) return math.distance(sxz, hi.a.xz) <= 1.5f * hi.hw + 40f;
            var d = hi.b.xz - hi.a.xz; float l = math.length(d);
            float raw = math.dot(sxz - hi.a.xz, d) / math.max(1e-3f, l * l);
            float tol = (1.5f * math.max(hi.hw, hi.hwEnd) + 40f) / math.max(1f, l);   // into the junction at its end, not hundreds of metres on
            return raw >= -tol && raw <= 1f + tol;
        }

        /// <summary>Floor height of a tube (axis, clamped to its ends) or chamber below a point.</summary>
        static float FloorOf(in TunnelPrim p, float2 xz) => p.type == TunnelPrim.Tube ? FloorAt(p, SegParam(xz, p.a.xz, p.b.xz)) : p.a.y;

        static int CompareKey(int l0, int i0, int j0, int l1, int i1, int j1)
        {
            if (l0 != l1) return l0.CompareTo(l1);
            if (i0 != i1) return i0.CompareTo(i1);
            return j0.CompareTo(j1);
        }

        /// <summary>
        /// Resolve the crossings between this cell's primitives and those of nearby cells (levels +-1, rows +-3,
        /// columns +-2). Each pair is handled once, by the cell with the smaller (level, i, j) key.
        /// </summary>
        void AddJunctionRamps(int level, int i, int j, List<TunnelPrim> own)
        {
            int nOwn = own.Count;
            for (int a = 0; a < nOwn; a++)
            {
                var X = own[a];
                if (X.type == TunnelPrim.Shaft) continue;
                for (int L2 = level - 1; L2 <= level + 1; L2++)
                for (int i2 = i - 3; i2 <= i + 3; i2++)
                for (int j2 = j - 2; j2 <= j + 2; j2++)
                {
                    if (!NodeExists(L2, i2, j2)) continue;
                    int cmp = CompareKey(L2, i2, j2, level, i, j);
                    if (cmp < 0) continue;
                    var other = cmp == 0 ? own : null;
                    var arr = cmp == 0 ? null : BaseCached(L2, i2, j2);
                    int count = cmp == 0 ? nOwn : arr.Length;
                    for (int b = cmp == 0 ? a + 1 : 0; b < count; b++)
                    {
                        var Y = cmp == 0 ? other[b] : arr[b];
                        if (!TryCrossing(X, Y, out var c) || !c.Junction) continue;
                        RampsFor(c, X.seed ^ (Y.seed * 31u), own);
                    }
                }
            }
        }

        /// <summary>Every crossing in a block of cells (tests): pairs of base primitives that meet at different floors.</summary>
        public List<Crossing> CrossingsIn(int level0, int level1, int i0, int i1, int j0, int j1)
        {
            var list = new List<Crossing>();
            var prims = new List<TunnelPrim>();
            for (int L = level0; L <= level1; L++) for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++)
                if (NodeExists(L, i, j)) prims.AddRange(BaseCached(L, i, j));
            for (int a = 0; a < prims.Count; a++)
                for (int b = a + 1; b < prims.Count; b++)
                    if (TryCrossing(prims[a], prims[b], out var c)) list.Add(c);
            return list;
        }

        // ------------------------------------------------------------------ queries used by gameplay

        /// <summary>Tube links leaving node (level, i, j): backbone, branch, lateral, ramp down (kind 1). Drivable both ways.</summary>
        public void Links(int level, int i, int j, List<(int3 to, byte kind)> outList)
        {
            if (!NodeExists(level, i, j)) return;
            int dj = BackboneDj(level, i, j);
            outList.Add((new int3(level, i + 1, j + dj), 0));
            if (Rand(level, i, j, 21) < 0.25f)
            {
                int dj2 = dj == 0 ? (Rand(level, i, j, 22) < 0.5f ? -1 : 1) : 0;
                outList.Add((new int3(level, i + 1, j + dj2), 0));
            }
            if (Rand(level, i, j, 24) < 0.3f) outList.Add((new int3(level, i, j + 1), 0));
            if (HasRamp(level, i, j)) { var rt = RampTarget(level, i, j); outList.Add((new int3(level + 1, rt.x, rt.y), 1)); }
        }

        /// <summary>The dead-end spur of node (level, i, j), if it has one (same numbers as <see cref="BaseCellPrims"/>).</summary>
        public bool Spur(int level, int i, int j, out TunnelPrim spur)
        {
            spur = default;
            if (!NodeExists(level, i, j) || Rand(level, i, j, 26) >= 0.5f) return false;
            foreach (var p in BaseCached(level, i, j))
                if (p.type == TunnelPrim.Tube && p.deadEnd != 0) { spur = p; return true; }
            return false;
        }

        /// <summary>Collapse shaft radius of node (level, i, j) (valid when <see cref="HasCollapse"/>).</summary>
        public float CollapseRadius(int level, int i, int j) => math.lerp(9f, 16f, Rand(level, i, j, 41));

        /// <summary>
        /// The SDF floor under p (first air -> rock going down from p.y + above), or NaN if there is no air there.
        /// Noise and rubble included; the streamed collider is within ~0.3 m of this.
        /// </summary>
        public float FloorY(float3 p, float above = 6f, float below = 12f)
        {
            var buf = new List<TunnelPrim>();
            Collect(p - new float3(30, below + 5, 30), p + new float3(30, above + 5, 30), 8f, buf);
            if (buf.Count == 0) return float.NaN;
            var arr = new NativeArray<TunnelPrim>(buf.ToArray(), Allocator.Temp);
            float result = float.NaN; bool wasAir = false;
            for (float y = p.y + above; y >= p.y - below; y -= 0.1f)
            {
                bool air = TunnelSdf.Eval(arr, arr.Length, new float3(p.x, y, p.z), out _, out _) < 0f;
                if (wasAir && !air) { result = y + 0.05f; break; }
                wasAir = air;
            }
            arr.Dispose();
            return result;
        }

        /// <summary>
        /// The mission route ("golden path"): the shortest drivable way from the pit mouth to a sealed spur on
        /// <paramref name="targetLevel"/>. Dijkstra over the junction graph (tubes both ways, level ramps down; links that
        /// pass over a collapse shaft cost x25), ending at the nearest node on that level whose dead-end spur runs into
        /// solid rock (no other tube within its last half: a closed tube end for the rubble seal).
        /// </summary>
        public GoldenRoute GoldenPath(int targetLevel, int maxI = 40, int maxJ = 14)
        {
            if (Flow != null) return Flow.Route(targetLevel);
            // Dijkstra, then check the chosen links on the real floor; block a failing link and search again
            var blocked = new HashSet<(int3, int3)>();
            var blockedGoals = new HashSet<int3>();
            var verified = new Dictionary<(int3, int3), bool>();
            GoldenRoute r = null, best = null;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                r = GoldenPathOnce(targetLevel, maxI, maxJ, blocked, blockedGoals);
                if (!r.Valid) { if (best != null) best.attempts = attempt; return best ?? r; }
                bool ok = true;
                for (int k = 1; k < r.nodes.Count; k++)   // every failing link of this candidate at once
                {
                    var key = Key(r.nodes[k - 1], r.nodes[k]);
                    if (!verified.TryGetValue(key, out bool good)) verified[key] = good = PathDrivable(LinkPath(r.nodes[k - 1], r.nodes[k]), out _, out _);
                    if (!good) { blocked.Add(key); ok = false; }
                }
                if (ok)
                {
                    // the spur to the seal
                    var goal = r.nodes[r.nodes.Count - 1];
                    var sp = new[] { NodePos(goal.x, goal.y, goal.z), r.points[r.points.Count - 2], r.points[r.points.Count - 1] };
                    if (!PathDrivable(sp, out _, out _)) { blockedGoals.Add(goal); ok = false; }
                }
                r.attempts = attempt + 1;
                if (ok) { r.verified = true; return r; }
                best ??= r;
            }
            if (best != null) best.attempts = 60;
            return best ?? r;
        }

        static (int3, int3) Key(int3 a, int3 b) => CompareKey(a.x, a.y, a.z, b.x, b.y, b.z) <= 0 ? (a, b) : (b, a);

        /// <summary>Steepest slope (deg, over 8 m) a link may have on the real floor: the rover climbs ~30-32 deg on rock.</summary>
        public const float DrivableDeg = 28f;

        /// <summary>
        /// Follow the real floor (SDF, noise and rubble included) along a polyline every ~4 m, like a wheel would:
        /// false if a sample has no floor within a few metres of the last one, or an 8 m stretch is steeper than
        /// <see cref="DrivableDeg"/>.
        /// </summary>
        public bool PathDrivable(float3[] path, out float worstDeg, out float3 worstAt)
        {
            worstDeg = 0; worstAt = default;
            var ps = new List<float3>();
            for (int seg = 1; seg < path.Length; seg++)
            {
                var a = path[seg - 1]; var b = path[seg];
                int n = math.max(1, (int)math.ceil(math.distance(a.xz, b.xz) / 4f));
                for (int s = seg == 1 ? 0 : 1; s <= n; s++) ps.Add(math.lerp(a, b, s / (float)n));
            }
            var ys = new List<float>(ps.Count);
            float prevY = float.NaN;
            var buf = new List<TunnelPrim>();
            // chunks of 10 samples share one (small) primitive list
            for (int c0 = 0; c0 < ps.Count; c0 += 10)
            {
                int c1 = math.min(ps.Count, c0 + 10);
                float3 lo = ps[c0], hi = ps[c0];
                for (int k = c0; k < c1; k++) { lo = math.min(lo, ps[k]); hi = math.max(hi, ps[k]); }
                Collect(lo - new float3(10, 20, 10), hi + new float3(10, 20, 10), 4f, buf);
                if (buf.Count == 0) { worstAt = ps[c0]; worstDeg = 90f; return false; }
                var arr = new NativeArray<TunnelPrim>(buf.ToArray(), Allocator.Temp);
                for (int k = c0; k < c1; k++)
                {
                    var q = ps[k];
                    float y = float.NaN;
                    if (!float.IsNaN(prevY)) y = ScanFloor(arr, new float3(q.x, prevY, q.z), 3.5f, 6f);
                    if (float.IsNaN(y)) y = ScanFloor(arr, q, 10f, 16f);
                    if (float.IsNaN(y)) { arr.Dispose(); worstAt = q; worstDeg = 90f; return false; }
                    prevY = y; ys.Add(y);
                }
                arr.Dispose();
            }
            for (int i = 0; i + 2 < ys.Count; i++)
            {
                float h = math.distance(ps[i].xz, ps[i + 2].xz);
                if (h < 3f) continue;
                float deg = math.degrees(math.atan(math.abs(ys[i + 2] - ys[i]) / h));
                if (deg > worstDeg) { worstDeg = deg; worstAt = ps[i]; }
            }
            return worstDeg <= DrivableDeg;
        }

        /// <summary>First air -> rock transition going down from p.y + above (0.25 m steps, refined), NaN if none.</summary>
        static float ScanFloor(in NativeArray<TunnelPrim> arr, float3 p, float above, float below)
        {
            bool wasAir = false;
            for (float y = p.y + above; y >= p.y - below; y -= 0.5f)
            {
                bool air = TunnelSdf.Eval(arr, arr.Length, new float3(p.x, y, p.z), out _, out _) < 0f;
                if (wasAir && !air)
                {
                    // refine between y and y + 0.5
                    float lo = y, hi = y + 0.5f;
                    for (int k = 0; k < 5; k++) { float mid = (lo + hi) * 0.5f; if (TunnelSdf.Eval(arr, arr.Length, new float3(p.x, mid, p.z), out _, out _) < 0f) hi = mid; else lo = mid; }
                    return (lo + hi) * 0.5f;
                }
                wasAir = air;
            }
            return float.NaN;
        }

        GoldenRoute GoldenPathOnce(int targetLevel, int maxI, int maxJ, HashSet<(int3, int3)> blocked, HashSet<int3> blockedGoals)
        {
            targetLevel = math.clamp(targetLevel, 0, maxLevels - 1);
            // graph (both directions), built once per (level, window) and reused by the re-searches
            var gk = new int3(targetLevel, maxI, maxJ);
            if (graph != null && math.all(graphKey == gk)) return Search(graph, targetLevel, blocked, blockedGoals);
            var adj = new Dictionary<int3, List<(int3 to, float cost, byte kind)>>();
            var links = new List<(int3 to, byte kind)>();
            void AddEdge(int3 a, int3 b, float c, byte k)
            {
                if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<(int3, float, byte)>(6);
                l.Add((b, c, k));
            }
            for (int L = 0; L <= targetLevel; L++)
            for (int i = 0; i <= maxI; i++)
            for (int j = -maxJ; j <= maxJ; j++)
            {
                links.Clear(); Links(L, i, j, links);
                var a = new int3(L, i, j); var pa = NodePos(L, i, j);
                foreach (var (to, kind) in links)
                {
                    if (to.x > targetLevel || to.y > maxI || math.abs(to.z) > maxJ) continue;
                    var path = LinkPath(a, to, true);
                    float c = 0; for (int k = 1; k < path.Length; k++) c += math.distance(path[k - 1], path[k]);
                    if (ShaftOnLink(a, to, path)) c *= 25f;
                    AddEdge(a, to, c, kind); AddEdge(to, a, c, kind);
                }
            }
            graph = adj; graphKey = gk;
            return Search(adj, targetLevel, blocked, blockedGoals);
        }

        Dictionary<int3, List<(int3 to, float cost, byte kind)>> graph; int3 graphKey;
        readonly Dictionary<int3, (bool ok, TunnelPrim spur)> spurCache = new();

        GoldenRoute Search(Dictionary<int3, List<(int3 to, float cost, byte kind)>> adj, int targetLevel, HashSet<(int3, int3)> blocked, HashSet<int3> blockedGoals)
        {
            var start = new int3(0, 0, 0);
            var dist = new Dictionary<int3, float> { [start] = math.distance(mouthFloor, NodePos(0, 0, 0)) };
            var prev = new Dictionary<int3, int3>();
            var open = new SortedSet<(float d, int seq, int3 n)>(Comparer<(float d, int seq, int3 n)>.Create((x, y) => x.d != y.d ? x.d.CompareTo(y.d) : x.seq.CompareTo(y.seq)));
            int seqN = 0;
            open.Add((dist[start], seqN++, start));
            var done = new HashSet<int3>();
            int3 goal = new int3(-1, -1, -1); TunnelPrim spur = default;
            while (open.Count > 0)
            {
                var top = open.Min; open.Remove(top);
                var n = top.n;
                if (!done.Add(n)) continue;
                if (n.x == targetLevel && n.y >= 1 && !blockedGoals.Contains(n))
                {
                    if (!spurCache.TryGetValue(n, out var sc)) { bool okS = SealableSpur(n, out var spS); spurCache[n] = sc = (okS, spS); }
                    if (sc.ok) { spur = sc.spur; goal = n; break; }
                }
                if (!adj.TryGetValue(n, out var es)) continue;
                foreach (var (to, c, _) in es)
                {
                    if (blocked.Count > 0 && blocked.Contains(Key(n, to))) continue;
                    float nd = top.d + c;
                    if (done.Contains(to) || (dist.TryGetValue(to, out var od) && od <= nd)) continue;
                    dist[to] = nd; prev[to] = n;
                    open.Add((nd, seqN++, to));
                }
            }
            var r = new GoldenRoute { targetLevel = targetLevel };
            if (goal.x < 0) return r;
            for (var n = goal; ; n = prev[n]) { r.nodes.Insert(0, n); if (!prev.ContainsKey(n)) break; }
            r.spur = spur;
            // the seal: across the spur just before its rounded end; GIMLI-2 parked 7 m short of it, facing it
            float len = math.distance(spur.a.xz, spur.b.xz);
            r.sealT = 0.8f;
            var dir = math.normalize(spur.b.xz - spur.a.xz);
            r.sealDir = dir;
            r.seal = math.lerp(spur.a, spur.b, r.sealT); r.seal.y = FloorAt(spur, r.sealT);
            float scoutT = r.sealT - 7f / len;
            r.scout = math.lerp(spur.a, spur.b, scoutT); r.scout.y = FloorAt(spur, scoutT);
            r.sealHalfWidth = math.lerp(spur.hw, spur.hwEnd, r.sealT);
            // polyline along the floors (level ramps through their side corridors)
            r.points.Add(mouthFloor); r.pointLevels.Add(0);
            for (int k = 0; k < r.nodes.Count; k++)
            {
                var n = r.nodes[k];
                if (k > 0)
                {
                    var p = LinkPath(r.nodes[k - 1], n);
                    for (int q = 1; q < p.Length - 1; q++) { r.points.Add(p[q]); r.pointLevels.Add(LevelAt(p[q])); }
                }
                r.points.Add(NodePos(n.x, n.y, n.z)); r.pointLevels.Add(n.x);
            }
            float tIn = math.min(0.3f, (spur.hw * 2f + 10f) / len);
            var sIn = math.lerp(spur.a, spur.b, tIn); sIn.y = FloorAt(spur, tIn);
            r.points.Add(sIn); r.pointLevels.Add(targetLevel);
            float stopT = math.max(tIn + 0.01f, scoutT - 8f / len);
            var stop = math.lerp(spur.a, spur.b, stopT); stop.y = FloorAt(spur, stopT);
            r.points.Add(stop); r.pointLevels.Add(targetLevel);
            for (int k = 1; k < r.points.Count; k++) r.Length += math.distance(r.points[k - 1], r.points[k]);
            return r;
        }

        /// <summary>The floor path of a link from node a to node b: straight, or through the ramp corridor (either way).</summary>
        public float3[] LinkPath(int3 a, int3 b, bool approx = false)
        {
            if (b.x == a.x + 1 && HasRamp(a.x, a.y, a.z)) { var p = RampPathPoints(a, approx); return p; }
            if (a.x == b.x + 1 && HasRamp(b.x, b.y, b.z)) { var p = RampPathPoints(b, approx); System.Array.Reverse(p); return p; }
            return new[] { NodePos(a.x, a.y, a.z), NodePos(b.x, b.y, b.z) };
        }

        /// <summary>n, a, b, m of a node's ramp; approx: the side heuristic at the base offset (route-graph costs, no corridor tests).</summary>
        float3[] RampPathPoints(int3 k, bool approx)
        {
            if (!approx) { RampPath(k.x, k.y, k.z, out var n, out var ra, out var rb, out var m, out _); return new[] { n, ra, rb, m }; }
            var n0 = NodePos(k.x, k.y, k.z); var rt = RampTarget(k.x, k.y, k.z); var m0 = NodePos(k.x + 1, rt.x, rt.y);
            float hw = BranchHalfWidth(k.x, k.y, k.z, 31) + 2f;
            float clear = math.max(math.max(ChamberHalfWidth(k.x, k.y, k.z), ChamberHalfWidth(k.x + 1, rt.x, rt.y)),
                                   1.2f * math.max(MainHalfWidth(k.x, k.y, k.z, 20), MainHalfWidth(k.x + 1, rt.x, rt.y, 20)));
            var o2 = dirV * (RampSide(k.x, k.y, k.z) * (math.min(clear, 120f) + hw + 6f)); var o = new float3(o2.x, 0, o2.y);
            return new[] { n0, n0 + o, m0 + o, m0 };
        }

        bool ShaftOnLink(int3 a, int3 b, float3[] path)
        {
            // the shaft in this level's floor, or the talus cone under the shaft of the level above
            foreach (var (n, above) in new[] { (a, 0), (b, 0), (a, 1), (b, 1) })
            {
                int L = n.x - above;
                if (!HasCollapse(L, n.y, n.z)) continue;
                var s = CollapsePos(L, n.y, n.z);
                if (above == 1) s.y = LevelFloor(n.x, ToUV(s.xz).x);
                for (int k = 1; k < path.Length; k++)
                {
                    if (math.abs(path[k].y - s.y) > 12f && math.abs(path[k - 1].y - s.y) > 12f) continue;
                    float t = SegParam(s.xz, path[k - 1].xz, path[k].xz);
                    if (math.distance(s.xz, math.lerp(path[k - 1].xz, path[k].xz, t)) < (above == 1 ? 30f : CollapseRadius(L, n.y, n.z) + 8f)) return true;
                }
            }
            return false;
        }

        /// <summary>The node's spur is long enough and its last half runs alone into solid rock, away from any shaft.</summary>
        bool SealableSpur(int3 n, out TunnelPrim spur)
        {
            if (!Spur(n.x, n.y, n.z, out spur)) return false;
            var s = spur;
            float len = math.distance(s.a.xz, s.b.xz);
            if (len < 70f) return false;
            var buf = new List<TunnelPrim>();
            Collect(math.min(s.a, s.b) - 40f, math.max(s.a, s.b) + 40f, 10f, buf);
            buf.RemoveAll(p => p.type == TunnelPrim.Tube && p.deadEnd != 0 && math.all(p.a == s.a) && math.all(p.b == s.b));
            if (buf.Count == 0) return true;
            var arr = new NativeArray<TunnelPrim>(buf.ToArray(), Allocator.Temp);
            bool ok = true;
            var dir = math.normalize(s.b.xz - s.a.xz);
            for (float t = 0.45f; t <= 1.0001f && ok; t += 0.05f)
            {
                var c = math.lerp(s.a, s.b, t); float fy = FloorAt(s, t);
                float hw = math.lerp(s.hw, s.hwEnd, t) + 3f;
                foreach (float side in new[] { -1f, 0f, 1f })
                {
                    var q = new float3(c.x - dir.y * hw * side, fy + 2.5f, c.z + dir.x * hw * side);
                    if (TunnelSdf.Eval(arr, arr.Length, q, out _, out _) < 4f) { ok = false; break; }
                }
                if (ok && CollapseNear(new float3(c.x, fy, c.z), 12f, out _, out _, out _)) ok = false;
            }
            var beyond = new float3(s.b.x + dir.x * 8f, s.b.y + 2.5f, s.b.z + dir.y * 8f);
            if (ok && TunnelSdf.Eval(arr, arr.Length, beyond, out _, out _) < 4f) ok = false;
            arr.Dispose();
            return ok;
        }

        /// <summary>Walk the network from the pit: follow the backbone, take ramps down; returns the junctions visited.</summary>
        public List<(int level, int i, int j)> ExplorePath(int steps, int targetLevel)
        {
            if (Flow != null) return new List<(int, int, int)> { (0, 0, 0), (0, 0, 1) };
            var path = new List<(int, int, int)>();
            int L = 0, i = 0, j = 0;
            path.Add((L, i, j));
            for (int s = 0; s < steps; s++)
            {
                if (L < targetLevel && HasRamp(L, i, j)) { var rt = RampTarget(L, i, j); L++; i = rt.x; j = rt.y; }
                else { j += BackboneDj(L, i, j); i++; }
                path.Add((L, i, j));
            }
            return path;
        }
    }

    /// <summary>The mission route from the pit mouth to GIMLI-2 (see <see cref="TunnelNetwork.GoldenPath"/>).</summary>
    public class GoldenRoute
    {
        public int targetLevel;
        public readonly List<int3> nodes = new();          // (level, i, j) junctions from (0, 0, 0) to the goal
        public readonly List<float3> points = new();       // floor polyline: mouth, junctions, into the spur, stop point
        public readonly List<int> pointLevels = new();
        /// <summary>Flow layout: which points are junctions (beacons go there; the polyline is dense along curved tubes).</summary>
        public readonly List<bool> pointIsJunction = new();
        public TunnelPrim spur;
        public float sealT, sealHalfWidth, Length;
        /// <summary>Every link was checked on the real floor (and how many searches it took).</summary>
        public bool verified; public int attempts;
        public float3 seal, scout;                          // floor points
        public float2 sealDir;                              // horizontal, into the seal
        /// <summary>Round 12 (S8): the seal is a cave-in part-way along a dead-end tube (Cadet), not at its closed end.</summary>
        public bool CaveIn;
        /// <summary>A through tube was sealed: a second cave-in closes the hollow part from the far side (facing back).</summary>
        public bool BackSeal; public float3 backSeal;
        /// <summary>
        /// Round 13 (T4): shaft descents on the route. Segment shaftAt[i] -> shaftAt[i] + 1 is the drop (top point: the
        /// rig's trolley spot on the upper floor once the game has rigged it, next point: the foot below); shaftIds[i] is
        /// the index in <see cref="FlowNetwork.Shafts"/>. ShaftsWanted: the tier's target (fewer if the layout can't).
        /// </summary>
        public readonly List<int> shaftAt = new(), shaftIds = new();
        public int ShaftsWanted;
        public int ShaftCount => shaftAt.Count;
        public bool IsShaftSegment(int k) => shaftAt.Contains(k);
        public void Recompute() { Length = 0; for (int k = 1; k < points.Count; k++) Length += math.distance(points[k - 1], points[k]); }
        public bool Valid => nodes.Count > 0;

        /// <summary>
        /// Remaining with a progress hint (round 12, S7): search the segments around the last match first - a route can
        /// pass near another part of itself (a spur beside the trunk, another flow under it), and the global closest
        /// point then jumps. Only a match &gt; 40 m off (a teleport, a winch trip off the route) searches the whole route.
        /// </summary>
        public float Remaining(float3 p, ref int hint, out int seg, out float off)
        {
            seg = 0; off = 0;
            if (points.Count < 2) return 0;
            hint = math.clamp(hint, 0, points.Count - 2);
            float best = float.MaxValue, bestT = 0; seg = hint;
            for (int k = math.max(0, hint - 2); k < math.min(points.Count - 1, hint + 8); k++)
            {
                var a = points[k]; var b = points[k + 1]; var ab = b - a;
                float t = math.saturate(math.dot(p - a, ab) / math.max(1e-4f, math.lengthsq(ab)));
                var q = a + ab * t;
                float d = math.distance(new float3(p.x, p.y * 2f, p.z), new float3(q.x, q.y * 2f, q.z));
                if (d < best) { best = d; seg = k; bestT = t; }
            }
            if (best > 40f) { float g = Remaining(p, out seg, out off); hint = seg; return g; }
            off = best; hint = seg;
            float rem = math.distance(points[seg], points[seg + 1]) * (1f - bestT);
            for (int k = seg + 1; k < points.Count - 1; k++) rem += math.distance(points[k], points[k + 1]);
            return rem;
        }

        /// <summary>Closest point on the polyline (xz, with a level match bonus): segment index and the distance left to the end.</summary>
        public float Remaining(float3 p, out int seg, out float off)
        {
            seg = 0; off = float.MaxValue; float best = float.MaxValue, bestT = 0;
            for (int k = 0; k < points.Count - 1; k++)
            {
                var a = points[k]; var b = points[k + 1]; var ab = b - a;
                float t = math.saturate(math.dot(p - a, ab) / math.max(1e-4f, math.lengthsq(ab)));
                var q = a + ab * t;
                float d = math.distance(new float3(p.x, p.y * 2f, p.z), new float3(q.x, q.y * 2f, q.z));   // vertical counts double: levels
                if (d < best) { best = d; seg = k; bestT = t; }
            }
            off = best;
            if (points.Count < 2) return 0;
            float rem = math.distance(points[seg], points[seg + 1]) * (1f - bestT);
            for (int k = seg + 1; k < points.Count - 1; k++) rem += math.distance(points[k], points[k + 1]);
            return rem;
        }
    }

    /// <summary>Signed-distance evaluation of a primitive list (Burst).</summary>
    public static class TunnelSdf
    {
        static float SMin(float a, float b, float k)
        {
            float h = math.saturate(0.5f + 0.5f * (b - a) / k);
            return math.lerp(b, a, h) - k * h * (1 - h);
        }

        /// <summary>&lt; 0 inside the cave air volume.</summary>
        public static float Eval(in NativeArray<TunnelPrim> prims, int count, float3 p, out int nearestLevel, out float floorDist)
        {
            float d = 1e9f; nearestLevel = -1; floorDist = 1e9f;
            for (int k = 0; k < count; k++)
            {
                var pr = prims[k];
                float dk, fd;
                if (pr.type == TunnelPrim.Tube) dk = TubeD(pr, p, out fd);
                else if (pr.type == TunnelPrim.Chamber) dk = ChamberD(pr, p, out fd);
                else dk = ShaftD(pr, p, out fd);
                if (dk < d) { nearestLevel = pr.level; }
                if (math.abs(fd) < math.abs(floorDist) && dk < 4f) floorDist = fd;
                d = SMin(d, dk, 6f);
            }
            if (d > 6f) return d;
            // wall texture: fluid lava-sculpted bulges + rough blocks; floors stay smoother
            float floorness = math.saturate((floorDist + 1.0f) / 3f);
            float rough = 1.1f * noise.snoise(p * 0.06f) + 0.5f * noise.snoise(p * 0.19f + 11.7f) + 0.18f * noise.snoise(p * 0.6f + 3.1f);
            return d + rough * floorness + 0.12f * noise.snoise(p * 0.45f) * (1 - floorness);
        }

        static float TubeD(in TunnelPrim t, float3 p, out float floorDist)
        {
            float2 ab = t.b.xz - t.a.xz;
            float len = math.length(ab);
            float rawT = math.dot(p.xz - t.a.xz, ab) / math.max(1e-3f, len * len);
            float tt = math.saturate(rawT);
            float2 c = t.a.xz + ab * tt;
            float dh = math.length(p.xz - c);
            // floor along the tube incl. a lava-fall step (level ramps: flat run-outs at both ends)
            float floorY = math.lerp(t.a.y, t.b.y, TunnelNetwork.RampT(t, tt));
            if (t.drop > 0) { float w = t.dropW > 0 ? t.dropW : 0.04f; floorY -= t.drop * (math.smoothstep(t.dropT - w, t.dropT + w, tt) - tt); }
            float s = tt * len;
            float hw = math.lerp(t.hw, t.hwEnd, tt) * (1f + 0.18f * noise.snoise(new float2(s / 55f, t.seed % 997)));
            if (t.deadEnd != 0) hw *= math.smoothstep(1.0f, 0.82f, tt) * 0.85f + 0.15f;
            hw = math.max(hw, 2f);
            float hvScale = t.hv / Tunnel.HeightFor(math.max(math.max(t.hw, t.hwEnd), 2f));   // roof clamp near the surface
            float hv = Tunnel.HeightFor(hw) * hvScale * (1f + 0.15f * noise.snoise(new float2(s / 40f + 50, t.seed % 991)));
            // breakdown pile
            if (t.moundH > 0)
            {
                float md = (tt - t.moundT) * len;
                floorY += t.moundH * math.exp(-(md * md + dh * dh * 0.5f) / (t.moundR * t.moundR));
            }
            floorY += 0.35f * noise.snoise(p.xz * 0.08f) + 0.12f * noise.snoise(p.xz * 0.5f);
            // ramp connectors: the rounded end cap's floor rises (else the flat cap pokes back over the sloping ramp it
            // joins, its lower floor cutting a ~3 m step into the ramp)
            if (t.kind == 3) floorY += 1.2f * math.max(0f, math.max(-rawT, rawT - 1f)) * len;
            float axisY = floorY + 0.55f * hv;      // flat solidified floor, arched vault above
            float dy = (p.y - axisY) * (hw / hv);
            float d = math.sqrt(dh * dh + dy * dy) - hw;
            // flow ledges ("bathtub rings"): small notches where the lava level stood
            float h = p.y - floorY;
            d -= 0.9f * math.exp(-math.pow((h - 0.45f * hv) / 0.7f, 2)) + 0.6f * math.exp(-math.pow((h - 0.95f * hv) / 0.6f, 2));
            floorDist = p.y - floorY;
            d = math.max(d, floorY - p.y);
            // closed dead end
            if (t.deadEnd != 0) d = math.max(d, (math.dot(p.xz - t.a.xz, ab / math.max(1e-3f, len)) - len) );
            return d;
        }

        static float ChamberD(in TunnelPrim c, float3 p, out float floorDist)
        {
            float2 dxz = p.xz - c.a.xz;
            float dh = math.length(dxz);
            float hw = c.hw * (1f + 0.12f * noise.snoise(new float2(math.atan2(dxz.y, dxz.x) * 1.7f, c.seed % 977)));
            float floorY = c.a.y + 0.4f * noise.snoise(p.xz * 0.05f);
            if (c.moundH > 0) floorY += c.moundH * math.exp(-(dh * dh) / (c.moundR * c.moundR));   // talus cone under a collapse
            float axisY = c.a.y + 0.45f * c.hv;
            float dy = (p.y - axisY) * (hw / c.hv);
            float d = math.sqrt(dh * dh + dy * dy) - hw;
            floorDist = p.y - floorY;
            return math.max(d, floorY - p.y);
        }

        static float ShaftD(in TunnelPrim s, float3 p, out float floorDist)
        {
            float r = s.hw * (1f + 0.2f * noise.snoise(new float3(p.x, p.y * 0.3f, p.z) * 0.08f));
            float dh = math.length(p.xz - s.a.xz) - r;
            float top = s.a.y + 2f, bottom = s.b.y + 10f;
            float dv = math.max(p.y - top, bottom - p.y);
            floorDist = 1e9f;
            return math.max(dh, dv);
        }
    }

    /// <summary>Shared size rule (also used by the Burst SDF).</summary>
    public static class Tunnel
    {
        public static float HeightFor(float hw) => hw / math.lerp(1.3f, 1.9f, math.saturate((hw - 8f) / 40f));
    }
}
