using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Mathematics;

namespace MoonBaseAlpha.Cave
{
    /// <summary>
    /// The lava-flow tube layout (<see cref="FlowTubes"/>) placed in the game frame and turned into SDF primitives:
    /// the layout is rotated so its top trunk runs along the rille (the pit axis), the floors follow the regional slope
    /// at their flow's depth, the trunk opens into the pit through both shaft walls (11 m wide mouths, as before), and
    /// collapses become talus ramps (kind 1) or shafts with a rubble cone below. Holds the route graph and the spatial
    /// index <see cref="TunnelNetwork.Collect"/> uses. Built once per world (a few ms).
    /// </summary>
    public class FlowNetwork
    {
        public readonly FlowTubes.Layout L;
        public readonly string Code;
        readonly TunnelNetwork net;
        readonly float rot, cosR, sinR, shaftR, dPit;
        public const float RampDeg = 18f, MouthHalfWidth = 11f, MouthWiden = 130f, UpstreamRise = 8.5f;

        public readonly List<TunnelPrim> Prims = new();
        public struct Shaft { public float3 top; public float radius, drop; public int flow, link; }
        public readonly List<Shaft> Shafts = new();
        /// <summary>Collapse link -> index in <see cref="Shafts"/> (links that became a shaft).</summary>
        public readonly Dictionary<int, int> LinkShaft = new();
        /// <summary>Frame floor polylines: one per way (edges first, then collapse links; see <see cref="FlowTubes.Ways"/>).</summary>
        public readonly List<float3[]> WayPts = new();
        public List<FlowTubes.Way> Ways;
        /// <summary>For each edge: the index range of its tube prims in <see cref="Prims"/>.</summary>
        public readonly List<(int first, int count)> EdgePrims = new();
        public readonly List<float3> NodeFrame = new();
        /// <summary>Talus-ramp first prim (index into Prims) per link; -1 for shafts. The ramp is a trench of several segments.</summary>
        public readonly List<int> LinkRamp = new();
        /// <summary>The talus ramp's floor polyline per link (top -> bottom), null for shafts.</summary>
        public readonly List<float3[]> RampPath = new();
        /// <summary>Dead ends are checked and driven only to this far short of the closed end (it narrows to a point).</summary>
        public const float DeadEndTrim = 15f;

        readonly Dictionary<int2, List<int>> grid = new();
        const float GridCell = 128f;

        public FlowNetwork(TunnelNetwork net, string code, FlowTubes.Settings settings = null)
        {
            this.net = net;
            Code = string.IsNullOrEmpty(code) ? "MOON" : code;
            var s = settings ?? new FlowTubes.Settings { maxFall = 4 };   // the game: every lava fall worn into a drivable rubble slope (<= 4 m)
            L = FlowTubes.Generate(Code, s);
            rot = math.atan2(net.dirU.y, net.dirU.x) - (float)L.downA;
            cosR = math.cos(rot); sinR = math.sin(rot);
            shaftR = math.distance(net.mouthFloor.xz, net.origin);
            dPit = (float)L.depth[0];
            for (int i = 0; i < L.nodes.Count; i++) NodeFrame.Add(ToFrame(L.nodes[i].x, L.nodes[i].y, L.depth[i]));
            BuildPrims();
            Ways = FlowTubes.Ways(L);
            for (int k = 0; k < L.links.Count; k++) { var w = Ways[L.edges.Count + k]; w.drive = L.links[k].drive && LinkRamp[k] >= 0; Ways[L.edges.Count + k] = w; }
            foreach (var w in Ways) WayPts.Add(null);
            for (int k = 0; k < L.edges.Count; k++) WayPts[k] = L.edges[k].deadEnd ? Trim(EdgeFrame(k), DeadEndTrim) : EdgeFrame(k);
            for (int k = 0; k < L.links.Count; k++) WayPts[L.edges.Count + k] = LinkFrame(k);
            SquashUnderCrossings();
            for (int k = 0; k < Prims.Count; k++) Index(k);
            for (int k = 0; k < L.edges.Count; k++)
                if (L.edges[k].tube == 0 && L.edges[k].a == 0 && math.distance(WayPts[k][0], net.mouthFloor) < 0.5f) { FirstDownstreamEdge = k; break; }
        }

        /// <summary>The top trunk's piece leaving the pit downstream (from the mouth).</summary>
        public int FirstDownstreamEdge { get; private set; }

        // ------------------------------------------------------------------ frame

        public float2 PlanToXZ(double x, double y) => net.origin + new float2((float)(cosR * x - sinR * y), (float)(sinR * x + cosR * y));
        public double2 XZToPlan(float2 xz) { var d = xz - net.origin; return new double2(cosR * d.x + sinR * d.y, -sinR * d.x + cosR * d.y); }

        /// <summary>Frame floor point of a plan point at a floor depth (m below the surface).</summary>
        public float3 ToFrame(double x, double y, double depth)
        {
            var xz = PlanToXZ(x, y);
            float yv = net.level0Floor + (dPit - (float)depth) + (float)L.Elev(x, y);
            if (depth <= L.flows[0].maxD + 0.01)
            {
                yv += UpstreamLift(xz);
                // the top trunk leaves the pit straight along the pit axis (as the old mouth tube; the pit floor ramp and the
                // mouth's light seal are built along it), easing into its own course by 150 m from the shaft wall
                var d = xz - net.origin; var perp = new float2(-net.dirU.y, net.dirU.x);
                float along = math.dot(d, net.dirU), lat = math.dot(d, perp);
                if (math.abs(along) < shaftR + 150f && math.abs(lat) < 40f)
                {
                    float k = math.smoothstep(shaftR + 10f, shaftR + 150f, math.abs(along));
                    xz = net.origin + net.dirU * along + perp * (lat * k);
                    // and a smooth floor there (no cut-down wander): the straight grade of the old mouth tube
                    float yLin = net.level0Floor + (float)L.Elev(x, y) + UpstreamLift(xz);
                    yv = math.lerp(yLin, yv, k);
                }
            }
            return new float3(xz.x, yv, xz.y);
        }

        /// <summary>
        /// Upstream of the pit the top trunk's floor rises to the pit floor at the mouth (the pit floor is 8.5 m above the
        /// downstream mouth, where the crane's ramp ends): over 150 m from the shaft wall.
        /// </summary>
        float UpstreamLift(float2 xz)
        {
            var d = xz - net.origin; float r = math.length(d);
            if (r > shaftR + 150f || math.dot(d, net.dirU) >= 0) return 0;
            float k = 1f - math.saturate((r - shaftR) / 150f);
            return UpstreamRise * k * k * (3 - 2 * k);
        }

        /// <summary>Nominal floor height of flow f under a frame xz point (its base depth + half the cut-down).</summary>
        public float FlowFloorY(int f, float2 xz)
        {
            var p = XZToPlan(xz);
            return net.level0Floor + dPit - (float)(L.flows[f].D + 2.5) + (float)L.Elev(p.x, p.y);
        }

        public int FlowAt(float3 p)
        {
            int best = 0; float bd = float.MaxValue;
            for (int f = 0; f < L.flows.Count; f++) { float d = math.abs(FlowFloorY(f, p.xz) - p.y); if (d < bd) { bd = d; best = f; } }
            return best;
        }

        /// <summary>Depth of flow f's tube floors below the pit rim (m).</summary>
        public float FlowDepth(int f) => net.rimY - FlowFloorY(f, net.origin);

        // ------------------------------------------------------------------ prims

        static uint Hash(int a, int b, int c) => math.hash(new int3(a, b, c));
        static float R01(uint h) => (h & 0xFFFFFF) / 16777216f;

