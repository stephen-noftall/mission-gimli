using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using MoonBaseAlpha.Buggy;

namespace MoonBaseAlpha.Editor
{
    /// <summary>Round 12: drive ranges (P2), edge assist (S5), shaft rigs (S6) and the other round-12 checks.</summary>
    public static partial class Verification
    {
        /// <summary>Build the world for a tube layout code (SET1 = MOON default seed) and tier.</summary>
        static GameBootstrap BuildWorldCode(string code, Mission.Tier tier)
        {
            WorldSeed.Set(code == "MOON" ? WorldSeed.Default : Mission.SeedFromCode(code), false);
            return BuildWorld(tier);
        }

        static void RestoreSeed() => WorldSeed.Set(WorldSeed.Default, false);

        /// <summary>Scripted hand controller on the flat test world: steps the driver at 50 Hz, physics at 200 Hz.</summary>
        static void DriveFor(BuggyController b, BuggyDriver d, float seconds, System.Func<float, float> v, ref float now, System.Action<float> each = null)
        {
            int steps = Mathf.RoundToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
            {
                if (i % 4 == 0) d.Drive(v(now), 0f, false, 4 * Dt, now);
                LunarPhysics.AimGravity(b.transform.position); b.Step(Dt); Physics.Simulate(Dt);
                now += Dt;
                each?.Invoke(now);
            }
        }

        /// <summary>P1: every rover renderer has an albedo texture (_BaseMap) or is on the allowlist (lenses, foil, lamps).</summary>
        static void RoverTextures()
        {
            NewFlatWorld(0f);
            var b = NewBuggy(Vector3.zero);
            // allowlist: lenses / lamps (emissive glass), foil (the crinkle is its normal map), the gold visor (a coating)
            string[] allow = { "LampLens", "GoldMLI", "SilverMLI", "Rope", "Lamp", "HelmetLamp", "GoldVisor" };
            var plain = new List<string>(); int n = 0;
            foreach (var r in b.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue; n++;
                    bool has = m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null;
                    if (!has && !allow.Any(a => m.name.StartsWith(a))) plain.Add($"{r.name} ({m.name})");
                }
            Note($"P1 rover: {n} renderer materials; plain (no albedo texture, not allowlisted): {(plain.Count == 0 ? "none" : string.Join(", ", plain.Distinct().Take(20)))}.");
            Check("P1 rover renderers without an albedo texture (outside the allowlist)", plain.Distinct().Count(), 0, 0, "");
        }

