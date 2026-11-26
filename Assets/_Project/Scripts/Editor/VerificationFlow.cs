using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using MoonBaseAlpha.Cave;

namespace MoonBaseAlpha.Editor
{
    /// <summary>
    /// Round 11: the lava-flow tube layout (FlowTubes / FlowNetwork). Every tested code (Mission.TestedCodes) must pass
    /// all checks, including all three tiers' GIMLI-2 routes driven on the real SDF floor; a few untested codes are
    /// reported for information.
    /// </summary>
    public static partial class Verification
    {
        static TunnelNetwork FlowNet(string code)
        {
            var net = new TunnelNetwork(12345u, new float2(0, 0), new float2(-0.9f, 0.44f), 0f, -48.5f, 30f) { surfaceAt = _ => 0f };
            net.UseFlowLayout(code);
            return net;
        }

        /// <summary>A rover is ~2.5 m wide; a kid needs a lane several times that to steer past a hole.</summary>
        const float KidLane = 12f;

        /// <summary>
        /// Driving the way a visitor does, not along the centreline:
        ///  - lanes: across the upper tube at every shaft and talus ramp, the floor left beside the hole is >= KidLane wide;
        ///  - wandering: every tube piece is also driven along lanes offset 40% of the half-width to each side; a drop met
        ///    there must be a warned hole (EDGE AHEAD / CollapseNear) or a talus trench (it is a ramp: you end up on it,
        ///    not stuck) - anything else is an unwarned drop;
        ///  - stranded: the bottom of every shaft and ramp must have a driving way back up to the pit (falling in never
        ///    strands anyone; the winch is a shortcut, not the only way out).
        /// </summary>
        static (int narrowLanes, int unwarned, int stranded) KidDriving(TunnelNetwork net, out string note)
        {
            var F = net.Flow; var L = F.L;
            int narrow = 0, unwarned = 0, stranded = 0, laneTests = 0, laneFails = 0, trenchFalls = 0, holeFalls = 0, probeN = 0;
            var lanes = new List<string>();
            // lane width beside a hole: scan across the upper tube at the hole, at its floor height
            float Lane(float3 at, float2 across, float hw, float floorY)
            {
                float best = 0, run = 0;
                for (float o = -hw * 1.2f; o <= hw * 1.2f; o += 1f)
                {
                    var q = at + new float3(across.x * o, 0, across.y * o); q.y = floorY;
                    float y = net.FloorY(q, 3f, 3f);
                    if (!float.IsNaN(y) && math.abs(y - floorY) < 2.5f) { run += 1f; best = math.max(best, run); } else run = 0;
                }
                return best;
            }
            float2 AcrossAt(float3 p, out float hw, out float floorY)
            {
                // the nearest tube piece of the upper flow at p
                hw = 10; floorY = p.y; float bd = float.MaxValue; float2 dir = new float2(1, 0);
                foreach (var pr in F.Prims)
                {
                    if (pr.type != TunnelPrim.Tube || pr.kind != 0) continue;
                    var ab = pr.b.xz - pr.a.xz; float t = math.saturate(math.dot(p.xz - pr.a.xz, ab) / math.max(1e-3f, math.lengthsq(ab)));
                    var c = math.lerp(pr.a, pr.b, t); float d = math.distance(c.xz, p.xz) + math.abs(c.y - p.y) * 2f;
                    if (d < bd) { bd = d; dir = math.normalizesafe(ab); hw = math.lerp(pr.hw, pr.hwEnd, t); floorY = TunnelNetwork.FloorAt(pr, t); }
                }
                return new float2(-dir.y, dir.x);
            }
            foreach (var s in F.Shafts)
            {
                var ac = AcrossAt(s.top, out float hw, out float fy);
                float w = Lane(s.top, ac, hw, fy); laneTests++;
                if (w < KidLane) { narrow++; lanes.Add($"shaft lane {w:0} m"); }
            }
            foreach (var rp in F.RampPath)
            {
                if (rp == null || rp.Length < 3) continue;
                var mid = rp[1];                                       // the open trench just below its top
                var ac = AcrossAt(mid + new float3(0, 4, 0), out float hw, out float fy);
                float w = Lane(mid, ac, hw, fy); laneTests++;
                if (w < KidLane) { narrow++; lanes.Add($"ramp lane {w:0} m"); }
            }
            // wandering lanes on every drivable tube piece
            for (int k = 0; k < L.edges.Count; k++)
            {
                if (!F.Ways[k].drive) continue;
                var pts = F.WayPts[k]; float hw = (float)L.edges[k].w / 2;
                foreach (float sgn in new[] { -1f, 1f })
                {
                    var lane = new float3[pts.Length];
                    for (int i = 0; i < pts.Length; i++)
                    {
                        var a = pts[math.max(0, i - 1)]; var b = pts[math.min(pts.Length - 1, i + 1)];
                        var d = math.normalizesafe(b.xz - a.xz);
                        // narrow at the ends (junctions, mouths): stay on the axis there
                        float ramp = math.saturate(math.min(i, pts.Length - 1 - i) / 2f);
                        // the pit mouths narrow to 11 m half-width at the shaft wall (widening over 130 m)
                        float hwHere = hw;
                        if ((L.edges[k].a == 0 || L.edges[k].b == 0) && L.edges[k].tube == 0)
                        {
                            float r = math.distance(pts[i].xz, net.origin) - math.distance(net.mouthFloor.xz, net.origin);
                            hwHere = math.lerp(FlowNetwork.MouthHalfWidth, hw, math.saturate(r / FlowNetwork.MouthWiden));
                        }
                        lane[i] = pts[i] + new float3(-d.y, 0, d.x) * (sgn * 0.4f * hwHere * ramp);
                    }
                    laneTests++;
                    if (net.PathDrivable(lane, out float wd, out float3 wat)) continue;
                    laneFails++;
                    // inside the pit box the game's pit mesh (CaveField, not built here) is the floor: tested in "Real terrain"
                    if (math.distance(wat.xz, net.origin) < 90f) { laneFails--; continue; }
                    if (net.CollapseNear(wat, 25f, out _, out _, out _)) { holeFalls++; continue; }
                    bool trench = false;
                    foreach (var rp in F.RampPath) if (rp != null) foreach (var q in rp) if (math.distance(q.xz, wat.xz) < 30f) { trench = true; break; }
                    if (trench) { trenchFalls++; continue; }
                    unwarned++;
                    if (L.edges[k].kind == "spur" && probeN++ < 1)
                    {
                        var prof = new List<string>();
                        for (int i = 0; i < lane.Length; i++) { float fy = net.FloorY(lane[i], 6f, 14f); prof.Add($"{i}:{lane[i].y:0.0}/{fy:0.0}"); }
                        var e = L.edges[k]; var par = L.tubes[e.tube];
                        var (pf, pc) = F.EdgePrims[k];
                        Note($"  spur probe edge {k}: hw {e.w / 2:0.0}, from tube {par.from}, pts {pts.Length}, lane y/floor: " + string.Join(" ", prof) +
                             $"; axis y: " + string.Join(" ", pts.Select(q => q.y.ToString("0.0"))) + $"; prims hv: " + string.Join(" ", Enumerable.Range(pf, pc).Select(i => F.Prims[i].hv.ToString("0.0") + (F.Prims[i].moundH > 0 ? "m" : ""))));
                        var p0 = pts[0]; var nearP = new List<TunnelPrim>(); net.Collect(p0 - new float3(40, 40, 40), p0 + new float3(40, 40, 40), 4f, nearP);
                        var arr = new Unity.Collections.NativeArray<TunnelPrim>(nearP.ToArray(), Unity.Collections.Allocator.Temp);
                        var col = new List<string>(); for (float dy = 8; dy >= -24; dy -= 2) col.Add($"{dy}:{TunnelSdf.Eval(arr, arr.Length, p0 + new float3(0, dy, 0), out _, out _):0.0}");
                        arr.Dispose();
                        Note($"  spur start {p0}: column " + string.Join(" ", col) + "; prims: " + string.Join(" | ", nearP.Select(q =>
                            $"t{q.type} k{q.kind} L{q.level} a({q.a.x:0},{q.a.y:0.0},{q.a.z:0}) b({q.b.x:0},{q.b.y:0.0},{q.b.z:0}) hw{q.hw:0.0}/{q.hwEnd:0.0} hv{q.hv:0.0} drop{q.drop:0.0} mound{q.moundH:0.0} de{q.deadEnd}")));
                    }
                    if (lanes.Count < 8) lanes.Add($"edge {k} ({L.edges[k].kind}, flow {L.edges[k].flow + 1}, pit edge {(L.edges[k].a == 0 || L.edges[k].b == 0)}) lane {sgn:+0;-0}: {(wd >= 89 ? "no floor" : $"{wd:0} deg")} at {math.distance(wat.xz, net.origin):0} m from the pit, {math.distance(wat.xz, pts[0].xz):0} m along");
                }
            }
            // stranded: every shaft / ramp bottom joined to the pit by driving
            var dist = FlowTubes.Distances(L, F.Ways).dist;
            var lined = new HashSet<int>(F.ShaftsNeedingLines().Select(s => s.link));
            for (int k = 0; k < L.links.Count; k++)
            {
                var lk = L.links[k]; int bottom = lk.b;
                if (!double.IsInfinity(dist[bottom]) || lined.Contains(k) || F.Intact(k)) continue;
                stranded++; if (lanes.Count < 10) lanes.Add($"link {k} bottom (flow {lk.c.low.flow + 1}) has no drive back up and no fixed line");
            }
            note = $"{lined.Count} shafts get the advance team's fixed line (no drive back up); {laneTests} lane tests; {narrow} narrow lanes beside holes; off-centre lanes: {laneFails} meet a drop ({holeFalls} warned holes, {trenchFalls} into a talus trench, {unwarned} unwarned); {stranded} stranded bottoms" +
                   (lanes.Count > 0 ? ". " + string.Join("; ", lanes) : "");
            return (narrow, unwarned, stranded);
        }

