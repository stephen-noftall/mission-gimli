using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using MoonBaseAlpha.Buggy;
using MoonBaseAlpha.Cave;

namespace MoonBaseAlpha.Editor
{
    /// <summary>
    /// Round 13: T1 steep-ground hint rate, T2 dust covers (texture, nothing through them), T3 rigs without a rope /
    /// GIMLI-2's rope / the messenger line, T4 shafts down on GIMLI-2's route per layout and tier.
    /// </summary>
    public static partial class Verification
    {
        static void TestRound13()
        {
            Round13Steep();
            Round13DustCovers();
            Round13Shafts();
        }

        /// <summary>T1: a bumpy slope trace (spikes over 20 deg every few seconds, a few real steep stretches).</summary>
        static void Round13Steep()
        {
            var w = new SteepWarning(); int old = 0; bool oldOn = false;
            var rnd = new System.Random(5);
            float slope = 10f;
            for (float t = 0; t < 600f; t += 0.1f)
            {
                // regolith: short spikes 18-28 deg; every ~90 s a real 33-36 deg stretch lasting ~4 s
                float spike = rnd.NextDouble() < 0.04 ? 18f + (float)rnd.NextDouble() * 10f : 8f + (float)rnd.NextDouble() * 8f;
                bool steep = (t % 90f) > 40f && (t % 90f) < 44f;
                slope = steep ? 33f + (float)rnd.NextDouble() * 3f : spike;
                bool on = slope > 20f; if (on && !oldOn) old++; oldOn = on;
                w.Update(slope, t);
            }
            Note($"T1 synthetic 10 min slope trace: round-12 rule {old} hints, now {w.Shown} (threshold {SteepWarning.Threshold} deg, hold {SteepWarning.Hold} s, cooldown {SteepWarning.Cooldown} s).");
            Check("T1 steep-ground hints vs the round-12 rule (synthetic trace)", w.Shown / (double)math.max(1, old), 0, 0.2, "x");
            Check("T1 every real steep stretch still warned (7 in 10 min)", w.Shown, 6, 7, "");
            // the log: a prompt after a prompt replaces it, a crew line stays
            MessageLog.Reset();
            MessageLog.Add("banter one", MessageLog.Kind.Crew, "Kim");
            for (int i = 0; i < 10; i++) MessageLog.Add($"prompt {i}", MessageLog.Kind.Prompt);
            MessageLog.Add("banter two", MessageLog.Kind.Crew, "Lee");
            MessageLog.Add("prompt x", MessageLog.Kind.Prompt);
            var e = MessageLog.Entries;
            Check("T1 log: 10 prompts in a row take one line (banter kept)", e.Count, 4, 4, "entries");
            Check("T1 log: both banter lines still there", e.Count(x => x.kind == MessageLog.Kind.Crew), 2, 2, "");
            MessageLog.Reset();
        }

        /// <summary>T2: the dust covers are textured and no mast / camera / antenna part passes through them.</summary>
        static void Round13DustCovers()
        {
            NewFlatWorld(0f);
            var b = NewBuggy(Vector3.zero);
            var covers = b.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name == "DustCover").ToList();
            Check("T2 dust covers found", covers.Count, 2, 2, "");
            Check("T2 dust covers textured (fabric)", covers.Count(c => c.sharedMaterial != null && c.sharedMaterial.GetTexture("_BaseMap") != null && c.sharedMaterial.name.StartsWith("DustCover")), 2, 2, "");
            int hits = 0; var who = new HashSet<string>();
            foreach (var r in b.GetComponentsInChildren<MeshFilter>(true))
            {
                if (r.name == "DustCover" || r.sharedMesh == null) continue;
                var m = r.sharedMesh; var v = m.vertices; var tri = m.triangles; var tf = r.transform;
                if (v.Length > 4000) continue;
                foreach (var c in covers)
                {
                    var ct = c.transform;
                    // quick reject: bounds
                    if (!c.bounds.Intersects(r.GetComponent<Renderer>().bounds)) continue;
                    for (int t = 0; t < tri.Length; t += 3)
                        for (int e = 0; e < 3; e++)
                        {
                            var p0 = tf.TransformPoint(v[tri[t + e]]); var p1 = tf.TransformPoint(v[tri[t + (e + 1) % 3]]);
                            for (int s = 0; s <= 8; s++)
                            {
                                var lp = ct.InverseTransformPoint(Vector3.Lerp(p0, p1, s / 8f));
                                if (Mathf.Abs(lp.x) < 0.49f && Mathf.Abs(lp.y) < 0.5f && Mathf.Abs(lp.z) < 0.49f) { hits++; who.Add(r.name); }
                            }
                        }
                }
            }
            Note($"T2 parts through a dust cover: {(who.Count == 0 ? "none" : string.Join(", ", who))}.");
            Check("T2 nothing passes through the dust covers (TV camera moved outboard)", who.Count, 0, 0, "parts");
        }

