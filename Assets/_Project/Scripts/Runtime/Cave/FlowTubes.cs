using System;
using System.Collections.Generic;
using System.Linq;

namespace MoonBaseAlpha.Cave
{
    /// <summary>
    /// The lava-tube layout generator, an exact port of the "Marius Tube Lab" tester (same seeded random sequence, so a
    /// seed code gives the layout seen there). Plan coordinates (x, y) in metres around the pit, depth = floor depth below
    /// the surface. One tube system per lava flow:
    ///  - a trunk following the flow downhill (the top flow's trunk runs through the pit: the pit is a skylight in its
    ///    roof), braided loops (parallel channels that split off and rejoin), distributary branches and short spurs, more
    ///    of them towards the flow front (Earth: Kazumura, Undara);
    ///  - the floor at a near-constant depth below the flow surface (it follows the slope), cut down a few metres in
    ///    places; lava falls are steps where it cut deeper;
    ///  - younger flows bury older ones: each lower flow is a separate system at its own depth and direction, joined to the
    ///    flow above only where the rock between two crossing tubes collapsed (&lt; 40 m rock: a talus ramp, else a shaft).
    /// Checks built in: no collapse within 300 m of the pit, every lower flow joined (else the next variant of the seed).
    /// </summary>
    public static class FlowTubes
    {
        public class Settings
        {
            public double size = 4, flows = 3, thick = 60, trunkW = 60, branching = 0.65, slope = 1.0, wander = 5,
                          fallRate = 0.6, sinuosity = 0.05, giantW = 100, cover = 8, collapse = 0.4;
            /// <summary>The game caps lava-fall steps (rubble slopes the rover can drive); the tester does not.</summary>
            public double maxFall = 1e9;
        }

        public struct P3 { public double x, y, d; public P3(double x, double y, double d) { this.x = x; this.y = y; this.d = d; } }

        public class Flow { public double D, thick, dir, hCap, maxD; public bool linked; }

        public class Tube
        {
            public List<P3> pts = new(); public List<(int k, double step)> falls = new();
            public int f; public string kind; public double w, h, acc0; public bool giant;   // acc0: cut-down inherited at its start
            public (int t, int i)? from, to; public List<int> children = new();
        }

        public class Edge
        {
            public int a, b, tube, i0, i1, flow, falls; public List<P3> pts; public double w, h, drop, curveLen, pitch;
            public string kind; public bool loop, isFall, deadEnd; public double bx0, by0, bx1, by1;
        }

        public class Cross { public double x, y, dUp, dLo, cover; public Edge up, low; public int iUp, iLo; public bool collapse, talus; }

        public class Link { public int a, b; public List<P3> pts; public double curveLen; public bool drive; public Cross c; public bool upFromA, loFromA; }

        public class Layout
        {
            public double R, downA; public int variant;
            public List<(double x, double y)> nodes = new(); public List<double> depth = new();
            public List<Edge> edges = new(); public List<Link> links = new(); public List<Cross> X = new();
            public List<Flow> flows = new(); public List<Tube> tubes = new();
            public int pitIdx;                                   // index of the pit point on tube 0 (the top trunk)
            public double Elev(double x, double y) => -slope * (x * Math.Cos(downA) + y * Math.Sin(downA));
            public double slope;
        }

        // ------------------------------------------------------------------ seeded random (JS semantics)

        public static uint HashStr(string s)
        {
            int h = unchecked((int)2166136261u);
            foreach (char c in s) { h ^= c; h = unchecked(h * 16777619); }
            return (uint)h;
        }

        static int Imul(int a, int b) => unchecked(a * b);
        static int Ushr(int a, int n) => (int)((uint)a >> n);

        public static Func<double> Mulberry(int seed)
        {
            int a = seed;
            return () =>
            {
                a = unchecked(a + 0x6D2B79F5);
                int t = Imul(a ^ Ushr(a, 15), 1 | a);
                t = unchecked(t + Imul(t ^ Ushr(t, 7), 61 | t)) ^ t;
                return (uint)(t ^ Ushr(t, 14)) / 4294967296.0;
            };
        }