        /// <summary>Frame floor points of edge k: the pit mouths replace the points inside the shaft.</summary>
        float3[] EdgeFrame(int k) => EdgeFrame(k, out _);

        /// <summary>map[i] = index in the result of e.pts[i] (-1 if dropped inside the shaft).</summary>
        float3[] EdgeFrame(int k, out int[] map)
        {
            var e = L.edges[k];
            var list = new List<float3>(); map = new int[e.pts.Count];
            bool pitA = e.a == 0 && e.tube == 0, pitB = e.b == 0 && e.tube == 0;
            if (pitA) list.Add(Mouth(e, true));
            for (int i = 0; i < e.pts.Count; i++)
            {
                var p = e.pts[i];
                double r = math.sqrt(p.x * p.x + p.y * p.y);
                if ((pitA || pitB) && r < shaftR + 2) { map[i] = -1; continue; }
                map[i] = list.Count;
                list.Add(ToFrame(p.x, p.y, p.d));
            }
            if (pitB) list.Add(Mouth(e, false));
            return list.ToArray();
        }

        /// <summary>The pit mouth on this edge's side of the shaft (downstream: the old mouth point).</summary>
        float3 Mouth(FlowTubes.Edge e, bool atA)
        {
            var q = e.pts[atA ? math.min(2, e.pts.Count - 1) : math.max(0, e.pts.Count - 3)];
            var d = math.normalizesafe(PlanToXZ(q.x, q.y) - net.origin, net.dirU);
            bool downstream = math.dot(d, net.dirU) > 0;
            if (downstream) return net.mouthFloor;
            var xz = net.origin + d * shaftR;
            return new float3(xz.x, net.level0Floor + UpstreamRise, xz.y);
        }

        void BuildPrims()
        {
            // degree of each node (junctions get a chamber)
            var deg = new int[L.nodes.Count]; var hwMax = new float[L.nodes.Count]; var hMax = new float[L.nodes.Count];
            foreach (var e in L.edges) foreach (int n in new[] { e.a, e.b }) { deg[n]++; hwMax[n] = math.max(hwMax[n], (float)e.w / 2); hMax[n] = math.max(hMax[n], (float)e.h); }
            for (int k = 0; k < L.edges.Count; k++) AddEdge(k);
            for (int n = 1; n < L.nodes.Count; n++)
            {
                if (deg[n] < 3) continue;
                var c = NodeFrame[n];
                // round 12 (S4): the junction is as wide / tall as its widest tube (was x1.1: a bulge with its own shape)
                float hw = hwMax[n], hv = hMax[n] / 1.45f;
                Prims.Add(Clamp(new TunnelPrim { type = TunnelPrim.Chamber, a = c, hw = hw, hv = hv, seed = Hash(n, 1, 77), level = FlowOfNode(n) }));
            }
            for (int k = 0; k < L.links.Count; k++) AddCollapse(k);
        }

        int FlowOfNode(int n) { foreach (var e in L.edges) if (e.a == n || e.b == n) return e.flow; return 0; }

        TunnelPrim Clamp(TunnelPrim p) => net.ClampRoofPublic(p);

        void AddEdge(int k)
        {
            var e = L.edges[k];
            int first = Prims.Count;
            var F = EdgeFrame(k, out var map);
            if (F.Length < 2) { EdgePrims.Add((first, 0)); return; }
            // falls: point index in the tube -> index in F
            var fallAt = new Dictionary<int, double>();
            var tube = L.tubes[e.tube];
            foreach (var (fk, st) in tube.falls)
            {
                if (fk <= e.i0 || fk > e.i1) continue;
                int local = map[fk - e.i0];
                if (local >= 1 && local < F.Length) fallAt[local] = st;
            }
            // segment breaks: every 2 points, except a fall gets its own 3-point segment [k-2, k+1]
            var forbid = new HashSet<int>(); var breaks = new SortedSet<int> { 0, F.Length - 1 };
            foreach (var fk in fallAt.Keys) { breaks.Add(math.max(0, fk - 2)); breaks.Add(math.min(F.Length - 1, fk + 1)); forbid.Add(fk - 1); forbid.Add(fk); }
            var mand = breaks.ToList();
            for (int m = 0; m + 1 < mand.Count; m++)
                for (int i = mand[m] + 2; i < mand[m + 1] - 1; i += 2) if (!forbid.Contains(i)) breaks.Add(i);
            float hwT = (float)e.w / 2, hv = (float)e.h / 1.55f;
            bool pit = (e.a == 0 || e.b == 0) && e.tube == 0;
            // the pit mouths: one straight piece over the first ~130 m (as the old mouth tube), no joints near the pit box face
            int mouthEnd = -1;
            if (pit)
            {
                bool atA = e.a == 0; float acc = 0;
                if (atA) { for (int i = 1; i < F.Length; i++) { acc += math.distance(F[i - 1].xz, F[i].xz); if (acc >= MouthWiden) { mouthEnd = i; break; } } }
                else { for (int i = F.Length - 2; i >= 0; i--) { acc += math.distance(F[i + 1].xz, F[i].xz); if (acc >= MouthWiden) { mouthEnd = i; break; } } }
                if (mouthEnd > 0 && mouthEnd < F.Length - 1)
                {
                    if (atA) breaks.RemoveWhere(i => i > 0 && i < mouthEnd); else breaks.RemoveWhere(i => i > mouthEnd && i < F.Length - 1);
                    breaks.Add(mouthEnd);
                }
            }
            var B = breaks.ToList();
            // a breakdown pile on some pieces (a slab of roof on the floor): one per edge, never on a fall segment
            uint hs = Hash(k, 3, 11);
            int moundSeg = R01(hs) < 0.3f && B.Count > 3 && !((e.a == 0 || e.b == 0) && e.tube == 0) ? 1 + (int)(R01(Hash(k, 4, 12)) * (B.Count - 3)) : -1;
            for (int s = 0; s + 1 < B.Count; s++)
            {
                int i0 = B[s], i1 = B[s + 1];
                var a = F[i0]; var b = F[i1];
                float len = math.distance(a.xz, b.xz);
                float hwA = hwT, hwB = hwT;
                if (pit)
                {
                    // narrow mouths through the shaft wall, widening over the first 130 m (as the old pit tube)
                    float dA = math.distance(a.xz, net.origin) - shaftR, dB = math.distance(b.xz, net.origin) - shaftR;
                    hwA = math.lerp(MouthHalfWidth, hwT, math.saturate(dA / MouthWiden)); hwB = math.lerp(MouthHalfWidth, hwT, math.saturate(dB / MouthWiden));
                }
                float hvS = pit ? math.min(hv, Tunnel.HeightFor(math.max(hwA, hwB))) : hv;   // the mouth's vault as the old mouth tube
                var p = new TunnelPrim { type = TunnelPrim.Tube, a = a, b = b, hw = hwA, hwEnd = hwB, hv = hvS, seed = Hash(k, s, 5), level = e.flow };
                // lava fall inside this segment
                foreach (var (fk, st) in fallAt)
                {
                    if (fk <= i0 || fk > i1) continue;
                    float arc = 0; for (int i = i0 + 1; i < fk; i++) arc += math.distance(F[i - 1].xz, F[i].xz);
                    arc += 0.5f * math.distance(F[fk - 1].xz, F[fk].xz);
                    // the real step at this point (a fall clamped at the flow's cut-down limit is smaller, or none)
                    float expected = math.distance(F[fk - 1].xz, F[fk].xz) * (float)L.slope;
                    p.drop = math.clamp(F[fk - 1].y - F[fk].y - expected, 0f, (float)st);
                    if (p.drop < 1f) { p.drop = 0; continue; }
                    p.dropW = math.clamp(4.5f * p.drop / math.max(len, 1f), 0.04f, 0.45f);
                    p.dropT = math.clamp(arc / math.max(len, 1f), p.dropW + 0.02f, 1f - p.dropW - 0.02f);
                }
                if (p.drop == 0) { p.dropW = 0; p.dropT = 0; }
                if (s == moundSeg && p.drop == 0)
                {
                    p.moundT = 0.5f; p.moundR = 10f + 15f * R01(Hash(k, 5, 13));
                    p.moundH = math.min(2f + 6f * R01(Hash(k, 6, 14)), 0.3f * p.moundR);
                }
                if (e.deadEnd && s + 2 == B.Count) { p.deadEnd = 1; p.hwEnd = p.hw * 0.5f; }
                Prims.Add(Clamp(p));
            }
            EdgePrims.Add((first, Prims.Count - first));
        }

