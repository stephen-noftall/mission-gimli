using System.Collections.Generic;
using Unity.Mathematics;

namespace MoonBaseAlpha.Cave
{
    /// <summary>
    /// Special places in the tunnel network for the site cycle (key 5 / Shift+5): collapse shafts (from the
    /// upper edge, and from the chamber below looking up), lava-fall steps, ramps down a level, large
    /// chambers, and the scout rover last. Found over the first ~40 junctions, sorted by network distance
    /// from the pit mouth (shortest drivable route, Dijkstra over the tube links).
    /// </summary>
    public class SiteCatalog
    {
        public enum Kind { CollapseTop, CollapseBelow, LavaFall, Ramp, Chamber, Scout, Gimli1, Giant }

        public struct Site
        {
            public Kind kind;
            public float3 feature;     // frame: what to look at (floor level)
            public float2 approach;    // horizontal unit vector: the direction the rover drives towards the feature
            public float standoff;     // m before the feature
            public int level;
            public float networkDist;  // m from the pit mouth
            public string label;
        }

        public readonly List<Site> Sites = new();
        public const int MaxJunctions = 40;

        readonly TunnelNetwork net;

        public SiteCatalog(TunnelNetwork network, float3? scout = null, float2 scoutDir = default, int scoutLevel = 2, float3? gimli1 = null)
        {
            net = network;
            Build(scout, scoutDir, scoutLevel, gimli1);
        }

        struct Node { public int L, i, j; public Node(int L, int i, int j) { this.L = L; this.i = i; this.j = j; } }