        static void TestRound12Driving()
        {
            RoverTextures();
            Tuning.RoverSpeed.Value = Tuning.RoverSpeed.Default;   // the game's x1.25 (SLOW = round-11 top speed)
            Tuning.WheelGrip.Value = Tuning.WheelGrip.Default;
            try
            {
                // ---- drive ranges: top speed, single press = no shift, double-tap
                NewFlatWorld(0f);
                GroundMaterial.Provider = _ => Soil.Basalt;
                var b = NewBuggy(Vector3.zero); var d = b.GetComponent<BuggyDriver>();
                Step(b, 400);
                float now = 0f;
                DriveFor(b, d, 40f, _ => 1f, ref now);
                float slow = b.Speed * 3.6f;
                Check("P2 SLOW top speed (round-11 top speed)", slow, 17.5, 19.5, "km/h");
                Check("P2 a single press (held) does not shift", d.RangeChanges, 0, 0, "");
                // double-tap forward: release 0.12 s, press again
                float t0 = now;
                // double-tap forward: release, tap, release, press (two presses within 0.35 s)
                DriveFor(b, d, 60f, t => { float k = t - t0; return k < 0.08f ? 0f : k < 0.16f ? 1f : k < 0.24f ? 0f : 1f; }, ref now);
                Check("P2 double-tap forward -> FAST", b.RangeMode == BuggyController.DriveRange.Fast ? 1 : 0, 1, 1, "");
                float fast = b.Speed * 3.6f;
                Check("P2 FAST top speed (2x SLOW, provisional)", fast, 33, 38.5, "km/h");
                // braking distance from FAST (space)
                float x0 = b.transform.position.z; int steps = 0;
                while (b.Speed > 0.05f && steps < 200 * 60) { if (steps % 4 == 0) d.Drive(0f, 0f, true, 4 * Dt, now); LunarPhysics.AimGravity(b.transform.position); b.Step(Dt); Physics.Simulate(Dt); now += Dt; steps++; }
                float brakeDist = b.transform.position.z - x0;
                Note($"P2 braking from {fast:0.0} km/h (hard brake, basalt, grip x{Tuning.WheelGrip.Value:0}): {brakeDist:0.0} m in {steps * Dt:0.0} s (v^2 / (2 mu g) with mu {0.75f * Tuning.WheelGrip.Value:0.00}: {(fast / 3.6f) * (fast / 3.6f) / (2 * 0.75f * Tuning.WheelGrip.Value * Lunar.Gravity):0} m).");
                Check("P2 braking distance from FAST (the edge assist's look-ahead covers it)", brakeDist, 5, BuggyDriver.SlowDistanceFor(fast / 3.6f) - BuggyDriver.StopDistance, "m");
                // double-tap back while moving forward: SLOW, and no reversing
                DriveFor(b, d, 40f, _ => 1f, ref now);
                float t1 = now, minSpeed = 99f;
                DriveFor(b, d, 0.6f, t => { float k = t - t1; return k < 0.1f ? -1f : k < 0.2f ? 0f : k < 0.3f ? -1f : 0f; }, ref now, _ => minSpeed = Mathf.Min(minSpeed, b.Speed));
                Check("P2 double-tap back -> SLOW", b.RangeMode == BuggyController.DriveRange.Slow ? 1 : 0, 1, 1, "");
                Check("P2 double-tap back at speed does not reverse", minSpeed, 0.3, 99, "m/s");
                GroundMaterial.Provider = null;

                // ---- edge assist: a virtual drop ahead (the game's EdgeDistanceAhead answers this in the world)
                foreach (bool goFast in new[] { false, true })
                {
                    NewFlatWorld(0f);
                    GroundMaterial.Provider = _ => Soil.Regolith;
                    b = NewBuggy(Vector3.zero); d = b.GetComponent<BuggyDriver>();
                    Step(b, 400);
                    float edgeZ = goFast ? 420f : 160f;
                    d.edgeAhead = (p, f) => f.z > 0.5f ? Mathf.Max(0f, edgeZ - p.z - 2.1f) : -1f;   // distance from the rover's nose (~2.1 m ahead of its origin)
                    now = 0f;
                    if (goFast) { d.SetRange(BuggyController.DriveRange.Fast, false); }
                    float topV = 0f;
                    DriveFor(b, d, 140f, _ => 1f, ref now, _ => topV = Mathf.Max(topV, b.Speed));
                    float left = edgeZ - b.transform.position.z - 2.1f;
                    string tag = goFast ? "FAST" : "SLOW";
                    Note($"S5 edge assist from {tag} ({topV * 3.6f:0.0} km/h top): stopped {left:0.0} m from the edge, state {d.Edge}, auto-stops {d.AutoStops}, range now {b.RangeMode}.");
                    Check($"S5 {tag}: auto-stop before the edge (holding forward)", left, 10, BuggyDriver.StopDistance + 1.5, "m");
                    Check($"S5 {tag}: stopped (speed)", Mathf.Abs(b.Speed), 0, 0.1, "m/s");
                    Check($"S5 {tag}: dropped to SLOW near the edge", b.RangeMode == BuggyController.DriveRange.Slow ? 1 : 0, 1, 1, "");
                    // release, press again: creep at walking pace up to the edge
                    DriveFor(b, d, 1.0f, _ => 0f, ref now);
                    float creepTop = 0f;
                    DriveFor(b, d, 9f, _ => 1f, ref now, _ => creepTop = Mathf.Max(creepTop, b.Speed));
                    float left2 = edgeZ - b.transform.position.z - 2.1f;
                    Check($"S5 {tag}: second press creeps on (walking pace)", creepTop, 0.5, BuggyDriver.CreepSpeed + 0.25, "m/s");
                    Check($"S5 {tag}: creeping closes on the edge", left - left2, 3, 20, "m");
                    GroundMaterial.Provider = null;
                }
            }
            finally { Tuning.Neutral(); }
        }