        /// <summary>Floor height of edge k's frame polyline nearest to xz (and the tangent there).</summary>
        static float3 Nearest(float3[] F, float2 xz, out int seg, out float t)
        {
            seg = 0; t = 0; float best = float.MaxValue; float3 res = F[0];
            for (int i = 0; i + 1 < F.Length; i++)
            {
                var ab = F[i + 1].xz - F[i].xz; float tt = math.saturate(math.dot(xz - F[i].xz, ab) / math.max(1e-4f, math.lengthsq(ab)));
                var q = math.lerp(F[i], F[i + 1], tt); float d = math.distance(q.xz, xz);
                if (d < best) { best = d; res = q; seg = i; t = tt; }
            }
            return res;
        }

        void AddCollapse(int k)
        {
            var l = L.links[k]; var c = l.c;
            var top = ToFrame(c.x, c.y, c.dUp); var bot = ToFrame(c.x, c.y, c.dLo);
            int upE = L.edges.IndexOf(c.up), loE = L.edges.IndexOf(c.low);
            var FU = EdgeFrame(upE); var FL = EdgeFrame(loE);
            top = Nearest(FU, top.xz, out _, out _); bot = Nearest(FL, bot.xz, out _, out _);
            top.xz = PlanToXZ(c.x, c.y); bot.xz = top.xz;
            float drop = top.y - bot.y;
            if (l.drive && TryTrench(k, l, c, FU, FL, top, bot)) return;
            // a shaft needs a safe lane beside it (>= 12 m); in a narrow tube leave the roof intact instead, unless this is
            // the lower flow's only connection
            if ((float)c.up.w / 2 < 24f && L.links.Count(o => o != l && o.c.low.flow == c.low.flow) > 0) { LinkRamp.Add(-1); RampPath.Add(null); intact.Add(k); return; }
            // a shaft (winch): through the upper floor into the lower tube, a rubble cone below
            LinkRamp.Add(-1); RampPath.Add(null);
            // off-centre: the hole sits beside the upper tube's axis (a lane past it), slid along the lower tube so it
            // still opens into it; a tube too narrow for a lane is simply blocked there (routes avoid it)
            float hwU = (float)c.up.w / 2;
            float r = math.min(9f + 7f * R01(Hash(k, 10, 22)), math.max(5f, (hwU - 10f) / 2f));
            {
                Nearest(FL, bot.xz, out int ls, out _);
                var ld = math.normalizesafe(FL[math.min(FL.Length - 1, ls + 1)].xz - FL[ls].xz);
                Nearest(FU, top.xz, out int us, out _);
                var ud = math.normalizesafe(FU[math.min(FU.Length - 1, us + 1)].xz - FU[us].xz);
                var across = new float2(-ud.y, ud.x);
                float along = math.dot(ld, across); if (math.abs(along) < 0.2f) along = 0.2f * math.sign(along + 1e-3f);
                float want = hwU - r - 4f;                                   // distance from the upper axis
                var shift = ld * (want / math.abs(along)) * (R01(Hash(k, 14, 26)) < 0.5f ? 1f : -1f);
                if (math.length(shift) > 60f) shift = math.normalizesafe(shift) * 60f;
                top.xz += shift; bot.xz += shift;
                top.y = Nearest(FU, top.xz, out _, out _).y; bot.y = Nearest(FL, bot.xz, out _, out _).y;
                drop = top.y - bot.y;
            }
            Prims.Add(new TunnelPrim { type = TunnelPrim.Shaft, a = top, b = bot, hw = r, seed = Hash(k, 11, 23), level = c.up.flow });
            float hwc = math.max((float)c.low.w / 2, r + 8f);
            Prims.Add(new TunnelPrim { type = TunnelPrim.Chamber, a = bot, hw = hwc, hv = (float)c.low.h / 1.45f, seed = Hash(k, 12, 24), level = c.low.flow,
                                       moundH = 6f + 4f * R01(Hash(k, 13, 25)), moundR = hwc * 0.7f });
            LinkShaft[k] = Shafts.Count;
            Shafts.Add(new Shaft { top = top, radius = r, drop = drop, flow = c.up.flow, link = k });
        }
        /// <summary>
        /// Along the trench (all but its foot, which opens into the lower tube on purpose), no other tube's air just under
        /// its floor: that would be a hole / cliff in the ramp.
        /// </summary>
        bool TrenchClear(List<float3> path, FlowTubes.Cross c)
        {
            var lo = path[0]; var hi = path[0];
            foreach (var q in path) { lo = math.min(lo, q); hi = math.max(hi, q); }
            var near = new List<TunnelPrim>();
            foreach (var p in Prims)
                if (p.type != TunnelPrim.Shaft && TunnelNetwork.OverlapsBox(p, lo - new float3(20, 30, 20), hi + new float3(20, 10, 20), 4f)) near.Add(p);
            if (near.Count == 0) return true;
            var arr = new NativeArray<TunnelPrim>(near.ToArray(), Allocator.Temp);
            bool ok = true; var foot = path[path.Count - 1];
            float total = 0; for (int i = 1; i < path.Count; i++) total += math.distance(path[i - 1].xz, path[i].xz);
            float run = 0;
            for (int i = 1; i < path.Count && ok; i++)
            {
                float seg = math.distance(path[i - 1].xz, path[i].xz);
                for (float d = 0; d < seg && ok; d += 4f)
                {
                    var q = math.lerp(path[i - 1], path[i], d / seg);
                    if (math.distance(q.xz, foot.xz) < 35f) continue;          // the foot opens into the lower tube
                    if (TunnelSdf.Eval(arr, arr.Length, q - new float3(0, 2.5f, 0), out _, out _) < 0f) ok = false;
                }
                run += seg;
            }
            arr.Dispose();
            return ok;
        }