        void Build(float3? scout, float2 scoutDir, int scoutLevel, float3? gimli1)
        {
            if (net.IsFlow) { BuildFlow(); AddMissionSites(scout, scoutDir, scoutLevel, gimli1); return; }
            // ---- graph over a window around the pit (undirected tube links between junctions)
            var adj = new Dictionary<Node, List<(Node to, float len)>>();
            var prims = new List<TunnelPrim>();
            var links = new List<(int3 to, byte kind)>();
            void Link(Node a, Node b, float len)
            {
                if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<(Node, float)>();
                if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new List<(Node, float)>();
                la.Add((b, len)); lb.Add((a, len));
            }
            for (int L = 0; L < 3; L++)
            for (int i = 0; i < 10; i++)
            for (int j = -6; j <= 6; j++)
            {
                links.Clear();
                net.Links(L, i, j, links);
                foreach (var (to, _) in links)
                {
                    if (!net.NodeExists(to.x, to.y, to.z)) continue;
                    var path = net.LinkPath(new int3(L, i, j), to);
                    float len = 0; for (int k = 1; k < path.Length; k++) len += math.distance(path[k - 1], path[k]);
                    Link(new Node(L, i, j), new Node(to.x, to.y, to.z), len);
                }
            }

            // ---- Dijkstra from the first junction (mouth -> junction 0 tube first)
            var start = new Node(0, 0, 0);
            var dist = new Dictionary<Node, float> { [start] = math.distance(net.mouthFloor, net.NodePos(0, 0, 0)) };
            var prev = new Dictionary<Node, Node>();
            var done = new HashSet<Node>();
            var order = new List<Node>();
            while (order.Count < MaxJunctions)
            {
                Node best = default; float bd = float.MaxValue; bool found = false;
                foreach (var kv in dist) if (!done.Contains(kv.Key) && kv.Value < bd) { bd = kv.Value; best = kv.Key; found = true; }
                if (!found) break;
                done.Add(best); order.Add(best);
                if (!adj.TryGetValue(best, out var edges)) continue;
                foreach (var (to, len) in edges)
                {
                    float nd = bd + len;
                    if (!dist.TryGetValue(to, out var od) || nd < od) { dist[to] = nd; prev[to] = best; }
                }
            }

            float2 InDir(Node n)
            {
                var p = net.NodePos(n.L, n.i, n.j);
                var from = prev.TryGetValue(n, out var pn) ? net.NodePos(pn.L, pn.i, pn.j) : net.mouthFloor;
                return math.normalizesafe(p.xz - from.xz, net.dirU);
            }

            foreach (var n in order)
            {
                var np = net.NodePos(n.L, n.i, n.j);
                float d0 = dist[n];
                prims.Clear();
                net.CellPrims(n.L, n.i, n.j, prims);
                TunnelPrim? chamber = null;
                foreach (var p in prims) if (p.type == TunnelPrim.Chamber && p.a.Equals(np)) { chamber = p; break; }
                float chw = chamber?.hw ?? 20f;
                var inDir = InDir(n);

                if (net.HasCollapse(n.L, n.i, n.j))
                {
                    var top = net.CollapsePos(n.L, n.i, n.j);
                    float rs = 0; foreach (var p in prims) if (p.type == TunnelPrim.Shaft) rs = p.hw;
                    float drop = top.y - net.LevelFloor(n.L + 1, net.ToUV(top.xz).x);
                    var dir = math.normalizesafe(top.xz - np.xz, inDir);
                    Sites.Add(new Site { kind = Kind.CollapseTop, feature = top, approach = dir, standoff = rs + 12f, level = n.L, networkDist = d0,
                                         label = $"Collapse L{n.L} -> L{n.L + 1}, {drop:0} m drop" });
                    var below = new float3(top.x, net.LevelFloor(n.L + 1, net.ToUV(top.xz).x), top.z);
                    Sites.Add(new Site { kind = Kind.CollapseBelow, feature = below, approach = dir, standoff = 25f, level = n.L + 1, networkDist = d0 + drop,
                                         label = $"Under the collapse, L{n.L + 1}: looking up {drop:0} m to L{n.L}" });
                }
                foreach (var p in prims)
                {
                    if (p.type != TunnelPrim.Tube || p.deadEnd != 0 || !p.a.Equals(np)) continue;
                    var dir = math.normalizesafe(p.b.xz - p.a.xz, inDir);
                    float len = math.distance(p.a.xz, p.b.xz);
                    if (p.drop > 0)
                    {
                        var f = math.lerp(p.a, p.b, p.dropT);
                        Sites.Add(new Site { kind = Kind.LavaFall, feature = f, approach = dir, standoff = 25f, level = n.L, networkDist = d0 + p.dropT * len,
                                             label = $"Lava-fall step L{n.L}, {p.drop:0.0} m" });
                    }
                }
                if (net.HasRamp(n.L, n.i, n.j))
                {
                    // on the ramp, ~30 m down from its top (its own corridor beside the junction)
                    net.RampPath(n.L, n.i, n.j, out var rn, out var ra, out var rb, out var rm, out _);
                    var dir = math.normalizesafe(rb.xz - ra.xz, inDir);
                    float len = math.distance(ra.xz, rb.xz);
                    // just past the flat run-out, on the slope (the floor height from the ramp's real profile)
                    float flat = math.min(0.3f, TunnelNetwork.RampFlat / math.max(1f, len));
                    float t = math.min(flat + 25f / math.max(1f, len), 0.5f);
                    var f = math.lerp(ra, rb, t);
                    f.y = math.lerp(ra.y, rb.y, math.saturate((t - flat) / math.max(1e-3f, 1f - 2f * flat)));
                    Sites.Add(new Site { kind = Kind.Ramp, feature = f, approach = dir, standoff = 25f, level = n.L, networkDist = d0 + math.distance(rn, ra) + 30f,
                                         label = $"Ramp L{n.L} -> L{n.L + 1}, {rn.y - rm.y:0} m down" });
                }
                if (chamber.HasValue && chw >= 60f)
                {
                    // the entrance where the incoming tube opens into the chamber
                    var f = np - new float3(inDir.x, 0, inDir.y) * (chw * 0.85f);
                    Sites.Add(new Site { kind = Kind.Chamber, feature = f, approach = inDir, standoff = 25f, level = n.L, networkDist = d0 - chw,
                                         label = $"Chamber L{n.L}, {chw * 2:0} m across" });
                }
            }
            Sites.Sort((a, b) => a.networkDist.CompareTo(b.networkDist));
            AddMissionSites(scout, scoutDir, scoutLevel, gimli1);
        }