        static Func<double, double, double> ValueNoise(int seed)
        {
            double H(int i, int j)
            {
                int x = Imul(i, 374761393) ^ Imul(j, 668265263) ^ seed;
                x = Imul(x ^ Ushr(x, 13), 1274126177);
                return (uint)(x ^ Ushr(x, 16)) / 4294967296.0 * 2 - 1;
            }
            double Sm(double t) => t * t * (3 - 2 * t);
            return (x, y) =>
            {
                int i = (int)Math.Floor(x), j = (int)Math.Floor(y); double fx = Sm(x - i), fy = Sm(y - j);
                double a = H(i, j), b = H(i + 1, j), c = H(i, j + 1), d = H(i + 1, j + 1);
                return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
            };
        }

        static double AngDiff(double a, double b)
        {
            double d = (a - b) % (2 * Math.PI);
            if (d > Math.PI) d -= 2 * Math.PI; if (d < -Math.PI) d += 2 * Math.PI;
            return d;
        }

        public const double PitDepth = 45;

        // ------------------------------------------------------------------ generator

        public static Layout Generate(string code, Settings P = null)
        {
            P ??= new Settings();
            Layout g = null;
            for (int v = 0; v < 8; v++) { g = GenerateOnce(code, P, v); g.variant = v; if (g.flows.Skip(1).All(f => f.linked)) break; }
            return g;
        }