        /// <summary>
        /// Talus ramp: the roof between collapsed and the rubble slid into a slope. Built as a trench that follows the upper
        /// tube's axis from S down to the lower tube's footprint at <see cref="RampDeg"/> (in its own short segments, so it
        /// stays inside the curving upper tube), on whichever side of the crossing has room within the tube piece; the
        /// link's upper junction moves to that side. No room on either side: false (it becomes a shaft).
        /// </summary>
        bool TryTrench(int k, FlowTubes.Link l, FlowTubes.Cross c, float3[] FU, float3[] FL, float3 top, float3 bot)
        {
            Nearest(FU, top.xz, out int seg, out float st);
            Nearest(FL, bot.xz, out int ls, out _);
            var loDir = math.normalizesafe(FL[math.min(FL.Length - 1, ls + 1)].xz - FL[ls].xz);
            float hwLo = (float)c.low.w / 2, hwUp = (float)c.up.w / 2, tanR = math.tan(math.radians(RampDeg));
            foreach (bool towardsA in l.upFromA ? new[] { true, false } : new[] { false, true })
            {
                // the upper axis from the crossing back to that end
                var back = new List<float3> { math.lerp(FU[seg], FU[seg + 1], st) };
                if (towardsA) for (int i = seg; i >= 0; i--) back.Add(FU[i]); else for (int i = seg + 1; i < FU.Length; i++) back.Add(FU[i]);
                var dir = math.normalizesafe(back[0].xz - back[math.min(1, back.Count - 1)].xz);
                float sinT = math.abs(dir.x * loDir.y - dir.y * loDir.x);
                float inset = math.min(hwLo * 0.85f / math.max(sinT, 0.3f), 60f);
                var exz = top.xz - dir * inset;
                float ey = Nearest(FL, exz, out _, out _).y;
                // walk back until the arc can carry the drop at RampDeg (20 m to spare before the junction)
                float arc = 0; var path = new List<float3> { new float3(exz.x, ey, exz.y) };
                bool ok = false;
                for (int i = 0; i + 1 < back.Count; i++)
                {
                    arc += math.distance(back[i].xz, back[i + 1].xz);
                    if (arc < inset + 10f) continue;                  // behind the foot: skip (no hairpin)
                    path.Add(back[i + 1]);
                    if ((arc - inset) * tanR >= back[i + 1].y - ey) { ok = i + 2 < back.Count || arc > 30f; break; }
                }
                if (ok && path.Count < 2) ok = false;
                if (!ok) continue;
                path.Reverse();                                        // S (top) -> E (bottom)
                float total = 0; for (int i = 1; i < path.Count; i++) total += math.distance(path[i - 1].xz, path[i].xz);
                float yS = path[0].y, run = 0;
                for (int i = 1; i < path.Count; i++) { run += math.distance(path[i - 1].xz, path[i].xz); var q = path[i]; q.y = math.lerp(yS, ey, run / total); path[i] = q; }
                // beside the upper axis (the through lane stays intact): needs a tube >= ~2x the trench wide
                float hw = math.clamp((hwUp - 8f) / 2f, 5f, 8f);
                float o = hwUp - hw - 3f;
                if (o < hw + 4f) return false;
                float side = R01(Hash(k, 15, 27)) < 0.5f ? 1f : -1f;
                for (int i = 0; i < path.Count; i++)
                {
                    var a0 = path[math.max(0, i - 1)]; var a1 = path[math.min(path.Count - 1, i + 1)];
                    var d = math.normalizesafe(a1.xz - a0.xz); var q = path[i];
                    q.xz += new float2(-d.y, d.x) * (side * o);                 // the same offset to the foot (no hairpin)
                    q.y = i == path.Count - 1 ? Nearest(FL, q.xz, out _, out _).y : q.y;
                    path[i] = q;
                }
                if (!TrenchClear(path, c)) continue;
                LinkRamp.Add(Prims.Count);
                for (int i = 1; i < path.Count; i++)
                    Prims.Add(new TunnelPrim { type = TunnelPrim.Tube, a = path[i - 1], b = path[i], hw = hw, hwEnd = hw, hv = Tunnel.HeightFor(hw), seed = Hash(k, 9 + i, 21), level = c.up.flow, kind = 1 });
                RampPath.Add(path.ToArray());
                if (towardsA != l.upFromA) { l.upFromA = towardsA; l.a = towardsA ? c.up.a : c.up.b; }
                return true;
            }
            return false;
        }

        /// <summary>Frame polyline of collapse link k: junction -> upper floor to the ramp top -> ramp -> lower floor -> junction.</summary>
        float3[] LinkFrame(int k)
        {
            var l = L.links[k]; var c = l.c;
            var FU = EdgeFrame(L.edges.IndexOf(c.up)); var FL = EdgeFrame(L.edges.IndexOf(c.low));
            var res = new List<float3>();
            if (LinkRamp[k] >= 0)
            {
                var rp = RampPath[k]; var S = rp[0]; var E = rp[rp.Length - 1];
                Nearest(FU, S.xz, out int su, out _);
                if (l.upFromA) { for (int i = 0; i < su; i++) res.Add(FU[i]); }
                else { for (int i = FU.Length - 1; i > su + 1; i--) res.Add(FU[i]); }
                res.AddRange(rp);
                Nearest(FL, E.xz, out int el, out _);
                if (l.loFromA) { for (int i = el; i >= 0; i--) res.Add(FL[i]); }
                else { for (int i = el + 1; i < FL.Length; i++) res.Add(FL[i]); }
            }
            else
            {
                foreach (var p in l.pts) res.Add(ToFrame(p.x, p.y, p.d));
            }
            // the lower piece is a dead end and the link runs to its tip: stop short of the closed end
            if (c.low.deadEnd && !l.loFromA) return Trim(res.ToArray(), DeadEndTrim);
            return res.ToArray();
        }

        /// <summary>The polyline without its last <paramref name="cut"/> metres.</summary>
        static float3[] Trim(float3[] F, float cut)
        {
            var res = new List<float3>(F); float left = cut;
            while (res.Count > 2 && left > 0)
            {
                float d = math.distance(res[res.Count - 1].xz, res[res.Count - 2].xz);
                if (d <= left) { res.RemoveAt(res.Count - 1); left -= d; }
                else { res[res.Count - 1] = math.lerp(res[res.Count - 1], res[res.Count - 2], left / d); left = 0; }
            }
            return res.ToArray();
        }

        /// <summary>
        /// Where a tube passes over a lower one (no collapse), keep the lower vault clear of the upper floor in the SDF too:
        /// its wall noise (+-15% height, rough blocks) and the smooth union add a few metres the generator's cover doesn't
        /// see, so the lower tube's prims within 70 m of the crossing get a lower vault (>= 8 m rock left).
        /// </summary>
        void SquashUnderCrossings()
        {
            foreach (var c in L.X)
            {
                if (c.collapse) continue;
                var up = ToFrame(c.x, c.y, c.dUp); var xz = up.xz;
                var (first, count) = EdgePrims[L.edges.IndexOf(c.low)];
                for (int k = first; k < first + count; k++)
                {
                    var p = Prims[k];
                    float t = math.saturate(math.dot(xz - p.a.xz, p.b.xz - p.a.xz) / math.max(1e-3f, math.lengthsq(p.b.xz - p.a.xz)));
                    if (math.distance(math.lerp(p.a.xz, p.b.xz, t), xz) > 70f) continue;
                    float floorLo = math.max(p.a.y, p.b.y);
                    float room = up.y - 8f - 4f - floorLo;            // 8 m rock + ~4 m of wall noise
                    float maxHv = room / (1.55f * 1.15f);
                    if (p.hv > maxHv) { p.hv = math.max(4f, maxHv); Prims[k] = p; }
                }
            }
        }

        // ------------------------------------------------------------------ spatial index

        static void Bounds(in TunnelPrim p, out float2 lo, out float2 hi)
        {
            float r = math.max(math.max(p.hw, p.hwEnd), p.moundR) + 8f;
            if (p.type == TunnelPrim.Tube) { lo = math.min(p.a.xz, p.b.xz) - r; hi = math.max(p.a.xz, p.b.xz) + r; }
            else { lo = p.a.xz - r; hi = p.a.xz + r; }
        }

        void Index(int k)
        {
            Bounds(Prims[k], out var lo, out var hi);
            int2 c0 = (int2)math.floor(lo / GridCell), c1 = (int2)math.floor(hi / GridCell);
            for (int x = c0.x; x <= c1.x; x++) for (int y = c0.y; y <= c1.y; y++)
            {
                var key = new int2(x, y);
                if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>(8);
                l.Add(k);
            }
        }