        void AddMissionSites(float3? scout, float2 scoutDir, int scoutLevel, float3? gimli1)
        {
            if (scout.HasValue)
                Sites.Add(new Site { kind = Kind.Scout, feature = scout.Value, approach = scoutDir, standoff = 22f, level = scoutLevel, networkDist = float.MaxValue,
                                     label = "GIMLI-2 at the seal" });
            if (gimli1.HasValue)
                Sites.Add(new Site { kind = Kind.Gimli1, feature = gimli1.Value, approach = default, standoff = 30f, level = -1, networkDist = float.MaxValue,
                                     label = "GIMLI-1 on the rim (lander + first rover)" });
        }

        /// <summary>
        /// Lava-flow layout: collapse shafts (top and below), talus ramps between flows, lava-fall steps and the giant tube,
        /// sorted by driving distance from the pit (nearest junction).
        /// </summary>
        void BuildFlow()
        {
            var F = net.Flow; var dist = F.DistFromPit();
            float DistAt(float3 p)
            {
                int best = 0; float bd = float.MaxValue;
                for (int i = 0; i < F.NodeFrame.Count; i++) { float d = math.distance(F.NodeFrame[i], p); if (d < bd && !double.IsInfinity(dist[i])) { bd = d; best = i; } }
                return (float)dist[best] + bd;
            }
            foreach (var s in F.Shafts)
            {
                var dir = math.normalizesafe(s.top.xz - net.origin, net.dirU);
                Sites.Add(new Site { kind = Kind.CollapseTop, feature = s.top, approach = dir, standoff = s.radius + 12f, level = s.flow, networkDist = DistAt(s.top),
                                     label = $"Collapse shaft, flow {s.flow + 1} -> below, {s.drop:0} m drop" });
                Sites.Add(new Site { kind = Kind.CollapseBelow, feature = s.top - new float3(0, s.drop, 0), approach = dir, standoff = 25f, level = s.flow + 1, networkDist = DistAt(s.top) + s.drop,
                                     label = $"Under the collapse: looking up {s.drop:0} m" });
            }
            for (int k = 0; k < F.LinkRamp.Count; k++)
            {
                if (F.LinkRamp[k] < 0) continue;
                var rp = F.RampPath[k]; var r = F.Prims[F.LinkRamp[k]];
                var f = rp[math.min(1, rp.Length - 1)];
                var dir = math.normalizesafe(f.xz - rp[0].xz, net.dirU);
                Sites.Add(new Site { kind = Kind.Ramp, feature = f, approach = dir, standoff = 25f, level = r.level, networkDist = DistAt(rp[0]),
                                     label = $"Collapse ramp, flow {r.level + 1} -> below, {rp[0].y - rp[rp.Length - 1].y:0} m down" });
            }
            int falls = 0;
            foreach (var p in F.Prims)
            {
                if (p.type != TunnelPrim.Tube || p.drop <= 0 || falls >= 12) continue;
                var f = math.lerp(p.a, p.b, p.dropT); f.y = TunnelNetwork.FloorAt(p, p.dropT);
                Sites.Add(new Site { kind = Kind.LavaFall, feature = f, approach = math.normalizesafe(p.b.xz - p.a.xz, net.dirU), standoff = 25f, level = p.level, networkDist = DistAt(f),
                                     label = $"Lava-fall step, flow {p.level + 1}, {p.drop:0.0} m" });
                falls++;
            }
            for (int k = 0; k < F.L.edges.Count; k++)
            {
                if (F.L.edges[k].kind != "giant" || F.WayPts[k].Length < 3) continue;
                var pts = F.WayPts[k]; var f = pts[pts.Length / 2];
                Sites.Add(new Site { kind = Kind.Giant, feature = f, approach = math.normalizesafe(pts[pts.Length / 2 + 1].xz - f.xz, net.dirU), standoff = 40f, level = F.L.edges[k].flow,
                                     networkDist = DistAt(f), label = $"Giant tube, flow {F.L.edges[k].flow + 1}, {F.L.edges[k].w:0} m wide" });
                break;
            }
            Sites.Sort((a, b) => a.networkDist.CompareTo(b.networkDist));
        }
    }
}