        static Layout GenerateOnce(string code, Settings P, int variant)
        {
            string s0 = string.IsNullOrEmpty(code) ? "MOON" : code.ToUpperInvariant();
            uint seedNum = HashStr(s0) ^ (uint)Imul(variant, unchecked((int)0x9E3779B9u));
            var rng = Mulberry((int)seedNum);
            double R = P.size * 500, lim = R * 1.1, slope = P.slope / 100;
            double downA = rng() * Math.PI * 2;
            var L = new Layout { R = R, downA = downA, slope = slope };
            bool Inside(double x, double y) => Math.Abs(x) <= lim && Math.Abs(y) <= lim;
            var nz = ValueNoise((int)(seedNum ^ 0x3c1));

            // flows (0 = youngest, on top, holds the pit)
            var flows = L.flows; double D = PitDepth;
            int nFlows = (int)P.flows;
            int gf = P.giantW > 0 && nFlows >= 2 ? nFlows - 1 : -1; double hG = Math.Min(P.giantW / 6, 120);
            for (int f = 0; f < nFlows; f++)
            {
                double thick = P.thick * (0.8 + 0.4 * rng());
                if (f == gf) thick = Math.Max(thick, hG + 40);
                if (f > 0) D += thick;
                double turn = 0;
                if (f > 0) for (int tries = 0; tries < 12; tries++)
                {
                    turn = (rng() < 0.5 ? -1 : 1) * (0.35 + 0.35 * rng());
                    double tt = turn;
                    if (flows.All(o => Math.Abs(AngDiff(downA + tt, o.dir)) > 0.44)) break;
                }
                flows.Add(new Flow { D = D, thick = thick, dir = downA + turn, hCap = f == 0 ? PitDepth - 12 : Math.Max(6, thick - 30) });
            }
            foreach (var fl in flows) fl.maxD = fl.D + Math.Max(2, 30 - P.cover - 1);
            double DepthAt(int f, double x, double y) => flows[f].D + P.wander * (0.5 + 0.5 * nz(x / 450 + f * 37.1, y / 450));

            var tubes = L.tubes; const double ST = 25;
            Tube Trace(double x, double y, double hd, double len, int f, string kind, double w,
                       double? pullO = null, double? dirO = null, double acc = 0, bool fromOutside = false, bool noFalls = false, (int, int)? from = null)
            {
                double lam = 500 + rng() * 400, ph = rng() * 6.28, amp = P.sinuosity * lam, pull = pullO ?? 0.04, dir = dirO ?? flows[f].dir;
                var t = new Tube { f = f, kind = kind, w = w, h = Math.Min(w / 1.6, flows[f].hCap), from = from, acc0 = acc };
                double bx = x, by = y; bool entered = !fromOutside;
                for (double s = 0; s <= len; s += ST)
                {
                    double taper = Math.Min(1, s / 150), off = amp * (Math.Sin(2 * Math.PI * s / lam + ph) - Math.Sin(ph)) * taper;
                    double px = bx - Math.Sin(hd) * off, py = by + Math.Cos(hd) * off;
                    bool inR = Inside(px, py);
                    if (!entered && inR) entered = true;
                    if (entered && !inR && t.pts.Count > 3) break;
                    if (entered)
                    {
                        if (!noFalls && t.pts.Count > 4 && rng() < P.fallRate / 1000 * ST)
                        {
                            double r2 = rng(), step = Math.Min(P.maxFall, 1.5 + r2 * r2 * 12);
                            acc += step; t.falls.Add((t.pts.Count, step));
                        }
                        t.pts.Add(new P3(px, py, Math.Min(flows[f].maxD, DepthAt(f, px, py) + acc)));
                    }
                    bx += Math.Cos(hd) * ST; by += Math.Sin(hd) * ST;
                    hd += (rng() - 0.5) * 0.08 + pull * AngDiff(dir, hd);
                }
                tubes.Add(t); return t;
            }
            double HeadingAt(Tube t, int i) { var a = t.pts[Math.Max(0, i - 1)]; var b = t.pts[Math.Min(t.pts.Count - 1, i + 1)]; return Math.Atan2(b.y - a.y, b.x - a.x); }
            // the cut-down at point i: inherited at the tube's start + its own falls so far (the tester missed acc0: a spur
            // off a branch started up to ~13 m above it)
            double AccAt(Tube t, int i) => t.acc0 + t.falls.Where(q => q.k <= i).Sum(q => q.step);

            void Sprout(Tube t, int ti, int depthLevel)
            {
                int n = t.pts.Count;
                for (int i = 8 + (int)Math.Floor(rng() * 10); i < n - 8; i += 8 + (int)Math.Floor(rng() * 16))
                {
                    double front = (double)i / n, pr = P.branching * (0.35 + 0.65 * front) * (depthLevel != 0 ? 0.5 : 1);
                    if (rng() >= pr) continue;
                    double r = rng(); int side = rng() < 0.5 ? -1 : 1; double hd = HeadingAt(t, i);
                    if (r < 0.4 && i + 10 < n - 2 && t.w <= 200)
                    {
                        int j = Math.Min(n - 2, i + 8 + (int)Math.Floor(rng() * 12)); double B = 50 + rng() * 90, w = t.w * (0.45 + 0.25 * rng());
                        var b = new Tube { f = t.f, kind = "braid", w = w, h = Math.Min(w / 1.6, flows[t.f].hCap), from = (ti, i), to = (ti, j) };
                        for (int k = i; k <= j; k++)
                        {
                            double u = (double)(k - i) / (j - i), a = HeadingAt(t, k), off = side * B * Math.Sin(Math.PI * u);
                            var q = t.pts[k]; b.pts.Add(new P3(q.x - Math.Sin(a) * off, q.y + Math.Cos(a) * off, q.d));
                        }
                        tubes.Add(b); t.children.Add(i); t.children.Add(j);
                        i = j;
                    }
                    else if (r < 0.75 && depthLevel == 0)
                    {
                        double a = hd + side * (0.45 + 0.35 * rng());
                        double len = 400 + rng() * 1000, w = Math.Min(120, t.w * (0.35 + 0.25 * rng()));
                        var b = Trace(t.pts[i].x, t.pts[i].y, a, len, t.f, "branch", w, 0.015, a, AccAt(t, i), false, false, (ti, i));
                        t.children.Add(i);
                        if (b.pts.Count > 12) Sprout(b, tubes.Count - 1, 1);
                        else if (b.pts.Count < 3) { tubes.RemoveAt(tubes.Count - 1); t.children.RemoveAt(t.children.Count - 1); }
                    }
                    else
                    {
                        double a = hd + side * (0.8 + 0.6 * rng());
                        double len = 60 + rng() * 160, w = 12 + rng() * 8;
                        var b = Trace(t.pts[i].x, t.pts[i].y, a, len, t.f, "spur", w, 0, a, AccAt(t, i), false, true, (ti, i));
                        if (b.pts.Count < 3) tubes.RemoveAt(tubes.Count - 1); else t.children.Add(i);
                    }
                }
            }

            // top flow: the trunk runs through the pit, upstream and downstream
            double tw0 = P.trunkW * (0.85 + 0.3 * rng());
            var dn = Trace(0, 0, downA, 3 * R, 0, "trunk", tw0); tubes.RemoveAt(tubes.Count - 1);
            var up = Trace(0, 0, downA + Math.PI, 3 * R, 0, "trunk", tw0, null, downA + Math.PI, 0, false, true); tubes.RemoveAt(tubes.Count - 1);
            int pitIdx = up.pts.Count - 1; L.pitIdx = pitIdx;
            var trunk0 = new Tube { f = 0, kind = "trunk", w = tw0, h = Math.Min(tw0 / 1.6, flows[0].hCap) };
            for (int k = up.pts.Count - 1; k >= 0; k--) trunk0.pts.Add(up.pts[k]);
            for (int k = 1; k < dn.pts.Count; k++) trunk0.pts.Add(dn.pts[k]);
            foreach (var (k, st) in dn.falls) trunk0.falls.Add((k + pitIdx, st));
            trunk0.children.Add(pitIdx);
            tubes.Add(trunk0);
            Sprout(trunk0, 0, 0);
            // older flows: trunks crossing the region near its middle (offset sideways from the pit), each in its own direction
            for (int f = 1; f < flows.Count; f++)
            {
                double fd = flows[f].dir, sgn = rng() < 0.5 ? -1 : 1, c = sgn * (0.15 + 0.2 * rng()) * R;
                double px = -Math.Sin(fd) * c - Math.Cos(fd) * 1.3 * R, py = Math.Cos(fd) * c - Math.Sin(fd) * 1.3 * R;
                double w = f == gf ? P.giantW : P.trunkW * (0.7 + 0.4 * rng());
                var t = Trace(px, py, fd, 3.2 * R, f, "trunk", w, null, null, 0, true);
                if (f == gf) { t.h = hG; t.giant = true; }
                if (t.pts.Count < 6) { tubes.RemoveAt(tubes.Count - 1); continue; }
                Sprout(t, tubes.Count - 1, 0);
            }

            // ---- graph: junction nodes, tube pieces between them
            var key = new Dictionary<(int, int), int>();
            int Node(int ti, int i)
            {
                if (key.TryGetValue((ti, i), out int k)) return k;
                var p = tubes[ti].pts[i]; L.nodes.Add((p.x, p.y)); L.depth.Add(p.d);
                key[(ti, i)] = L.nodes.Count - 1; return L.nodes.Count - 1;
            }
            // tube 0 must be the top trunk (it was pushed after the branches of nothing: find it)
            int t0 = tubes.IndexOf(trunk0);
            Node(t0, pitIdx);
            for (int ti = 0; ti < tubes.Count; ti++)
            {
                var t = tubes[ti];
                if (t.from.HasValue) key[(ti, 0)] = Node(t.from.Value.t, t.from.Value.i);
                if (t.to.HasValue) key[(ti, t.pts.Count - 1)] = Node(t.to.Value.t, t.to.Value.i);
            }
            for (int ti = 0; ti < tubes.Count; ti++)
            {
                var t = tubes[ti];
                var cuts = new SortedSet<int> { 0, t.pts.Count - 1 }; foreach (var c in t.children) cuts.Add(c);
                var cl = cuts.ToList();
                for (int c = 0; c + 1 < cl.Count; c++)
                {
                    int i0 = cl[c], i1 = cl[c + 1]; if (i1 <= i0) continue;
                    var pts = t.pts.GetRange(i0, i1 - i0 + 1);
                    var fall = t.falls.Where(q => q.k > i0 && q.k <= i1).ToList();
                    double drop = fall.Count > 0 ? fall.Max(q => q.step) : 0, len = 0, pitch = 0;
                    for (int k = 1; k < pts.Count; k++)
                    {
                        double hz = Math.Sqrt((pts[k].x - pts[k - 1].x) * (pts[k].x - pts[k - 1].x) + (pts[k].y - pts[k - 1].y) * (pts[k].y - pts[k - 1].y)); len += hz;
                        int kk = i0 + k; if (fall.Any(q => q.k == kk)) continue;
                        double y1 = L.Elev(pts[k].x, pts[k].y) - pts[k].d, y0 = L.Elev(pts[k - 1].x, pts[k - 1].y) - pts[k - 1].d;
                        pitch = Math.Max(pitch, Math.Atan(Math.Abs(y1 - y0) / Math.Max(1e-3, hz)) * 180 / Math.PI);
                    }
                    var e = new Edge
                    {
                        a = Node(ti, i0), b = Node(ti, i1), tube = ti, i0 = i0, i1 = i1, pts = pts, w = t.w, h = t.h, flow = t.f,
                        kind = t.giant ? "giant" : t.kind, loop = t.kind == "braid", drop = drop, isFall = drop >= 8, falls = fall.Count,
                        curveLen = len, pitch = pitch, deadEnd = c + 1 == cl.Count - 1 && (t.kind == "spur" || t.kind == "branch"),
                    };
                    e.bx0 = pts.Min(p => p.x); e.by0 = pts.Min(p => p.y); e.bx1 = pts.Max(p => p.x); e.by1 = pts.Max(p => p.y);
                    L.edges.Add(e);
                }
            }

            // ---- crossings between tubes of different flows
            var E = L.edges;
            for (int i = 0; i < E.Count; i++) for (int j = i + 1; j < E.Count; j++)
            {
                var A = E[i]; var B = E[j]; if (A.flow == B.flow) continue;
                if (A.bx0 > B.bx1 || B.bx0 > A.bx1 || A.by0 > B.by1 || B.by0 > A.by1) continue;
                for (int s = 0; s < A.pts.Count - 1; s++) for (int t = 0; t < B.pts.Count - 1; t++)
                {
                    var p = A.pts[s]; var p2 = A.pts[s + 1]; var q = B.pts[t]; var q2 = B.pts[t + 1];
                    double rx = p2.x - p.x, ry = p2.y - p.y, sx = q2.x - q.x, sy = q2.y - q.y, den = rx * sy - ry * sx;
                    if (Math.Abs(den) < 1e-9) continue;
                    double u = ((q.x - p.x) * sy - (q.y - p.y) * sx) / den, v = ((q.x - p.x) * ry - (q.y - p.y) * rx) / den;
                    if (u < 0 || u > 1 || v < 0 || v > 1) continue;
                    double dA = p.d + (p2.d - p.d) * u, dB = q.d + (q2.d - q.d) * v; bool aUp = dA < dB;
                    L.X.Add(new Cross
                    {
                        x = p.x + rx * u, y = p.y + ry * u, dUp = Math.Min(dA, dB), dLo = Math.Max(dA, dB), up = aUp ? A : B, low = aUp ? B : A,
                        iUp = aUp ? s : t, iLo = aUp ? t : s, cover = Math.Max(dA, dB) - (aUp ? B : A).h - Math.Min(dA, dB),
                    });
                }
            }
            var crng = Mulberry((int)(seedNum ^ 0x51ed));
            const double PitClear = 300;
            bool ClearOfPit(Cross c) => Math.Sqrt(c.x * c.x + c.y * c.y) >= PitClear;
            foreach (var c in L.X) c.collapse = c.cover >= P.cover && ClearOfPit(c) && crng() < P.collapse * (c.cover < 40 ? 1 : 0.35);
            for (int f = 1; f < flows.Count; f++)
            {
                bool Ok(Cross c) => c.low.flow == f && c.up.flow < f && c.cover >= P.cover && ClearOfPit(c);
                var mine = L.X.Where(c => Ok(c) && c.up.flow == f - 1).OrderBy(c => c.cover)
                              .Concat(L.X.Where(c => Ok(c) && c.up.flow < f - 1).OrderBy(c => c.cover)).ToList();
                if (mine.Count > 0 && !mine.Any(c => c.collapse)) mine[0].collapse = true;
                int ff = f;
                flows[f].linked = L.X.Any(c => c.collapse && ((c.low.flow == ff && c.up.flow < ff) || (c.up.flow == ff && c.low.flow < ff)));
            }
            foreach (var c in L.X) c.talus = c.collapse && c.cover < 40;
            // collapse links: from the upper tube's nearer junction, down the collapse, to the lower tube's
            foreach (var c in L.X)
            {
                if (!c.collapse) continue;
                var U = c.up; var Lw = c.low;
                bool uToA = c.iUp < U.pts.Count / 2.0, lToA = c.iLo < Lw.pts.Count / 2.0;
                var upP = uToA ? U.pts.GetRange(0, c.iUp + 1) : Enumerable.Reverse(U.pts.GetRange(c.iUp + 1, U.pts.Count - c.iUp - 1)).ToList();
                var loP = lToA ? Enumerable.Reverse(Lw.pts.GetRange(0, c.iLo + 1)).ToList() : Lw.pts.GetRange(c.iLo + 1, Lw.pts.Count - c.iLo - 1);
                var path = new List<P3>(upP) { new P3(c.x, c.y, c.dUp), new P3(c.x, c.y, c.dLo) }; path.AddRange(loP);
                double len = 0;
                for (int k = 1; k < path.Count; k++) { double dx = path[k].x - path[k - 1].x, dy = path[k].y - path[k - 1].y, dd = path[k].d - path[k - 1].d; len += Math.Sqrt(dx * dx + dy * dy + dd * dd); }
                L.links.Add(new Link { a = uToA ? U.a : U.b, b = lToA ? Lw.a : Lw.b, pts = path, curveLen = len, drive = c.talus, c = c, upFromA = uToA, loFromA = lToA });
            }
            return L;
        }