        readonly HashSet<int> seen = new();
        public void Collect(float3 min, float3 max, float margin, List<TunnelPrim> result)
        {
            result.Clear();
            lock (seen)
            {
                seen.Clear();
                int2 c0 = (int2)math.floor((min.xz - margin) / GridCell), c1 = (int2)math.floor((max.xz + margin) / GridCell);
                for (int x = c0.x; x <= c1.x; x++) for (int y = c0.y; y <= c1.y; y++)
                {
                    if (!grid.TryGetValue(new int2(x, y), out var l)) continue;
                    foreach (int k in l)
                        if (seen.Add(k) && TunnelNetwork.OverlapsBox(Prims[k], min, max, margin)) result.Add(Prims[k]);
                }
            }
        }

        public bool CollapseNear(float3 p, float range, out float3 top, out float radius, out float drop)
        {
            top = default; radius = 0; drop = 0; float best = range; bool found = false;
            foreach (var s in Shafts)
            {
                if (math.abs(s.top.y - p.y) > 8f) continue;
                float edge = math.distance(s.top.xz, p.xz) - s.radius;
                if (edge < best) { best = edge; top = s.top; radius = s.radius; drop = s.drop; found = true; }
            }
            return found;
        }

        // ------------------------------------------------------------------ the mission route

        /// <summary>Driving-distance range of GIMLI-2 from the pit per tier (m): Cadet, Explorer (the tester's rule), Commander.</summary>
        /// <remarks>Round 12 (S8): Cadet 0.6-0.8 km (~5 min of hand driving; was 0.6-1.5 km). Explorer / Commander unchanged.</remarks>
        public static (double lo, double hi) TierRange(int targetLevel) =>
            targetLevel <= 2 ? (600, 800) : targetLevel <= 4 ? (1500, 3500) : (3000, 7000);

        /// <summary>
        /// GIMLI-2's route for a tier: the deepest real dead end in the tier's driving range, every way of it checked on
        /// the real floor (<see cref="TunnelNetwork.PathDrivable"/>); a failing way is blocked and the search repeats, a
        /// dead end that can't take the seal is skipped.
        /// </summary>
        public GoldenRoute Route(int targetLevel)
        {
            // round 13 (T4): Explorer goes down 1 shaft on the way to GIMLI-2, Commander 2 (fewer if the layout can't)
            int want = ShaftsFor(targetLevel);
            for (int S = want; S >= 1; S--)
            {
                var rs = RouteWithShafts(targetLevel, S);
                if (rs != null) { rs.ShaftsWanted = want; return rs; }
            }
            var r0 = RouteDriving(targetLevel);
            r0.ShaftsWanted = want;
            return r0;
        }

        /// <summary>Shafts down on GIMLI-2's route per tier (round 13, T4): Cadet 0, Explorer 1, Commander 2.</summary>
        public static int ShaftsFor(int targetLevel) => targetLevel <= 2 ? 0 : targetLevel <= 4 ? 1 : 2;
        /// <summary>Route cost of a shaft descent on top of its length (the rig ride takes ~2-3 min).</summary>
        public const double ShaftCost = 60;

        /// <summary>
        /// Distances from the pit over (node, shafts used so far): tubes / talus ramps both ways within a layer, a shaft
        /// only downwards (upper junction -> lower junction) into the next layer.
        /// </summary>
        (double[,] dist, int[,] prevN, int[,] prevL, int[,] prevW) Layered(int S, HashSet<int> blocked)
        {
            int N = L.nodes.Count;
            var adj = new List<(int to, double len, int k, int dl)>[N]; for (int i = 0; i < N; i++) adj[i] = new();
            for (int k = 0; k < Ways.Count; k++)
            {
                if (blocked.Contains(k)) continue;
                var w = Ways[k];
                if (w.drive) { adj[w.a].Add((w.b, w.len, k, 0)); adj[w.b].Add((w.a, w.len, k, 0)); }
                else if (w.link >= 0 && LinkShaft.ContainsKey(w.link)) adj[w.a].Add((w.b, w.len + ShaftCost, k, 1));
            }
            var dist = new double[N, S + 1]; var pn = new int[N, S + 1]; var pl = new int[N, S + 1]; var pw = new int[N, S + 1];
            var done = new bool[N, S + 1];
            for (int i = 0; i < N; i++) for (int l = 0; l <= S; l++) { dist[i, l] = double.PositiveInfinity; pn[i, l] = -1; pw[i, l] = -1; }
            dist[0, 0] = 0;
            for (;;)
            {
                int u = -1, ul = 0; double b = double.PositiveInfinity;
                for (int i = 0; i < N; i++) for (int l = 0; l <= S; l++) if (!done[i, l] && dist[i, l] < b) { b = dist[i, l]; u = i; ul = l; }
                if (u < 0) break;
                done[u, ul] = true;
                foreach (var (v, len, k, dl) in adj[u])
                {
                    int vl = ul + dl; if (vl > S) continue;
                    if (dist[u, ul] + len < dist[v, vl]) { dist[v, vl] = dist[u, ul] + len; pn[v, vl] = u; pl[v, vl] = ul; pw[v, vl] = k; }
                }
            }
            return (dist, pn, pl, pw);
        }

        /// <summary>
        /// GIMLI-2's route down exactly <paramref name="S"/> shafts: the deepest dead end in the tier's range that the
        /// shafts really lead to (shorter with all S shafts than with fewer, so the descent is the way there), else the
        /// one nearest the middle of the range. Tube ways are checked on the real floor (a shaft way: its upper and lower
        /// parts). Null if no such route.
        /// </summary>
        /// <summary>Why a shaft route was not found (tests / the verification report).</summary>
        public string ShaftRouteNote = "";