        /// <summary>
        /// S5 in the world: the edge distance on SET1 at a shaft (head-on, on the lane beside it), the pit rim from the
        /// surface, and none on the crane deck.
        /// </summary>
        static void EdgeWorldChecks(GameBootstrap game)
        {
            var net = game.Cave.Network;
            if (!net.IsFlow || net.Flow.Shafts.Count == 0) { Note("S5 world: no shafts."); return; }
            var s = net.Flow.Shafts[0];
            var top = s.top;
            var dir = new Vector3(1, 0, 0);
            var p = WorldOrigin.FrameToWorld((double3)(top - new float3(dir.x, 0, dir.z) * (s.radius + 40f)));
            float dHead = game.EdgeDistanceAhead(p, dir, 120f);
            Check("S5 world: shaft head-on 40 m from its edge", dHead, 37, 41, "m");
            var lane = WorldOrigin.FrameToWorld((double3)(top + new float3(0, 0, s.radius + 7f) - new float3(dir.x, 0, dir.z) * 40f));
            Check("S5 world: driving past on the lane beside the hole (no trigger)", game.EdgeDistanceAhead(lane, dir, 120f), -1, -1, "");
            Check("S5 world: facing away from the hole (no trigger)", game.EdgeDistanceAhead(p, -dir, 120f), -1, -1, "");
            // the pit rim from the plain, 60 m out, facing the centre
            var f = game.Cave.Field; var hf = f.hf;
            var c = new float2(hf.pitCentre.x, hf.pitCentre.y); var outDir = new float2(0.6f, 0.8f);
            var q = c + outDir * (hf.innerA + 60f);
            var pw = WorldOrigin.FrameToWorld(new double3(q.x, hf.Height(q.x, q.y, 0f) + 1f, q.y));
            float dPit = game.EdgeDistanceAhead(pw, new Vector3(-outDir.x, 0, -outDir.y), 120f, out float pitDrop);
            Note($"S5 world: pit rim ahead from 60 m outside the inner wall (major semi-axis): {dPit:0} m, drop {pitDrop:0} m.");
            Check("S5 world: pit rim detected from the plain", dPit, 25, 62, "m");
            Check("S5 world: no assist on the crane deck", game.EdgeDistanceAhead(game.Crane.Platform.position, game.Crane.Platform.transform.forward, 120f), -1, -1, "");
            // talus ramps are no edge: every talus ramp's top, facing down it
            int rampHits = 0, ramps = 0;
            for (int k = 0; k < net.Flow.RampPath.Count; k++)
            {
                var rp = net.Flow.RampPath[k]; if (rp == null) continue; ramps++;
                var a = rp[0]; var bb = rp[math.min(2, rp.Length - 1)];
                var rd = math.normalizesafe(bb.xz - a.xz);
                if (game.EdgeDistanceAhead(WorldOrigin.FrameToWorld((double3)(a + new float3(0, 1, 0))), new Vector3(rd.x, 0, rd.y), 60f) >= 0f) rampHits++;
            }
            Check($"S5 world: no trigger at the top of talus ramps ({ramps})", rampHits, 0, 0, "");
        }