        // ------------------------------------------------------------------ routes

        /// <summary>A way through the graph: a tube piece (edge) or a collapse link.</summary>
        public struct Way { public int a, b; public List<P3> pts; public double len; public bool drive; public int edge, link; }

        public static List<Way> Ways(Layout g)
        {
            var w = new List<Way>();
            for (int k = 0; k < g.edges.Count; k++) { var e = g.edges[k]; w.Add(new Way { a = e.a, b = e.b, pts = e.pts, len = e.curveLen, drive = !e.isFall, edge = k, link = -1 }); }
            for (int k = 0; k < g.links.Count; k++) { var l = g.links[k]; w.Add(new Way { a = l.a, b = l.b, pts = l.pts, len = l.curveLen, drive = l.drive, edge = -1, link = k }); }
            return w;
        }

        /// <summary>Driving distances from the pit (Dijkstra, lowest index wins ties like the tester), skipping blocked ways.</summary>
        public static (double[] dist, int[] prev, int[] prevW) Distances(Layout g, List<Way> ways, HashSet<int> blocked = null)
        {
            int N = g.nodes.Count;
            var adj = new List<(int to, double len, int k)>[N]; for (int i = 0; i < N; i++) adj[i] = new();
            for (int k = 0; k < ways.Count; k++)
            {
                var w = ways[k]; if (!w.drive || (blocked != null && blocked.Contains(k))) continue;
                adj[w.a].Add((w.b, w.len, k)); adj[w.b].Add((w.a, w.len, k));
            }
            var dist = Enumerable.Repeat(double.PositiveInfinity, N).ToArray(); var prev = Enumerable.Repeat(-1, N).ToArray(); var prevW = Enumerable.Repeat(-1, N).ToArray();
            var done = new bool[N]; dist[0] = 0;
            for (;;)
            {
                int u = -1; double b = double.PositiveInfinity;
                for (int i = 0; i < N; i++) if (!done[i] && dist[i] < b) { b = dist[i]; u = i; }
                if (u < 0) break; done[u] = true;
                foreach (var (v, l, k) in adj[u]) if (dist[u] + l < dist[v]) { dist[v] = dist[u] + l; prev[v] = u; prevW[v] = k; }
            }
            return (dist, prev, prevW);
        }