        GoldenRoute RouteWithShafts(int targetLevel, int S)
        {
            var (lo, hi) = TierRange(targetLevel);
            int N = L.nodes.Count;
            var deg = new int[N]; foreach (var e in L.edges) { deg[e.a]++; deg[e.b]++; }
            bool EdgeOfMap(int i) => System.Math.Max(System.Math.Abs(L.nodes[i].x), System.Math.Abs(L.nodes[i].y)) > L.R;
            var blocked = new HashSet<int>(); var skip = new HashSet<int>(); var verified = new Dictionary<int, bool>();
            for (int attempt = 0; attempt < 60; attempt++)
            {
                var (dist, pn, pl, pw) = Layered(S, blocked);
                int goal = -1; double best = double.NegativeInfinity, mid = (lo + hi) / 2;
                // strict first: goals only the shafts lead to (or where they're the short way); then any goal reached
                // down S shafts (the tube below can often be driven to as well: GIMLI-2 took the shaft)
                bool strict = true;
                bool Needs(int i)
                {
                    double d = dist[i, S]; if (double.IsInfinity(d)) return false;
                    if (!strict) return true;
                    for (int l = 0; l < S; l++) if (dist[i, l] <= d + 1) return false;
                    return true;
                }
                for (int pass = 0; pass < 2 && goal < 0; pass++)
                {
                    strict = pass == 0;
                    for (int i = 1; i < N; i++)
                        if (deg[i] == 1 && !EdgeOfMap(i) && !skip.Contains(i) && Needs(i) && dist[i, S] >= lo && dist[i, S] <= hi && L.depth[i] > best) { best = L.depth[i]; goal = i; }
                    if (goal < 0)
                        for (int i = 1; i < N; i++)
                            if (deg[i] == 1 && !EdgeOfMap(i) && !skip.Contains(i) && Needs(i) && (goal < 0 || System.Math.Abs(dist[i, S] - mid) < System.Math.Abs(dist[goal, S] - mid))) goal = i;
                }
                strict = false;   // the cave-in fallback below: any node down the shafts
                if (goal < 0)
                {
                    // no dead end down there: GIMLI-2 stopped at a cave-in part-way along a tube past the shafts (as Cadet)
                    var cut = ShaftCaveIn(targetLevel, S, dist, pn, pl, pw, blocked, verified, lo, hi, Needs);
                    if (cut != null) { cut.attempts = attempt + 1; cut.verified = true; return cut; }
                    ShaftRouteNote += $" S={S}: no dead end or tube for a cave-in past the shafts (attempt {attempt + 1}, {blocked.Count} ways blocked);";
                    return null;
                }
                var path = new List<(int way, bool fwd)>();
                {
                    int n = goal, l = S;
                    while (pn[n, l] >= 0) { int w = pw[n, l]; path.Insert(0, (w, Ways[w].b == n)); int nn = pn[n, l]; l = pl[n, l]; n = nn; }
                }
                bool ok = true;
                foreach (var (w, _) in path)
                {
                    if (!verified.TryGetValue(w, out bool good))
                    {
                        if (Ways[w].drive)
                        {
                            good = net.PathDrivable(WayPts[w], out float wd, out float3 wa);
                            if (!good) ShaftRouteNote += $" way {w} ({(Ways[w].edge >= 0 ? $"tube, flow {L.edges[Ways[w].edge].flow + 1}{(L.edges[Ways[w].edge].deadEnd ? ", dead end" : "")}" : "talus ramp")}, {Ways[w].len:0} m) not drivable: {(wd >= 89 ? "no floor" : $"{wd:0} deg")} at ({wa.x:0},{wa.y:0},{wa.z:0});";
                        }
                        else
                        {
                            // the rig ride covers the hole's edge and the rubble cone below: check the floor clear of both
                            var (up, down) = SplitShaftWay(w);
                            var sh = Shafts[LinkShaft[Ways[w].link]];
                            float clear = sh.radius + 14f;
                            up = up.Where(q => math.distance(q.xz, sh.top.xz) > clear).ToArray();
                            down = down.Where(q => math.distance(q.xz, sh.top.xz) > clear + 10f).ToArray();
                            bool gu = up.Length < 2 || net.PathDrivable(up, out _, out _), gd = down.Length < 2 || net.PathDrivable(down, out _, out _);
                            good = gu && gd;
                            if (!good) ShaftRouteNote += $" shaft {LinkShaft[Ways[w].link]}: {(gu ? "" : "upper")}{(gd ? "" : " lower")} part not drivable;";
                        }
                        verified[w] = good;
                    }
                    if (!good) { blocked.Add(w); ok = false; }
                }
                if (!ok) continue;
                var r = Assemble(goal, path, targetLevel);
                if (r == null || r.shaftAt.Count != S) { ShaftRouteNote += $" S={S}: goal {goal} {(r == null ? "can't take the seal" : $"route has {r.shaftAt.Count} drops")};"; skip.Add(goal); continue; }
                r.attempts = attempt + 1; r.verified = true;
                return r;
            }
            return null;
        }

        /// <summary>
        /// A route down S shafts that ends at a cave-in part-way along a drivable tube leaving a node only the shafts lead
        /// to (the far side caved in too: a sealed hollow, like Cadet's through-tube cut). Longest fitting route wins.
        /// </summary>
        GoldenRoute ShaftCaveIn(int targetLevel, int S, double[,] dist, int[,] pn, int[,] pl, int[,] pw, HashSet<int> blocked,
                                Dictionary<int, bool> verified, double lo, double hi, System.Func<int, bool> needs)
        {
            GoldenRoute best = null; double bestScore = double.NegativeInfinity, mid = (lo + hi) / 2;
            int N = L.nodes.Count;
            for (int n = 1; n < N; n++)
            {
                if (!needs(n)) continue;
                var path = new List<(int way, bool fwd)>();
                { int m = n, l = S; while (pn[m, l] >= 0) { int w = pw[m, l]; path.Insert(0, (w, Ways[w].b == m)); int mm = pn[m, l]; l = pl[m, l]; m = mm; } }
                if (path.Count == 0 || Ways[path[path.Count - 1].way].drive == false && ShaftJump(WayPts[path[path.Count - 1].way]) < 0) continue;
                for (int k = 0; k < Ways.Count; k++)
                {
                    var w = Ways[k];
                    if (!w.drive || w.edge < 0 || blocked.Contains(k) || (w.a != n && w.b != n) || w.len < 200) continue;
                    if (path.Any(p => p.way == k)) continue;
                    if (!verified.TryGetValue(k, out bool good)) verified[k] = good = net.PathDrivable(WayPts[k], out _, out _);
                    if (!good) continue;
                    var full = path.ToList(); full.Add((k, w.a == n));
                    var open = AssembleOpen(full, targetLevel);
                    if (open == null || open.shaftAt.Count != S) continue;
                    float start = open.Length - (float)WayLen(k);
                    foreach (float off in new[] { 90f, 70f, 120f, 60f })
                    {
                        var c = CutShort(open, full, start + off, targetLevel, through: true);
                        if (c == null || c.shaftAt.Count != S) continue;
                        double score = -System.Math.Abs(c.Length - mid);
                        if (score > bestScore) { bestScore = score; best = c; }
                        break;
                    }
                }
            }
            return best;
        }

        double WayLen(int k) { var P = WayPts[k]; double l = 0; for (int i = 1; i < P.Length; i++) l += math.distance(P[i - 1], P[i]); return l; }

        /// <summary>The polyline of a path (no seal, no stop point): for cutting a cave-in into its last tube.</summary>
        GoldenRoute AssembleOpen(List<(int way, bool fwd)> path, int targetLevel)
        {
            var r = new GoldenRoute { targetLevel = targetLevel };
            foreach (var (w, fwd) in path)
            {
                var pts = WayPts[w];
                var seq = fwd ? pts : pts.Reverse().ToArray();
                int i0 = r.points.Count == 0 ? 0 : 1, base0 = r.points.Count;
                if (!Ways[w].drive && Ways[w].link >= 0 && LinkShaft.TryGetValue(Ways[w].link, out int si) && fwd)
                {
                    int j = ShaftJump(seq);
                    if (j >= i0) { r.shaftAt.Add(base0 + j - i0); r.shaftIds.Add(si); }
                }
                for (int i = i0; i < seq.Length; i++) { r.points.Add(seq[i]); r.pointLevels.Add(FlowAt(seq[i])); r.pointIsJunction.Add(i == 0 || i == seq.Length - 1); }
                r.nodes.Add(new int3(Ways[w].edge >= 0 ? L.edges[Ways[w].edge].flow : 0, 0, fwd ? Ways[w].b : Ways[w].a));
            }
            r.nodes.Insert(0, new int3(0, 0, 0));
            r.Recompute();
            return r;
        }

        /// <summary>A shaft link's floor polyline split at the drop: (upper tube part to the hole, lower part from the foot).</summary>
        public (float3[] up, float3[] down) SplitShaftWay(int w)
        {
            var P = WayPts[w]; int j = ShaftJump(P);
            if (j < 0) return (P, new float3[0]);
            return (P.Take(j + 1).ToArray(), P.Skip(j + 1).ToArray());
        }

        /// <summary>Index of the top of the vertical drop in a shaft way's polyline (-1 if none).</summary>
        static int ShaftJump(float3[] P)
        {
            for (int j = 0; j + 1 < P.Length; j++)
                if (math.distance(P[j].xz, P[j + 1].xz) < 0.5f && P[j].y - P[j + 1].y > 3f) return j;
            return -1;
        }

        GoldenRoute RouteDriving(int targetLevel)
        {
            var (lo, hi) = TierRange(targetLevel);
            var blocked = new HashSet<int>(); var skip = new HashSet<int>(); var verified = new Dictionary<int, bool>();
            GoldenRoute best = null;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                var (dist, prev, prevW) = FlowTubes.Distances(L, Ways, blocked);
                int goal = FlowTubes.Goal(L, dist, lo, hi, skip);
                if (goal == 0) break;
                var path = FlowTubes.PathTo(goal, prev, prevW, Ways);
                bool ok = true;
                foreach (var (w, _) in path)
                {
                    if (!verified.TryGetValue(w, out bool good)) verified[w] = good = net.PathDrivable(WayPts[w], out _, out _);
                    if (!good) { blocked.Add(w); ok = false; }
                }
                if (!ok) continue;
                var r = Assemble(goal, path, targetLevel);
                if (r == null) { skip.Add(goal); continue; }
                // Cadet: no real dead end in range (the nearest one is much further): GIMLI-2 stopped at a cave-in in that
                // dead-end tube instead, at the middle of the range (the sealed part of the tube lies beyond it)
                if (targetLevel <= 2 && r.Length > hi + 100)
                {
                    var cut = CadetCut(r, path, lo, hi, blocked, verified, targetLevel);
                    if (cut != null) { cut.attempts = attempt + 1; cut.verified = true; return cut; }
                }
                r.attempts = attempt + 1;
                r.verified = true;
                return r;
            }
            return best ?? new GoldenRoute { targetLevel = targetLevel };
        }