        /// <summary>
        /// S6: every rig on SET1-3: down + up without hull contact on the lip / roof, ride times, the trolley on the span,
        /// restart restores the rig; on SET1 also a rig kit installed by the crew (fade, clock, parked under the trolley)
        /// and a ride on the rover's own rope (stock 3 -> 2 -> 3).
        /// </summary>
        static void TestRound12Rigs()
        {
            var rows = new List<string>();
            try
            {
                foreach (var code in Mission.TestedCodes)
                {
                    string name = Mission.TestedNames[System.Array.IndexOf(Mission.TestedCodes, code)];
                    var game = BuildWorldCode(code, Mission.Tier.Cadet);
                    var w = game.Winch; var b = game.Buggy;
                    Step(b, 400, i => { if (i % 200 == 0) game.Streamer.BuildNow(b.transform.position, b.transform.position); });
                    if (code == "MOON") EdgeWorldChecks(game);
                    RouteLabelCheck(game, name);
                    var rigs = w.Rigs.ToList();
                    Note($"{name} ({code}): {rigs.Count} rigs (advance team), ropes {w.Ropes}, rig kits {w.RigKits}, rope length {w.RopeLength:0} m.");
                    Check($"{name}: rope stock covers the tier routes' rigged shafts + 1", w.Ropes, 1 + 0, 99, "");
                    foreach (var r in rigs)
                    {
                        // trolley on the span: park and hole points on the anchor line, between the anchors
                        float onA = DistToSeg((float3)r.park, (float3)r.anchorA, (float3)r.anchorB), onB = DistToSeg((float3)r.roller, (float3)r.anchorA, (float3)r.anchorB);
                        Check($"{name} rig {r.key}: trolley stays on the span", math.max(onA, onB), 0, 0.5, "m");
                        Check($"{name} rig {r.key}: span height above the lane", (float)(r.park.y - r.rimSpot.y), 5, 6.5, "m");
                        int ropesBefore = w.Ropes;
                        var res = RideRig(game, r, true, out string why);
                        rows.Add($"| {name} | {r.key} | {r.drop:0} | {res.down:0} | {res.up:0} | {res.hullDown} / {res.hullUp} | {res.endUp:0.0} | {why} |");
                        Check($"{name} rig {r.key}: ride down reached the bottom", res.downOk ? 1 : 0, 1, 1, "");
                        Check($"{name} rig {r.key}: ride up back on the lane", res.upOk ? 1 : 0, 1, 1, "");
                        Check($"{name} rig {r.key}: hull contacts on the lip / walls / roof while hanging", res.hullDown + res.hullUp, 0, 0, "");
                        // round 13 (T3): the advance team's rigs carry no rope; the rover's own goes down and comes back up
                        if (r.gimliRope) Check($"{name} rig {r.key}: GIMLI-2's rope still hanging after the trip", r.RopeHanging ? 1 : 0, 1, 1, "");
                        else Check($"{name} rig {r.key}: own rope used and reeled back in (no rope left hanging)", !r.RopeHanging && w.Ropes == ropesBefore && res.ropesAtBottom == ropesBefore - 1 ? 1 : 0, 1, 1, "");
                    }
                    // restart restores the advance team's rigs
                    int n0 = w.RiggedCount;
                    game.Teleport(0);
                    Check($"{name}: restart restores the rigs (ropes only where GIMLI-2 went down)", w.RiggedCount == n0 && w.Rigs.All(x => x.preRigged && (x.gimliRope ? x.trolleyT > 0.99f && x.RopeHanging : x.trolleyT < 0.01f && !x.RopeHanging)) ? 1 : 0, 1, 1, "");
                    if (code == "MOON" && game.Cave.Network.Flow.Shafts.Count > 0)
                    {
                        // a rig kit at a shaft with no rig: clear the advance team's rigs, drive to the edge, install
                        w.ClearAllLines();
                        var s = game.Cave.Network.Flow.Shafts[0];
                        var clock = SimClock.Ensure(); double c0 = clock.elapsedSeconds;
                        // stand 8 m from the edge on solid floor
                        PlaceRover(game, ProbeLane(game, s), new float2(1, 0), true);
                        bool can = w.CanInstall(out string whyNot);
                        Note($"S6 rig kit at {s.top}: can install: {can} {whyNot}");
                        bool ok = w.InstallRig(instant: true);
                        Check("S6 rig kit: installed (kits 2 -> 1)", ok ? w.RigKits : -1, 1, 1, "");
                        Check("S6 rig kit: the rig appears while the screen is black (fade alpha at spawn)", w.LastSpawnAlpha, 1, 1, "");
                        Check("S6 rig kit: mission clock advanced", (clock.elapsedSeconds - c0) / 60.0, RoverWinch.RigSetupMinutes - 0.1, RoverWinch.RigSetupMinutes + 0.1, "min");
                        var nr = w.Rigs.FirstOrDefault();
                        if (nr != null)
                        {
                            Step(b, 300);
                            float park = (float)math.distance(WorldOrigin.WorldToFrame(b.transform.position).xz, nr.rimSpot.xz);
                            Check("S6 rig kit: rover parked under the trolley", park, 0, 2.0, "m");
                            int ropes0 = w.Ropes;
                            var res = RideRig(game, nr, false, out string why2);
                            rows.Add($"| {name} (own kit + rope) | {nr.key} | {nr.drop:0} | {res.down:0} | {res.up:0} | {res.hullDown} / {res.hullUp} | {res.endUp:0.0} | {why2} |");
                            Check("S6 own rope: down + up without hull contact", res.downOk && res.upOk ? res.hullDown + res.hullUp : 99, 0, 0, "");
                            Check("S6 own rope: stock back after reeling in (3 -> 2 -> 3)", res.ropesAtBottom == ropes0 - 1 && w.Ropes == ropes0 ? 1 : 0, 1, 1, "");
                        }
                        game.Teleport(0);
                    }
                }
            }
            finally { RestoreSeed(); Mission.Current = Mission.Tier.Cadet; }
            report.AppendLine("\n| Layout | Rig | Drop (m) | Down (s) | Up (s) | Hull contacts down / up | End height vs lane (m) | Note |");
            report.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var r in rows) report.AppendLine(r);
        }