        /// <summary>
        /// GIMLI-2's goal: a real dead end (not the edge of the region), the deepest one whose driving distance from the pit
        /// is in [lo, hi] (the tester's rule is 1.5-3.5 km); else the dead end closest to the middle of that range.
        /// </summary>
        public static int Goal(Layout g, double[] dist, double lo, double hi, HashSet<int> skip = null)
        {
            int N = g.nodes.Count; var deg = new int[N];
            foreach (var e in g.edges) { deg[e.a]++; deg[e.b]++; }
            bool EdgeOfMap(int i) => Math.Max(Math.Abs(g.nodes[i].x), Math.Abs(g.nodes[i].y)) > g.R * 1.0;
            bool Skip(int i) => skip != null && skip.Contains(i);
            int goal = -1; double best = double.NegativeInfinity, mid = (lo + hi) / 2;
            for (int i = 1; i < N; i++) if (deg[i] == 1 && !EdgeOfMap(i) && !Skip(i) && dist[i] >= lo && dist[i] <= hi && g.depth[i] > best) { best = g.depth[i]; goal = i; }
            if (goal < 0) for (int i = 1; i < N; i++) if (deg[i] == 1 && !double.IsInfinity(dist[i]) && !EdgeOfMap(i) && !Skip(i) && (goal < 0 || Math.Abs(dist[i] - mid) < Math.Abs(dist[goal] - mid))) goal = i;
            if (goal < 0) for (int i = 1; i < N; i++) if (!double.IsInfinity(dist[i]) && !Skip(i) && (goal < 0 || Math.Abs(dist[i] - mid) < Math.Abs(dist[goal] - mid))) goal = i;
            return goal < 0 ? 0 : goal;
        }

        /// <summary>The ways (indices) from the pit to node n, in driving order, each with its direction (true = a to b).</summary>
        public static List<(int way, bool fwd)> PathTo(int n, int[] prev, int[] prevW, List<Way> ways)
        {
            var list = new List<(int, bool)>();
            for (int m = n; m > 0 && prev[m] >= 0; m = prev[m]) list.Insert(0, (prevW[m], ways[prevW[m]].b == m));
            return list;
        }
    }
}
