using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Mathematics;
using UnityEditor.SceneManagement;
using UnityEngine;
using MoonBaseAlpha.Base;
using MoonBaseAlpha.Buggy;
using MoonBaseAlpha.Cave;
using MoonBaseAlpha.Crew;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha.Editor
{
    /// <summary>The GIMLI-2 expedition checks (round 9): dialogue data, golden routes, triggers, drill, debrief, timing.</summary>
    public static partial class Verification
    {
        static GameBootstrap BuildWorld(Mission.Tier tier)
        {
            Mission.Current = tier;
            EditorSceneManager.OpenScene("Assets/_Project/Scenes/Main.unity");
            var game = UnityEngine.Object.FindAnyObjectByType<GameBootstrap>();
            game.Initialise();
            Radio.Clear();
            return game;
        }

        /// <summary>Radio + mission director time without physics (the rover stands still).</summary>
        static void TickStory(GameBootstrap game, float seconds, float dt = 0.05f)
        {
            for (float t = 0; t < seconds; t += dt) { Radio.Tick(dt); game.Director.Tick(dt); }
        }

        static void PlaceRover(GameBootstrap game, double3 frame, float2 dir, bool underground)
        {
            var w = WorldOrigin.FrameToWorld(frame);
            game.Streamer.BuildNow(w, w);
            if (underground) game.Tunnels.BuildNow(w, 120f);
            Physics.SyncTransforms();
            var up = -TerrainFrame.DownAt(w);
            if (Physics.Raycast(w + up * 6f, -up, out var hit, 20f, ~(1 << 2), QueryTriggerInteraction.Ignore)) w = hit.point;
            game.Buggy.PlaceAt(w + up * 0.6f, Quaternion.LookRotation(new Vector3(dir.x, 0, dir.y), up));
            game.Buggy.throttle = 0; game.Buggy.brake = 1; game.Buggy.steering = 0;
            Physics.SyncTransforms();
            Step(game.Buggy, 300);
        }

        static float WorstOverlap()
        {
            float worst = -1000f;
            for (int i = 1; i < Radio.History.Count; i++) worst = Mathf.Max(worst, Radio.History[i - 1].start + Radio.History[i - 1].dur - Radio.History[i].start);
            return worst;
        }

        static bool InOrder(params string[] ids)
        {
            int prev = -1;
            foreach (var id in ids) { int i = Radio.Spoken.IndexOf(id); if (i <= prev) return false; prev = i; }
            return true;
        }

        // ------------------------------------------------------------------ item 1: dialogue data

        static void TestDialog()
        {
            Dialog.DirOverride = null; Dialog.Reload();
            // the recording script for voice actors / TTS, generated from the CSV
            var script = Dialog.RecordingScript();
            Directory.CreateDirectory("Docs");
            File.WriteAllText("Docs/dialog-recording-script.md", script);
            int rows = script.Split('\n').Count(l => l.StartsWith("| `"));
            Check("Dialogue: Docs/dialog-recording-script.md lists every line of dialog.csv", rows, Dialog.Entries.Count, Dialog.Entries.Count, "lines");
            Note($"Dialogue table: {Dialog.Entries.Count} lines.");

            // every ID the code uses exists (literals in Radio calls / the director; tiered IDs; battery bands)
            var used = new SortedSet<string>();
            var rx = new Regex("\"((?:b[1-5]|d|t|s|crane|hard|bump|winch|fall)_[a-z0-9_]+)\"");
            foreach (var file in Directory.GetFiles("Assets/_Project/Scripts/Runtime", "*.cs", SearchOption.AllDirectories))
                foreach (Match m in rx.Matches(File.ReadAllText(file))) used.Add(m.Groups[1].Value);
            var missing = new List<string>();
            foreach (var id in used)
            {
                if (Dialog.Has(id)) continue;
                // a tier base ID (Mission.Tiered): all three recordings must exist
                if (Mission.Tiers.All(t => Dialog.Has(id + "_" + t.suffix))) continue;
                missing.Add(id);
            }
            for (int band = 10; band <= 90; band += 10) if (!Dialog.Has($"s_batt_{band}")) missing.Add($"s_batt_{band}");
            Check($"Dialogue: every ID the code uses is in dialog.csv ({used.Count} IDs + 9 battery bands)", missing.Count, 0, 0, "missing");
            if (missing.Count > 0) Note("Missing IDs: " + string.Join(", ", missing));

            // a recording plays and sets the subtitle time; a missing file falls back to text
            var dir = Path.Combine(Path.GetTempPath(), "mba_dialog_test");
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            File.WriteAllText(Path.Combine(dir, "dialog.csv"), "id,speaker,text,notes\ntest_voice,SPEC,\"Testing, one two.\",test\ntest_text,CMDR,\"No recording for this one.\",test\ntest_houston,HOUSTON,\"Copy.\",test\n");
            var samples = new float[44100 * 3 / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = 0.3f * Mathf.Sin(2 * Mathf.PI * 220 * i / 44100f);
            Dialog.WriteWav(Path.Combine(dir, "test_voice.wav"), samples, 44100);
            Dialog.WriteWav(Path.Combine(dir, "test_houston.wav"), samples, 44100);
            Dialog.DirOverride = dir; Dialog.Reload();
            try
            {
                Radio.Clear();
                Radio.Say("test_voice");
                Radio.Tick(0.05f);
                var l = Radio.Shown.FirstOrDefault(x => x.id == "test_voice");
                Check("Dialogue: a .wav recording is loaded for its line", l.clip != null ? l.clip.length : -1, 1.49, 1.51, "s");
                Check("Dialogue: the subtitle stays up for the clip length + 1 s", l.show, 2.49, 2.51, "s");
                Radio.Clear();
                Radio.Say("test_text");
                Radio.Tick(0.05f);
                var lt = Radio.Shown.FirstOrDefault(x => x.id == "test_text");
                Check("Dialogue: no recording -> text only (no clip)", lt.id == "test_text" && lt.clip == null ? 1 : 0, 1, 1, "");
                Check("Dialogue: text-only subtitle time (today's timing)", lt.show, Radio.ShowFor, Radio.ShowFor + 0.01, "s");
                Radio.Clear();
                Radio.FromEarth("test_houston");
                Radio.Tick(0.05f);
                Check("Dialogue: Houston waits for the light time", Radio.Shown.Count, 0, 0, "lines shown");
                for (int i = 0; i < 100; i++) Radio.Tick(0.05f);
                var lh = Radio.Shown.FirstOrDefault(x => x.id == "test_houston");
                Check("Dialogue: Houston recording framed by Quindar tones (clip + 2 x 0.25 s)", lh.dur, 1.99, 2.01, "s");
                // one queue: two lines queued together never overlap
                Radio.Clear();
                Radio.Say("test_voice"); Radio.Say("test_text"); Radio.Say("test_voice");
                for (int i = 0; i < 400; i++) Radio.Tick(0.05f);
                Check("Dialogue: three queued lines, all said, never at once (worst overlap)", Radio.History.Count == 3 ? WorstOverlap() : 99, -1000, 0.001, "s");
            }
            finally
            {
                Dialog.DirOverride = null; Dialog.Reload(); Radio.Clear();
            }
            // seed codes
            int bad = 0;
            var rnd = new System.Random(7);
            for (int i = 0; i < 200; i++)
            {
                var c = new string(Enumerable.Range(0, 4).Select(_ => Mission.Alphabet[rnd.Next(36)]).ToArray());
                if (Mission.CodeFromSeed(Mission.SeedFromCode(c)) != c) bad++;
            }
            Check("Mission seed: 4-character code -> world seed -> the same code (200 random codes)", bad, 0, 0, "mismatches");
        }

        // ------------------------------------------------------------------ item 3: golden routes on bare networks

        /// <summary>Real floor along the route polyline every ~4 m: fraction with air (a tube there) and the worst 8 m slope.</summary>
        static void RouteFloor(TunnelNetwork net, GoldenRoute r, out float airFrac, out float worstDeg, out float3 worstAt, out List<string> nanAt)
        {
            // follow the floor like a wheel would: search a few metres above / below the last floor found (a fixed
            // window around the nominal height jumps to another tube's floor where the route passes under a thin roof)
            var pts = new List<float3>(); var ys = new List<float>(); var brk = new HashSet<int>();
            float prevY = float.NaN;
            for (int k = 0; k < r.points.Count - 1; k++)
            {
                var a = r.points[k]; var b = r.points[k + 1];
                // round 13 (T4): a shaft drop is ridden on the rig, not driven: no floor samples, no slope across it
                if (r.IsShaftSegment(k)) { brk.Add(pts.Count); prevY = float.NaN; continue; }
                int n = Mathf.Max(1, Mathf.CeilToInt(math.distance(a.xz, b.xz) / 4f));
                for (int s = 0; s < n; s++)
                {
                    var q = math.lerp(a, b, s / (float)n);
                    float y = float.NaN;
                    if (!float.IsNaN(prevY) && math.abs(prevY - q.y) < 14f)
                    {
                        var qq = new float3(q.x, prevY, q.z);
                        y = net.FloorY(qq, 3f, 6f);
                        if (float.IsNaN(y)) y = net.FloorY(qq, 6f, 9f);
                    }
                    if (float.IsNaN(y)) y = net.FloorY(q, 10f, 16f);
                    pts.Add(q); ys.Add(y);
                    if (!float.IsNaN(y)) prevY = y;
                }
            }
            nanAt = pts.Where((p, i) => float.IsNaN(ys[i])).Take(3).Select(p => $"({p.x:0},{p.y:0},{p.z:0})").ToList();
            int air = ys.Count(y => !float.IsNaN(y));
            airFrac = ys.Count > 0 ? air / (float)ys.Count : 0;
            worstDeg = 0; worstAt = default;
            for (int i = 0; i + 2 < ys.Count; i++)
            {
                if (float.IsNaN(ys[i]) || float.IsNaN(ys[i + 2])) continue;
                if (brk.Contains(i + 1) || brk.Contains(i + 2)) continue;
                float h = math.distance(pts[i].xz, pts[i + 2].xz);
                if (h < 4f) continue;
                float deg = math.degrees(math.atan(math.abs(ys[i + 2] - ys[i]) / h));
                if (deg > worstDeg) { worstDeg = deg; worstAt = pts[i]; }
            }
        }

        static void GoldenRoutes(GameBootstrap game, Mission.Tier tier)
        {
            var baseNet = game.Cave.Network;
            int L = Mission.Tiers[(int)tier].targetLevel;
            var seeds = baseNet.IsFlow ? Mission.TestedCodes.Select(c => Mission.SeedFromCode(c)).ToArray() : new[] { baseNet.seed, Mission.SeedFromCode("MOON"), Mission.SeedFromCode("GIML") };
            int ok = 0; float worstAll = 0, airMin = 1;
            var sb = new StringBuilder();
            foreach (var seed in seeds)
            {
                var net = new TunnelNetwork(seed, baseNet.origin, baseNet.dirU, game.Cave.Field.rim, baseNet.level0Floor, math.distance(baseNet.mouthFloor.xz, baseNet.origin)) { surfaceAt = baseNet.surfaceAt, maxLevels = baseNet.maxLevels };
                if (baseNet.IsFlow) net.UseFlowLayout(Mission.CodeFromSeed(seed) ?? "MOON");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = net.GoldenPath(L);
                long ms = sw.ElapsedMilliseconds;
                if (!r.Valid) { sb.Append($"[seed {Mission.CodeFromSeed(seed) ?? seed.ToString()}: NO ROUTE] "); continue; }
                RouteFloor(net, r, out float airF, out float worst, out var wAt, out var nanAt);
                bool good = (baseNet.IsFlow ? r.verified : r.nodes[r.nodes.Count - 1].x == L) && airF > 0.97f && worst <= 30f;
                if (good) ok++;
                worstAll = Mathf.Max(worstAll, worst); airMin = Mathf.Min(airMin, airF);
                int ramps = 0; for (int k = 1; k < r.nodes.Count; k++) if (r.nodes[k].x > r.nodes[k - 1].x) ramps++;
                sb.Append($"[seed {Mission.CodeFromSeed(seed) ?? seed.ToString()}: {(r.verified ? "verified" : "UNVERIFIED")} after {r.attempts} searches, {r.Length:0} m, {r.ShaftCount}/{r.ShaftsWanted} shafts down, {r.nodes.Count} junctions, {ramps} level ramps, seal {r.sealHalfWidth * 2:0} m wide, floor {airF * 100:0.0}% air, worst 8 m slope {worst:0} deg at ({wAt.x:0},{wAt.y:0},{wAt.z:0}), no floor at {string.Join(" ", nanAt)}, {ms} ms] ");
            }
            Note($"Golden routes, {Mission.Tiers[(int)tier].zone} (level {L}): {sb}");
            Check($"Golden route {Mission.Tiers[(int)tier].zone}: valid and drivable on 3 seeds (ends on level {L}, tube floor all the way, slopes <= 30 deg)", ok, 3, 3, "seeds");
            Check($"Golden route {Mission.Tiers[(int)tier].zone}: worst 8 m slope on the real floor (3 seeds; rover climbs 30-32 deg on talus/basalt)", worstAll, 0, 30, "deg");
        }

        /// <summary>Distinct tube features near the route (call-out detection along the polyline every 10 m).</summary>
        static Dictionary<string, int> FeaturesAlongRoute(GameBootstrap game)
        {
            var r = game.Route; var seen = new HashSet<string>(); var counts = new Dictionary<string, int>();
            for (int k = 0; k < r.points.Count - 1; k++)
            {
                var a = r.points[k]; var b = r.points[k + 1];
                var dir = math.normalizesafe((b - a).xz);
                int n = Mathf.Max(1, Mathf.CeilToInt(math.distance(a.xz, b.xz) / 10f));
                for (int s = 0; s < n; s++)
                {
                    var q = math.lerp(a, b, s / (float)n);
                    foreach (var f in game.Director.FeaturesAhead(q, dir))
                        if (seen.Add($"{f.type}:{math.round(f.at.x / 8f)}:{math.round(f.at.z / 8f)}"))
                            counts[f.type] = counts.TryGetValue(f.type, out var c) ? c + 1 : 1;
                }
            }
            return counts;
        }

        // ------------------------------------------------------------------ items 2-5, 7: each tier's world

        static void TestMissionTiers()
        {
            foreach (Mission.Tier tier in new[] { Mission.Tier.Cadet, Mission.Tier.Explorer, Mission.Tier.Commander })
            {
                var info = Mission.Tiers[(int)tier];
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var game = BuildWorld(tier);
                var hf = game.Height;
                Note($"**{info.zone} ({info.rank})**: world built in {sw.ElapsedMilliseconds} ms.");
                // the rover start moves with the tier, on bearing 045 from the pit; the base doesn't
                var s = game.StartFrame;
                var d = (float2)(s.xz) - hf.pitCentre;
                float dist = math.length(d), brg = (math.degrees(math.atan2(d.x, d.y)) + 360f) % 360f;
                Check($"{info.zone}: rover start distance from the pit (Houston: {info.startDistance / 1000f:0.#} km)", dist, info.startDistance - 60, info.startDistance + 60, "m");
                Check($"{info.zone}: rover start bearing from the pit (Houston: 045)", brg, 39, 51, "deg");
                Check($"{info.zone}: crew lander stays put", game.LanderFrame.HasValue ? math.distance((float2)game.LanderFrame.Value.xz, new float2(game.landerXZ.x, game.landerXZ.y)) : 999, 0, 60, "m");
                // golden route + GIMLI-2 at the seal
                var r = game.Route;
                Check($"{info.zone}: golden route found", r != null && r.Valid ? 1 : 0, 1, 1, "");
                if (r == null || !r.Valid) continue;
                var sf = (float3)game.ScoutFrame.Value;
                int scoutLevel = game.Cave.Network.LevelAt(sf);
                int wantLevel = game.Cave.Network.IsFlow ? r.pointLevels[r.pointLevels.Count - 1] : info.targetLevel;
                Check($"{info.zone}: GIMLI-2 on the route's last level", scoutLevel, wantLevel, wantLevel, "");
                if (game.Cave.Network.IsFlow)
                {
                    var rng = MoonBaseAlpha.Cave.FlowNetwork.TierRange(info.targetLevel);
                    Note($"{info.zone}: flow layout {Mission.TubeCode}: GIMLI-2 {r.Length / 1000:0.00} km in, flow {wantLevel + 1} (tier range {rng.lo / 1000:0.#}-{rng.hi / 1000:0.#} km; nearest dead end when none in range).");
                }
                Check($"{info.zone}: GIMLI-2 parked short of the seal, facing it", game.SealFrame.HasValue ? math.distance(sf.xz, ((float3)game.SealFrame.Value).xz) : -1, 5, 9, "m");
                Check($"{info.zone}: the rubble seal and the sealed chamber exist (chamber hidden until the drill)", game.SealObject != null && game.ChamberObject != null && !game.ChamberObject.activeSelf ? 1 : 0, 1, 1, "");
                Check($"{info.zone}: chamber boulder (granite on 3/5-level runs only)", game.ChamberObject.transform.Find("GraniteBoulder") != null ? 1 : 0, info.targetLevel >= 3 ? 1 : 0, info.targetLevel >= 3 ? 1 : 0, "");
                // a Cadet cave-in route (round 12) is cut short: count the junctions it really passes
                int junctions = r.CaveIn ? r.pointIsJunction.Skip(1).Take(Math.Max(0, r.points.Count - 3)).Count(x => x) : r.nodes.Count - 3;
                Check($"{info.zone}: relay beacons at the route's junctions and ramp ends", game.Beacons.Count, Math.Max(1, junctions - 1), r.points.Count, "beacons");
                Check($"{info.zone}: golden route verified on the real floor", r.verified ? 1 : 0, 1, 1, "");
                Note($"{info.zone}: route {r.Length:0} m, {r.nodes.Count} junctions, found after {r.attempts} searches.");
                // GIMLI-1 on the rim
                {
                    var g = (float3)game.GimliRoverFrame.Value; var gl = (float3)game.GimliLanderFrame.Value;
                    var craneBase = WorldOrigin.WorldToFrame(game.Crane.transform.position);
                    var pad = WorldOrigin.WorldToFrame(game.Crane.PadWorld);
                    float dBase = math.distance(g.xz, new float2((float)craneBase.x, (float)craneBase.z));
                    float dPad = math.distance(g.xz, new float2((float)pad.x, (float)pad.z));
                    var st = new float2((float)s.x, (float)s.z); var pd = new float2((float)pad.x, (float)pad.z);
                    var ab = pd - st; float t = math.saturate(math.dot(g.xz - st, ab) / math.lengthsq(ab));
                    float dLine = math.distance(g.xz, st + ab * t);
                    Check($"{info.zone}: GIMLI-1 rover on the rim (PitRho)", hf.PitRho(g.xz), 1.6, 2.35, "");
                    Check($"{info.zone}: GIMLI-1 clear of the crane's slew circle (from the mast base)", dBase, 66, 1e6, "m");
                    Check($"{info.zone}: GIMLI-1 lander a short walk from its rover", math.distance(g.xz, gl.xz), 15, 70, "m");
                    Note($"{info.zone}: GIMLI-1 {dLine:0} m from the straight drive start -> crane pad (aim 50-100), {dPad:0} m from the pad.");
                }
                int trackObjs = UnityEngine.Object.FindObjectsByType<Transform>().Count(x => x.name.StartsWith("Tracks: "));
                Check($"{info.zone}: pre-made tracks (cargo lander -> pad and back, GIMLI-1 traverses)", trackObjs, 3, 10, "paths");
                // tube features near the route (round 8 built them; the crew calls them out)
                var counts = FeaturesAlongRoute(game);
                Note($"{info.zone}: features near the route ({r.Length:0} m): " + string.Join(", ", new[] { "junction", "breakdown", "collapse_ramp", "lavafall", "chamber", "deadend", "shaft", "levelramp" }.Select(k => $"{k} {(counts.TryGetValue(k, out var c) ? c : 0)}")));
                Check($"{info.zone}: junction call-outs possible along the route", counts.TryGetValue("junction", out var cj) ? cj : 0, 1, 1000, "");
                if (!game.Cave.Network.IsFlow || r.pointLevels.Max() > 0)
                    Check($"{info.zone}: level ramps or shafts down along the route (target level {info.targetLevel})", (counts.TryGetValue("levelramp", out var cr) ? cr : 0) + r.ShaftCount, 1, 1000, "");
                if (r.CaveIn) Check($"{info.zone}: the route ends at a cave-in seal (Cadet, round 12)", game.SealObject != null ? 1 : 0, 1, 1, "");
                else Check($"{info.zone}: the route ends in a dead-end spur (the seal)", counts.TryGetValue("deadend", out var cd) ? cd : 0, 1, 1000, "");
                GoldenRoutes(game, tier);
                if (tier == Mission.Tier.Cadet) Shots(game, "40_start_cadet");
            }
            Mission.Current = Mission.Tier.Cadet;
        }

        // ------------------------------------------------------------------ items 5-9: the story on the Cadet world

        static void TestMissionStory()
        {
            var game = BuildWorld(Mission.Tier.Cadet);
            var b = game.Buggy; var dir = game.Director;
            Step(b, 400, i => { if (i % 200 == 0) game.Streamer.BuildNow(b.transform.position, b.transform.position); });
            // beat 1: the opening lines
            game.BeginMission();
            Check("Story: the director runs once the mission begins", dir.phase == MissionDirector.Phase.Drive ? 1 : 0, 1, 1, "");
            TickStory(game, 40f);
            Check("Beat 1: touchdown, Houston's distance (cadet), the specialist's joke - in order", InOrder("b1_touchdown", "b1_distance_cadet", "b1_smell") ? 1 : 0, 1, 1, "");

            // GIMLI-1 in view (it may already have come into view from the landing zone)
            game.Teleport(4);
            Step(b, 400);
            TickStory(game, 1f);
            Check("Beat 1: GIMLI-1 line when it comes into view (site 4 approach)", dir.Fired("b1_gimli1_spec") ? 1 : 0, 1, 1, "");
            TickStory(game, 60f);
            Check("Beat 1: GIMLI-1 lines once, then Houston, then the GIMLI name joke", Radio.Spoken.Count(x => x == "b1_gimli1_spec") == 1 && InOrder("b1_gimli1_spec", "b1_gimli1_houston", "b1_gimli1_name") ? 1 : 0, 1, 1, "");
            Shots(game, "41_gimli1", fpOnly: true);
            {
                var cam = game.cam; var g = game.GimliWorld.Value; var up = -TerrainFrame.DownAt(g);
                var lw = WorldOrigin.FrameToWorld(game.GimliLanderFrame.Value);
                cam.transform.position = g + (g - lw).normalized * 14f + up * 4f + Vector3.Cross(up, (g - lw).normalized) * 6f;
                cam.transform.LookAt((g + lw) * 0.5f + up * 1.5f, up);
                cam.fieldOfView = 60f;
                Render(game, Path.Combine(OutDir, "41_gimli1_site.png"));
                cam.fieldOfView = 75f;
            }
            // the cargo lander and the tracks to the crane
            {
                var c = (float3)game.CargoFrame.Value;
                var padF = WorldOrigin.WorldToFrame(game.Crane.PadWorld);
                var toCargo = math.normalizesafe(c.xz - new float2((float)padF.x, (float)padF.z));
                var p = c.xz - toCargo * 80f;
                PlaceRover(game, new double3(p.x, game.Height.Height(p.x, p.y, 0f), p.y), toCargo, false);
                TickStory(game, 1f);
                Check("Beat 1: cargo lander line when passing it", dir.Fired("b1_cargo_lander") ? 1 : 0, 1, 1, "");
                TickStory(game, 30f);
                Check("Beat 1: cargo lander line said once (and GIMLI-1's once)", Radio.Spoken.Count(x => x == "b1_cargo_lander") == 1 && Radio.Spoken.Count(x => x == "b1_gimli1_spec") == 1 ? 1 : 0, 1, 1, "");
                var cam = game.cam; var cw = WorldOrigin.FrameToWorld(game.CargoFrame.Value); var up = -TerrainFrame.DownAt(cw);
                cam.transform.position = cw - new Vector3(toCargo.x, 0, toCargo.y) * 30f + up * 9f;
                cam.transform.LookAt(cw - new Vector3(toCargo.x, 0, toCargo.y) * 70f, up);
                Render(game, Path.Combine(OutDir, "42_cargo_tracks.png"));
            }

            // live tyre tracks: regolith yes, tube basalt no
            game.Teleport(0);
            Step(b, 400);
            {
                float l0 = game.Tracks.LaidMetres;
                b.parkingBrake = false; b.brake = 0; b.throttle = 0.6f;
                var tsw = System.Diagnostics.Stopwatch.StartNew(); int samples = 0;
                for (int k = 0; k < 200 * 15; k++)
                {
                    LunarPhysics.AimGravity(b.transform.position); b.Step(Dt); Physics.Simulate(Dt);
                    if (k % 7 == 0) { game.Tracks.Sample(); samples++; }
                    if (k % 200 == 0) game.Streamer.BuildNow(b.transform.position, b.transform.position);
                }
                double msPer = tsw.Elapsed.TotalMilliseconds;
                b.throttle = 0; b.brake = 1; Step(b, 400);
                Check("Tyre tracks: laid on regolith behind the rear wheels (15 s drive)", game.Tracks.LaidMetres - l0, 20, 400, "m (2 wheels)");
                Note($"Tyre tracks: {game.Tracks.ChunkCount} chunk meshes, live update {TyreTracks.AvgMs:0.000} ms per sample (target < 0.1 ms).");
                Check("Tyre tracks: CPU per sample", TyreTracks.AvgMs, 0, 0.1, "ms");
                var cam = game.cam; var bt = b.transform;
                cam.transform.position = bt.position + bt.forward * 3f + bt.up * 3.2f;
                cam.transform.LookAt(bt.position - bt.forward * 25f, bt.up);
                Render(game, Path.Combine(OutDir, "43_own_tracks.png"));
            }
            AllLamps(game, true);
            game.Teleport(2);
            Step(b, 400);
            {
                float l0 = game.Tracks.LaidMetres;
                b.parkingBrake = false; b.brake = 0; b.throttle = 0.5f;
                for (int k = 0; k < 200 * 5; k++) { LunarPhysics.AimGravity(b.transform.position); b.Step(Dt); Physics.Simulate(Dt); if (k % 7 == 0) game.Tracks.Sample(); }
                b.throttle = 0; b.brake = 1; Step(b, 200);
                Check("Tyre tracks: none on bare tube basalt", game.Tracks.LaidMetres - l0, 0, 0.5, "m");
            }
            AllLamps(game, false);   // deposit sightings are tested on their own below
            Radio.Clear();
            TickStory(game, 1f);
            Check("Beat 3 A: tube temperature line ~40 m into the tube", dir.Fired("b3_tube_temp") ? 1 : 0, 1, 1, "");

            // tube features: detection at known places
            {
                var sites = game.Sites.Sites; var net = game.Cave.Network;
                string Probe(SiteCatalog.Kind kind, float back, string expect)
                {
                    int k = sites.FindIndex(x => x.kind == kind);
                    if (k < 0) return "no site";
                    var s = sites[k];
                    var p = s.feature - new float3(s.approach.x, 0, s.approach.y) * back;
                    float fy = net.FloorY(p); if (!float.IsNaN(fy)) p.y = fy;
                    var list = dir.FeaturesAhead(p, s.approach);
                    return list.Any(f => f.type == expect) ? "ok" : "missed (" + string.Join(",", list.Select(f => f.type)) + ")";
                }
                var res = new Dictionary<string, string>
                {
                    ["lavafall"] = Probe(SiteCatalog.Kind.LavaFall, 20f, "lavafall"),
                    ["chamber"] = Probe(SiteCatalog.Kind.Chamber, 20f, "chamber"),
                    ["shaft"] = Probe(SiteCatalog.Kind.CollapseTop, 25f, "shaft"),
                    ["levelramp"] = Probe(SiteCatalog.Kind.Ramp, 0f, "levelramp"),
                };
                // the route's own spur (dead end) and its junctions
                var r = game.Route;
                var sp = math.lerp(r.spur.a, r.spur.b, 0.4f); sp.y = TunnelNetwork.FloorAt(r.spur, 0.4f);
                // (a Cadet cave-in seal part-way along a tube has no dead end at GIMLI-2: round 12, S8)
                res["deadend"] = r.CaveIn ? "no site" : dir.FeaturesAhead(sp, r.sealDir).Any(f => f.type == "deadend") ? "ok" : "missed";
                Note("Feature detection at known places: " + string.Join(", ", res.Select(kv => $"{kv.Key} {kv.Value}")));
                Check("Tube features: detected at known places (lava fall, chamber, shaft, level ramp, dead end)", res.Count(kv => kv.Value == "ok" || kv.Value == "no site"), res.Count, res.Count, "");
            }
            // firing: once, then paced (lamps off: no deposit lines in between)
            AllLamps(game, false);
            {
                int k = game.Sites.Sites.FindIndex(x => x.kind == SiteCatalog.Kind.LavaFall);
                if (k < 0) k = game.Sites.Sites.FindIndex(x => x.kind == SiteCatalog.Kind.CollapseTop);
                game.GotoSite(k);
                AllLamps(game, false);
                Step(b, 300, i => { if (i % 150 == 0) game.Tunnels.BuildNow(b.transform.position, 120f); });
                Radio.Clear();
                int n0 = dir.FeatureLog.Count;
                TickStory(game, 1.5f);
                int fired = dir.FeatureLog.Count - n0;
                Check("Tube features: a call-out at the site (first of its kind)", fired, 1, 1, "lines");
                string firstType = fired > 0 ? dir.FeatureLog[n0].Split(' ').Last() : "?";
                TickStory(game, 60f);
                Check($"Tube features: '{firstType}' not repeated within the 2-minute pacing (60 s parked there)", dir.FeatureLog.Skip(n0).Count(x => x.EndsWith(" " + firstType)), 1, 1, "lines");
                float minGap = 999; var times = dir.FeatureLog.Skip(n0).Select(x => float.Parse(x.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)).ToList();
                for (int i = 1; i < times.Count; i++) minGap = Mathf.Min(minGap, times[i] - times[i - 1]);
                if (times.Count > 1) Check("Tube features: gap between two call-outs (first of a type >= 12 s, repeats >= 45 s)", minGap, MissionDirector.FirstFeatureGap, 1e6, "s");
                Note("Feature log: " + string.Join(" | ", dir.FeatureLog));
                Check("Radio: no overlaps (features)", WorstOverlap(), -1000, 0.001, "s");
            }
            // status reports and low battery
            {
                Radio.Clear();
                b.stateOfCharge = 0.83f;
                TickStory(game, MissionDirector.StatusEvery + 20f);
                Check("Status report every ~3.5 min: battery band 80% and Houston's ack", Radio.Spoken.Contains("s_batt_80") && (Radio.Spoken.Contains("s_ack_1") || Radio.Spoken.Contains("s_ack_2")) ? 1 : 0, 1, 1, "");
                b.stateOfCharge = 0.25f;
                TickStory(game, 1f);
                Check("Beat 3 E: low battery line below 30%", dir.Fired("b3_lowbatt") ? 1 : 0, 1, 1, "");
                TickStory(game, 20f);
                Check("Beat 3 E: low battery said once", Radio.Spoken.Count(x => x == "b3_lowbatt"), 1, 1, "");
                b.stateOfCharge = 0.7f;
            }
            // deposit sightings: line + reply + Houston (first only) + map tag
            {
                AllLamps(game, false);
                TickStory(game, 30f);
                Radio.Clear();
                int tags0 = dir.Tags.Count;
                var at = WorldOrigin.WorldToFrame(b.transform.position);
                bool s1 = dir.Sighting(RockDeposits.Kind.OrangeGlass, at);
                TickStory(game, 45f);
                bool s2 = dir.Sighting(RockDeposits.Kind.OrangeGlass, at);
                bool s3 = dir.Sighting(RockDeposits.Kind.Glaze, at);
                TickStory(game, 30f);
                Check("Deposits: first orange glass sighting -> line, commander's reply, Houston", s1 && InOrder("d_orange", "d_tag_1", "d_houston") ? 1 : 0, 1, 1, "");
                Check("Deposits: the same deposit type is not called twice; a new one is (Houston only once)", !s2 && s3 && Radio.Spoken.Count(x => x == "d_houston") == 1 && Radio.Spoken.Contains("d_tag_2") ? 1 : 0, 1, 1, "");
                Check("Deposits: tagged on the 3D map", dir.Tags.Count - tags0, 2, 2, "tags");
                // real headlight rays in a tube: which deposits the crew would spot here
                AllLamps(game, true);
                Radio.Clear();
                int tags1 = dir.Tags.Count;
                TickStory(game, 3f);
                Note("Deposit sightings from the headlights at this site: " + (dir.Tags.Count > tags1 ? string.Join(", ", dir.Tags.Skip(tags1).Select(t => t.kind.ToString())) : "(none new in the beams here)"));
            }
            // HUD markers: surface set on the surface, underground set underground; pit / crane in both; the 3D map in both
            {
                var hud = UnityEngine.Object.FindAnyObjectByType<MoonBaseAlpha.UI.DebugHud>();
                if (hud == null) Fail("no DebugHud in the scene");
                else
                {
                    if (hud.game == null) hud.game = game;
                    string Ids(bool under) => string.Join(",", hud.MarkersFor(game.cam.transform.position, under).Select(m => m.id));
                    game.Teleport(0); Step(b, 200);
                    var sf = WorldOrigin.WorldToFrame(b.transform.position);
                    bool surfUnder = game.Height.DataHeight(sf.x, sf.z) - sf.y >= 15f;
                    string surf = Ids(surfUnder);
                    Note($"Markers on the surface: {surf}");
                    Check("HUD on the surface: Earth, Sun, crane; no underground markers (GIMLI-2 guide, pit, fixed lines)",
                          !surfUnder && surf.Contains("earth") && surf.Contains("sun") && surf.Contains("crane") && !surf.Contains("gimli2") && !surf.Contains("pit") && !surf.Contains("line") ? 1 : 0, 1, 1, "");
                    game.Map.SetOpen(true);
                    Check("3D tube map opens on the surface", MoonBaseAlpha.UI.TunnelMap.IsOpen && game.Map.RenderOnce() != null ? 1 : 0, 1, 1, "");
                    game.Map.SetOpen(false);
                    game.Teleport(3); Step(b, 400, i => { if (i % 200 == 0) game.Tunnels.BuildNow(b.transform.position, 120f); });
                    var uf = WorldOrigin.WorldToFrame(b.transform.position);
                    bool under = game.Height.DataHeight(uf.x, uf.z) - uf.y >= 15f;
                    string ug = Ids(under);
                    Note($"Markers in the tubes: {ug}");
                    Check("HUD in the tubes: GIMLI-2 guide and the pit / crane; no Earth, Sun, GIMLI-1 or surface crane",
                          under && ug.Contains("gimli2") && ug.Contains("pit") && !ug.Contains("earth") && !ug.Contains("sun") && !ug.Contains("gimli1") && !ug.Split(',').Contains("crane") ? 1 : 0, 1, 1, "");
                    game.Map.SetOpen(true);
                    Check("3D tube map opens in the tubes", MoonBaseAlpha.UI.TunnelMap.IsOpen && game.Map.RenderOnce() != null ? 1 : 0, 1, 1, "");
                    game.Map.SetOpen(false);
                }
            }
            // beat 3 F + beat 4 + the drill
            {
                Radio.Clear();
                game.Teleport(3);
                Step(b, 600, i => { if (i % 200 == 0) game.Tunnels.BuildNow(b.transform.position, 120f); });
                TickStory(game, 1f);
                Check("Beat 3 F: Houston's 'on GIMLI-2's level' call on the target level", dir.Fired("b3_final_level") ? 1 : 0, 1, 1, "");
                TickStory(game, 20f);
                Check("Beat 3 F: Houston first, then the commander", InOrder("b3_final_level", "b3_final_cmdr") ? 1 : 0, 1, 1, "");
                Check("Beat 4: no 'visual' yet at the approach (22 m)", dir.Fired("b4_visual") ? 1 : 0, 0, 0, "");
                var sf = game.ScoutFrame.Value; var sd = game.Route.sealDir;
                PlaceRover(game, sf - new double3(sd.x, 0, sd.y) * 5.5, sd, true);
                TickStory(game, 0.5f);
                Check("Beat 4: 'Visual on GIMLI-2' within ~10 m", dir.Fired("b4_visual") ? 1 : 0, 1, 1, "");
                Check("Beat 4: drill prompt ([F]) at the seal", dir.DrillReady ? 1 : 0, 1, 1, "");
                Shots(game, "44_seal", fpOnly: true);
                {
                    var cam = game.cam; var bt = b.transform;
                    cam.transform.position = bt.position - bt.forward * 6f + bt.up * 3f + bt.right * 2.5f;
                    cam.transform.LookAt(WorldOrigin.FrameToWorld(game.SealFrame.Value) + Vector3.up * 2f, bt.up);
                    Render(game, Path.Combine(OutDir, "44_seal_3p.png"));
                }
                dir.SetDrillKey(true); TickStory(game, 2f);
                float half = dir.DrillProgress;
                dir.SetDrillKey(false); TickStory(game, 0.6f);
                float released = dir.DrillProgress;
                Check("Drill: hold F -> progress (2 s of 4)", half, 0.45, 0.55, "");
                Check("Drill: releasing F stops (and slowly loses) progress", released, 0.1, half - 0.05, "");
                dir.SetDrillKey(true); TickStory(game, 4f);
                dir.SetDrillKey(false);
                Check("Drill: breakthrough after holding F (vapour burst phase)", dir.phase == MissionDirector.Phase.Venting ? 1 : 0, 1, 1, "");
                var bore = game.SealObject.transform.Find("BoreHole");
                Check("Drill: bore hole visible in the seal", bore != null && bore.gameObject.activeSelf ? 1 : 0, 1, 1, "");
                var ps = game.SealObject.GetComponentInChildren<ParticleSystem>();
                Check("Drill: vapour burst emitter", ps != null ? 1 : 0, 1, 1, "");
                if (ps != null)
                {
                    ps.Simulate(1.2f, true, true);
                    var cam = game.cam; var eye = BuggyBuilder.DriverEye(b);
                    cam.transform.SetPositionAndRotation(eye.position, Quaternion.LookRotation(WorldOrigin.FrameToWorld(game.SealFrame.Value) + Vector3.up * 1.5f - eye.position, Vector3.up));
                    Render(game, Path.Combine(OutDir, "45_vapour_burst.png"));
                    Note($"Vapour burst: {ps.particleCount} particles after 1.2 s.");
                }
                TickStory(game, MissionDirector.VentSeconds + 0.2f);
                Check("Discovery: probe camera phase after the venting", dir.phase == MissionDirector.Phase.Probe ? 1 : 0, 1, 1, "");
                Check("Discovery: sealed chamber shown", game.ChamberObject.activeSelf ? 1 : 0, 1, 1, "");
                Check("Discovery: HUD hidden for the probe camera", MissionDirector.HideHud ? 1 : 0, 1, 1, "");
                if (dir.ProbePose(8f, out var pp, out var pr))
                {
                    var cam = game.cam;
                    var lamp = new GameObject("TestProbeLamp").AddComponent<Light>();
                    lamp.type = LightType.Point; lamp.range = 14f; lamp.gameObject.AddComponent<PhysicalLight>().physicalIntensity = 220f;
                    lamp.transform.position = pp + pr * new Vector3(0.3f, 0.2f, 0.2f);
                    cam.transform.SetPositionAndRotation(pp, pr);
                    Check("Discovery: probe camera metering forced (inside solid rock the meter sees nothing)", game.Exposure.ForcedLuminance, MissionDirector.ProbeLuminance, MissionDirector.ProbeLuminance, "cd/m^2");
                    // S11: as in play - no snap by the test (the director snapped on entry), visor lift off
                    Check("S11 probe: the visor lift is off on the probe camera", game.Visor.active() ? 1 : 0, 0, 0, "");
                    var ptex = Render(game, Path.Combine(OutDir, "46_probe_chamber.png"), true, false);
                    {
                        var px = ptex.GetPixels32(); double sum = 0; int clip = 0;
                        foreach (var c in px) { float l = (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f; sum += l; if (c.r > 250 && c.g > 250 && c.b > 250) clip++; }
                        Check("S11 probe view: mean brightness (dark, lamp-lit frost)", sum / px.Length, 0.04, 0.55, "");
                        Check("S11 probe view: clipped white pixels", 100.0 * clip / px.Length, 0, 3, "%");
                        UnityEngine.Object.DestroyImmediate(ptex);
                    }
                    UnityEngine.Object.DestroyImmediate(lamp.gameObject);
                    Check("Discovery: the probe camera is inside the chamber", Vector3.Distance(pp, game.ChamberObject.transform.position), 0, 6, "m");
                }
                TickStory(game, MissionDirector.ProbeMaxSeconds + 1f);
                Check("Debrief: reached after the discovery lines", dir.phase == MissionDirector.Phase.Debrief ? 1 : 0, 1, 1, "");
                Check("Beat 5 (2-level run): gas, probe, frost-only, Houston - in order", InOrder("b5_gas", "b5_probe", "b5_frost_only", "b5_houston") && !Radio.Spoken.Contains("b5_granite") ? 1 : 0, 1, 1, "");
                Check("Radio: no overlaps (beats 3-5)", WorstOverlap(), -1000, 0.001, "s");
                Note($"Debrief stats: time {dir.TimeAtDiscovery:0} s, battery {dir.BatteryAtDiscovery * 100:0}%, deepest level {dir.MaxLevel}, tags {dir.Tags.Count}, rig kits used {RoverWinch.RigKitsPerMission - game.Winch.RigKits}, ropes {game.Winch.Ropes}.");
            }
        }

        // ------------------------------------------------------------------ item 11: scripted mission timing

        static void TestMissionTiming()
        {
            var rows = new StringBuilder();
            foreach (Mission.Tier tier in new[] { Mission.Tier.Cadet, Mission.Tier.Explorer, Mission.Tier.Commander })
            {
                var info = Mission.Tiers[(int)tier];
                var game = BuildWorld(tier);
                Tuning.RoverSpeed.Value = Tuning.RoverSpeed.Default;   // the shipped game: x1.25 top speed
                Tuning.WheelGrip.Value = Tuning.WheelGrip.Default;     // and x2 grip (round 12)
                Tuning.CraneSpeed.Value = Tuning.CraneSpeed.Default; Tuning.CraneSway.Value = Tuning.CraneSway.Default;
                var wall = System.Diagnostics.Stopwatch.StartNew();
                var res = ScriptedMission(game, out string log);
                Tuning.Neutral();
                // Cadet: the crane ride runs as a x3 time-lapse (real time = sim time / 3)
                if (tier == Mission.Tier.Cadet) res.crane /= GameBootstrap.CadetCraneTimeScale;
                float total = res.surface + res.crane + res.tubes + 45f;   // + drill (4 s) and the discovery sequence (~40 s)
                rows.AppendLine($"| {info.zone} ({info.rank}) | {info.time} | {res.surface / 60f:0.0} | {res.crane / 60f:0.0} | {res.tubes / 60f:0.0} | **{total / 60f:0.0}** | {res.routeM:0} m, level {info.targetLevel} | {res.battery * 100:0}% | {res.assists} | {(res.reached ? "yes" : "NO")} | {wall.Elapsed.TotalSeconds:0} s |");
                Note($"{info.zone} scripted run: {log}");
                Check($"Timing {info.zone}: the scripted driver reached GIMLI-2", res.reached ? 1 : 0, 1, 1, "");
                Check($"T4 {info.zone}: shafts ridden down by the scripted driver = the route's", res.shafts, game.Route.ShaftCount, game.Route.ShaftCount, "");
                Note($"T1 {info.zone}: steep-ground hints on the surface drive: round-12 rule {res.steepOld}, now {res.steepNew}.");
                if (res.steepOld >= 5) Check($"T1 {info.zone}: steep-ground hints cut to <= ~1/5 of round 12", res.steepNew / (double)res.steepOld, 0, 0.25, "x");
            }
            report.AppendLine("\n| Zone | Target | Surface (min) | Crane (min) | Tubes (min) | Total incl. drill + discovery (min) | Route | Battery left | Assists | Reached | Wall clock |");
            report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            report.Append(rows);
            Mission.Current = Mission.Tier.Cadet;
        }

        /// <summary>
        /// Look-ahead point on a route that may double back over itself on another level: the closest point counts height
        /// (x3) and only searches from the last segment a little way on (no jumping to a segment on another level).
        /// </summary>
        static double3 RouteTarget3(List<double3> route, double3 f, double look, ref int seg)
        {
            int bi = seg; double bt = 0, bd = double.MaxValue;
            for (int i = math.max(0, seg - 1); i < math.min(route.Count - 1, seg + 4); i++)
            {
                var a = route[i]; var c = route[i + 1]; var ab = c - a;
                double t = math.saturate(math.dot((f - a).xz, ab.xz) / math.max(1e-6, math.lengthsq(ab.xz)));
                var q = a + ab * t;
                double d = math.distance(f.xz, q.xz) + 3.0 * math.abs(f.y - q.y);
                if (d < bd) { bd = d; bi = i; bt = t; }
            }
            seg = bi;
            double remaining = look;
            var p = math.lerp(route[bi], route[bi + 1], bt);
            for (int i = bi; i < route.Count - 1; i++)
            {
                var end = route[i + 1];
                double sl = math.distance(p.xz, end.xz);
                if (sl >= remaining) return p + (end - p) * (remaining / math.max(sl, 1e-6));
                remaining -= sl; p = end;
            }
            return route[route.Count - 1];
        }

        struct RunResult { public float surface, crane, tubes, routeM, battery; public int assists, steepOld, steepNew, shafts; public bool reached; }

        /// <summary>
        /// A sensible visitor, scripted: drive straight at the crane at up to ~15 km/h (steering round what it hits),
        /// drive onto the deck, ride down, [G], then follow GIMLI-2's beacons along the route to the seal.
        /// Stuck > 25 s near one spot: the run is nudged 15 m along the route (an "assist", counted).
        /// </summary>
        static RunResult ScriptedMission(GameBootstrap game, out string log)
        {
            var res = new RunResult(); var b = game.Buggy; var crane = game.Crane;
            var sb = new StringBuilder();
            game.Teleport(0);
            Step(b, 400, i => { if (i % 200 == 0) game.Streamer.BuildNow(b.transform.position, b.transform.position); });
            void Phys() { LunarPhysics.AimGravity(b.transform.position); b.Step(Dt); crane.Step(Dt); Physics.Simulate(Dt); }
            void Shift()
            {
                var p = b.transform.position;
                if (Mathf.Abs(p.x) > 1024f || Mathf.Abs(p.z) > 1024f || Mathf.Abs(p.y) > 1024f) WorldOrigin.ShiftBy(new Vector3(Mathf.Round(p.x), Mathf.Round(p.y), Mathf.Round(p.z)));
            }
            float Steer(Vector3 to) => Vector3.SignedAngle(Vector3.ProjectOnPlane(b.transform.forward, b.transform.up), Vector3.ProjectOnPlane(to, b.transform.up), b.transform.up);

            // ---- surface: to the crane's approach point (18 m out from the deck, like key 2)
            var pad = WorldOrigin.WorldToFrame(crane.Platform.position);
            var cb = WorldOrigin.WorldToFrame(crane.transform.position);
            var outward = math.normalize((pad - cb).xz);
            var goal = new double3(pad.x + outward.x * 18, 0, pad.z + outward.y * 18);
            // the way a driver would go: straight unless that crosses the pit; then round the rim at ~170 m
            var pc = (double2)game.Height.pitCentre;
            var sf0 = WorldOrigin.WorldToFrame(b.transform.position);
            var surf = new List<double3> { sf0 };
            {
                double a0 = math.atan2(sf0.z - pc.y, sf0.x - pc.x), a1 = math.atan2(goal.z - pc.y, goal.x - pc.x);
                double da = a1 - a0; while (da > math.PI) da -= 2 * math.PI; while (da < -math.PI) da += 2 * math.PI;
                var ab = goal.xz - sf0.xz; double tt0 = math.saturate(math.dot(pc - sf0.xz, ab) / math.lengthsq(ab));
                if (math.distance(sf0.xz + ab * tt0, pc) < 170)
                {
                    double r0 = math.min(math.distance(sf0.xz, pc), 400);
                    int steps = (int)math.ceil(math.abs(da) / 0.2);
                    for (int q = 1; q < steps; q++)
                    {
                        double a = a0 + da * q / steps, rr = math.max(170, math.lerp(r0, 170, math.saturate(q / 3.0)));
                        surf.Add(new double3(pc.x + math.cos(a) * rr, 0, pc.y + math.sin(a) * rr));
                    }
                }
                surf.Add(goal);
            }
            b.parkingBrake = false; b.brake = 0;
            float t = 0, stuck = 0; int rights = 0; var stuckLog = new List<string>();
            // T1 (round 13): steep-ground hints on this drive, the round-12 rule (> 20 deg, every appearance) vs now
            var steep = new SteepWarning(); int steepOld = 0; bool oldOn = false;
            for (int k = 0; k < 200 * 1500; k++)
            {
                t += Dt;
                var f = WorldOrigin.WorldToFrame(b.transform.position);
                double d = math.distance(f.xz, goal.xz);
                if (d < 12) break;
                var aimF = RouteTarget(surf, new double3(f.x, 0, f.z), 25.0);
                var to = WorldOrigin.FrameToWorld(new double3(aimF.x, f.y, aimF.z)) - b.transform.position;
                float ang = Steer(to);
                b.steering = Mathf.Clamp(ang / 40f, -0.8f, 0.8f);
                float vmax = Mathf.Abs(ang) > 30f ? 2.5f : 4.6f;
                b.throttle = b.Speed < vmax ? 0.9f : 0f;
                b.brake = b.Speed > vmax + 0.6f ? 0.3f : 0f;
                if (Vector3.Angle(b.transform.up, -TerrainFrame.DownAt(b.transform.position)) > 70f && Mathf.Abs(b.Speed) < 0.5f) { b.Right(); rights++; Physics.SyncTransforms(); }
                if (Mathf.Abs(b.Speed) < 0.3f) stuck += Dt; else stuck = 0;
                if (stuck > 4f)
                {
                    if (rights < 6) { var pcs = game.Height.pitCentre; stuckLog.Add($"({f.x - pcs.x:0},{f.z - pcs.y:0}) y{f.y:0} tilt {Vector3.Angle(b.transform.up, -TerrainFrame.DownAt(b.transform.position)):0} slip {b.wheels[0].slip:0.00} lim {b.wheels[0].limit} hull {b.HullContact} grounded {b.wheels.Count(w => w.grounded)}"); }
                    // back off and go round
                    for (int r = 0; r < 400; r++) { b.throttle = -0.6f; b.steering = 0.7f; Phys(); }
                    for (int r = 0; r < 400; r++) { b.throttle = 0.7f; b.steering = -0.5f; Phys(); }
                    t += 4f; stuck = 0; rights++;
                }
                Phys();
                if (k % 20 == 0)
                {
                    float sd = UI.DebugHud.SlopeAhead(game, b);
                    bool on = sd > 20f; if (on && !oldOn) steepOld++; oldOn = on;
                    steep.Update(sd, t);
                }
                if (k % 50 == 0) { Shift(); game.Streamer.BuildNow(b.transform.position, b.transform.position); }
            }
            res.surface = t + 10f;   // + lining up with the deck
            res.steepOld = steepOld; res.steepNew = steep.Shown;
            double sLen = 0; for (int q = 1; q < surf.Count; q++) sLen += math.distance(surf[q - 1].xz, surf[q].xz);
            if (stuckLog.Count > 0) sb.Append("[stuck at " + string.Join("; ", stuckLog) + "] ");
            sb.Append($"surface {t:0} s over {sLen:0} m ({rights} recoveries, {math.distance(WorldOrigin.WorldToFrame(b.transform.position).xz, goal.xz):0} m from the crane at the end); ");

            // ---- crane: on the deck, ride down, [G]
            game.Teleport(1);
            Step(b, 200, i => crane.Step(Dt));
            var plat = crane.Platform.transform;
            float tc = 0; b.parkingBrake = false; b.brake = 0;
            for (int k = 0; k < 200 * 120 && !(crane.RoverFullyOnDeck && Mathf.Abs(b.Speed) < 0.05f && b.parkingBrake); k++)
            {
                Phys(); tc += Dt;
                var local = plat.InverseTransformPoint(b.transform.position);
                var aim = plat.TransformPoint(new Vector3(0, 0, Mathf.Clamp(local.z * 0.6f, -2f, 2f) * (Mathf.Abs(local.z) > 3f ? 1f : 0f)));
                b.steering = Mathf.Clamp(Steer(aim - b.transform.position) / 20f, -1f, 1f);
                float maxV = Mathf.Abs(local.z) > 8f ? 1.2f : 0.4f;
                bool there = Mathf.Abs(local.z) < 0.6f && Mathf.Abs(local.x) < 1.0f;
                b.throttle = !there && b.Speed < maxV ? 0.3f : 0f;
                b.brake = there ? 1f : (b.Speed > maxV + 0.2f ? 0.5f : 0f);
                if (there) b.parkingBrake = true;
            }
            crane.CommandLower();
            for (int k = 0; k < 200 * 400 && crane.state != CraneController.State.AtBottom; k++) { Phys(); tc += Dt; }
            tc += 3f;   // the crew reads the prompt and presses G
            crane.OpenGates();
            for (int k = 0; k < 200 * 3; k++) { Phys(); tc += Dt; }
            res.crane = tc;
            sb.Append($"crane {tc:0} s; ");

            // ---- tubes: follow the route to GIMLI-2
            AllLamps(game, true);
            var route = new List<double3> { WorldOrigin.WorldToFrame(b.transform.position) };
            foreach (var p in game.Route.points) route.Add((double3)p);
            res.routeM = game.Route.Length;
            var scout = game.ScoutFrame.Value;
            float tt = 0; stuck = 0; int assists = 0; double3 stuckAt = default; float stuckLong = 0; int routeSeg = 0;
            // round 13 (T4): shaft descents on the route (route point index shifted by 1: the start point is prepended)
            var shaftTops = game.Route.shaftAt.Select(i => i + 1).ToList(); int nextShaft = 0; int rides = 0;
            b.parkingBrake = false; b.brake = 0;
            bool reached = false;
            var wall = System.Diagnostics.Stopwatch.StartNew();
            for (int k = 0; k < 200 * 2400; k++)
            {
                tt += Dt;
                var f = WorldOrigin.WorldToFrame(b.transform.position);
                if (math.distance(f, scout) < 14) { reached = true; break; }
                if (wall.Elapsed.TotalMinutes > 14) { sb.Append("(wall-clock limit) "); break; }
                var target = RouteTarget3(route, f, 12.0, ref routeSeg);
                float vcap = 99f;
                if (nextShaft < shaftTops.Count && routeSeg >= shaftTops[nextShaft] - 8)
                {
                    // the trolley spot: drive onto it slowly, stop, ride the rig down, carry on from the foot
                    var rim = route[shaftTops[nextShaft]];
                    float dr = (float)math.distance(f.xz, rim.xz);
                    if (dr < 30f) { target = rim; vcap = math.max(0.4f, dr * 0.18f); }
                    if (dr < 1.6f)
                    {
                        b.throttle = 0; b.steering = 0; b.brake = 1f;
                        for (int q = 0; q < 600 && b.Body.linearVelocity.magnitude > 0.1f; q++) { Phys(); tt += Dt; }
                        b.parkingBrake = true;
                        var w = game.Winch;
                        bool okRide = w.RideDown(out string whyR);
                        float tr = 0;
                        while (okRide && w.state != RoverWinch.State.Free && tr < 600f)
                        {
                            LunarPhysics.AimGravity(b.transform.position); w.Step(Dt); b.Step(Dt); Physics.Simulate(Dt); tr += Dt;
                            if ((int)(tr / Dt) % 200 == 0) { Shift(); game.Tunnels.BuildNow(b.transform.position, 120f); Physics.SyncTransforms(); }
                        }
                        tt += tr; rides++;
                        sb.Append($"[shaft {nextShaft + 1}: {(okRide ? $"rode down in {tr:0} s" : "refused: " + whyR)}] ");
                        b.parkingBrake = false; b.brake = 0;
                        routeSeg = shaftTops[nextShaft] + 1; nextShaft++; stuck = 0; stuckLong = 0;
                        continue;
                    }
                }
                var to = WorldOrigin.FrameToWorld(target) - b.transform.position;
                float ang = Steer(to);
                b.steering = Mathf.Clamp(ang / 30f, -1f, 1f);
                float vmax = Mathf.Min(vcap, Mathf.Abs(ang) > 25f ? 2.5f : 4.6f);
                b.throttle = b.Speed < vmax ? 0.9f : 0f;
                b.brake = b.Speed > vmax + 0.6f ? 0.3f : 0f;
                if (Vector3.Angle(b.transform.up, -TerrainFrame.DownAt(b.transform.position)) > 70f && Mathf.Abs(b.Speed) < 0.5f) { b.Right(); Physics.SyncTransforms(); }
                if (Mathf.Abs(b.Speed) < 0.3f) stuck += Dt; else stuck = 0;
                if (math.distance(f, stuckAt) < 8) stuckLong += Dt; else { stuckAt = f; stuckLong = 0; }
                if (stuck > 4f && stuckLong < 25f)
                {
                    for (int r = 0; r < 300; r++) { b.throttle = -0.6f; b.steering = -Mathf.Sign(ang) * 0.6f; Phys(); }
                    tt += 1.5f; stuck = 0;
                }
                else if (stuckLong > 25f)
                {
                    // assist: 15 m on along the route
                    var next = RouteTarget3(route, f, 15.0, ref routeSeg);
                    // never past a shaft still to ride (that would drop the rover in): the trolley spot instead
                    if (nextShaft < shaftTops.Count && routeSeg >= shaftTops[nextShaft]) { next = route[shaftTops[nextShaft]]; routeSeg = shaftTops[nextShaft] - 1; }
                    var nw = WorldOrigin.FrameToWorld(next);
                    game.Tunnels.BuildNow(nw, 120f); Physics.SyncTransforms();
                    var up = -TerrainFrame.DownAt(nw);
                    if (Physics.Raycast(nw + up * 5f, -up, out var hit, 20f, ~(1 << 2), QueryTriggerInteraction.Ignore)) nw = hit.point;
                    int sg2 = routeSeg; var nxt = WorldOrigin.FrameToWorld(RouteTarget3(route, next, 10.0, ref sg2));
                    b.PlaceAt(nw + up * 0.6f, Quaternion.LookRotation(Vector3.ProjectOnPlane(nxt - nw, up).normalized, up));
                    Physics.SyncTransforms();
                    assists++; stuckLong = 0; stuck = 0;
                    if (assists <= 6) sb.Append($"[assist at ({f.x:0},{f.y:0},{f.z:0}) level {game.Cave.Network.LevelAt((float3)f)}] ");
                }
                Phys();
                if (k % 100 == 0) { Shift(); game.Tunnels.BuildNow(b.transform.position, 120f); game.Streamer.BuildNow(b.transform.position, b.transform.position); }
                if (k % (200 * 240) == 0 && k > 0 && rides > 0)
                    sb.Append($"[t {tt:0} s: seg {routeSeg}/{route.Count - 1}, at ({f.x:0},{f.y:0},{f.z:0}), {Mathf.Abs(b.Speed):0.0} m/s, {math.distance(f, scout):0} m from GIMLI-2] ");
            }
            res.tubes = tt; res.assists = assists; res.reached = reached; res.battery = b.stateOfCharge; res.shafts = rides;
            sb.Append($"tubes {tt:0} s, {assists} assists, battery {b.stateOfCharge * 100:0}%");
            log = sb.ToString();
            return res;
        }
    }
}