        /// <summary>
        /// S7: the GIMLI-2 marker along the Cadet route, every 10 m: no "level", the distance counts down smoothly to the
        /// seal (no jumps where the route passes near itself), and it comes back to the same value after a trip off the
        /// route (a winch ride down a shaft and back up).
        /// </summary>
        static void RouteLabelCheck(GameBootstrap game, string name)
        {
            var r = game.Route; var dir = game.Director;
            if (r == null || !r.Valid) { Fail($"{name}: no route"); return; }
            Note($"{name} Cadet route: {r.Length:0} m{(r.CaveIn ? " (cave-in seal part-way along a dead-end tube)" : " (to a dead end)")}.");
            Check($"S8 {name}: Cadet route length (~0.6-0.8 km; a real dead end nearer than that is kept)", r.Length, 400, 900, "m");
            float prev = float.MaxValue, worstUp = 0; bool level = false; var labels = new List<string>(); string last = null; float lastRem = -1;
            var P = r.points; float acc = 0; int samples = 0; float midRem = -1; float3 mid = default;
            for (int k = 1; k < P.Count; k++)
            {
                float l = math.distance(P[k - 1], P[k]);
                for (float s = 0; s < l; s += 10f)
                {
                    var q = math.lerp(P[k - 1], P[k], s / l) + new float3(0, 1.5f, 0);
                    if (!dir.GuideTarget(WorldOrigin.FrameToWorld((double3)q), out _, out string lab)) continue;
                    if (lab.Contains("level")) level = true;
                    float rem = dir.GuideRemaining;
                    if (acc + s > 60f && prev < float.MaxValue) worstUp = math.max(worstUp, rem - prev);
                    prev = rem; samples++; last = lab; lastRem = rem;
                    if (samples % 15 == 0) labels.Add(lab);
                    if (midRem < 0 && acc + s > r.Length * 0.5f) { midRem = rem; mid = q; }
                }
                acc += l;
            }
            Note($"S7 {name}: {samples} samples along the route, labels: {string.Join(" | ", labels)} ... last \"{last}\".");
            Check($"S7 {name}: no \"level N\" in the GIMLI-2 label", level ? 1 : 0, 0, 0, "");
            Check($"S7 {name}: the remaining distance counts down (largest step up between 10 m samples)", worstUp, 0, 3, "m");
            Check($"S7 {name}: remaining distance at the stop point", lastRem, 0, 30, "m");
            // a trip off the route (100 m down) and back: the same value again
            if (midRem >= 0)
            {
                dir.GuideTarget(WorldOrigin.FrameToWorld((double3)(mid - new float3(0, 100, 0))), out _, out _);
                dir.GuideTarget(WorldOrigin.FrameToWorld((double3)mid), out _, out _);
                Check($"S7 {name}: back on the route after a trip off it, the same distance", math.abs(dir.GuideRemaining - midRem), 0, 2, "m");
            }
            Check($"S7 {name}: label format above 1 km", MissionDirector.Dist(1250f) == "1.25 km" && MissionDirector.Dist(882f) == "882 m" ? 1 : 0, 1, 1, "");
        }