        static void TestFlowTubes()
        {
            // determinism + tester parity (the generator is an exact port: the same random sequence)
            var a = FlowNet("MOON"); var b = FlowNet("MOON"); var c = FlowNet("GIML");
            bool same = a.Flow.Prims.Count == b.Flow.Prims.Count && a.Flow.Prims.Zip(b.Flow.Prims, (p, q) => p.a.Equals(q.a) && p.b.Equals(q.b)).All(x => x);
            Check("Flow layout: same code -> identical tubes", same ? 1 : 0, 1, 1);
            Check("Flow layout: different code -> different tubes", a.Flow.Prims.Count != c.Flow.Prims.Count || !a.Flow.Prims[0].a.Equals(c.Flow.Prims[0].a) ? 1 : 0, 1, 1);
            var tester = FlowTubes.Generate("MOON");   // tester defaults (no fall cap): the numbers the Tube Lab shows
            Note($"MOON with the Tube Lab defaults: {tester.flows.Count} flows (floors {string.Join(" / ", tester.flows.Select(f => f.D.ToString("0")))} m), " +
                 $"{tester.tubes.Count} tubes ({tester.tubes.Count(t => t.kind == "trunk")} main, {tester.tubes.Count(t => t.kind == "braid")} braids, {tester.tubes.Count(t => t.kind == "branch")} branches, {tester.tubes.Count(t => t.kind == "spur")} spurs), " +
                 $"{tester.edges.Sum(e => e.curveLen) / 1000:0.0} km, {tester.X.Count} crossings, {tester.X.Count(x => x.collapse)} collapses, variant {tester.variant}.");

            // candidate scan (info): which codes pass every check - the pool for the tested layouts
            {
                var pass = new List<string>(); var failN = new List<string>();
                foreach (var cand in new[] { "RILL", "LAVA", "TUBE", "PITS", "MARE", "DOME", "CAVE", "ROCK", "DUST", "VENT", "FLOW", "BASA" })
                {
                    var n2 = FlowNet(cand); var why = new List<string>();
                    if (n2.Flow.L.X.Any(x => x.cover < 8)) why.Add("thin");
                    if (n2.Flow.L.flows.Skip(1).Any(f => !f.linked)) why.Add("unjoined");
                    foreach (int tl in new[] { 2, 3, 5 }) { var r = n2.GoldenPath(tl); if (!(r.Valid && r.verified && n2.PathDrivable(r.points.ToArray(), out _, out _))) { why.Add($"route{tl}"); break; } }
                    var k2 = KidDriving(n2, out _);
                    if (k2.narrowLanes > 0) why.Add("lanes"); if (k2.unwarned > 0) why.Add($"unwarned{k2.unwarned}"); if (k2.stranded > 0) why.Add("stranded");
                    if (why.Count == 0) pass.Add(cand); else failN.Add($"{cand} ({string.Join(",", why)})");
                }
                Note($"Candidate scan: pass all checks: {string.Join(" ", pass)}; fail: {string.Join("; ", failN)}");
            }
            foreach (var code in Mission.TestedCodes.Concat(new[] { "ZQ7K", "B4RT" }))
            {
                bool tested = Mission.IsTested(code);
                string tag = tested ? $"{code} (tested)" : $"{code} (untested, info)";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var net = FlowNet(code); var F = net.Flow; var L = F.L;
                long buildMs = sw.ElapsedMilliseconds;
                int thin = L.X.Count(x => x.cover < 8), nearPit = L.X.Count(x => x.collapse && math.sqrt(x.x * x.x + x.y * x.y) < 300);
                int unlinked = L.flows.Skip(1).Count(f => !f.linked);
                int talus = F.LinkRamp.Count(r => r >= 0), shafts = F.Shafts.Count;
                Note($"{tag}: {L.flows.Count} flows, {L.tubes.Count} tubes, {L.edges.Sum(e => e.curveLen) / 1000:0.0} km, {F.Prims.Count} prims, {talus} talus ramps, {shafts} shafts, variant {L.variant}, built in {buildMs} ms.");
                // diagnostics: every way driven on the real floor
                {
                    int okW = 0, nW = 0, probes = 0; var fails = new List<string>();
                    for (int w = 0; w < F.Ways.Count; w++)
                    {
                        if (!F.Ways[w].drive) continue; nW++;
                        bool good = net.PathDrivable(F.WayPts[w], out float wd, out float3 wat);
                        if (good) okW++;
                        else if (fails.Count < 8)
                        {
                            string kind = F.Ways[w].edge >= 0 ? L.edges[F.Ways[w].edge].kind + $" flow {L.edges[F.Ways[w].edge].flow + 1} drop {L.edges[F.Ways[w].edge].drop:0.0}" : "talus link";
                            float distA = math.distance(wat, F.WayPts[w][0]);
                            fails.Add($"way {w} ({kind}, {F.WayPts[w].Length} pts): {(wd >= 89 ? "no floor" : $"{wd:0} deg")} at {distA:0} m from its start");
                            if (F.Ways[w].edge < 0 && probes++ < 2)
                            {
                                var near = new List<TunnelPrim>(); net.Collect(wat - new float3(40, 40, 40), wat + new float3(40, 40, 40), 4f, near);
                                Note($"  probe at {wat}: polyline y there {wat.y:0.0}; FloorY {net.FloorY(wat, 15f, 25f):0.0}; prims: " + string.Join(" | ", near.Select(q =>
                                    $"t{q.type} k{q.kind} L{q.level} a({q.a.x:0},{q.a.y:0.0},{q.a.z:0}) b({q.b.x:0},{q.b.y:0.0},{q.b.z:0}) hw{q.hw:0.0}/{q.hwEnd:0.0} hv{q.hv:0.0} drop{q.drop:0.0}@{q.dropT:0.00}w{q.dropW:0.00} mound{q.moundH:0.0} de{q.deadEnd}")));
                                var col = new List<string>();
                                for (float dy = 12; dy >= -20; dy -= 2) { var arr = new Unity.Collections.NativeArray<TunnelPrim>(near.ToArray(), Unity.Collections.Allocator.Temp); col.Add($"{dy}:{TunnelSdf.Eval(arr, arr.Length, wat + new float3(0, dy, 0), out _, out _):0.0}"); arr.Dispose(); }
                                Note("  SDF column (dy: d): " + string.Join(" ", col));
                                var wp = F.WayPts[w]; int li = F.Ways[w].link; var rp = F.RampPath[li];
                                Note($"  link {li}: way pts y " + string.Join(" ", wp.Select(q => q.y.ToString("0"))) + $"; ramp S {rp[0]} E {rp[rp.Length - 1]} ({rp.Length} pts)");
                            }
                        }
                    }
                    Note($"{tag}: {okW}/{nW} drivable ways pass on the real floor. " + (fails.Count > 0 ? "Failing: " + string.Join("; ", fails) : ""));
                }
                // routes per tier, driven on the real floor
                var lens = new List<string>(); int bad = 0;
                foreach (int tl in new[] { 2, 3, 5 })
                {
                    sw.Restart();
                    var r = net.GoldenPath(tl);
                    bool ok = r.Valid && r.verified;
                    float worst = 0; bool drive = ok;
                    // round 13: the driven pieces between shaft drops (a drop is ridden on the rig)
                    {
                        var piece = new List<float3>();
                        // as the route search: the hole's edge and the rubble cone below are covered by the rig ride
                        bool NearShaft(float3 p) { for (int i = 0; i < r.ShaftCount; i++) { var sh = net.Flow.Shafts[r.shaftIds[i]]; if (math.distance(p.xz, sh.top.xz) < sh.radius + (p.y < sh.top.y - 3f ? 24f : 14f)) return true; } return false; }
                        for (int q = 0; q < r.points.Count && drive; q++)
                        {
                            if (!NearShaft(r.points[q])) piece.Add(r.points[q]);
                            if (r.IsShaftSegment(q) || q == r.points.Count - 1)
                            {
                                if (piece.Count >= 2) { drive = net.PathDrivable(piece.ToArray(), out float wq, out _); worst = math.max(worst, wq); }
                                piece.Clear();
                            }
                        }
                    }
                    float sy = ok ? net.FloorY(r.scout) : float.NaN;
                    int deepest = ok ? r.pointLevels.Max() : -1;
                    lens.Add($"tier {tl}: {(ok ? $"{r.Length / 1000:0.00} km to flow {deepest + 1}, worst {worst:0} deg, {r.attempts} searches, {sw.ElapsedMilliseconds} ms" : "NO ROUTE")}");
                    if (!(ok && drive && !float.IsNaN(sy))) bad++;
                    if (tested)
                    {
                        Check($"{code} tier {tl}: GIMLI-2 route found and every way checked", ok ? 1 : 0, 1, 1);
                        Check($"{code} tier {tl}: whole route drivable on the real floor (<= {TunnelNetwork.DrivableDeg} deg over 8 m)", drive ? 1 : 0, 1, 1);
                        Check($"{code} tier {tl}: floor under GIMLI-2", float.IsNaN(sy) ? 0 : 1, 1, 1);
                    }
                }
                Note($"{tag} routes: " + string.Join("; ", lens));
                var kid = KidDriving(net, out string kidNote);
                Note($"{tag} kid driving: {kidNote}");
                if (tested)
                {
                    Check($"{code}: lane beside every collapse hole / ramp >= {KidLane} m wide", kid.narrowLanes, 0, 0);
                    Check($"{code}: unwarned drops a wandering driver can reach (off-centre lanes)", kid.unwarned, 0, 0);
                    Check($"{code}: shaft / ramp bottoms with no drive back up to the pit (stranded)", kid.stranded, 0, 0);
                }
                if (tested)
                {
                    // S4: floating rock islands in junction corners (every junction + shaft-bottom chamber, 5 chunks each)
                    int found = 0, left = 0, nJ = 0;
                    var seenK = new HashSet<int3>();
                    foreach (var pr in F.Prims)
                    {
                        if (pr.type != TunnelPrim.Chamber) continue;
                        nJ++;
                        float o = pr.hw * 0.7f;
                        foreach (var off in new[] { new float3(0, 4, 0), new float3(o, 4, 0), new float3(-o, 4, 0), new float3(0, 4, o), new float3(0, 4, -o) })
                        {
                            var q = pr.a + off;
                            if (!seenK.Add((int3)math.floor(q / CaveStreamer.ChunkSize))) continue;
                            var (fd, lf) = CaveStreamer.IslandsAt(net, q, CaveStreamer.Voxel);
                            found += fd; left += lf;
                        }
                    }
                    // islands crossing a chunk face: 2x2x2 welded blocks around every junction / shaft-bottom chamber
                    int crossRaw = 0, crossLeft = 0;
                    foreach (var pr in F.Prims)
                    {
                        if (pr.type != TunnelPrim.Chamber) continue;
                        crossRaw += CaveStreamer.IslandsBlock(net, pr.a + new float3(0, 4, 0), CaveStreamer.Voxel, false);
                        crossLeft += CaveStreamer.IslandsBlock(net, pr.a + new float3(0, 4, 0), CaveStreamer.Voxel, true);
                    }
                    Note($"{code}: {nJ} junction / shaft chambers, {seenK.Count} chunks meshed: floating islands (< {TunnelChunkJob.IslandMax} m) inside one chunk {found} before the filter; in welded 2x2x2 blocks {crossRaw} before / {crossLeft} after the filter.");
                    left += crossLeft;
                    Check($"{code}: floating islands left at junctions after the mesher's filter", left, 0, 0);
                    Check($"{code}: crossings with too little rock", thin, 0, 0);
                    Check($"{code}: collapses within 300 m of the pit", nearPit, 0, 0);
                    Check($"{code}: lower flows not joined to the flows above", unlinked, 0, 0);
                    // the pit: the trunk leaves through the downstream mouth; its first piece starts there
                    Check($"{code}: the network starts at the pit mouth", math.distance(F.WayPts[F.FirstDownstreamEdge][0], net.mouthFloor), 0, 0.5, "m");
                    Check($"{code}: floor at the mouth (SDF)", float.IsNaN(net.FloorY(net.mouthFloor + new float3(net.dirU.x * 20f, 1f, net.dirU.y * 20f))) ? 0 : 1, 1, 1);
                }
                else Note($"{tag}: {bad} of 3 tiers failed; {thin} thin crossings, {nearPit} collapses near the pit, {unlinked} unjoined flows.");
            }
        }
    }
}