        GoldenRoute Assemble(int goal, List<(int way, bool fwd)> path, int targetLevel)
        {
            if (path.Count == 0) return null;
            // the goal's dead-end piece (the last prim of that edge)
            var lastWay = Ways[path[path.Count - 1].way];
            if (lastWay.edge < 0) return null;
            var (first, count) = EdgePrims[lastWay.edge];
            if (count == 0) return null;
            var spur = Prims[first + count - 1];
            if (spur.deadEnd == 0 || !SealClear(spur)) return null;
            var r = new GoldenRoute { targetLevel = targetLevel, spur = spur };
            float len = math.distance(spur.a.xz, spur.b.xz);
            r.sealT = 0.8f;
            var dir = math.normalize(spur.b.xz - spur.a.xz); r.sealDir = dir;
            r.seal = math.lerp(spur.a, spur.b, r.sealT); r.seal.y = TunnelNetwork.FloorAt(spur, r.sealT);
            float scoutT = r.sealT - 7f / math.max(len, 8f);
            r.scout = math.lerp(spur.a, spur.b, scoutT); r.scout.y = TunnelNetwork.FloorAt(spur, scoutT);
            r.sealHalfWidth = math.lerp(spur.hw, spur.hwEnd, r.sealT);
            // the floor polyline: every way in driving order, junctions marked
            foreach (var (w, fwd) in path)
            {
                var pts = WayPts[w];
                var seq = fwd ? pts : pts.Reverse().ToArray();
                int i0 = r.points.Count == 0 ? 0 : 1, base0 = r.points.Count;
                if (!Ways[w].drive && Ways[w].link >= 0 && LinkShaft.TryGetValue(Ways[w].link, out int si) && fwd)
                {
                    int j = ShaftJump(seq);
                    if (j >= i0) { r.shaftAt.Add(base0 + j - i0); r.shaftIds.Add(si); }
                }
                for (int i = i0; i < seq.Length; i++)
                {
                    r.points.Add(seq[i]); r.pointLevels.Add(FlowAt(seq[i])); r.pointIsJunction.Add(i == 0 || i == seq.Length - 1);
                }
                r.nodes.Add(new int3(L.edges.Count > 0 ? (Ways[w].edge >= 0 ? L.edges[Ways[w].edge].flow : 0) : 0, 0, fwd ? Ways[w].b : Ways[w].a));
            }
            r.nodes.Insert(0, new int3(0, 0, 0));
            // stop short of the seal: drop points beyond the stop point, end at it
            var stop = r.scout - new float3(dir.x, 0, dir.y) * 8f;
            stop.y = TunnelNetwork.FloorAt(spur, math.max(0, scoutT - 8f / math.max(len, 8f)));
            while (r.points.Count > 2 && math.dot(r.points[r.points.Count - 1].xz - stop.xz, dir) > -1f)
            { r.points.RemoveAt(r.points.Count - 1); r.pointLevels.RemoveAt(r.pointLevels.Count - 1); r.pointIsJunction.RemoveAt(r.pointIsJunction.Count - 1); }
            r.points.Add(stop); r.pointLevels.Add(spur.level); r.pointIsJunction.Add(false);
            for (int k = 1; k < r.points.Count; k++) r.Length += math.distance(r.points[k - 1], r.points[k]);
            return r;
        }

        /// <summary>
        /// A route cut short at <paramref name="target"/> m inside its last tube piece: a breakdown cave-in seals the tube
        /// there (S8, Cadet). Needs >= 60 m of that piece before the cut and no other tube within reach around the seal.
        /// Null if it doesn't fit.
        /// </summary>
        GoldenRoute CadetCut(GoldenRoute first, List<(int way, bool fwd)> firstPath, double lo, double hi, HashSet<int> blocked, Dictionary<int, bool> verified, int targetLevel)
        {
            float mid = (float)((lo + hi) / 2);
            var targets = new[] { mid, mid - 50f, mid + 50f, (float)lo, (float)hi };
            foreach (var t in targets) { var c = CutShort(first, firstPath, t, targetLevel); if (c != null) return c; }
            // other dead-end tubes further than the range, nearest first: a cave-in inside one of them
            var (dist, prev, prevW) = FlowTubes.Distances(L, Ways, blocked);
            var deg = new int[L.nodes.Count];
            foreach (var e in L.edges) { deg[e.a]++; deg[e.b]++; }
            var cands = Enumerable.Range(1, L.nodes.Count - 1).Where(i => deg[i] == 1 && !double.IsInfinity(dist[i]) && dist[i] > hi).OrderBy(i => dist[i]).Take(16);
            foreach (int n in cands)
            {
                var path = FlowTubes.PathTo(n, prev, prevW, Ways);
                bool ok = path.Count > 0;
                foreach (var (w, _) in path)
                {
                    if (!verified.TryGetValue(w, out bool good)) verified[w] = good = net.PathDrivable(WayPts[w], out _, out _);
                    if (!good) { ok = false; break; }
                }
                if (!ok) continue;
                var r = Assemble(n, path, targetLevel);
                if (r == null) continue;
                foreach (var t in targets) { var c = CutShort(r, path, t, targetLevel); if (c != null) return c; }
            }
            // no dead-end tube spans the range: a cave-in across a through tube on the first route, sealed at both ends
            foreach (var t in targets) { var c = CutShort(first, firstPath, t, targetLevel, through: true); if (c != null) return c; }
            return null;
        }