        /// <summary>
        /// S3: from the pit floor (sky shown) looking down both tube mouths to the stream edge, and from 60 m in looking back
        /// at the pit: no white tube end, no light specks. Counts "light" pixels (all channels &gt; 0.5) in the middle of the
        /// frame (where the far end of the tube is), with the pit-mode shell off (before) and on (the game).
        /// </summary>
        static void TestRound12TubeEnds()
        {
            try
            {
                var game = BuildWorldCode("MOON", Mission.Tier.Cadet);
                var cam = game.cam; var net = game.Cave.Network; var f = game.Cave.Field;
                GimliBowl(game);
                AllLamps(game, true);
                int Light(Texture2D tex, float wx, float wy, float cy)
                {
                    int n = 0;
                    for (int y = (int)(tex.height * (cy - wy)); y < tex.height * (cy + wy); y++)
                        for (int x = (int)(tex.width * (0.5f - wx)); x < tex.width * (0.5f + wx); x++)
                        { var c = tex.GetPixel(x, y); if (c.r > 0.5f && c.g > 0.5f && c.b > 0.5f) n++; }
                    return n;
                }
                // isolated light dots in darkness: a pixel > 0.3 whose surrounding ring (radius 3-4 px) averages < 0.04
                int Specks(Texture2D tex)
                {
                    int n = 0; var px = tex.GetPixels(); int W = tex.width, H = tex.height;
                    float G(int x, int y) => px[Mathf.Clamp(y, 0, H - 1) * W + Mathf.Clamp(x, 0, W - 1)].grayscale;
                    for (int y = 4; y < H - 4; y++)
                        for (int x = 4; x < W - 4; x++)
                        {
                            if (G(x, y) < 0.3f) continue;
                            float ring = 0; int k = 0;
                            for (int d = -4; d <= 4; d += 2) { ring += G(x + d, y - 4) + G(x + d, y + 4) + G(x - 4, y + d) + G(x + 4, y + d); k += 4; }
                            if (ring / k < 0.04f) n++;
                        }
                    return n;
                }
                var landing = new double3(f.landingSpot.x, f.pitFloor + 1.5f + 1.8f, f.landingSpot.y);
                foreach (var (tag, d2) in new[] { ("downstream", net.dirU), ("upstream", -net.dirU) })
                {
                    var pos = WorldOrigin.FrameToWorld(landing);
                    game.Streamer.BuildNow(pos, pos); game.Tunnels.BuildNow(pos); game.FarTunnels.BuildNow(pos); game.Tunnels.PushShaderGlobals();
                    Physics.SyncTransforms();
                    // aim at the tube floor 150 m along, eye height
                    var tgt = WorldOrigin.FrameToWorld(new double3(net.origin.x + d2.x * 200f, net.mouthFloor.y + 3f, net.origin.y + d2.y * 200f));
                    cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(tgt - pos));
                    game.Sky.UpdateVisibility(); game.Shell.Refresh();
                    Check($"S3 pit floor, {tag}: sky shown and the pit-mode shell on", game.Sky.SkyShown && game.Shell.PitMode && game.Shell.Active ? 1 : 0, 1, 1, "");
                    // before: no pit-mode shell (as round 11)
                    var keep = game.Shell.pitClipY; game.Shell.pitClipY = null; game.Shell.Refresh();
                    var t0 = Render(game, System.IO.Path.Combine(OutDir, $"50_pit_tube_end_{tag}_before.png"), true, true);
                    int before = Light(t0, 0.12f, 0.12f, 0.5f); Object.DestroyImmediate(t0);
                    game.Shell.pitClipY = keep; game.Shell.Refresh();
                    var t1 = Render(game, System.IO.Path.Combine(OutDir, $"50_pit_tube_end_{tag}.png"), true, false);
                    int after = Light(t1, 0.12f, 0.12f, 0.5f), sp = Specks(t1); Object.DestroyImmediate(t1);
                    Note($"S3 pit floor looking {tag} down the tube: light pixels at the far end {before} before (no pit shell) -> {after}; isolated specks {sp}.");
                    Check($"S3 pit floor, {tag}: far tube end not white (light pixels in the middle of the frame)", after, 0, 30, "px");
                    // sky still visible straight up (the shell is clipped above the rim plane)
                    cam.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
                    game.Shell.Refresh();
                    var t2 = Render(game, System.IO.Path.Combine(OutDir, $"50_pit_up_{tag}.png"), true, false);
                    var px = t2.GetPixel(t2.width / 2, t2.height / 2); Object.DestroyImmediate(t2);
                    Note($"S3 pit floor looking up: centre pixel {px} (sky / Earth / stars, not the shell).");
                }
                // 60 m in, looking back at the pit (tube mode, shell + terrain culling): specks on the pit walls
                {
                    var fp = game.Cave.TubeFloorPoint(60f, out float2 dir);
                    var pos = WorldOrigin.FrameToWorld(fp) + Vector3.up * 1.8f;
                    game.Streamer.BuildNow(pos, pos); game.Tunnels.BuildNow(pos); game.FarTunnels.BuildNow(pos); Physics.SyncTransforms();
                    cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(new Vector3(-dir.x, 0.05f, -dir.y)));
                    game.Sky.UpdateVisibility(); game.Shell.Refresh();
                    AllLamps(game, false);
                    var t3 = Render(game, System.IO.Path.Combine(OutDir, "51_tube_look_back_pit.png"), true, true);
                    int sp = Specks(t3); Object.DestroyImmediate(t3);
                    Note($"S3 60 m in, looking back at the pit (lamps off, settled exposure): isolated light specks {sp}; shell active {game.Shell.Active}, pit mode {game.Shell.PitMode}.");
                    Check("S3 looking back at the pit from 60 m in: isolated light specks", sp, 0, 40, "px");
                    AllLamps(game, true);
                }
            }
            finally { RestoreSeed(); }
        }

        /// <summary>
        /// S2: the bowl just past GIMLI-1 on the Cadet drive (SET1; where the scripted driver and the play-tester got stuck,
        /// ~(184, 43) m from the pit centre): from 40 m back, full throttle at the crane pad, grip x1 vs the default x2.
        /// </summary>
        static void GimliBowl(GameBootstrap game)
        {
            var b = game.Buggy; var hf = game.Height;
            var stuck = new double3(hf.pitCentre.x + 184, 0, hf.pitCentre.y + 43);
            var padF = WorldOrigin.WorldToFrame(game.Crane.PadWorld);
            var s0 = game.StartFrame;
            var inDir = math.normalizesafe((stuck - s0).xz);
            foreach (float grip in new[] { 1f, Tuning.WheelGrip.Default })
            {
                Tuning.WheelGrip.Value = grip;
                var st = stuck - new double3(inDir.x, 0, inDir.y) * 40.0;
                st.y = hf.Height(st.x, st.z, 0f) + 1.0;
                var w = WorldOrigin.FrameToWorld(st);
                game.Streamer.BuildNow(w, w); Physics.SyncTransforms();
                var up = -TerrainFrame.DownAt(w);
                b.PlaceAt(w, Quaternion.LookRotation(new Vector3((float)inDir.x, 0, (float)inDir.y), up));
                b.throttle = 0; b.brake = 0; b.parkingBrake = true; Step(b, 300); b.parkingBrake = false;
                float t = 0, maxPast = -99; int spins = 0;
                for (int k = 0; k < 200 * 90; k++)
                {
                    var fr = WorldOrigin.WorldToFrame(b.transform.position);
                    var to = WorldOrigin.FrameToWorld(new double3(padF.x, fr.y, padF.z)) - b.transform.position;
                    float ang = Vector3.SignedAngle(Vector3.ProjectOnPlane(b.transform.forward, up), Vector3.ProjectOnPlane(to, up), up);
                    b.steering = Mathf.Clamp(ang / 40f, -0.8f, 0.8f); b.throttle = 1f;
                    LunarPhysics.AimGravity(b.transform.position); b.Step(Dt); Physics.Simulate(Dt); t += Dt;
                    if (k % 200 == 0) { game.Streamer.BuildNow(b.transform.position, b.transform.position); if (b.wheels[0].limit == "grip") spins++; }
                    maxPast = Mathf.Max(maxPast, (float)math.dot((fr - stuck).xz, inDir));
                    if (maxPast > 40f) break;
                }
                Note($"S2 GIMLI-1 bowl, grip x{grip:0}: got {maxPast:0} m past the stuck point in {t:0} s (wheelspin seconds {spins}).");
                if (grip > 1f) Check("S2 GIMLI-1 bowl: climbed out with a run-up at the default grip (x2)", maxPast, 25, 1000, "m");
            }
            Tuning.Neutral();
        }

        static void TestRound12Hud()
        {
            // life support: drain rates vs the science report (xEMU ~8 h of EVA work, 29.6 kPa)
            {
                var ls = new MoonBaseAlpha.Crew.LifeSupport();
                for (int i = 0; i < 3600; i++) ls.Step(1f, 0, false);   // CDR walking, MS seated, 1 h
                var cdr = ls.crew[0]; var ms = ls.crew[1];
                float walkKgH = (1f - cdr.o2) * MoonBaseAlpha.Crew.LifeSupport.O2Kg, seatKgH = (1f - ms.o2) * MoonBaseAlpha.Crew.LifeSupport.O2Kg;
                Note($"P6 life support after 1 h: CDR (walking) O2 {cdr.o2 * 100:0.0}% ({walkKgH * 1000:0} g/h, {cdr.O2HoursLeft:0.0} h left), MS (seated) {ms.o2 * 100:0.0}% ({seatKgH * 1000:0} g/h); suit battery {cdr.battery * 100:0.0}%; HR {cdr.hr:0} / {ms.hr:0}; CO2 {cdr.co2KPa:0.00} / {ms.co2KPa:0.00} kPa.");
                Check("P6 suit O2 endurance walking (~8 h, xEMU)", MoonBaseAlpha.Crew.LifeSupport.O2Kg / walkKgH, 7, 9.5, "h");
                Check("P6 O2 use seated (~0.84 kg/day at rest = 35 g/h; seated light work ~45 g/h)", seatKgH * 1000, 35, 55, "g/h");
                Check("P6 suit pressure (4.3 psi)", cdr.pressureKPa, 29.4, 29.8, "kPa");
                Check("P6 suit battery after 1 h (~8 h)", (1f - cdr.battery) * 100, 11, 14, "%/h");
                Check("P6 CO2 partial pressure under the 1 kPa limit", Mathf.Max(cdr.co2KPa, ms.co2KPa), 0.05, 0.6, "kPa");
            }
            // the log: order and cap
            {
                MessageLog.Reset();
                for (int i = 0; i < MessageLog.Cap + 15; i++) MessageLog.Add($"line {i}", MessageLog.Kind.System);
                var e = MessageLog.Entries;
                Check("P5 log caps its length", e.Count, MessageLog.Cap, MessageLog.Cap, "");
                Check("P5 log keeps order (newest last)", e[e.Count - 1].text == $"line {MessageLog.Cap + 14}" && e[0].text == "line 15" ? 1 : 0, 1, 1, "");
                MessageLog.AddOnce("edge", "EDGE AHEAD", MessageLog.Kind.Warning); MessageLog.AddOnce("edge", "EDGE AHEAD", MessageLog.Kind.Warning);
                Check("P5 a standing warning is logged once per approach", e.Count(x => x.text == "EDGE AHEAD"), 1, 1, "");
                MessageLog.Clear("edge"); MessageLog.AddOnce("edge", "EDGE AHEAD", MessageLog.Kind.Warning);
                Check("P5 ... and again on the next approach", e.Count(x => x.text == "EDGE AHEAD"), 2, 2, "");
                MessageLog.Reset();
            }
            // the CURRENT STEP line through the beats (SET1, Cadet)
            try
            {
                var game = BuildWorldCode("MOON", Mission.Tier.Cadet);
                var dir = game.Director;
                dir.Begin();
                Step(game.Buggy, 200);
                string s0 = dir.CurrentStep;
                game.Teleport(1); Step(game.Buggy, 200, i => game.Crane.Step(Dt));
                string s1 = dir.CurrentStep;
                game.Teleport(2); Step(game.Buggy, 200); TickStory(game, 0.2f);
                game.Director.GuideTarget(game.cam.transform.position, out _, out _);
                string s2 = dir.CurrentStep;
                game.Teleport(3); Step(game.Buggy, 200); TickStory(game, 0.5f);
                string s3 = dir.CurrentStep;
                Note($"P5 CURRENT STEP: start \"{s0}\"; at the crane \"{s1}\"; tube \"{s2}\"; at GIMLI-2 \"{s3}\".");
                Check("P5 step text at every beat (start, crane, tubes, GIMLI-2)", new[] { s0, s1, s2, s3 }.Count(x => !string.IsNullOrEmpty(x)), 4, 4, "");
                Check("P5 step at the start says where to go (the crane)", s0 != null && s0.Contains("crane") ? 1 : 0, 1, 1, "");
                Check("P5 step in the tubes follows GIMLI-2", s2 != null && s2.Contains("GIMLI-2") ? 1 : 0, 1, 1, "");
            }
            finally { RestoreSeed(); }
        }

        static float DistToSeg(float3 p, float3 a, float3 b)
        {
            var ab = b - a; float t = math.saturate(math.dot(p - a, ab) / math.max(1e-6f, math.lengthsq(ab)));
            return math.distance(p, a + ab * t);
        }

        /// <summary>A floor point on the lane beside a shaft, 8 m from its edge (for the rig-kit test).</summary>
        static double3 ProbeLane(GameBootstrap game, MoonBaseAlpha.Cave.FlowNetwork.Shaft s)
        {
            var net = game.Cave.Network;
            foreach (var ang in Enumerable.Range(0, 24).Select(i => i * 15f))
            {
                var d = new float2(math.cos(math.radians(ang)), math.sin(math.radians(ang)));
                var q = s.top + new float3(d.x, 0, d.y) * (s.radius + 8f);
                float y = net.FloorY(q + new float3(0, 2, 0), 4f, 6f);
                if (!float.IsNaN(y) && math.abs(y - s.top.y) < 2f) return new double3(q.x, y, q.z);
            }
            return (double3)(s.top + new float3(s.radius + 8f, 0, 0));
        }

        struct RideResult { public float down, up, endUp; public int hullDown, hullUp, ropesAtBottom; public bool downOk, upOk; }

        static RideResult RideRig(GameBootstrap game, RoverWinch.Rig r, bool place, out string why)
        {
            var res = new RideResult(); why = "";
            var w = game.Winch; var b = game.Buggy;
            if (place) PlaceRover(game, r.rimSpot, math.normalizesafe(r.top.xz - (float2)r.rimSpot.xz), true);
            b.parkingBrake = true; Step(b, 400);
            if (!w.RideDown(out why)) { why = "ride down refused: " + why; return res; }
            int hullMask = ~((1 << 2) | (1 << b.gameObject.layer));
            float Run(System.Func<bool> done, float maxT, ref int hull)
            {
                float t = 0; int n = 0;
                while (!done() && t < maxT)
                {
                    LunarPhysics.AimGravity(b.transform.position); w.Step(Dt); b.Step(Dt); Physics.Simulate(Dt); t += Dt; n++;
                    if (n % 200 == 0) { game.Tunnels.BuildNow(b.transform.position, 120f); Physics.SyncTransforms(); }
                    if (n % 20 == 0)
                    {
                        int grounded = 0; foreach (var wh in b.wheels) grounded += wh.grounded ? 1 : 0;
                        bool hanging = w.Attached && grounded == 0;
                        if (hanging && Physics.OverlapBox(b.transform.TransformPoint(new Vector3(0, 0.8f, -0.2f)), new Vector3(1.05f, 0.65f, 2.0f), b.transform.rotation, hullMask, QueryTriggerInteraction.Ignore).Length > 0) hull++;
                        // cage / roof
                        if (hanging && Physics.CheckSphere(b.transform.TransformPoint(RoverWinch.CageHook), 0.25f, hullMask, QueryTriggerInteraction.Ignore)) hull++;
                    }
                }
                return t;
            }
            res.down = Run(() => w.state == RoverWinch.State.Free, 600f, ref res.hullDown);
            var fb = WorldOrigin.WorldToFrame(b.transform.position);
            res.downOk = w.state == RoverWinch.State.Free && fb.y < r.top.y - r.drop * 0.5f;
            res.ropesAtBottom = w.Ropes;
            Step(b, 300);
            if (!w.RideUp()) { why += " ride up refused"; return res; }
            res.up = Run(() => w.state == RoverWinch.State.Free, 600f, ref res.hullUp);
            var fu = WorldOrigin.WorldToFrame(b.transform.position);
            res.endUp = (float)(fu.y - r.rimSpot.y);
            res.upOk = w.state == RoverWinch.State.Free && math.abs(res.endUp) < 1.5f && Vector3.Angle(b.transform.up, -TerrainFrame.DownAt(b.transform.position)) < 20f;
            return res;
        }
    }
}