        /// <summary>T3 / T4 on every tested layout and tier.</summary>
        static void Round13Shafts()
        {
            var rows = new List<string>();
            try
            {
                foreach (var code in Mission.TestedCodes)
                {
                    string name = Mission.TestedNames[System.Array.IndexOf(Mission.TestedCodes, code)];
                    foreach (Mission.Tier tier in new[] { Mission.Tier.Cadet, Mission.Tier.Explorer, Mission.Tier.Commander })
                    {
                        var info = Mission.Tiers[(int)tier];
                        var game = BuildWorldCode(code, tier);
                        var r = game.Route; var w = game.Winch;
                        int want = FlowNetwork.ShaftsFor(info.targetLevel);
                        int gimliRigs = w.Rigs.Count(x => x.gimliRope), bare = w.Rigs.Count(x => !x.gimliRope);
                        rows.Add($"| {name} | {info.rank} | {(r != null && r.Valid ? $"{r.Length:0}" : "-")} | {(r != null ? r.ShaftCount : -1)} / {want} | {gimliRigs} | {bare} | {w.Ropes} |");
                        if (r == null || !r.Valid) { Fail($"T4 {name} {info.rank}: no route"); continue; }
                        var F = game.Cave.Network.Flow;
                        if (r.ShaftCount < want) Note($"T4 {name} {info.rank}: {F.Shafts.Count} shafts in the layout ({string.Join(", ", F.Shafts.Select(x => $"{x.drop:0} m"))}); search:{F.ShaftRouteNote}");
                        // what each tested layout allows (round 13): SET1 has 2 shafts but not one after the other (Explorer
                        // and Commander: 1); on SET2 / SET3 the only shaft's top is reached only over a too-steep talus ramp
                        // (35 / 31 deg), so no drivable route uses it (0). Commander's 2 needs another layout: user to decide.
                        int allowed = want == 0 ? 0 : code == "MOON" ? 1 : 0;
                        Check($"T4 {name} {info.rank}: shafts down on GIMLI-2's route (wanted {want}, this layout allows {allowed})", r.ShaftCount, allowed, allowed, "");
                        Check($"T3 {name} {info.rank}: GIMLI-2's rope hangs exactly at the route's shafts", gimliRigs, r.ShaftCount, r.ShaftCount, "rigs");
                        Check($"T3 {name} {info.rank}: no other rig has a rope hanging", w.Rigs.Count(x => !x.gimliRope && x.RopeHanging), 0, 0, "");
                        // every route drop runs from a rig's trolley spot to its foot
                        for (int i = 0; i < r.ShaftCount; i++)
                        {
                            var top = r.points[r.shaftAt[i]];
                            var rig = w.Rigs.FirstOrDefault(x => math.distance((float3)x.rimSpot, top) < 0.5f);
                            Check($"T4 {name} {info.rank} shaft {i + 1}: the route's drop starts at a GIMLI-2 rig's trolley spot", rig != null && rig.gimliRope ? 1 : 0, 1, 1, "");
                        }
                        if (code == "MOON" && tier == Mission.Tier.Explorer && r.ShaftCount > 0) Round13GimliRide(game, r);
                        if (code == "MOON" && tier == Mission.Tier.Cadet) Round13Messenger(game);
                    }
                }
            }
            finally { RestoreSeed(); Mission.Current = Mission.Tier.Cadet; }
            report.AppendLine("\n| Layout | Tier | Route (m) | Shafts down / wanted | Rigs with GIMLI-2's rope | Rigs without a rope | Rover ropes |");
            report.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var row in rows) report.AppendLine(row);
        }

        /// <summary>T3: at a route shaft, USE = the crew-out fade, GIMLI-2's rope clipped on (+10 min), no rope from stock.</summary>
        static void Round13GimliRide(GameBootstrap game, GoldenRoute route)
        {
            var w = game.Winch;
            var rig = w.Rigs.First(x => x.gimliRope);
            var clock = SimClock.Ensure(); double c0 = clock.elapsedSeconds; int ropes0 = w.Ropes;
            var res = RideRig(game, rig, true, out string why);
            Note($"T3 SET1 Explorer: GIMLI-2's rope at {rig.key}: down {res.down:0} s (incl. the fade), up {res.up:0} s, hull {res.hullDown}/{res.hullUp} {why}");
            Check("T3 GIMLI-2's rope: ride down after the crew-out fade", res.downOk ? 1 : 0, 1, 1, "");
            Check("T3 GIMLI-2's rope: no rope from the rover's stock", res.ropesAtBottom, ropes0, ropes0, "");
            Check("T3 GIMLI-2's rope: mission clock +10 min (+ the ride)", (clock.elapsedSeconds - c0) / 60.0, RoverWinch.GimliClipMinutes - 0.1, RoverWinch.GimliClipMinutes + 15, "min");
            Check("T3 GIMLI-2's rope: hull contacts", res.hullDown + res.hullUp, 0, 0, "");
            Check("T3 GIMLI-2's rope: still hanging after the trip (it stays)", rig.gimliRope && rig.RopeHanging ? 1 : 0, 1, 1, "");
            game.Teleport(0);
        }

        /// <summary>T3: at the foot of a rig with no rope, USE sends one of the rover's ropes up (fade), then the ride up.</summary>
        static void Round13Messenger(GameBootstrap game)
        {
            var w = game.Winch; var b = game.Buggy;
            var rig = w.Rigs.FirstOrDefault(x => !x.RopeHanging);
            if (rig == null) { Note("T3 messenger: no rope-less rig on this layout"); return; }
            PlaceRover(game, rig.foot, new float2(1, 0), true);
            b.parkingBrake = true; Step(b, 400);
            int ropes0 = w.Ropes;
            bool ok = w.SendRopeUp(out string why);
            Check("T3 messenger line: accepted at the foot of a rope-less rig", ok ? 1 : 0, 1, 1, "");
            if (!ok) { Note($"T3 messenger refused: {why}"); return; }
            float t = 0; int n = 0;
            while (w.state != RoverWinch.State.Free && t < 600f)
            {
                LunarPhysics.AimGravity(b.transform.position); w.Step(Dt); b.Step(Dt); Physics.Simulate(Dt); t += Dt; n++;
                if (n % 200 == 0) { game.Tunnels.BuildNow(b.transform.position, 120f); Physics.SyncTransforms(); }
            }
            var f = WorldOrigin.WorldToFrame(b.transform.position);
            Note($"T3 messenger at {rig.key}: up in {t:0} s, end {f.y - rig.rimSpot.y:0.0} m vs the lane, ropes {ropes0} -> {w.Ropes}.");
            Check("T3 messenger line: rover back up on the lane", math.abs(f.y - rig.rimSpot.y), 0, 1.5, "m");
            Check("T3 messenger line: the rope reeled back in at the top (stock back)", w.Ropes, ropes0, ropes0, "");
            game.Teleport(0);
        }
    }
}