        GoldenRoute CutShort(GoldenRoute full, List<(int way, bool fwd)> path, float target, int targetLevel, bool through = false)
        {
            // the way the cut falls in: the last (dead-end) way, or for a through cut whichever tube way holds the target
            int wi = path.Count - 1; float start = 0, wayLen = 0;
            {
                float acc0 = 0;
                for (int i = 0; i < path.Count; i++)
                {
                    var pts = WayPts[path[i].way]; float l = 0; for (int k = 1; k < pts.Length; k++) l += math.distance(pts[k - 1], pts[k]);
                    if (through ? acc0 + l > target : i == path.Count - 1) { wi = i; start = acc0; wayLen = l; break; }
                    acc0 += l;
                }
            }
            var lastWay = Ways[path[wi].way];
            if (lastWay.edge < 0) return null;
            if (target < start + (through ? 40f : 60f) || target > full.Length - 40f) return null;
            // a through cut needs room for the hollow part and the far-side cave-in before the next junction
            const float hollow = 60f;   // the hollow part between the two piles holds the sealed chamber
            if (through && target + hollow + 30f > start + wayLen) return null;
            var P = full.points;
            float3 Along(float d)
            {
                float a2 = 0;
                for (int k = 1; k < P.Count; k++) { float l = math.distance(P[k - 1], P[k]); if (a2 + l >= d) return math.lerp(P[k - 1], P[k], (d - a2) / math.max(l, 1e-3f)); a2 += l; }
                return P[P.Count - 1];
            }
            var seal = Along(target);
            var dir = math.normalizesafe(Along(target + 12f).xz - Along(target - 12f).xz);
            if (math.lengthsq(dir) < 0.5f) return null;
            // the tube piece at the seal (for its size)
            var (first, count) = EdgePrims[lastWay.edge];
            TunnelPrim at = default; float bd = float.MaxValue, atT = 0;
            for (int k = first; k < first + count; k++)
            {
                var pr = Prims[k]; var ab = pr.b.xz - pr.a.xz; float t = math.saturate(math.dot(seal.xz - pr.a.xz, ab) / math.max(1e-3f, math.lengthsq(ab)));
                float d = math.distance(pr.a.xz + ab * t, seal.xz);
                if (d < bd) { bd = d; at = pr; atT = t; }
            }
            if (bd > 15f) return null;
            float fy = net.FloorY(seal + new float3(0, 3, 0), 4f, 8f);
            if (!float.IsNaN(fy)) seal.y = fy;
            float hw = math.lerp(at.hw, at.hwEnd, atT);
            // nothing from another tube near the seal (the sealed part must be closed)
            var buf = new List<TunnelPrim>();
            Collect(seal - 60f, seal + 60f, 10f, buf);
            buf.RemoveAll(q => { for (int k = first; k < first + count; k++) if (q.type == Prims[k].type && math.all(q.a == Prims[k].a) && math.all(q.b == Prims[k].b)) return true; return false; });
            if (buf.Count > 0)
            {
                var arr = new NativeArray<TunnelPrim>(buf.ToArray(), Allocator.Temp); bool ok = true;
                foreach (float s in new[] { -1f, 0f, 1f }) foreach (float f in new[] { -10f, 0f, 10f, 20f })
                {
                    var q = new float3(seal.x - dir.y * (hw + 3f) * s + dir.x * f, seal.y + 2.5f, seal.z + dir.x * (hw + 3f) * s + dir.y * f);
                    if (TunnelSdf.Eval(arr, arr.Length, q, out _, out _) < 4f) ok = false;
                }
                arr.Dispose();
                if (!ok) return null;
            }
            var dir3 = new float3(dir.x, 0, dir.y);
            var r = new GoldenRoute { targetLevel = targetLevel };
            r.spur = new TunnelPrim { type = TunnelPrim.Tube, a = seal - dir3 * 30f, b = seal + dir3 * 1f, hw = hw, hwEnd = hw, hv = at.hv, level = at.level, seed = at.seed };
            r.sealT = 30f / 31f; r.seal = seal; r.sealDir = dir;
            r.scout = Along(target - 7f); { float sy = net.FloorY(r.scout + new float3(0, 3, 0), 4f, 8f); if (!float.IsNaN(sy)) r.scout.y = sy; }
            r.sealHalfWidth = hw * 1.2f;   // the cave-in fills the whole section (wall noise bulges up to +18%)
            r.nodes.AddRange(full.nodes);
            float stopAt = target - 15f, acc = 0;
            r.points.Add(P[0]); r.pointLevels.Add(full.pointLevels[0]); r.pointIsJunction.Add(full.pointIsJunction[0]);
            for (int k = 1; k < P.Count; k++)
            {
                float l = math.distance(P[k - 1], P[k]);
                if (acc + l >= stopAt) break;
                acc += l;
                r.points.Add(P[k]); r.pointLevels.Add(full.pointLevels[k]); r.pointIsJunction.Add(full.pointIsJunction[k]);
            }
            var stop = Along(stopAt); { float sy = net.FloorY(stop + new float3(0, 3, 0), 4f, 8f); if (!float.IsNaN(sy)) stop.y = sy; }
            r.points.Add(stop); r.pointLevels.Add(at.level); r.pointIsJunction.Add(false);
            // round 13: shaft drops on the way (their point indexes are unchanged: the points are copied from the start)
            for (int i = 0; i < full.shaftAt.Count; i++) if (full.shaftAt[i] + 1 < r.points.Count - 1) { r.shaftAt.Add(full.shaftAt[i]); r.shaftIds.Add(full.shaftIds[i]); }
            for (int k = 1; k < r.points.Count; k++) r.Length += math.distance(r.points[k - 1], r.points[k]);
            r.CaveIn = true;
            if (through)
            {
                r.BackSeal = true;
                r.backSeal = Along(target + hollow);
                float by = net.FloorY(r.backSeal + new float3(0, 3, 0), 4f, 8f); if (!float.IsNaN(by)) r.backSeal.y = by;
            }
            return r;
        }

        /// <summary>The dead end runs into solid rock: nothing else within reach around its last part and beyond its end.</summary>
        bool SealClear(in TunnelPrim s)
        {
            var buf = new List<TunnelPrim>();
            Collect(math.min(s.a, s.b) - 40f, math.max(s.a, s.b) + 40f, 10f, buf);
            var sa = s.a; var sb = s.b;
            buf.RemoveAll(p => p.type == TunnelPrim.Tube && math.all(p.a == sa) && math.all(p.b == sb));
            if (buf.Count == 0) return true;
            var arr = new NativeArray<TunnelPrim>(buf.ToArray(), Allocator.Temp);
            var dir = math.normalize(s.b.xz - s.a.xz); bool ok = true;
            for (float t = 0.6f; t <= 1.0001f && ok; t += 0.1f)
            {
                var c = math.lerp(s.a, s.b, t); float fy = TunnelNetwork.FloorAt(s, t);
                float hw = math.lerp(s.hw, s.hwEnd, t) + 3f;
                foreach (float side in new[] { -1f, 0f, 1f })
                {
                    var q = new float3(c.x - dir.y * hw * side, fy + 2.5f, c.z + dir.x * hw * side);
                    if (TunnelSdf.Eval(arr, arr.Length, q, out _, out _) < 4f) { ok = false; break; }
                }
            }
            var beyond = new float3(s.b.x + dir.x * 10f, s.b.y + 2.5f, s.b.z + dir.y * 10f);
            if (ok && TunnelSdf.Eval(arr, arr.Length, beyond, out _, out _) < 4f) ok = false;
            arr.Dispose();
            return ok;
        }

        /// <summary>
        /// Shafts whose bottom has no driving way back up to the pit: the advance team left a fixed line in each (exhibit
        /// safety: whoever falls in winches back up at its foot; the winch is otherwise a shortcut, not the only way out).
        /// </summary>
        public List<Shaft> ShaftsNeedingLines()
        {
            var dist = FlowTubes.Distances(L, Ways).dist;
            var res = new List<Shaft>();
            foreach (var s in Shafts) if (double.IsInfinity(dist[L.links[s.link].b])) res.Add(s);   // (intact links have no shaft)
            return res;
        }

        /// <summary>Collapse links left intact (no hole: narrow upper tube, the lower flow is reached elsewhere).</summary>
        readonly HashSet<int> intact = new();
        public bool Intact(int link) => intact.Contains(link);

        /// <summary>Driving distance from the pit to the nearest node of a frame point (site catalog).</summary>
        public double[] DistFromPit() => FlowTubes.Distances(L, Ways).dist;
    }
}
