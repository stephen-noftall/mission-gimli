using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MoonBaseAlpha.Buggy;
using MoonBaseAlpha.Crew;
using MoonBaseAlpha.Rendering;
using MoonBaseAlpha.Cave;
using MoonBaseAlpha.Base;
using System.Linq;
using MoonBaseAlpha.Sky;

namespace MoonBaseAlpha.Editor
{
    /// <summary>
    /// Headless verification of the first pass. Physics is stepped manually (SimulationMode.Script) at
    /// 200 Hz, so results are deterministic. Writes Docs/verification/report.md, CSV traces and screenshots.
    /// Batch: Unity -batchmode -quit -projectPath . -executeMethod MoonBaseAlpha.Editor.Verification.Run
    /// </summary>
    public static partial class Verification
    {
        static readonly string OutDir = Path.GetFullPath("Docs/verification");
        static StringBuilder report;
        static int pass, fail;
        const float Dt = LunarPhysics.FixedDt;

        /// <summary>Quick: build the world and only run the lander -> pit drive (debugging).</summary>
        public static void RunDrive()
        {
            Directory.CreateDirectory(OutDir);
            report = new StringBuilder(); pass = fail = 0;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var prevMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            try
            {
                EditorSceneManager.OpenScene("Assets/_Project/Scenes/Main.unity");
                var game = UnityEngine.Object.FindAnyObjectByType<GameBootstrap>();
                game.Initialise();
                Step(game.Buggy, 1000, i => { if (i % 200 == 0) game.Streamer.BuildNow(game.Buggy.transform.position, game.Buggy.transform.position); });
                LanderToPitDrive(game);
            }
            catch (Exception e) { Fail(e.ToString()); }
            finally { Physics.simulationMode = prevMode == SimulationMode.Script ? SimulationMode.FixedUpdate : prevMode; }
            File.WriteAllText(Path.Combine(OutDir, "drive_report.md"), report.ToString());
            Debug.Log(report.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(fail == 0 ? 0 : 1);
        }

        [MenuItem("Tools/Moon Base Alpha/Run Verification")]
        public static void Run()
        {
            Directory.CreateDirectory(OutDir);
            report = new StringBuilder();
            pass = fail = 0;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            report.AppendLine("# MR-4 / world verification report\n");
            report.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}. Physics stepped at {1f / Dt:0} Hz, lunar g = {Lunar.Gravity} m/s^2.\n");
            var prevMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            Tuning.Neutral();   // physics checks see the base model; tests that need a tuned value set it themselves
            try
            {
                Section("Ephemeris", TestEphemeris);
                Section("Free fall (1/6 g)", TestFreeFall);
                Section("Static sag and settling", TestSag);
                Section("Bump response (shock tuning)", TestBump);
                Section("Acceleration, top speed, energy", TestAcceleration);
                Section("Braking", TestBraking);
                Section("Slope hold (parking brake)", TestSlopeHold);
                Section("Hill climb", TestClimb);
                Section("Max climbable grade per soil (traction limit)", TestMaxGrade);
                Section("Steady turn / rollover margin", TestTurn);
                Section("Astronaut on foot (1/6 g)", TestAstronaut);
                Section("Rover righting (R)", TestRighting);
                Section("Rollover: no pinball relaunch", TestRollover);
                Section("Hard landings (shock sensor)", TestHardLanding);
                Section("Tunnel network: seeds and science", TestNetwork);
                Section("Tube junctions: every step drivable, no dead ends", TestJunctions);
                Section("Flow tubes: tested layouts, routes, checks", TestFlowTubes);
                Section("Real terrain + cave + rendering", TestWorld);
                Section("Round 8: world features", TestRound8);
                Section("Mission: dialogue data", TestDialog);
                Section("Mission: tiers (start, golden route, GIMLI-1, features)", TestMissionTiers);
                Section("Mission: story (beats, features, deposits, drill, discovery, debrief)", TestMissionStory);
                Section("Mission timing (scripted driver)", TestMissionTiming);
                Section("Round 12: drive ranges + edge assist", TestRound12Driving);
                Section("Round 12: shaft rigs (span + trolley)", TestRound12Rigs);
                Section("Round 12: tube ends from the pit (S3)", TestRound12TubeEnds);
                Section("Round 12: helmet HUD, log, step, life support (P5, P6)", TestRound12Hud);
                Section("Round 13: steep hint, dust covers, shaft rigs + routes (T1-T4)", TestRound13);
            }
            catch (Exception e)
            {
                Fail($"EXCEPTION: {e}");
            }
            finally
            {
                Physics.simulationMode = prevMode == SimulationMode.Script ? SimulationMode.FixedUpdate : prevMode;
            }
            try { EditorSceneManager.OpenScene("Assets/_Project/Scenes/Main.unity"); } catch { }
            report.Insert(0, $"**{pass} passed, {fail} failed**\n\n");
            File.WriteAllText(Path.Combine(OutDir, "report.md"), report.ToString());
            Debug.Log($"[Verification] {pass} passed, {fail} failed. Report: {OutDir}/report.md");
            if (Application.isBatchMode) EditorApplication.Exit(fail == 0 ? 0 : 1);
        }

        // ------------------------------------------------------------------ helpers

        // optional "-sections a,b": run only sections whose name contains one of these (targeted iteration)
        static string[] onlySections;
        /// <summary>Headlights + driver lamps + the crew's always-on helmet lamps (tests that need true darkness).</summary>
        static void AllLamps(GameBootstrap game, bool on)
        {
            game.SetLights(on);
            game.Buggy.GetComponent<CrewHeads>()?.SetLamps(on);
        }

        static void Section(string name, Action a)
        {
            if (onlySections == null)
            {
                var args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-sections");
                onlySections = i >= 0 && i + 1 < args.Length ? args[i + 1].Split(',') : new string[0];
            }
            if (onlySections.Length > 0 && !onlySections.Any(x => name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) return;
            report.AppendLine($"\n## {name}\n");
            try { a(); }
            catch (Exception e) { Fail($"{name} threw: {e.GetType().Name}: {e.Message}\n```\n{e.StackTrace}\n```"); }
        }

        static void Check(string what, double value, double lo, double hi, string unit = "")
        {
            bool ok = value >= lo && value <= hi && !double.IsNaN(value);
            if (ok) pass++; else fail++;
            report.AppendLine($"- {(ok ? "PASS" : "**FAIL**")} {what}: **{value:0.###} {unit}** (expected {lo:0.###} .. {hi:0.###})");
        }
        static void Note(string s) => report.AppendLine($"- {s}");
        static void Fail(string s) { fail++; report.AppendLine($"- **FAIL** {s}"); }

        static UnityEngine.SceneManagement.Scene NewFlatWorld(float slopeDeg = 0f)
        {
            var s = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            WorldOrigin.ResetOrigin();
            TerrainFrame.R0 = 1735832.0;
            LunarPhysics.Configure();
            GroundMaterial.Provider = null;
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(2000, 1, 2000);
            ground.transform.rotation = Quaternion.Euler(slopeDeg, 0, 0);   // +slope: rises towards -z.. (x rotation)
            ground.transform.position = new Vector3(0, -0.5f / Mathf.Cos(slopeDeg * Mathf.Deg2Rad), 0);
            return s;
        }

        static BuggyController NewBuggy(Vector3 pos, float headingDeg = 0f)
        {
            var b = BuggyBuilder.Build();
            var up = -TerrainFrame.DownAt(pos);
            b.PlaceAt(pos, Quaternion.AngleAxis(headingDeg, up));
            Physics.SyncTransforms();
            return b;
        }

        static void Step(BuggyController b, int steps, Action<int> perStep = null)
        {
            for (int i = 0; i < steps; i++)
            {
                LunarPhysics.AimGravity(b.transform.position);
                b.Step(Dt);
                Physics.Simulate(Dt);
                perStep?.Invoke(i);
            }
        }

        static float MeanX(BuggyController b) { float s = 0; foreach (var w in b.wheels) s += w.x; return s / b.wheels.Length; }

        // ------------------------------------------------------------------ tests

        static void TestEphemeris()
        {
            var s = Ephemeris.Compute(SimClock.MissionStartUtc, 14.0917, 303.2299, 1735.8);
            double el(double3 v) => math.degrees(math.asin(v.z));
            double az(double3 v) => (math.degrees(math.atan2(v.x, v.y)) + 360) % 360;
            Check("Sun elevation at mission start", el(s.sunEnu), 18, 22, "deg");
            Check("Sun azimuth (east)", az(s.sunEnu), 85, 105, "deg");
            Check("Earth elevation", el(s.earthEnu), 28, 36, "deg");
            Check("Earth azimuth (just south of east)", az(s.earthEnu), 92, 105, "deg");
            Check("Earth angular diameter", s.earthAngularRadiusDeg * 2, 1.78, 2.06, "deg");
            Check("Earth lit fraction (new Earth in lunar morning)", s.earthLitFraction * 100, 0, 5, "%");
            var e = Ephemeris.Compute(new DateTime(2030, 6, 15, 18, 10, 0, DateTimeKind.Utc), 14.0917, 303.2299, 1735.8);
            Check("Sun hidden by Earth at 2030-06-15 18:10 UTC (eclipse)", (1 - e.sunVisibleFraction) * 100, 50, 100, "%");
            var n = Ephemeris.Compute(SimClock.MissionStartUtc.AddDays(14.8), 14.0917, 303.2299, 1735.8);
            Check("Earth lit fraction ~2 weeks later (lunar evening/night)", n.earthLitFraction * 100, 85, 100, "%");
        }

        static void TestFreeFall()
        {
            NewFlatWorld();
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.transform.position = new Vector3(5, 10.5f, 5);   // bottom at 10 m
            var rb = ball.AddComponent<Rigidbody>();
            rb.linearDamping = 0; rb.angularDamping = 0;
            Physics.SyncTransforms();
            float t = 0;
            while (ball.transform.position.y > 0.5f + 1e-3f && t < 10f)
            {
                LunarPhysics.AimGravity(ball.transform.position);
                Physics.Simulate(Dt); t += Dt;
            }
            Check("Time to fall 10 m (theory sqrt(2h/g) = 3.508 s)", t, 3.45, 3.56, "s");
        }

        static void TestSag()
        {
            NewFlatWorld();
            var b = NewBuggy(Vector3.zero);
            b.parkingBrake = true;
            var csv = new StringBuilder("t,chassisY,susp_mean\n");
            float y0 = b.transform.position.y;
            Step(b, 2000, i => { if (i % 4 == 0) csv.AppendLine($"{i * Dt:0.000},{b.transform.position.y - y0:0.0000},{MeanX(b):0.0000}"); });
            File.WriteAllText(Path.Combine(OutDir, "settle.csv"), csv.ToString());
            float sag = b.travel - MeanX(b);
            Check("Static sag from full droop (design 12 cm)", sag * 100, 10, 14, "cm");
            float load = 0; foreach (var w in b.wheels) load += w.load;
            Check("Total tyre load = full rover weight (1640 kg x 1.625)", load, 1640 * 1.625 * 0.97, 1640 * 1.625 * 1.03, "N");
            Check("Body speed after 10 s settling (should be at rest)", b.Body.linearVelocity.magnitude * 1000, 0, 5, "mm/s");
            float sink = 0; foreach (var w in b.wheels) sink += w.sinkage;
            Check("Wheel sinkage in regolith (Bekker; Apollo LRV saw ~1-2 cm at lighter load)", sink / 4 * 100, 1.5, 4, "cm");
        }

        static void TestBump()
        {
            NewFlatWorld();
            var b = NewBuggy(Vector3.zero);
            b.parkingBrake = true;
            Step(b, 1000);
            // drop the whole rover 0.3 m (like rolling off a 30 cm ledge) and watch the body settle
            float yRest = b.transform.position.y;
            b.transform.position += Vector3.up * 0.3f; b.Body.position = b.transform.position; Physics.SyncTransforms();
            var csv = new StringBuilder("t,dy\n");
            float minY = float.MaxValue, maxAfter = float.MinValue; int tMin = 0;
            Step(b, 2400, i =>
            {
                float dy = b.transform.position.y - yRest;
                if (i % 2 == 0) csv.AppendLine($"{i * Dt:0.000},{dy:0.0000}");
                if (dy < minY) { minY = dy; tMin = i; }
            });
            File.WriteAllText(Path.Combine(OutDir, "drop_0p3m.csv"), csv.ToString());
            // overshoot above rest after the first compression
            var lines = csv.ToString().Split('\n');
            for (int k = 2 + tMin / 2; k < lines.Length - 1; k++)
            {
                var parts = lines[k].Split(','); if (parts.Length < 2) continue;
                maxAfter = Mathf.Max(maxAfter, float.Parse(parts[1], CultureInfo.InvariantCulture));
            }
            Check("Max compression below rest after 30 cm drop", -minY * 100, 3, 18, "cm");
            Check("Rebound overshoot above rest (well-damped: small)", maxAfter * 100, -1, 3.5, "cm");
            Check("Settled at rest height after 12 s", (b.transform.position.y - yRest) * 100, -0.5, 0.5, "cm");

            // drive over a 25 cm high, 1.5 m long bump at 12 km/h: does the rover leave the ground?
            NewFlatWorld();
            var bump = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            bump.transform.position = new Vector3(0, -0.5f, 30f);
            bump.transform.localScale = new Vector3(1.5f, 4f, 1.5f);   // cylinder lying across the path
            bump.transform.rotation = Quaternion.Euler(0, 0, 90);
            bump.transform.position = new Vector3(0, -0.75f + 0.25f, 30f);
            b = NewBuggy(Vector3.zero);
            Step(b, 400);
            b.throttle = 1f;
            float airTime = 0, maxLift = 0; float yGround = b.transform.position.y;
            float vAtBump = 0;
            var csv2 = new StringBuilder("t,z,y,FL,FR,RL,RR\n");
            Step(b, 4000, i =>
            {
                if (b.transform.position.z < 26f) b.throttle = 1f; else b.throttle = 0.4f;
                if (Mathf.Abs(b.transform.position.z - 29f) < 0.1f) vAtBump = b.Speed;
                int airborne = 0; foreach (var w in b.wheels) if (!w.grounded) airborne++;
                if (airborne == 4) airTime += Dt;
                if (b.transform.position.z > 28) maxLift = Mathf.Max(maxLift, b.transform.position.y - yGround);
                if (i % 4 == 0) csv2.AppendLine($"{i * Dt:0.00},{b.transform.position.z:0.00},{b.transform.position.y - yGround:0.000},{(b.wheels[0].grounded ? 1 : 0)},{(b.wheels[1].grounded ? 1 : 0)},{(b.wheels[2].grounded ? 1 : 0)},{(b.wheels[3].grounded ? 1 : 0)}");
            });
            File.WriteAllText(Path.Combine(OutDir, "bump_25cm.csv"), csv2.ToString());
            Note($"25 cm bump at {vAtBump * 3.6f:0.0} km/h: all four wheels off the ground for {airTime:0.00} s, body rose up to {maxLift * 100:0} cm.");
            Check("Rover still upright after bump", Vector3.Dot(b.transform.up, Vector3.up), 0.9, 1.0, "cos");
        }

        static void TestAcceleration()
        {
            NewFlatWorld();
            var b = NewBuggy(Vector3.zero);
            Step(b, 600);
            b.throttle = 1f;
            float t10 = -1, t = 0;
            double e0 = b.EnergyUsedWh, d0 = b.DistanceM;
            var csv = new StringBuilder("t,v_kmh,P_kW,slip_FL\n");
            Step(b, 6000, i =>
            {
                t += Dt;
                if (t10 < 0 && b.Speed >= 10f / 3.6f) t10 = t;
                if (i % 20 == 0) csv.AppendLine($"{t:0.00},{b.Speed * 3.6f:0.00},{b.PowerDraw / 1000f:0.000},{b.wheels[0].slip:0.000}");
            });
            File.WriteAllText(Path.Combine(OutDir, "acceleration.csv"), csv.ToString());
            Check("0-10 km/h time (LRV-like gentle acceleration)", t10, 2, 12, "s");
            Check("Governed top speed", b.Speed * 3.6f, 14, 15.6, "km/h");
            float slip = 0; foreach (var w in b.wheels) slip += w.slip;
            Check("Cruise wheel slip on regolith (LRV measured a few %)", slip / 4 * 100, 0, 12, "%");
            // steady cruise energy
            double e1 = b.EnergyUsedWh, d1 = b.DistanceM;
            Step(b, 4000);
            double whkm = (b.EnergyUsedWh - e1) / ((b.DistanceM - d1) / 1000.0);
            Check("Energy at 15 km/h cruise on level regolith", whkm, 40, 200, "Wh/km");
            Note($"Range on a full 40 kWh pack at that rate: {40000 / whkm:0} km (flat, no climbing).");
        }

        static void TestBraking()
        {
            NewFlatWorld();
            var b = NewBuggy(Vector3.zero);
            Step(b, 400);
            b.throttle = 1f;
            Step(b, 6000);
            float v0 = b.Speed;
            float z0 = b.transform.position.z;
            b.throttle = 0; b.brake = 1f;
            float t = 0;
            Step(b, 6000, i => { if (b.Speed > 0.02f) t += Dt; });
            float d = b.transform.position.z - z0;
            Note($"Braking from {v0 * 3.6f:0.0} km/h: {d:0.0} m in {t:0.0} s (mean decel {v0 / Mathf.Max(t, 0.01f):0.00} m/s^2 = {v0 / Mathf.Max(t, 0.01f) / Lunar.Gravity:0.00} g_moon).");
            Check("Stopping distance from top speed (friction-limited, ~v^2/(2 mu g))", d, 3, 15, "m");
            Check("Rover upright after emergency stop", Vector3.Dot(b.transform.up, Vector3.up), 0.95, 1, "cos");
        }

        static void TestSlopeHold()
        {
            NewFlatWorld(15f);
            var b = NewBuggy(new Vector3(0, 0, 0), 90f);   // parked across... heading east on a north-south slope -> facing along x
            b.transform.rotation = Quaternion.LookRotation(Quaternion.Euler(15, 0, 0) * Vector3.forward, Quaternion.Euler(15, 0, 0) * Vector3.up);
            b.Body.rotation = b.transform.rotation;
            b.PlaceAt(b.transform.position, b.transform.rotation);
            b.parkingBrake = true;
            Step(b, 1000);
            var p0 = b.transform.position;
            Step(b, 2000);
            float drift = (b.transform.position - p0).magnitude;
            Check("Drift over 10 s parked nose-down on a 15 deg slope", drift * 100, 0, 3, "cm");
        }

        static void TestClimb()
        {
            foreach (float slope in new[] { 15f, 25f })
            {
                NewFlatWorld(slope);
                var rot = Quaternion.Euler(-slope, 0, 0);   // nose up the slope? ground rotated +x -> rises towards -z; face -z
                var b = NewBuggy(Vector3.zero);
                var up = Quaternion.Euler(slope, 0, 0) * Vector3.up;
                var fwd = Quaternion.Euler(slope, 0, 0) * Vector3.back;
                b.PlaceAt(Vector3.zero + up * 0.1f, Quaternion.LookRotation(fwd, up));
                Step(b, 600);
                b.throttle = 1f;
                float y0 = b.transform.position.y;
                Step(b, 4000);
                float climbed = b.transform.position.y - y0;
                float slip = 0; foreach (var w in b.wheels) slip += w.slip;
                Note($"{slope:0} deg slope: speed {b.Speed * 3.6f:0.0} km/h, mean slip {slip / 4 * 100:0}%, climbed {climbed:0.0} m in 20 s.");
                if (slope <= 15f) Check($"Can climb {slope:0} deg (LRV rated 25 deg)", climbed, 5, 200, "m");
                else Check($"Progress on {slope:0} deg (near the regolith traction limit)", climbed, 0.5, 200, "m");
            }
        }

        /// <summary>
        /// Steepest grade the rover climbs from rest (full throttle, 20 s) on regolith, talus and bare basalt, and what
        /// stops it beyond that: grip (slip -> 1, friction ellipse) or the motors (torque / power). Answers the
        /// round-7 "can't climb the side-tube bank" note: if grip, the bank is steeper than the soil allows.
        /// </summary>
        static void TestMaxGrade()
        {
            MaxGradeAt(1f);
            // round 12: the game's default grip (x2) - the GIMLI-1 bowl case: what limits the climb now (grip or torque)?
            Tuning.WheelGrip.Value = Tuning.WheelGrip.Default;
            try { MaxGradeAt(Tuning.WheelGrip.Default); } finally { Tuning.WheelGrip.Value = 1f; }
        }

        static void MaxGradeAt(float grip)
        {
            var soils = new (string name, Soil soil)[] { ("regolith", Soil.Regolith), ("talus", Soil.Talus), ("basalt", Soil.Basalt) };
            foreach (var (name, soil) in soils)
            {
                float best = 0; string why = "-";
                for (float slope = 15f; slope <= 50f; slope += 2.5f)
                {
                    NewFlatWorld(slope);
                    var sl = soil; GroundMaterial.Provider = _ => sl;
                    var b = NewBuggy(Vector3.zero);
                    var up = Quaternion.Euler(slope, 0, 0) * Vector3.up;
                    var fwd = Quaternion.Euler(slope, 0, 0) * Vector3.back;
                    b.PlaceAt(up * 0.1f, Quaternion.LookRotation(fwd, up));
                    b.parkingBrake = true; Step(b, 400); b.parkingBrake = false;
                    b.throttle = 1f;
                    float y0 = b.transform.position.y;
                    var lim = new Dictionary<string, int>();
                    Step(b, 4000, i => { if (i > 1000 && i % 50 == 0) foreach (var w in b.wheels) { var k = string.IsNullOrEmpty(w.limit) ? "-" : w.limit; lim[k] = lim.TryGetValue(k, out var c) ? c + 1 : 1; } });
                    float climbed = b.transform.position.y - y0;
                    string top = lim.Count > 0 ? lim.OrderByDescending(kv => kv.Value).First().Key : "-";
                    float slip = 0; foreach (var w in b.wheels) slip += Mathf.Abs(w.slip);
                    if (climbed > 2f) best = slope;
                    else { why = $"{top} (mean slip {slip / 4 * 100:0}%)"; break; }
                }
                GroundMaterial.Provider = null;
                Note($"Grip x{grip:0.#}: max climbable grade on {name}: **{best:0.0} deg**; one step steeper it stalls, limited by {why}.");
                if (grip > 1f)
                {
                    if (name == "regolith") { Check("Grip x2 (default): regolith climb from rest", best, 25, 60, "deg"); }
                    continue;
                }
                if (name == "basalt") Check("Bare tube basalt: climbs the 14 deg design ramps with margin", best, 20, 60, "deg");
                if (name == "talus") Check("Talus (ramps, pit floor): climbs the 14 deg design ramps with margin", best, 20, 60, "deg");
            }
        }

        static void TestTurn()
        {
            NewFlatWorld();
            var b = NewBuggy(Vector3.zero);
            Step(b, 400);
            b.throttle = 0.6f;
            Step(b, 3000);
            b.steering = 1f;
            float maxRoll = 0, lat = 0;
            Step(b, 4000, i =>
            {
                maxRoll = Mathf.Max(maxRoll, Vector3.Angle(b.transform.up, Vector3.up));
                lat = Vector3.Dot(b.Body.linearVelocity, b.transform.right);
            });
            float v = b.Speed; float w = b.Body.angularVelocity.y;
            Note($"Full lock at {v * 3.6f:0.0} km/h: yaw rate {w * Mathf.Rad2Deg:0} deg/s, lateral accel {Mathf.Abs(v * w):0.00} m/s^2 ({Mathf.Abs(v * w) / Lunar.Gravity:0.00} g_moon), body roll {maxRoll:0.0} deg.");
            Check("Rover stays upright in a full-lock turn", maxRoll, 0, 20, "deg");
        }


        static void TestAstronaut()
        {
            NewFlatWorld();
            var a = AstronautController.Create(new Vector3(0, 0.02f, 0), Quaternion.identity);
            Physics.SyncTransforms();
            void StepA(int n, Action<int> per = null)
            {
                for (int i = 0; i < n; i++) { LunarPhysics.AimGravity(a.transform.position); a.Step(Dt); Physics.Simulate(Dt); per?.Invoke(i); }
            }
            StepA(400);
            Check("Standing: astronaut at rest on the ground", a.Body.linearVelocity.magnitude, 0, 0.02, "m/s");
            Check("Weight on the Moon (205 kg suited)", AstronautController.Mass * Lunar.Gravity, 330, 336, "N");
            float y0 = a.transform.position.y, peak = y0, air = 0;
            a.jumpRequest = true;
            StepA(800, i => { peak = Mathf.Max(peak, a.transform.position.y); if (!a.Grounded) air += Dt; });
            Check("Standing jump height (theory v^2/2g = 0.48 m)", peak - y0, 0.4, 0.56, "m");
            Check("Jump airtime (theory 2v/g = 1.54 s)", air, 1.3, 1.7, "s");
            a.move = new Vector2(0, 1); a.run = false;
            StepA(1600);
            Check("Walking speed", a.Speed, 0.9, 1.25, "m/s");
            a.run = true;
            float airFrac = 0; int n2 = 0; float vSum = 0;
            StepA(3000, i => { if (i > 1000) { n2++; if (!a.Grounded) airFrac += 1; vSum += Vector3.ProjectOnPlane(a.Body.linearVelocity, Vector3.up).magnitude; } });
            Check("Lope (bounding) speed", vSum / n2, 1.6, 2.6, "m/s");
            Check("Lope: fraction of time airborne (Apollo lope ~0.4-0.6)", airFrac / n2, 0.25, 0.8, "");
            a.move = Vector2.zero; a.run = false;
            var p0 = a.transform.position; float tStop = 0;
            StepA(2000, i => { if (a.Speed > 0.05f) tStop += Dt; });
            Note($"Stopping from a lope: {(a.transform.position - p0).magnitude:0.0} m in {tStop:0.0} s (friction-limited, mu 0.6).");
            Check("Stops from a lope within", (a.transform.position - p0).magnitude, 0.5, 6, "m");
            // standing idle on a 20 deg slope: the boots hold (no sliding)
            NewFlatWorld(20f);
            a = AstronautController.Create(new Vector3(0, 0.05f, 0), Quaternion.identity);
            Physics.SyncTransforms();
            StepA(400);
            var s0 = a.transform.position;
            StepA(2000);
            Check("On foot: idle drift over 10 s on a 20 deg slope", (a.transform.position - s0).magnitude * 100, 0, 5, "cm");
        }

        static void TestRighting()
        {
            NewFlatWorld();
            var b = NewBuggy(Vector3.zero);
            b.transform.rotation = Quaternion.Euler(0, 30, 95);
            b.transform.position = new Vector3(0, 1.2f, 0); b.Body.position = b.transform.position; b.Body.rotation = b.transform.rotation;
            Physics.SyncTransforms();
            Step(b, 800);
            Note($"Before R: tilt {Vector3.Angle(b.transform.up, Vector3.up):0} deg.");
            b.Right();
            Physics.SyncTransforms();
            Step(b, 1000);
            int g = 0; foreach (var w in b.wheels) g += w.grounded ? 1 : 0;
            Check("After R: tilt", Vector3.Angle(b.transform.up, Vector3.up), 0, 5, "deg");
            Check("After R: wheels on the ground", g, 4, 4, "wheels");
        }

        /// <summary>
        /// Play-test round 4: flipping on a slope and landing on the wheel edges relaunched the rover into
        /// repeated flips. Drops rolled 60-80 deg from 0.5 m on flat ground and a tip on a 20 deg side slope.
        /// </summary>
        static void TestHardLanding()
        {
            foreach (var (h, expectHard) in new[] { (40f, true), (0.3f, false) })
            {
                NewFlatWorld();
                Radio.Clear();
                var b = NewBuggy(Vector3.zero);
                Step(b, 400, _ => Radio.Tick(Dt));   // settle at static sag
                int hard0 = b.Impact.HardCount, rem0 = b.Impact.RemarkCount;
                b.transform.position += Vector3.up * h; b.Body.position = b.transform.position;
                b.Body.linearVelocity = Vector3.zero; b.Body.angularVelocity = Vector3.zero;
                Physics.SyncTransforms();
                float peak = 0, tHard = -1;
                Step(b, 200 * 14, _ => { Radio.Tick(Dt); peak = Mathf.Max(peak, b.Impact.CurrentG); if (tHard < 0 && b.Impact.HardCount > hard0) tHard = Radio.Now; });
                Note($"Drop {h:0.0} m: peak {peak:0.0} g (0.1 s window), hard {b.Impact.HardCount - hard0}, remarks {b.Impact.RemarkCount - rem0}, HUD '{(b.Impact.HardCount > hard0 ? b.Impact.HudText : "-")}'. Radio: {string.Join(" | ", Radio.Log)}");
                if (expectHard)
                {
                    Check($"Hard landing: {h:0} m drop flagged (> {ImpactMonitor.HardG} g)", b.Impact.HardCount - hard0, 1, 1, "events");
                    string capcom = Radio.Log.LastOrDefault(l => l.Contains("HOUSTON") && l.Contains("suspension"));
                    float at = -1;
                    if (capcom != null) { var parts = capcom.Split(' '); float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out at); }
                    Check("Hard landing: CAPCOM asks about the suspension after the round trip", capcom != null && tHard > 0 ? at - tHard : -1, Radio.RoundTrip, Radio.RoundTrip + 3, "s");
                }
                else
                    Check($"Hard landing: {h * 100:0} cm drop -> no warning, no remark", (b.Impact.HardCount - hard0) + (b.Impact.RemarkCount - rem0), 0, 0, "events");
            }
            Radio.Clear();
        }

        static void TestRollover()
        {
            var cases = new (string name, float slope, float roll)[] { ("flat, rolled 60", 0, 60), ("flat, rolled 70", 0, 70), ("flat, rolled 80", 0, 80),
                                                                         ("20 deg side slope, rolled 70 downhill", 20, -70), ("20 deg side slope, rolled 70 uphill", 20, 70) };
            float worstRebound = 0, worstRest = 0, worstTurn = 0;
            var sb = new StringBuilder();
            foreach (var (name, slope, roll) in cases)
            {
                NewFlatWorld(slope);
                // heading along +x: the slope (rising towards -z) is to the rover's side
                var b = NewBuggy(Vector3.zero, 90f);
                var rot = Quaternion.Euler(slope, 0, 0) * Quaternion.Euler(0, 90, 0) * Quaternion.Euler(0, 0, roll);
                b.transform.rotation = rot;
                b.Body.rotation = rot;
                Physics.SyncTransforms();
                // lowest hull/wheel point 0.5 m above the ground
                var ground = GameObject.Find("Ground").GetComponent<Collider>();
                float lift = 0;
                for (int k = 0; k < 40 && lift == 0; k++)
                {
                    var p = b.transform.position;
                    bool clear = true;
                    foreach (var c in b.GetComponentsInChildren<Collider>())
                        if (Physics.ComputePenetration(c, c.transform.position, c.transform.rotation, ground, ground.transform.position, ground.transform.rotation, out _, out _)) clear = false;
                    if (clear) lift = 1; else { b.transform.position = p + Vector3.up * 0.1f; Physics.SyncTransforms(); }
                }
                b.transform.position += Vector3.up * 0.5f; b.Body.position = b.transform.position;
                b.Body.linearVelocity = Vector3.zero; b.Body.angularVelocity = Vector3.zero;
                Physics.SyncTransforms();
                float y0 = b.Body.worldCenterOfMass.y, minY = float.MaxValue, rebound = 0, turned = 0, restT = -1, impactT = 0;
                bool impact = false; int still = 0;
                var tr = new StringBuilder("t,comY,vy,tilt,wheels,hull,angVel\n");
                Step(b, 200 * 10, i =>
                {
                    float y = b.Body.worldCenterOfMass.y;
                    if (i % 10 == 0) { int gw = 0; foreach (var w in b.wheels) gw += w.grounded ? 1 : 0; tr.AppendLine($"{i * Dt:0.00},{y:0.000},{b.Body.linearVelocity.y:0.000},{Vector3.Angle(b.transform.up, Vector3.up):0.0},{gw},{(b.HullContact ? 1 : 0)},{b.Body.angularVelocity.magnitude:0.000}"); }
                    if (!impact && b.Body.linearVelocity.y > -0.05f && i > 20) { impact = true; impactT = i * Dt; }
                    if (impact)
                    {
                        // ballistic rebound: rise while nothing (no tyre, no hull) touches the ground
                        int gw2 = 0; foreach (var w in b.wheels) gw2 += w.grounded ? 1 : 0;
                        bool air = gw2 == 0 && !b.HullContact;
                        if (!air) minY = y; else rebound = Mathf.Max(rebound, y - minY);
                        turned += b.Body.angularVelocity.magnitude * Dt * Mathf.Rad2Deg;
                    }
                    bool rest = b.Body.linearVelocity.magnitude < 0.05f && b.Body.angularVelocity.magnitude < 0.05f;
                    still = rest ? still + 1 : 0;
                    if (still == 100 && restT < 0) restT = (i - 99) * Dt - impactT;
                });
                if (restT < 0) restT = 99;
                File.WriteAllText(Path.Combine(OutDir, $"rollover_{slope:0}_{roll:0}.csv"), tr.ToString());
                sb.Append($"{name}: rebound {rebound:0.00} m, rest {restT:0.0} s after impact, rotation after impact {turned:0} deg, ends tilted {Vector3.Angle(b.transform.up, Vector3.up):0} deg; ");
                worstRebound = Mathf.Max(worstRebound, rebound); worstRest = Mathf.Max(worstRest, restT); worstTurn = Mathf.Max(worstTurn, turned);
                Util.Kill(b.gameObject);
            }
            Note(sb.ToString());
            Check("Rollover drops: worst ballistic rebound after first impact", worstRebound, 0, 0.3, "m");
            Check("Rollover drops: worst time to come to rest", worstRest, 0, 7, "s");
            Check("Rollover drops: worst rotation after impact (a second flip would be > 270)", worstTurn, 0, 270, "deg");
        }

        // ------------------------------------------------------------------ real world

        /// <summary>
        /// Round 8: (a) tubes that meet at different floor heights (or cross with a thin roof) became junctions with talus
        /// ramps; clean over/under crossings keep >= MinRoof of rock. (b) every height difference has a ramp under the
        /// design grade, measured on the real SDF floor (noise included). (c) every tube in a test block is reachable by
        /// driving from the pit mouth, on every level, and so has a drivable way back up to level 0.
        /// </summary>
        static void TestJunctions()
        {
            var net = new TunnelNetwork(12345u, new float2(0, 0), new float2(-0.9f, 0.44f), 0f, -48.5f, 30f);
            int L0 = 0, L1 = 3, I0 = 0, I1 = 10, J0 = -5, J1 = 5;
            var crossings = net.CrossingsIn(L0, L1, I0, I1, J0, J1);
            var all = new List<TunnelPrim>();
            for (int L = L0; L <= L1 + 1; L++) for (int i = I0 - 3; i <= I1 + 1; i++) for (int j = J0 - 2; j <= J1 + 2; j++) net.CellPrims(L, i, j, all);
            var ramps = all.Where(p => p.kind == 2).ToList();
            int junctions = 0, unresolved = 0, clean = 0, overRamp = 0; float minCleanGap = 1e9f;
            var expect = new List<TunnelPrim>();
            foreach (var c in crossings)
            {
                if (!c.Junction) { clean++; minCleanGap = Mathf.Min(minCleanGap, c.gap); continue; }
                junctions++;
                if (c.hi.kind == 1) { overRamp++; continue; }   // a level ramp over a lower tube: no trench (see RampsFor)
                expect.Clear(); TunnelNetwork.RampsFor(c, 0u, expect);
                int n = expect.Count(e => ramps.Any(r => math.distance(r.a, e.a) < 0.01f && math.distance(r.b, e.b) < 0.01f));
                if (expect.Count == 0 && c.drop / math.tan(math.radians(TunnelNetwork.JunctionRampDeg)) > 150f) { overRamp++; continue; }   // too deep for a 12 deg trench inside the tube (no room)
                if (expect.Count == 0 || n < expect.Count) { unresolved++; if (unresolved <= 4) Note($"unresolved: hi L{c.hi.level} type{c.hi.type} kind{c.hi.kind} a{c.hi.a} b{c.hi.b} hw{c.hi.hw:0}; lo L{c.lo.level} type{c.lo.type} kind{c.lo.kind} a{c.lo.a}; drop {c.drop:0.0} gap {c.gap:0.0}; expect {expect.Count}, found {n}"); }
            }
            Note($"Crossings in levels {L0}-{L1}, rows {I0}-{I1}, columns {J0}-{J1}: {crossings.Count} at different floors -> {junctions} junctions (talus ramps; {overRamp} of them level ramps over a lower tube or too deep for a trench, left as they are), {clean} clean over/under (min roof {(clean > 0 ? minCleanGap : 0):0.0} m).");
            Check("(a) every crossing at a different floor height is a junction with ramps (unresolved)", unresolved, 0, 0, "");
            if (clean > 0) Check("(a) over/under crossings keep a solid roof", minCleanGap, TunnelNetwork.MinRoof, 1e6, "m");
            float maxJ = 0, maxL = 0;
            foreach (var p in all)
            {
                float h = math.distance(p.a.xz, p.b.xz); if (h < 1f) continue;
                float g = math.degrees(math.atan(math.abs(p.a.y - p.b.y) / h));
                if (p.kind == 2) maxJ = Mathf.Max(maxJ, g);
                if (p.kind == 1) maxL = Mathf.Max(maxL, g);
            }
            // 12 deg by design; a ramp cut along an 18 deg level ramp follows that ramp's own grade
            Check("(b) junction ramps: mean grade", maxJ, 0, TunnelNetwork.MaxLevelRampDeg + 2, "deg");
            Check("(b) level ramps: mean grade (incl. node jitter)", maxL, 0, TunnelNetwork.MaxLevelRampDeg + 0.5, "deg");

            // (b) measured on the SDF floor: junction ramps and lava falls, 4 m baseline (noise and rubble included)
            float worstRamp = 0, worstFall = 0; int measuredR = 0, measuredF = 0; float lastWorst = 0; string worstProfile = "";
            var buf = new List<TunnelPrim>();
            float FloorSdf(float3 p, Unity.Collections.NativeArray<TunnelPrim> arr, int n)
            {
                float y = p.y + 3f;
                if (TunnelSdf.Eval(arr, n, new float3(p.x, y, p.z), out _, out _) > 0) return float.NaN;   // not in air above
                for (int k = 0; k < 120; k++) { y -= 0.1f; if (TunnelSdf.Eval(arr, n, new float3(p.x, y, p.z), out _, out _) > 0) return y; }
                return float.NaN;
            }
            float Profile(float3 a, float3 b)
            {
                buf.Clear();
                net.Collect(math.min(a, b) - 30f, math.max(a, b) + 30f, 8f, buf);
                var arr = new Unity.Collections.NativeArray<TunnelPrim>(buf.ToArray(), Unity.Collections.Allocator.Temp);
                float len = math.distance(a.xz, b.xz), worst = 0, prev = float.NaN;
                var prof = new StringBuilder();
                for (float d = 0; d <= len; d += 8f)
                {
                    var p = math.lerp(a, b, d / len);
                    float f = FloorSdf(p, arr, arr.Length);
                    prof.Append(float.IsNaN(f) ? "x " : $"{f - a.y:0.0} ");
                    if (!float.IsNaN(f) && !float.IsNaN(prev)) worst = Mathf.Max(worst, math.degrees(math.atan(math.abs(f - prev) / 8f)));
                    prev = f;
                }
                arr.Dispose();
                if (worst > lastWorst) { lastWorst = worst; worstProfile = prof.ToString(); }
                return worst;
            }
            foreach (var r in ramps.Take(12)) { worstRamp = Mathf.Max(worstRamp, Profile(r.a, r.b)); measuredR++; }
            foreach (var p in all.Where(p => p.type == TunnelPrim.Tube && p.drop > 0 && p.kind == 0).Take(12))
            {
                float w = p.dropW > 0 ? p.dropW : 0.04f;
                var a = math.lerp(p.a, p.b, math.max(0, p.dropT - w * 1.5f)); var b = math.lerp(p.a, p.b, math.min(1, p.dropT + w * 1.5f));
                a.y = TunnelNetwork.FloorAt(p, math.max(0, p.dropT - w * 1.5f)); b.y = TunnelNetwork.FloorAt(p, math.min(1, p.dropT + w * 1.5f));
                worstFall = Mathf.Max(worstFall, Profile(a, b)); measuredF++;
            }
            // reference: plain level tube floors (rubble + noise only)
            float plain = 0;
            foreach (var p in all.Where(p => p.type == TunnelPrim.Tube && p.drop == 0 && p.kind == 0 && p.moundH == 0).Take(6))
                plain = Mathf.Max(plain, Profile(math.lerp(p.a, p.b, 0.3f), math.lerp(p.a, p.b, 0.7f)));
            Note($"SDF floor profiles (8 m steps): {measuredR} junction ramps, worst {worstRamp:0.0} deg; {measuredF} lava falls, worst {worstFall:0.0} deg; " +
                 $"plain tube floors (rubble only) {plain:0.0} deg. Steepest profile (m vs start): {worstProfile}. Rover: ~30 deg on talus, ~32 deg on basalt.");
            Check("(b) junction ramps on the real floor (8 m slope)", worstRamp, 0, 25, "deg");
            Check("(b) lava-fall steps on the real floor (8 m slope)", worstFall, 0, 25, "deg");

            // (c) reachability: prims meet when their floors join (same level within 2.5 m, footprints touching)
            var prims = all.Where(p => p.type != TunnelPrim.Shaft).Distinct().ToList();
            bool Meets(in TunnelPrim x, in TunnelPrim y)
            {
                if (x.type == TunnelPrim.Chamber && y.type == TunnelPrim.Chamber) return math.distance(x.a, y.a) < 1f;
                float3 px, py; float wx, wy;
                if (x.type == TunnelPrim.Tube && y.type == TunnelPrim.Tube)
                {
                    // endpoints first (junction nodes), then the closest approach
                    foreach (var e1 in new[] { 0f, 1f }) foreach (var e2 in new[] { 0f, 1f })
                    {
                        var p1 = math.lerp(x.a, x.b, e1); var p2 = math.lerp(y.a, y.b, e2);
                        if (math.distance(p1.xz, p2.xz) < 2f && math.abs(TunnelNetwork.FloorAt(x, e1) - TunnelNetwork.FloorAt(y, e2)) < 2.5f) return true;
                    }
                    float best = 1e9f; float bs = 0, bt = 0;
                    for (float s = 0; s <= 1f; s += 0.02f)
                    {
                        var q = math.lerp(x.a, x.b, s);
                        var ab = y.b.xz - y.a.xz; float t = math.saturate(math.dot(q.xz - y.a.xz, ab) / math.max(1e-3f, math.lengthsq(ab)));
                        float d = math.distance(q.xz, math.lerp(y.a.xz, y.b.xz, t));
                        if (d < best && math.abs(TunnelNetwork.FloorAt(x, s) - TunnelNetwork.FloorAt(y, t)) < 2.5f) { best = d; bs = s; bt = t; }
                    }
                    wx = math.lerp(x.hw, x.hwEnd, bs); wy = math.lerp(y.hw, y.hwEnd, bt);
                    return best < wx + wy;
                }
                var T = x.type == TunnelPrim.Tube ? x : y; var C = x.type == TunnelPrim.Tube ? y : x;
                for (float s = 0; s <= 1f; s += 0.02f)
                {
                    var q = math.lerp(T.a, T.b, s);
                    if (math.distance(q.xz, C.a.xz) < C.hw + math.lerp(T.hw, T.hwEnd, s) * 0.5f && math.abs(TunnelNetwork.FloorAt(T, s) - C.a.y) < 2.5f) return true;
                }
                return false;
            }
            int start = prims.FindIndex(p => p.type == TunnelPrim.Tube && math.distance(p.a, net.mouthFloor) < 0.01f);
            var seen = new bool[prims.Count]; var queue = new Queue<int>();
            if (start >= 0) { seen[start] = true; queue.Enqueue(start); }
            while (queue.Count > 0)
            {
                int k = queue.Dequeue();
                for (int m = 0; m < prims.Count; m++)
                    if (!seen[m] && Meets(prims[k], prims[m])) { seen[m] = true; queue.Enqueue(m); }
            }
            // inner block: prims whose own node (a for tubes, centre for chambers) lies well inside the scanned area
            int inner = 0, reached = 0; var perLevel = new int[L1 + 1]; var perLevelOk = new int[L1 + 1];
            var missing = new List<string>();
            for (int k = 0; k < prims.Count; k++)
            {
                var uv = net.ToUV(prims[k].a.xz);
                if (uv.x < 1.2f * TunnelNetwork.Cell || uv.x > 7f * TunnelNetwork.Cell || math.abs(uv.y) > 2.5f * TunnelNetwork.Cell) continue;
                if (prims[k].level > L1) continue;
                int lv = math.clamp(prims[k].level, 0, L1);
                inner++; perLevel[lv]++;
                if (seen[k]) { reached++; perLevelOk[lv]++; }
                else if (missing.Count < 6) missing.Add($"L{prims[k].level} {(prims[k].type == TunnelPrim.Chamber ? "chamber" : prims[k].kind == 2 ? "junction ramp" : prims[k].kind == 1 ? "level ramp" : prims[k].deadEnd != 0 ? "spur" : "tube")} at uv {uv.x:0},{uv.y:0}");
            }
            Note($"Reachability from the pit mouth: {reached}/{inner} tubes/chambers in the inner block; per level " +
                 string.Join(", ", Enumerable.Range(0, L1 + 1).Select(l => $"L{l} {perLevelOk[l]}/{perLevel[l]}")) + (missing.Count > 0 ? ". Not reached: " + string.Join("; ", missing) : "."));
            Check("(c) every tube / chamber in the block drivable from the pit (and back up to level 0)", inner > 0 ? reached / (float)inner : 0, 0.999, 1, "");
        }

        static void TestNetwork()
        {
            var a = new TunnelNetwork(12345u, new Unity.Mathematics.float2(0, 0), new Unity.Mathematics.float2(-0.9f, 0.44f), 0f, -48.5f, 30f);
            var b = new TunnelNetwork(12345u, new Unity.Mathematics.float2(0, 0), new Unity.Mathematics.float2(-0.9f, 0.44f), 0f, -48.5f, 30f);
            var c = new TunnelNetwork(999u, new Unity.Mathematics.float2(0, 0), new Unity.Mathematics.float2(-0.9f, 0.44f), 0f, -48.5f, 30f);
            var la = new List<TunnelPrim>(); var lb = new List<TunnelPrim>(); var lc = new List<TunnelPrim>();
            var mn = new Unity.Mathematics.float3(-3000, -400, -3000); var mx = new Unity.Mathematics.float3(0, 0, 0);
            a.Collect(mn, mx, 0, la); b.Collect(mn, mx, 0, lb); c.Collect(mn, mx, 0, lc);
            bool same = la.Count == lb.Count && la.Zip(lb, (p, q) => p.a.Equals(q.a) && p.b.Equals(q.b) && p.hw == q.hw).All(x => x);
            Check("Same seed -> identical tunnel network", same ? 1 : 0, 1, 1, "");
            bool diff = la.Count != lc.Count || !la.Zip(lc, (p, q) => p.a.Equals(q.a)).All(x => x);
            Check("Different seed -> different network", diff ? 1 : 0, 1, 1, "");

            // follow the backbone a long way: widths, grade, slopes, depth
            var widths = new List<float>(); float maxSlope = 0; int steps = 400;
            int L = 0, i = 0, j = 0;
            var start = a.NodePos(0, 0, 0); var pos = start;
            for (int s2 = 0; s2 < steps; s2++)
            {
                var tmp = new List<TunnelPrim>(); a.CellPrims(L, i, j, tmp);
                var bb = tmp.First(t => t.type == TunnelPrim.Tube);
                widths.Add(2 * bb.hw);
                float run = Unity.Mathematics.math.length(bb.b.xz - bb.a.xz);
                float slope = Unity.Mathematics.math.degrees(Unity.Mathematics.math.atan(Unity.Mathematics.math.abs(bb.b.y - bb.a.y) / run));
                // a lava-fall step: smoothstep over 2 x dropW of the segment, peak slope 1.5 x its mean
                float w = bb.dropW > 0 ? bb.dropW : 0.04f;
                float stepSlope = bb.drop > 0 ? Unity.Mathematics.math.degrees(Unity.Mathematics.math.atan(1.5f * bb.drop / (2f * w * run) + Unity.Mathematics.math.abs(bb.b.y - bb.a.y) / run)) : slope;
                maxSlope = Unity.Mathematics.math.max(maxSlope, Unity.Mathematics.math.max(slope, stepSlope));
                j += a.BackboneDj(L, i, j); i++;
                pos = a.NodePos(L, i, j);
            }
            widths.Sort();
            float along = a.ToUV(pos.xz).x - a.ToUV(start.xz).x;
            Check("Backbone median width (published estimates: tens to ~100+ m)", widths[widths.Count / 2], 45, 80, "m");
            Check("Backbone min width (drivable >= 40 m)", widths[0], 40, 200, "m");
            Check("Mean grade along the network (rille above: 1.04%)", (start.y - pos.y) / along * 100f, 0.8, 1.3, "%");
            Check("Steepest backbone slope incl. lava-fall steps (rover rated 25 deg)", maxSlope, 0, 25, "deg");
            Note($"Backbone followed {steps} junctions = {along / 1000f:0.0} km down-tube (endless: every junction has a way onward).");
            // level spacing and ramps exist
            var path = a.ExplorePath(40, 3);
            Check("Explorer path reaches level 3 within 40 junctions (ramps down exist)", path[path.Count - 1].level, 3, 3, "");
        }

        static void TestWorld()
        {
            EditorSceneManager.OpenScene("Assets/_Project/Scenes/Main.unity");
            var game = UnityEngine.Object.FindAnyObjectByType<GameBootstrap>();
            if (game == null) { Fail("Main scene has no GameBootstrap (run SceneBuilder.Build)"); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            game.Initialise();
            Note($"World initialised in {sw.ElapsedMilliseconds} ms (terrain, pit, base, crane, rover). Seed {WorldSeed.Value}.");
            var b = game.Buggy;
            var cave = game.Cave;

            // ---- start at the lander
            Step(b, 1000, i => { if (i % 200 == 0) game.Streamer.BuildNow(b.transform.position, b.transform.position); });
            float g0 = 0; foreach (var w in b.wheels) g0 += w.grounded ? 1 : 0;
            Check("Start (by the lander): all wheels on the ground", g0, 4, 4, "wheels");
            Check("Start: rover at rest", b.Body.linearVelocity.magnitude, 0, 0.05, "m/s");
            Shots(game, "01_start");
            {
                // tuning keys: \ selects, ] raises x1.25 (exposure: +1/3 EV), Shift+\ resets
                var hl = PhysicalLight.All.FirstOrDefault(p => p != null && p.name == "HeadlightSpot");
                float b0 = hl != null ? hl.physicalIntensity : 0;
                Tuning.Select(Tuning.Headlights); Tuning.Apply(game.Exposure); Tuning.Step(+1); Tuning.Step(+1); Tuning.Apply(game.Exposure);
                Check("Tuning: headlights after two ] presses (x1.25^2)", hl != null ? hl.physicalIntensity / b0 : -1, 1.56, 1.57, "x");
                Tuning.Reset(); Tuning.Apply(game.Exposure);
                Check("Tuning: Shift+backslash resets the headlights", hl != null ? hl.physicalIntensity / b0 : -1, 0.999, 1.001, "x");
                Tuning.Select(Tuning.ExposureEv); Tuning.Step(+1); Tuning.Apply(game.Exposure);
                Check("Tuning: exposure step", game.Exposure.evCompensation, 0.33, 0.34, "EV");
                Tuning.Reset(); Tuning.Apply(game.Exposure); Tuning.Selected = 0;
                Check("Tuning: the round-8 knobs come first (rover top speed)", Tuning.All[0] == Tuning.RoverSpeed ? 1 : 0, 1, 1, "");
            }
            LanderToPitDrive(game);
            game.Teleport(0);
            Step(b, 600);
            BaseShots(game);
            VisorShots(game);

            // ---- crane: drive onto the platform, lower into the pit, drive off
            var crane = game.Crane;
            var plat = crane.Platform.transform;
            // drive onto the platform from the approach spot (teleport 2), like a player would
            float DriveOntoDeck()
            {
                float tDrive = 0; b.parkingBrake = false; b.brake = 0;
                for (int k = 0; k < 200 * 120 && !(crane.RoverFullyOnDeck && Mathf.Abs(b.Speed) < 0.05f && b.parkingBrake); k++)
                {
                    LunarPhysics.AimGravity(b.transform.position); crane.Step(Dt); b.Step(Dt); Physics.Simulate(Dt); tDrive += Dt;
                    // aim at the deck centreline: a point ahead along the platform axis until close
                    var local = plat.InverseTransformPoint(b.transform.position);
                    var aim = plat.TransformPoint(new Vector3(0, 0, Mathf.Clamp(local.z * 0.6f, -2f, 2f) * (Mathf.Abs(local.z) > 3f ? 1f : 0f)));
                    var to = aim - b.transform.position;
                    float dist = Mathf.Abs(local.z);
                    float ang = Vector3.SignedAngle(Vector3.ProjectOnPlane(b.transform.forward, b.transform.up), Vector3.ProjectOnPlane(to, b.transform.up), b.transform.up);
                    b.steering = Mathf.Clamp(ang / 20f, -1f, 1f);
                    float maxV = dist > 8f ? 1.2f : 0.4f;           // creep up the ramp
                    bool there = Mathf.Abs(local.z) < 0.6f && Mathf.Abs(local.x) < 1.0f;
                    b.throttle = !there && b.Speed < maxV ? 0.3f : 0f;
                    b.brake = there ? 1f : (b.Speed > maxV + 0.2f ? 0.5f : 0f);
                    if (there) b.parkingBrake = true;
                }
                b.throttle = 0; b.brake = 1; b.parkingBrake = true;
                return tDrive;
            }
            float DeckAboveGround()
            {
                var pf = WorldOrigin.WorldToFrame(crane.Platform.position);
                return (float)(pf.y - game.Streamer.Height.Height(pf.x, pf.z, 0f));
            }
            game.Teleport(1);
            Step(b, 400);
            Check("Key 2: crane deck on the ground (fresh)", DeckAboveGround(), -0.3, 0.3, "m");
            float tDrive = DriveOntoDeck();
            Note($"Drove onto the crane platform in {tDrive:0} s (deck-local position {plat.InverseTransformPoint(b.transform.position)}).");
            Physics.SyncTransforms();
            void StepCrane(int n, Action<int> per = null)
            {
                for (int k = 0; k < n; k++) { LunarPhysics.AimGravity(b.transform.position); crane.Step(Dt); b.Step(Dt); Physics.Simulate(Dt); Radio.Tick(Dt); game.Director.Tick(Dt); per?.Invoke(k); }
            }
            // the mission story listens to the ride (beat 2 lines)
            game.Director.Begin();
            StepCrane(600);
            Check("Crane: rover parked on the platform", crane.RoverOnPlatform ? 1 : 0, 1, 1, "");
            CraneShot(game, "14_crane_loaded");
            // the shipped ride: crane speed / sway tuning at their start values (x1.5, 0.4)
            Tuning.CraneSpeed.Value = Tuning.CraneSpeed.Default; Tuning.CraneSway.Value = Tuning.CraneSway.Default;
            crane.CommandLower();
            float t = 0, maxSway = 0, slewSway = 0, maxAcc = 0, prevVy = 0; bool shotMid = false;
            float gateAtHoist = -1, maxGateWhileMoving = 0, roverJump = 0, shudderAcc = 0, shudderSlide = 0;
            float tMotion = -1, tLift = -1, tBottom = -1, tSlew = -1;
            var platStart = crane.Platform.position; var upC = crane.transform.up;
            Vector3? shudderStartRel = null;
            var gateCol = crane.Gates[0].GetComponentInChildren<BoxCollider>();
            Vector3? gateClosedLocal = null;
            var clog = new StringBuilder("t,state,cable,winchV,platY,roverRelX,roverRelY,roverRelZ,roverVy,roverAccY,sway,gate,platTilt\n");
            StepCrane(200 * 400, k =>
            {
                t += Dt;
                var st = crane.state;
                var tip = crane.TipWorld; var anchor = crane.Platform.transform.TransformPoint(new Vector3(0, crane.slingHeight, 0));
                float sway = Vector3.ProjectOnPlane(anchor - tip, Vector3.up).magnitude;
                bool taut = crane.CableLength <= Vector3.Distance(tip, anchor) + 0.1f;
                if (tMotion < 0 && Mathf.Abs(crane.WinchSpeed) > 0.01f) tMotion = t;
                if (tSlew < 0 && st == CraneController.State.SlewingOut) tSlew = t;
                if (tBottom < 0 && st == CraneController.State.AtBottom) tBottom = t;
                if (gateAtHoist < 0 && Vector3.Dot(crane.Platform.position - platStart, upC) > 0.05f)
                { tLift = t; gateAtHoist = crane.GateAngle; gateClosedLocal = crane.Platform.transform.InverseTransformPoint(gateCol.bounds.center); }
                if ((st == CraneController.State.Hoisting && tLift >= 0) || st == CraneController.State.SlewingOut || st == CraneController.State.Lowering)
                {
                    maxGateWhileMoving = Mathf.Max(maxGateWhileMoving, crane.GateAngle);
                    var relp = crane.Platform.transform.InverseTransformPoint(b.transform.position);
                    roverJump = Mathf.Max(roverJump, relp.y);
                }
                if (st == CraneController.State.SlewingOut) slewSway = Mathf.Max(slewSway, sway);
                if (crane.ShudderActive)
                {
                    var relS = crane.Platform.transform.InverseTransformPoint(b.transform.position);
                    shudderStartRel ??= relS;
                    shudderSlide = Mathf.Max(shudderSlide, Vector3.ProjectOnPlane(relS - shudderStartRel.Value, Vector3.up).magnitude);
                }
                if (st == CraneController.State.Lowering && taut && Vector3.Dot(crane.Platform.linearVelocity, Vector3.down) > 0.15f)
                {
                    maxSway = Mathf.Max(maxSway, sway);
                    if (!shotMid && crane.CableLength > 25f) { shotMid = true; CraneShot(game, "15_crane_lowering"); }
                }
                float accY = 0;
                if (k % 20 == 0)
                {
                    // rover vertical acceleration over 0.1 s windows (hoist, slew, descent, touchdown)
                    float vy = Vector3.Dot(b.Body.linearVelocity, -Physics.gravity.normalized);
                    accY = (vy - prevVy) / (20 * Dt);
                    if (k > 0 && st != CraneController.State.AtBottom && !crane.ShudderActive) maxAcc = Mathf.Max(maxAcc, Mathf.Abs(accY));
                    if (crane.ShudderActive) shudderAcc = Mathf.Max(shudderAcc, Mathf.Abs(accY));
                    prevVy = vy;
                }
                if (k % 100 == 0)
                {
                    var rel = crane.Platform.transform.InverseTransformPoint(b.transform.position);
                    clog.AppendLine($"{t:0.0},{st},{crane.CableLength:0.00},{crane.WinchSpeed:0.00},{WorldOrigin.WorldToFrame(crane.Platform.position).y:0.00},{rel.x:0.00},{rel.y:0.00},{rel.z:0.00},{Vector3.Dot(b.Body.linearVelocity, Vector3.up):0.00},{accY:0.00},{sway:0.00},{crane.GateAngle:0},{Vector3.Angle(crane.Platform.transform.up, Vector3.up):0.0}");
                }
            });
            File.WriteAllText(Path.Combine(OutDir, "crane_cycle.csv"), clog.ToString());
            Check("Crane: end gates fully closed before the platform lifts off", gateAtHoist, 0, 0.5, "deg open");
            Check("Crane: moving at once after L (winch running)", tMotion, 0, 0.6, "s");
            Note($"Crane timings at crane speed x{Tuning.CraneSpeed.Value:0.##}: L -> winch moving {tMotion:0.0} s, lift-off {tLift:0.0} s, slew starts {tSlew:0.0} s, " +
                 $"at the bottom {tBottom:0.0} s (round 7: 2.0 / 8.0 / 15.0 / 118.5 s).");
            Check("Crane: gates stay closed while the platform moves (after lift-off)", maxGateWhileMoving, 0, 0.5, "deg open");
            Check("Crane: rover vertical acceleration, hoist -> touchdown (0.1 s windows; limit 0.3 g)", maxAcc, 0, 0.3 * Lunar.Gravity, "m/s^2");
            Check("Crane shudder: happened at ~60% of the descent", crane.ShudderProgress * 100, 50, 70, "%");
            Check("Crane shudder: rover stayed put on the deck", shudderSlide * 100, 0, 3, "cm");
            Note($"Crane shudder: rover peak vertical accel {shudderAcc:0.00} m/s^2 (felt, not dangerous).");
            {
                string capcom = Radio.Log.LastOrDefault(l => l.Contains("HOUSTON") && l.Contains("within limits"));
                float at = -1;
                if (capcom != null) { var parts = capcom.Split(' '); float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out at); }
                // an urgent reaction: never before the round trip; a line already being spoken finishes first
                Check("Radio: Mission Control answers the shudder after the Earth-Moon round trip (+ any line in progress)", capcom != null ? at - crane.ShudderTime : -1, Radio.RoundTrip, Radio.RoundTrip + 12, "s");
                Note("Radio log: " + string.Join(" | ", Radio.Log));
            }
            Check("Crane: rover stays on the deck (no jump; body height above the deck)", roverJump, 0, 1.0, "m");
            Check("Crane: platform swing during the slew (from the vertical under the tip; brief: < 1 m)", slewSway, 0, 1.0, "m");
            Check("Crane: platform swing during the descent (taut cable, before the touchdown creep; brief: < 1 m)", maxSway, 0, 1.0, "m");
            Check("Crane: platform reached the pit floor", crane.state == CraneController.State.AtBottom ? 1 : 0, 1, 1, "");
            var bf = WorldOrigin.WorldToFrame(b.transform.position);
            Check("Crane: rover now ~40 m below the rim", cave.Field.rim - bf.y, 35, 50, "m");
            Check("Crane: rover upright after the descent", Vector3.Angle(b.transform.up, Vector3.up), 0, 12, "deg");
            {
                // beat 2 on the real ride: every line once, in order, never two at once
                var ids = new[] { "b2_platform", "b2_gates", "b2_below_rim", "b2_bounce", "b2_pit_temp", Mission.Tiered("b2_touchdown") };
                StepCrane(200 * 40);   // let the queue drain (Houston's touchdown call comes after the light delay)
                int inOrder = 0, prevIdx = -1, onceEach = 0;
                foreach (var id in ids)
                {
                    int idx = Radio.Spoken.IndexOf(id);
                    if (idx > prevIdx) inOrder++;
                    prevIdx = Math.Max(prevIdx, idx);
                    if (Radio.Spoken.Count(x => x == id) == 1) onceEach++;
                }
                Check("Beat 2 (crane ride): platform, gates, below the rim, bounce, pit temperature, Houston touchdown - each said once", onceEach, ids.Length, ids.Length, "lines");
                Check("Beat 2 (crane ride): in that order", inOrder, ids.Length, ids.Length, "lines");
                float worst = 0;
                for (int i = 1; i < Radio.History.Count; i++) worst = Mathf.Max(worst, Radio.History[i - 1].start + Radio.History[i - 1].dur - Radio.History[i].start);
                Check("Radio: no two lines at once over the ride (worst overlap)", worst, -1000, 0.001, "s");
                Note("Ride radio: " + string.Join(" > ", Radio.History.Select(h => $"{h.id}@{h.start:0}")));
            }
            Note($"Crane cycle: {tBottom:0} s to the bottom (jerk-limited winch, input-shaped slew, settle holds).");
            StepCrane(200 * 3);
            Check("Crane: end gates stay shut at the bottom until [G] (manual release)", crane.GatesHeld ? crane.GateAngle : 999, 0, 0.5, "deg open");
            Check("Crane: the HUD prompt asks for [G]", crane.state == CraneController.State.AtBottom && crane.GatesHeld ? 1 : 0, 1, 1, "");
            crane.OpenGates();
            StepCrane(200 * 3);
            Check("Crane: end gates open after [G] (2 s swing)", crane.GatesOpen ? crane.GateAngle : 0, 99, 101, "deg");
            {
                var openLocal = crane.Platform.transform.InverseTransformPoint(gateCol.bounds.center);
                Check("Crane: gate collider moved with the gate", gateClosedLocal.HasValue ? (openLocal - gateClosedLocal.Value).magnitude : 0, 1.5, 10, "m");
                Check("Crane: tie-downs released at the bottom", crane.TiedDown ? 1 : 0, 0, 0, "");
            }
            // [K] skip, both ways, with the rover aboard
            crane.CommandRaise();
            StepCrane(200 * 4);
            crane.SkipToEnd();
            StepCrane(200 * 2);
            Check("Crane [K] while raising: parked on the pad, gates open", crane.state == CraneController.State.Parked && crane.GatesOpen ? 1 : 0, 1, 1, "");
            Check("Crane [K] while raising: rover at rest on the deck", crane.RoverOnPlatform ? b.Body.linearVelocity.magnitude : 9, 0, 0.05, "m/s");
            crane.CommandLower();
            StepCrane(200 * 4);
            crane.SkipToEnd();
            StepCrane(200 * 2);
            Check("Crane [K] while lowering: at the bottom, gates shut until [G]", crane.state == CraneController.State.AtBottom && crane.GatesHeld && crane.GatesClosed ? 1 : 0, 1, 1, "");
            crane.OpenGates();
            StepCrane(200 * 3);
            Check("Crane [K] while lowering: gates open after [G]", crane.GatesOpen ? 1 : 0, 1, 1, "");
            Check("Crane [K] while lowering: rover at rest on the deck", crane.RoverOnPlatform ? b.Body.linearVelocity.magnitude : 9, 0, 0.05, "m/s");
            {
                var pf = Vector3.ProjectOnPlane(crane.Platform.transform.forward, Vector3.up);
                var ex = Vector3.ProjectOnPlane(crane.bottomExitDir, Vector3.up);
                float misalign = Vector3.Angle(pf, ex); if (misalign > 90f) misalign = 180f - misalign;
                Check("Crane: load rotator lined a ramp up with the tube mouth", misalign, 0, 15, "deg");
            }
            Shots(game, "16_pit_floor", fpOnly: false);

            // ---- drive off the platform towards the tube mouth
            b.parkingBrake = false; b.brake = 0; b.throttle = 0.4f;
            AllLamps(game, true);
            var p0 = b.transform.position;
            for (int k = 0; k < 200 * 25; k++)
            {
                LunarPhysics.AimGravity(b.transform.position); crane.Step(Dt); b.Step(Dt); Physics.Simulate(Dt);
                if (k == 400)
                {
                    int gw = 0; float tq = 0, fx = 0; foreach (var w in b.wheels) { gw += w.grounded ? 1 : 0; tq += w.motorTorque; fx += w.fx; }
                    var hits = Physics.OverlapBox(b.transform.TransformPoint(new Vector3(0, 0.6f, 0)), new Vector3(1.2f, 0.5f, 2.1f), b.transform.rotation, ~(1 << 2));
                    Note($"Drive-off at 2 s: speed {b.Speed:0.00} m/s, wheels {gw}, motor torque {tq:0} N m, Fx {fx:0} N, brake {b.brake} park {b.parkingBrake}, tie {crane.TiedDown}, joints on rover {b.GetComponents<Joint>().Length}, hull overlaps: {string.Join(",", hits.Select(h => h.name))}");
                }
                if (k % 200 == 0) { game.Streamer.BuildNow(b.transform.position, b.transform.position); game.Tunnels.BuildNow(b.transform.position, 120f); }
            }
            b.throttle = 0; b.brake = 1; Step(b, 400);
            Note($"Drive-off: rover deck-local position {crane.Platform.transform.InverseTransformPoint(b.transform.position)}, speed {b.Speed:0.00} m/s.");
            Check("Pit floor: drove off the platform (rover clear of the deck)", crane.RoverOnPlatform ? 0 : (b.transform.position - p0).magnitude, 4, 200, "m");
            PitRampProfile(game);
            {
                // [2] while the crane is down in the pit: crane back on the pad, rover beside it on the plain
                bool wasDown = crane.state == CraneController.State.AtBottom;
                game.Teleport(1);
                Step(b, 600, i => { crane.Step(Dt); });
                int g2 = 0; foreach (var w in b.wheels) g2 += w.grounded ? 1 : 0;
                var bf2 = WorldOrigin.WorldToFrame(b.transform.position);
                Note($"Key 2 with the crane {(wasDown ? "down in the pit" : "parked")}: rover {Vector3.Distance(b.transform.position, crane.Platform.position):0} m from the platform, {cave.Field.rim - bf2.y:0.0} m below the rim.");
                Check("Key 2 (crane lowered): crane parked on its pad", crane.state == CraneController.State.Parked ? 1 : 0, 1, 1, "");
                Check("Key 2 (crane lowered): rover near the pad", Vector3.Distance(b.transform.position, crane.Platform.position), 10, 30, "m");
                Check("Key 2 (crane lowered): 4 wheels down", g2, 4, 4, "wheels");
                Check("Key 2 (crane lowered): deck on the ground", DeckAboveGround(), -0.3, 0.3, "m");
                // [2] again with the crane already parked (the round-6 blocker: deck left ~2 m up)
                game.Teleport(1);
                Step(b, 600, i => { crane.Step(Dt); });
                Check("Key 2 (crane parked): deck on the ground", DeckAboveGround(), -0.3, 0.3, "m");
                float td2 = DriveOntoDeck();
                Physics.SyncTransforms();
                Check("Key 2 (crane parked): rover drove onto the deck", crane.RoverFullyOnDeck ? td2 : 999, 0, 120, "s");
                // full ride at the crane-speed limit (the \ tuning clamps here): still < 0.3 g, small swing
                Tuning.CraneSpeed.Value = Tuning.CraneSpeedLimit; Tuning.CraneSway.Value = Tuning.CraneSway.Min;
                crane.CommandLower();
                Step(b, 200 * 12, i => { crane.Step(Dt); });
                Check("Key 2 (crane parked): crane lowers with the rover", crane.state != CraneController.State.Parked && crane.RoverOnPlatform ? 1 : 0, 1, 1, "");
                {
                    float lt = 12f, lAcc = 0, lSway = 0, lJump = 0, pvy = 0; int kk = 0;
                    var relStart = crane.Platform.transform.InverseTransformPoint(b.transform.position);
                    float lSlide = 0;
                    while (crane.state != CraneController.State.AtBottom && lt < 300f)
                    {
                        LunarPhysics.AimGravity(b.transform.position); crane.Step(Dt); b.Step(Dt); Physics.Simulate(Dt); lt += Dt; kk++;
                        var stL = crane.state;
                        var tipL = crane.TipWorld; var anL = crane.Platform.transform.TransformPoint(new Vector3(0, crane.slingHeight, 0));
                        if (stL == CraneController.State.SlewingOut || (stL == CraneController.State.Lowering && Vector3.Dot(crane.Platform.linearVelocity, Vector3.down) > 0.15f))
                            lSway = Mathf.Max(lSway, Vector3.ProjectOnPlane(anL - tipL, Vector3.up).magnitude);
                        var rl = crane.Platform.transform.InverseTransformPoint(b.transform.position);
                        lJump = Mathf.Max(lJump, rl.y);
                        if (!crane.ShudderActive) lSlide = Mathf.Max(lSlide, Vector3.ProjectOnPlane(rl - relStart, Vector3.up).magnitude);
                        if (kk % 20 == 0)
                        {
                            float vy = Vector3.Dot(b.Body.linearVelocity, -Physics.gravity.normalized);
                            if (kk > 20 && !crane.ShudderActive) lAcc = Mathf.Max(lAcc, Mathf.Abs((vy - pvy) / (20 * Dt)));
                            pvy = vy;
                        }
                    }
                    Note($"Crane at the speed limit x{Tuning.CraneSpeedLimit} (sway x{Tuning.CraneSway.Min}): at the bottom after {lt:0} s.");
                    Check($"Crane at the speed limit: reached the pit floor", crane.state == CraneController.State.AtBottom ? 1 : 0, 1, 1, "");
                    Check($"Crane at the speed limit: rover vertical accel (limit 0.3 g)", lAcc, 0, 0.3 * Lunar.Gravity, "m/s^2");
                    Check($"Crane at the speed limit, least damping: platform swing (brief: < 1 m)", lSway, 0, 1.0, "m");
                    Check($"Crane at the speed limit: rover stays on the deck (slide)", lSlide, 0, 1.0, "m");
                }
                Tuning.CraneSpeed.Value = Tuning.CraneSpeed.Default; Tuning.CraneSway.Value = Tuning.CraneSway.Default;
                crane.SkipToEnd();
                Step(b, 400, i => { crane.Step(Dt); });
                // back to the state the later checks expect: crane parked on its pad, rover beside it, brakes off
                game.Teleport(1);
                b.parkingBrake = false; b.brake = 0;
                Step(b, 400, i => { crane.Step(Dt); });
            }
            CameraViews(game, StepCrane);
            CraneShadowsHigh(game);

            // ---- tube mouth -> into the network
            game.Teleport(2);
            Step(b, 600);
            Check("Tube: tunnel chunks streamed around the rover", game.Tunnels.ChunkCount, 10, 100000, "chunks");
            float gt = 0; foreach (var w in b.wheels) gt += w.grounded ? 1 : 0;
            Check("Tube: all wheels on the tube floor", gt, 4, 4, "wheels");
            Note($"Tube soil under the front-left wheel: {(b.wheels[0].soil.rigid ? "bare basalt" : "regolith/talus")}.");
            Shots(game, "04_tube");
            CrewInRover(game);
            game.cam.transform.position = BuggyBuilder.DriverEye(game.Buggy).position;
            game.Sky.UpdateVisibility();
            Check("Tube: sky (Sun/Earth/stars) hidden under the rock", game.Sky.SkyShown ? 0 : 1, 1, 1, "");
            PitSeamRays(game);
            TubeFarEnd(game);
            TubeMouthMaterial(game);
            TubeLookBack(game);
            CheckNoBlowout(game, "Tube (headlights on)");
            AllLamps(game, false);
            CheckNoBlowout(game, "Tube (lights off, eye adapted)");
            AllLamps(game, true);
            // drive 90 s into the network with streaming
            var tubeStart = b.transform.position;
            var route = new List<double3> { (double3)cave.Network.mouthFloor };
            if (cave.Network.IsFlow)
            {
                // down the top trunk from the mouth (the GIMLI-2 route may start upstream, behind the pit)
                var F = cave.Network.Flow;
                foreach (var k in Enumerable.Range(0, F.L.edges.Count).Where(k => F.L.edges[k].tube == 0 && F.L.edges[k].i0 >= F.L.pitIdx).OrderBy(k => F.L.edges[k].i0))
                    foreach (var p in F.WayPts[k]) route.Add((double3)p);
            }
            else foreach (var n in cave.Network.ExplorePath(8, 0)) route.Add((double3)cave.Network.NodePos(n.level, n.i, n.j));
            b.throttle = 0.7f;
            float lowest = float.MaxValue; bool fell = false;
            var dlog = new StringBuilder("t,x,y,z,speed,steer,grounded\n");
            for (int k = 0; k < 200 * 90; k++)
            {
                LunarPhysics.AimGravity(b.transform.position); b.Step(Dt); Physics.Simulate(Dt);
                if (k % 100 == 0)
                {
                    game.Tunnels.BuildNow(b.transform.position, 120f);
                    var f = WorldOrigin.WorldToFrame(b.transform.position);
                    lowest = Mathf.Min(lowest, (float)f.y);
                    if (f.y < cave.Field.mouthFloorY - 400) fell = true;
                    // driver follows the route: mouth -> first junction -> backbone junctions (look-ahead 25 m)
                    var target = RouteTarget(route, f, 25.0);
                    var to = WorldOrigin.FrameToWorld(target) - b.transform.position;
                    float ang = Vector3.SignedAngle(Vector3.ProjectOnPlane(b.transform.forward, b.transform.up), Vector3.ProjectOnPlane(to, b.transform.up), b.transform.up);
                    b.steering = Mathf.Clamp(ang / 25f, -1f, 1f);
                    int gg = 0; foreach (var w in b.wheels) gg += w.grounded ? 1 : 0;
                    dlog.AppendLine($"{k * Dt:0.0},{f.x:0.0},{f.y:0.0},{f.z:0.0},{b.Speed:0.00},{b.steering:0.00},{gg}");
                }
            }
            File.WriteAllText(Path.Combine(OutDir, "network_drive.csv"), dlog.ToString());
            float driven = (b.transform.position - tubeStart).magnitude;
            Check("Network drive: covered ground in 90 s (no fall-through, no stuck)", fell ? -1 : driven, 60, 2000, "m");
            Note($"Network drive: reached {cave.Field.mouthFloorY - lowest:0} m below the mouth floor; {game.Tunnels.ChunkCount} chunks resident.");
            b.throttle = 0; b.brake = 1; Step(b, 400);
            Shots(game, "17_network");

            // ---- scout rover, level 2
            game.Teleport(3);
            Step(b, 800);
            float gs = 0; foreach (var w in b.wheels) gs += w.grounded ? 1 : 0;
            Check("Scout site (level 2): rover standing on the tube floor", gs, 4, 4, "wheels");
            var sf = WorldOrigin.WorldToFrame(b.transform.position);
            Note($"Scout site: {game.Height.DataHeight(sf.x, sf.z) - sf.y:0} m below the surface.");
            Shots(game, "18_scout");

            // ---- on foot in the tube with helmet lamps
            game.Player.ExitRover();
            var walker = game.Player.Walker;
            Check("On foot: got out of the rover", walker != null && walker.gameObject.activeSelf ? 1 : 0, 1, 1, "");
            walker.SetLamps(true);
            for (int i = 0; i < 400; i++) { LunarPhysics.AimGravity(walker.transform.position); walker.Step(Dt); b.Step(Dt); Physics.Simulate(Dt); }
            Check("On foot in the tube: standing on the floor", walker.Grounded ? 1 : 0, 1, 1, "");
            WalkerShots(game, "07_tube_onfoot");
            HelmetComposite(game, "13_helmet_view");
            game.Player.EnterRoverForced();
            Check("Back in the rover", game.Player.State == PlayerControl.Mode.Driving ? 1 : 0, 1, 1, "");

            SiteCycle(game);
            CollapseFall(game);

            // ---- a collapse shaft between levels (look up from the chamber below)
            CollapseShot(game);
        }

        /// <summary>
        /// The far end of the tube must stay dark: renders 60 m and 150 m in, lights off, exposure locked at the
        /// dark-adapted value. First without the under-rock shell / culling (to find which renderer group glows),
        /// then with it (must be 0 blown-out pixels).
        /// </summary>
        static void TubeFarEnd(GameBootstrap game)
        {
            var cam = game.cam; var shell = game.Shell;
            AllLamps(game, false);
            int full = cam.cullingMask | (1 << RenderLayers.SurfaceTerrain) | (1 << RenderLayers.Pit);
            foreach (float along in new[] { 60f, 150f })
            {
                var fp = game.Cave.TubeFloorPoint(along, out float2 dir);
                var pos = WorldOrigin.FrameToWorld(fp) + Vector3.up * 1.8f;
                game.Streamer.BuildNow(pos, pos);
                game.Tunnels.BuildNow(pos);
                Physics.SyncTransforms();
                cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(new Vector3(dir.x, -0.03f, dir.y)));
                game.Sky.UpdateVisibility();
                shell.Refresh();
                game.Exposure.SnapToTarget();   // lock: dark-adapted for this view, then no more metering
                int lastBright = 0;
                int Blown(string tag, int mask, bool shellOn)
                {
                    shell.Refresh();
                    if (!shellOn) { foreach (Transform c in cam.transform) if (c.name == "UnderRockShell") c.gameObject.SetActive(false); }
                    cam.cullingMask = mask;
                    var tex = Render(game, Path.Combine(OutDir, $"24_tube_far_{along:0}m_{tag}.png"), true, snap: false);
                    int n = 0; lastBright = 0;
                    for (int y = 0; y < tex.height; y++) for (int x = 0; x < tex.width; x++)
                    {
                        var c = tex.GetPixel(x, y);
                        if (c.r > 0.97f && c.g > 0.97f && c.b > 0.97f) n++;
                        if (c.r + c.g + c.b > 0.9f) lastBright++;   // "bright patch": mid-grey or more in a dark tube
                    }
                    UnityEngine.Object.DestroyImmediate(tex);
                    shell.Refresh();
                    return n;
                }
                int before = Blown("before", full, false), brightBefore = lastBright;
                if (before > 0 || brightBefore > 0)
                {
                    var groups = new (string name, int layer)[] { ("tunnel chunks / rover body (Default)", 0), ("rover + crew (Ignore Raycast)", 2), ("surface terrain + skirts", RenderLayers.SurfaceTerrain), ("pit mesh", RenderLayers.Pit) };
                    var sb = new StringBuilder();
                    foreach (var (name, layer) in groups)
                    {
                        int without = Blown("no" + layer, full & ~(1 << layer), false);
                        sb.Append($"{name}: {before - without} blown / {brightBefore - lastBright} bright px; ");
                    }
                    Note($"Tube {along:0} m, no shell/culling: {before} blown-out, {brightBefore} bright px. Source by renderer group (px removed when hidden): {sb}");
                }
                else Note($"Tube {along:0} m, no shell/culling: 0 blown-out, 0 bright px (fully streamed).");
                Check($"Tube far end {along:0} m in (lights off, exposure locked): under-rock shell active", shell.Active ? 1 : 0, 1, 1, "");
                Check($"Tube far end {along:0} m in (lights off, exposure locked): blown-out pixels", Blown("after", cam.cullingMask, true), 0, 0, "px");
                Note($"Tube far end {along:0} m with shell: {lastBright} bright px.");
                FarRingView(game, along);
                if (along > 100f)
                {
                    // true darkness: all lamps off, eye fully adapted -> nothing to see (no fake fill)
                    var st = TubeView(game, $"25_dark_{along:0}m_off", snap: true);
                    Note($"Tube {along:0} m, lamps off, settled exposure (EV {Mathf.Log(1f / game.Exposure.Exposure, 2):0.0}, fill {game.Exposure.GameFillLux:0.####} lux).");
                    Check($"True darkness {along:0} m in, all lamps off: mean brightness", st.mean, 0, 0.01, "");
                    Check($"True darkness {along:0} m in, all lamps off: bright pixels", st.bright, 0, 0, "px");
                    var lamp = TestLamp(cam, 30600f);
                    var lit = TubeView(game, $"25_dark_{along:0}m_lamp", snap: true);
                    UnityEngine.Object.DestroyImmediate(lamp);
                    Check($"Tube {along:0} m in, a headlight-class lamp on: walls readable (mean brightness)", lit.mean, 0.03, 0.6, "");
                }
            }
            AllLamps(game, true);
        }

        struct ViewStats { public float mean; public int bright, blown, n; }

        /// <summary>V camera cycle: order (crane parked / moving), each view renders, the crane ride's end returns to first person.</summary>
        static void CameraViews(GameBootstrap game, Action<int, Action<int>> stepCrane)
        {
            var pc = game.Player; var crane = game.Crane; var cam = game.cam;
            pc.SetView(PlayerControl.View.FirstPerson);
            var seq = new List<string>();
            for (int i = 0; i < 3; i++) { pc.CycleView(); seq.Add(pc.ViewLabel); }
            Note("Cameras, crane parked: V -> " + string.Join(" -> ", seq));
            Check("Cameras (crane parked): V order mast -> base -> first person (crane cam skipped)", string.Join(">", seq) == "ROVER MAST CAM>BASE CAM>FIRST PERSON" ? 1 : 0, 1, 1, "");
            foreach (var v in new[] { PlayerControl.View.RoverMast, PlayerControl.View.Base })
            {
                pc.SetView(v); pc.UpdateViewCamera();
                var st = TubeView(game, "28_cam_" + v, snap: true);
                Check($"Camera {PlayerControl.Label(v)}: renders (mean brightness)", st.mean, 0.02, 0.9, "");
                if (v == PlayerControl.View.RoverMast)
                {
                    var vp = cam.WorldToViewportPoint(game.Buggy.transform.position + game.Buggy.transform.forward * 1.5f);
                    Check("Rover mast cam: the rover is in view", vp.z > 0 && vp.x > 0 && vp.x < 1 && vp.y > 0 && vp.y < 1 ? 1 : 0, 1, 1, "");
                }
                else
                {
                    var vp = cam.WorldToViewportPoint(crane.Platform.position);
                    Check("Base cam: the crane platform is in view", vp.z > 0 && vp.x > 0 && vp.x < 1 && vp.y > 0 && vp.y < 1 ? 1 : 0, 1, 1, "");
                }
            }
            // crane moving (empty platform): the crane cam joins the cycle
            pc.SetView(PlayerControl.View.FirstPerson);
            crane.CommandLower();
            stepCrane(200 * 8, null);
            Note($"Cameras: crane state {crane.state}, moving {crane.Moving}.");
            seq.Clear();
            for (int i = 0; i < 4; i++) { pc.CycleView(); seq.Add(pc.ViewLabel); if (pc.CurrentView == PlayerControl.View.Crane) { pc.UpdateViewCamera(); TubeView(game, "28_cam_Crane", snap: true); } }
            Note("Cameras, crane moving: V -> " + string.Join(" -> ", seq));
            Check("Cameras (crane moving): V order mast -> base -> crane -> first person", string.Join(">", seq) == "ROVER MAST CAM>BASE CAM>CRANE CAM>FIRST PERSON" ? 1 : 0, 1, 1, "");
            pc.SetView(PlayerControl.View.Crane); pc.UpdateViewCamera();
            {
                var vp = cam.WorldToViewportPoint(crane.Platform.position);
                Check("Crane cam: looks down at the platform (platform in view)", vp.z > 0 && vp.x > 0 && vp.x < 1 && vp.y > 0 && vp.y < 1 ? 1 : 0, 1, 1, "");
            }
            crane.SkipToEnd();
            stepCrane(200 * 3, null);
            pc.UpdateViewCamera();
            Check("Crane cam: the end of the ride returns to first person", pc.CurrentView == PlayerControl.View.FirstPerson ? 1 : 0, 1, 1, "");
            crane.ResetToPad();
            Physics.SyncTransforms();
            pc.SetView(PlayerControl.View.FirstPerson);
        }

        /// <summary>Report only: the crane truss and platform on the rim with the High preset's shadows.</summary>
        static void CraneShadowsHigh(GameBootstrap game)
        {
            var src = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            if (src == null) { Note("Crane shadows (High): no URP asset found."); return; }
            var prevQ = QualitySettings.renderPipeline;
            var hi = UnityEngine.Object.Instantiate(src);
            QualityPresets.Configure(hi, QualityPresets.Level.High);
            UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = hi; QualitySettings.renderPipeline = hi;
            try
            {
                var cam = game.cam; var c = game.Crane.transform;
                cam.fieldOfView = 60f;
                cam.transform.position = c.position + c.up * 7f - c.forward * 12f + c.right * 14f;
                cam.transform.LookAt(c.position + c.up * 9f + c.forward * 8f);
                Render(game, Path.Combine(OutDir, "29_crane_shadows_high_truss.png"));
                CraneShot(game, "29_crane_shadows_high_platform");
                Note($"Crane shadows (High preset: {hi.shadowDistance:0} m, {hi.shadowCascadeCount} cascades, {hi.mainLightShadowmapResolution} map): see 29_crane_shadows_high_*.png.");
            }
            finally
            {
                UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = src; QualitySettings.renderPipeline = prevQ;
                UnityEngine.Object.DestroyImmediate(hi);
                game.cam.fieldOfView = 75f;
            }
        }

        /// <summary>Seated crew: helmet lamps always on (no shadows), driver lamps on H, heads re-pick look targets within 8 s.</summary>
        static void CrewInRover(GameBootstrap game)
        {
            var b = game.Buggy;
            var heads = b.GetComponent<CrewHeads>();
            Check("Crew: seated passengers with helmet lamps", heads != null ? heads.Count : 0, 3, 3, "");
            if (heads == null) return;
            int Lit() { int n = 0; for (int i = 0; i < heads.Count; i++) if (heads.Lamp(i).enabled) n++; return n; }
            game.SetLights(true); heads.Step(0.02f);
            Check("Crew helmet lamps on (headlights on)", Lit(), 3, 3, "lamps");
            game.SetLights(false); heads.Step(0.02f);
            Check("Crew helmet lamps stay on (headlights off)", Lit(), 3, 3, "lamps");
            {
                var d = b.GetComponent<BuggyDriver>();
                var dl = d.headlights.Where(l => l.name == "HelmetLamp").ToArray();
                Check("Driver helmet lamps: two, off with the headlights", dl.Length == 2 && dl.All(l => !l.enabled) ? 2 : dl.Length, 2, 2, "lamps");
                game.SetLights(true);
                Check("Driver helmet lamps: on with the headlights", dl.Count(l => l.enabled), 2, 2, "lamps");
                Check("Roll-bar lamp aimed up (on with the headlights)", d.headlights.Count(l => l.name == "FloodLightRollBar" && l.enabled && Vector3.Dot(l.transform.forward, b.transform.up) > 0.3f), 1, 1, "lamps");
                var piv = dl.Length > 0 ? dl[0].transform.parent : null;
                var dc = game.cam.GetComponent<DriverCamera>();
                if (piv != null && dc != null && dc.seatEye != null)
                {
                    var keep = piv.localRotation;
                    piv.localRotation = Quaternion.Euler(10, 40, 0);   // as DriverCamera sets it from the look
                    Check("Driver helmet lamps turn with the look (40 deg yaw)", Vector3.Angle(Vector3.ProjectOnPlane(dl[0].transform.forward, b.transform.up), Vector3.ProjectOnPlane(b.transform.forward, b.transform.up)), 35, 45, "deg");
                    piv.localRotation = keep;
                }
            }
            int noShadow = 0; for (int i = 0; i < heads.Count; i++) if (heads.Lamp(i).shadows == LightShadows.None) noShadow++;
            Check("Crew helmet lamps: no shadow maps (no self-shadow artefacts)", noShadow, 3, 3, "lamps");
            var p0 = new int[heads.Count]; var r0 = new Quaternion[heads.Count];
            for (int i = 0; i < heads.Count; i++) { p0[i] = heads.Picks(i); r0[i] = heads.HeadTransform(i).localRotation; }
            for (int k = 0; k < 400; k++) heads.Step(0.02f);   // 8 s
            int minPicks = int.MaxValue; float maxTurn = 0; var sb = new StringBuilder();
            for (int i = 0; i < heads.Count; i++)
            {
                minPicks = Mathf.Min(minPicks, heads.Picks(i) - p0[i]);
                maxTurn = Mathf.Max(maxTurn, Quaternion.Angle(r0[i], heads.HeadTransform(i).localRotation));
                sb.Append($"{i}: {heads.Looking(i)} ({heads.Picks(i) - p0[i]} picks); ");
                var lampFwd = heads.Lamp(i).transform.forward; var headFwd = heads.HeadTransform(i).forward;
                if (Vector3.Angle(lampFwd, headFwd) > 10f) Fail($"Crew lamp {i} does not follow the head ({Vector3.Angle(lampFwd, headFwd):0} deg)");
            }
            Note("Crew heads after 8 s: " + sb);
            Check("Crew heads re-pick a look target within 8 s (each head)", minPicks, 1, 100, "picks");
            Check("Crew heads turned (largest head rotation over 8 s)", maxTurn, 3, 180, "deg");
            var cam = game.cam;
            cam.transform.position = b.transform.position + b.transform.forward * 4.5f + b.transform.up * 2.2f - b.transform.right * 1.5f;
            cam.transform.LookAt(b.transform.position + b.transform.up * 1.2f, b.transform.up);
            Render(game, Path.Combine(OutDir, "30_crew_lamps.png"));
        }

        /// <summary>
        /// Drive into the first collapse shaft with play-like streaming (the per-frame budget, not BuildNow):
        /// the rover must stop on the talus one level down (with a hard-landing warning), not fall through.
        /// </summary>
        static void CollapseFall(GameBootstrap game)
        {
            var b = game.Buggy; var sites = game.Sites.Sites; var net = game.Cave.Network;
            int k = sites.FindIndex(x => x.kind == MoonBaseAlpha.Cave.SiteCatalog.Kind.CollapseTop);
            if (k < 0) { Note("Collapse fall: no collapse site."); return; }
            var site = sites[k];
            game.GotoSite(k);
            Step(b, 400);
            {
                // diagnostics: colliders and the SDF on the vertical through the shaft centre
                var topW = WorldOrigin.FrameToWorld(new double3(site.feature.x, site.feature.y + 5f, site.feature.z));
                var hitsD = Physics.RaycastAll(topW, Vector3.down, 600f, ~(1 << 2), QueryTriggerInteraction.Ignore).OrderBy(h => h.distance);
                Note("Collapse column colliders: " + string.Join(", ", hitsD.Select(h => $"{h.collider.name}@{h.distance - 5f:0}")));
                var pl = new List<TunnelPrim>();
                net.Collect(site.feature - new float3(5, 420, 5), site.feature + new float3(5, 10, 5), 8f, pl);
                pl.Sort(TunnelNetwork.Compare);
                var na = new Unity.Collections.NativeArray<TunnelPrim>(pl.ToArray(), Unity.Collections.Allocator.Temp);
                var col = new StringBuilder();
                bool air = true;
                for (float dy = 5f; dy > -420f; dy -= 1f)
                {
                    bool a2 = TunnelSdf.Eval(na, na.Length, site.feature + new float3(0, dy, 0), out int lvl, out _) < 0;
                    if (a2 != air) { col.Append($"{(a2 ? "air" : "rock")} from {dy:0} (L{lvl}); "); air = a2; }
                }
                na.Dispose();
                Note($"Collapse column SDF ({pl.Count} prims, {pl.Count(x => x.type == TunnelPrim.Shaft)} shafts): {col}");
                Note("Collapse column prims: " + string.Join("; ", pl.Where(x => x.type != TunnelPrim.Tube).Select(x => $"{(x.type == TunnelPrim.Shaft ? "shaft" : "chamber")} L{x.level} a({x.a.x - site.feature.x:0},{x.a.y - site.feature.y:0},{x.a.z - site.feature.z:0}) b.y {x.b.y - site.feature.y:0} hw {x.hw:0} mound {x.moundH:0}")));
            }
            {
                string w = game.EdgeWarningAt(b.transform.position, b.transform.forward);
                string wBack = game.EdgeWarningAt(b.transform.position, -b.transform.forward);
                Note($"Edge warning facing the collapse: \"{w}\"; facing away: \"{wBack}\".");
                Check("Collapse edge: no warning when facing away", wBack == null ? 1 : 0, 1, 1, "");
            }
            var oldViewer = game.Tunnels.viewer;
            game.Tunnels.viewer = b.transform;        // play: the camera rides in the rover
            int hard0 = b.Impact.HardCount;
            float yTop = (float)WorldOrigin.WorldToFrame(b.transform.position).y, minY = yTop, t = 0, maxV = 0;
            var log = new StringBuilder();
            string warnText = null;
            b.parkingBrake = false; b.brake = 0;
            for (int i = 0; i < 200 * 60; i++)
            {
                var f = WorldOrigin.WorldToFrame(b.transform.position);
                var to = new Vector3(site.feature.x - (float)f.x, 0, site.feature.z - (float)f.z);
                float ang = Vector3.SignedAngle(Vector3.ProjectOnPlane(b.transform.forward, Vector3.up), to, Vector3.up);
                b.steering = Mathf.Clamp(ang / 20f, -1f, 1f); b.throttle = 0.6f;
                LunarPhysics.AimGravity(b.transform.position); game.Winch.Step(Dt); b.Step(Dt); Physics.Simulate(Dt); t += Dt;
                if (i % 3 == 0) game.Tunnels.Tick();
                if (f.y > yTop - 0.5f && i % 20 == 0 && warnText == null) warnText = game.EdgeWarningAt(b.transform.position, b.transform.forward);
                minY = Mathf.Min(minY, (float)f.y);
                maxV = Mathf.Max(maxV, b.Body.linearVelocity.magnitude);
                if (i % 100 == 0) log.Append($"{t:0}s y{f.y - yTop:0} v{b.Body.linearVelocity.magnitude:0.0} ");
                if (f.y < yTop - 20f && b.Body.linearVelocity.magnitude < 0.3f && t > 5f) break;   // landed and stopped
            }
            b.throttle = 0; b.brake = 1;
            game.Tunnels.viewer = oldViewer;
            var fe = WorldOrigin.WorldToFrame(b.transform.position);
            float below = net.LevelFloor(site.level + 1, net.ToUV(new float2((float)fe.x, (float)fe.z)).x);
            Note($"Collapse fall ({site.label}): {log}");
            Note($"Collapse fall: lowest {minY - yTop:0} m, ended {fe.y - yTop:0} m ({fe.y - below:0} m above the L{site.level + 1} floor), peak speed {maxV:0.0} m/s.");
            Check("Collapse fall: rover stopped on level L+1 (talus, not through the floor)", (float)(fe.y - below), -5, 25, "m");
            Note($"Collapse fall: hard-landing events {b.Impact.HardCount - hard0}, peak {b.Impact.LastPeakG:0.0} g.");
            Check("Collapse fall without a line: survivable mercy - rover upright", Vector3.Angle(b.transform.up, -TerrainFrame.DownAt(b.transform.position)), 0, 35, "deg");
            Check("Collapse fall without a line: ROVER DAMAGED (top speed capped)", b.Damaged ? b.MaxSpeedNow * 3.6f : -1, 1, 10, "km/h");
            b.Damaged = false;
            Note($"Collapse edge HUD on the approach: \"{warnText}\".");
            Check("Collapse edge: HUD warning on the approach (facing it, < 15 m)", warnText != null && warnText.StartsWith("EDGE AHEAD") ? 1 : 0, 1, 1, "");
        }

        /// <summary>Key 5 site cycle: every special site places the rover with 4 wheels down.</summary>
        static void SiteCycle(GameBootstrap game)
        {
            var b = game.Buggy; var sites = game.Sites.Sites;
            var kinds = sites.GroupBy(x => x.kind).Select(g => $"{g.Key} x{g.Count()}");
            Note($"Site cycle: {sites.Count} sites ({string.Join(", ", kinds)}).");
            Check("Site cycle: special sites found (first 40 junctions + scout)", sites.Count, 5, 200, "sites");
            Check("Site cycle: GIMLI-2 (scout) then GIMLI-1 on the rim come last", sites.Count > 1 && sites[sites.Count - 2].kind == MoonBaseAlpha.Cave.SiteCatalog.Kind.Scout && sites[sites.Count - 1].kind == MoonBaseAlpha.Cave.SiteCatalog.Kind.Gimli1 ? 1 : 0, 1, 1, "");
            int ok = 0; var bad = new StringBuilder(); bool shotTop = false, shotBelow = false;
            for (int k = 0; k < sites.Count; k++)
            {
                bool placed = game.GotoSite(k);
                Step(b, 600, i => { if (i % 200 == 0) game.Tunnels.BuildNow(b.transform.position, 120f); });
                int g = 0; foreach (var w in b.wheels) g += w.grounded ? 1 : 0;
                float tilt = Vector3.Angle(b.transform.up, -TerrainFrame.DownAt(b.transform.position));
                if (placed && g == 4) ok++;
                else bad.Append($"[{game.SiteLabel}: placed {placed}, wheels {g}, tilt {tilt:0}] ");
                if (k == 0) Check("Site cycle: HUD label format", game.SiteLabel.StartsWith($"SITE 1/{sites.Count} - ") ? 1 : 0, 1, 1, "");
                var kind = sites[k].kind;
                if ((kind == MoonBaseAlpha.Cave.SiteCatalog.Kind.CollapseTop && !shotTop) || (kind == MoonBaseAlpha.Cave.SiteCatalog.Kind.CollapseBelow && !shotBelow))
                {
                    if (kind == MoonBaseAlpha.Cave.SiteCatalog.Kind.CollapseTop) shotTop = true; else shotBelow = true;
                    Shots(game, "31_site_" + kind, fpOnly: false);
                }
            }
            if (bad.Length > 0) Note("Site cycle problems: " + bad);
            Check("Site cycle: every site places the rover with 4 wheels down", ok, sites.Count, sites.Count, "sites");
        }

        /// <summary>
        /// Far ring (render-only, 3 m voxels, near radius -> 2x): built, no bright pixels with lights off or on,
        /// and what it adds to the picture is dim (the tube recedes into darkness, no hard black edge).
        /// </summary>
        static void FarRingView(GameBootstrap game, float along)
        {
            var far = game.FarTunnels; var cam = game.cam;
            var pos = cam.transform.position;
            far.BuildNow(pos);
            game.Tunnels.PushShaderGlobals();
            Check($"Far ring {along:0} m in: coarse chunks built (to {far.radius:0} m)", far.ChunkCount, 10, 100000, "chunks");
            Note($"Far ring: near radius {game.Tunnels.radius:0} m, far {far.radius:0} m, shell {game.Shell.radiusFactor * game.Tunnels.DrawRadius:0} m.");
            foreach (bool lit in new[] { false, true })
            {
                AllLamps(game, false);
                GameObject lamp = lit ? TestLamp(cam, 30600f) : null;
                var st = TubeView(game, $"27_far_ring_{along:0}m_{(lit ? "lamp" : "off")}", snap: true);
                if (!lit) Check($"Far ring {along:0} m in, lights off: bright pixels", st.bright, 0, 0, "px");
                else Note($"Far ring {along:0} m in, lamp on: {CountBrightAbove(game, 0.55f)} bright px in the upper 45% of the frame.");
                if (lit)
                {
                    // the same view without the far ring: what the ring adds must be dim
                    var keep = far.gameObject.activeSelf;
                    far.gameObject.SetActive(false);
                    var tex0 = Render(game, Path.Combine(OutDir, $"27_far_ring_{along:0}m_lamp_noring.png"), true, snap: false);
                    far.gameObject.SetActive(keep);
                    var tex1 = Render(game, Path.Combine(OutDir, $"27_far_ring_{along:0}m_lamp.png"), true, snap: false);
                    var a = tex0.GetPixels(); var b2 = tex1.GetPixels();
                    int changed = 0; double sum = 0;
                    for (int i = 0; i < a.Length; i++)
                    {
                        float d = Mathf.Abs(a[i].r - b2[i].r) + Mathf.Abs(a[i].g - b2[i].g) + Mathf.Abs(a[i].b - b2[i].b);
                        if (d > 0.004f) { changed++; sum += (b2[i].r + b2[i].g + b2[i].b) / 3f; }
                    }
                    UnityEngine.Object.DestroyImmediate(tex0); UnityEngine.Object.DestroyImmediate(tex1);
                    Note($"Far ring {along:0} m, lamp on: ring changes {changed * 100f / a.Length:0.0}% of the screen, mean brightness there {(changed > 0 ? sum / changed : 0):0.000}; whole view mean {st.mean:0.000}.");
                    Check($"Far ring {along:0} m in, lamp on: far tube darker than the view (mean of ring pixels)", changed > 0 ? sum / changed : -1, 0, Mathf.Max(0.02f, st.mean), "");
                }
                if (lamp) UnityEngine.Object.DestroyImmediate(lamp);
            }
            AllLamps(game, false);
        }

        /// <summary>Bright pixels (r+g+b > 0.9) in the top part of the frame (above <paramref name="fromY"/>), i.e. far tube, not the lamp pool on the floor.</summary>
        static int CountBrightAbove(GameBootstrap game, float fromY)
        {
            var tex = Render(game, Path.Combine(OutDir, "_tmp_bright.png"), true, snap: false);
            int n = 0;
            for (int y = (int)(tex.height * fromY); y < tex.height; y++) for (int x = 0; x < tex.width; x++) { var c = tex.GetPixel(x, y); if (c.r + c.g + c.b > 0.9f) n++; }
            UnityEngine.Object.DestroyImmediate(tex);
            File.Delete(Path.Combine(OutDir, "_tmp_bright.png"));
            return n;
        }

        /// <summary>Render the current camera view and measure it (mean r+g+b / 3, bright = r+g+b > 0.9, blown = all channels > 0.97).</summary>
        static ViewStats TubeView(GameBootstrap game, string name, bool snap)
        {
            game.Shell.Refresh();
            var tex = Render(game, Path.Combine(OutDir, name + ".png"), true, snap: snap);
            var st = new ViewStats(); double sum = 0;
            var px = tex.GetPixels();
            foreach (var c in px)
            {
                float v = c.r + c.g + c.b; sum += v / 3f; st.n++;
                if (v > 0.9f) st.bright++;
                if (c.r > 0.97f && c.g > 0.97f && c.b > 0.97f) st.blown++;
            }
            st.mean = (float)(sum / Mathf.Max(1, st.n));
            UnityEngine.Object.DestroyImmediate(tex);
            return st;
        }

        static GameObject TestLamp(Camera cam, float candela, float angle = 70f)
        {
            var lamp = new GameObject("TestLamp").AddComponent<Light>();
            lamp.type = LightType.Spot; lamp.spotAngle = angle; lamp.innerSpotAngle = angle * 0.6f; lamp.range = 120;
            lamp.transform.SetPositionAndRotation(cam.transform.position + cam.transform.right * 0.6f - cam.transform.up * 0.5f, cam.transform.rotation);
            lamp.gameObject.AddComponent<PhysicalLight>().physicalIntensity = candela;
            return lamp.gameObject;
        }

        /// <summary>
        /// The first ~40 m of tube is pit mesh: it must use the tunnel material (no sun-lit regolith patches).
        /// Renders 15 m and 35 m in, lights off, exposure locked; rays to the walls check the material.
        /// </summary>
        /// <summary>
        /// Round-6 artefact: from inside the tube looking back at the pit (lights off, play-like shadow cascades),
        /// no bright pixels may land on surfaces under the rock roof; the open pit (far lip) stays sunlit.
        /// Each bright pixel is ray-classified by what it hits.
        /// </summary>
        static void TubeLookBack(GameBootstrap game)
        {
            var cam = game.cam; var cave = game.Cave; var hf = cave.Field.hf;
            var src = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            var prevQ = QualitySettings.renderPipeline;
            AllLamps(game, false);
            try
            {
                foreach (var lvl in new[] { QualityPresets.Level.Medium, QualityPresets.Level.High })
                {
                    UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset asset = null;
                    if (src != null)
                    {
                        asset = UnityEngine.Object.Instantiate(src);
                        QualityPresets.Configure(asset, lvl);
                        UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = asset; QualitySettings.renderPipeline = asset;
                    }
                    foreach (float along in new[] { 15f, 35f })
                    {
                        var fp = cave.TubeFloorPoint(along, out float2 dir);
                        var pos = WorldOrigin.FrameToWorld(fp) + Vector3.up * 1.8f;
                        game.Streamer.BuildNow(pos, pos);
                        game.Tunnels.BuildNow(pos);
                        Physics.SyncTransforms();
                        cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(new Vector3(-dir.x, 0.05f, -dir.y)));
                        game.Sky.UpdateVisibility();
                        game.Shell.Refresh();
                        var tex = Render(game, Path.Combine(OutDir, $"32_tube_lookback_{lvl}_{along:0}m.png"), true, snap: true);
                        int under = 0, open = 0, step = 3; var sb = new StringBuilder();
                        for (int y = 0; y < tex.height; y += step)
                        for (int x = 0; x < tex.width; x += step)
                        {
                            var c = tex.GetPixel(x, y);
                            if (c.r + c.g + c.b < 0.9f) continue;
                            var ray = cam.ViewportPointToRay(new Vector3((x + 0.5f) / tex.width, (y + 0.5f) / tex.height, 0));
                            if (!Physics.Raycast(ray, out var hit, 2000f, ~(1 << 2), QueryTriggerInteraction.Ignore)) continue;
                            var hp = WorldOrigin.WorldToFrame(hit.point + hit.normal * 0.3f);
                            // the rim lip / funnel (a few m below the plain) is open to the sky; under the roof = deeper
                            if (cave.SkyVisibility(hp) > 0.5f || hf.DataHeight(hp.x, hp.z) - (float)hp.y < 6f) { open++; continue; }
                            // silhouette pixels (the bright opening behind a rock edge): a neighbour ray sees the open pit
                            bool edge = false;
                            foreach (var (ox, oy) in new[] { (-3, 0), (3, 0), (0, -3), (0, 3) })
                            {
                                var r2 = cam.ViewportPointToRay(new Vector3((x + ox + 0.5f) / tex.width, (y + oy + 0.5f) / tex.height, 0));
                                if (!Physics.Raycast(r2, out var h2, 2000f, ~(1 << 2), QueryTriggerInteraction.Ignore)) { edge = true; break; }
                                var hp2 = WorldOrigin.WorldToFrame(h2.point + h2.normal * 0.3f);
                                if (cave.SkyVisibility(hp2) > 0.5f || hf.DataHeight(hp2.x, hp2.z) - (float)hp2.y < 6f) { edge = true; break; }
                            }
                            if (edge) { open++; continue; }
                            if (cave.Network.IsFlow)
                            {
                                // the flow layout's upstream tube opens into the sunlit pit by design: bounce light in it is real
                                float2 dd = (float2)hp.xz - hf.pitCentre;
                                if (math.dot(dd, hf.pitAxis) < -hf.innerA) { open++; continue; }
                            }
                            under++;
                            if (under <= 6)
                            {
                                float2 d = (float2)hp.xz - hf.pitCentre;
                                float u = math.dot(d, hf.pitAxis), v = d.x * hf.pitAxis.y - d.y * hf.pitAxis.x;
                                float re = math.sqrt(u * u / (hf.innerA * hf.innerA) + v * v / (hf.innerB * hf.innerB));
                                var mf = hit.collider.GetComponent<MeshFilter>();
                                int sub = mf != null && mf.sharedMesh.subMeshCount > 1 && hit.triangleIndex >= (int)mf.sharedMesh.GetIndexCount(0) / 3 ? 1 : 0;
                                sb.Append($"[{hit.collider.name} sub{sub} re {re:0.00} u {u:0} depth {hf.DataHeight(hp.x, hp.z) - (float)hp.y:0} n.y {hit.normal.y:0.0}] ");
                            }
                        }
                        UnityEngine.Object.DestroyImmediate(tex);
                        if (under > 0) Note($"Tube look-back {lvl} {along:0} m: bright under the roof: {sb}");
                        Check($"Tube look-back {lvl} {along:0} m in (lights off): bright pixels under the rock roof (1/9 sampled)", under, 0, 0, "px");
                        Check($"Tube look-back {lvl} {along:0} m in: the open pit is still sunlit (1/9 sampled)", open, 1, 1e7, "px");
                    }
                    if (asset != null) { UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = src; QualitySettings.renderPipeline = prevQ; UnityEngine.Object.DestroyImmediate(asset); }
                }
            }
            finally
            {
                if (src != null) { UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = src; QualitySettings.renderPipeline = prevQ; }
                AllLamps(game, true);
            }
        }

        static void TubeMouthMaterial(GameBootstrap game)
        {
            var cam = game.cam; var cave = game.Cave;
            AllLamps(game, false);
            int tubeTris = 0;
            foreach (var mf in cave.GetComponentsInChildren<MeshFilter>())
                if (mf.gameObject.layer == RenderLayers.Pit && mf.sharedMesh.subMeshCount > 1) tubeTris += (int)mf.sharedMesh.GetIndexCount(1) / 3;
            Check("Tube mouth: pit-mesh triangles inside the tube on the tunnel material", tubeTris, 1000, 1e7, "tris");
            foreach (float along in new[] { 15f, 35f })
            {
                var fp = cave.TubeFloorPoint(along, out float2 dir);
                var pos = WorldOrigin.FrameToWorld(fp) + Vector3.up * 1.8f;
                game.Streamer.BuildNow(pos, pos);
                game.Tunnels.BuildNow(pos);
                Physics.SyncTransforms();
                cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(new Vector3(dir.x, -0.03f, dir.y)));
                game.Sky.UpdateVisibility();
                int rays = 0, onTunnelMat = 0;
                var fwd = new Vector3(dir.x, 0, dir.y);
                for (int k = 0; k < 24; k++)
                {
                    var d = Quaternion.AngleAxis(k * 15f, fwd) * Vector3.Cross(fwd, Vector3.up);
                    if (!Physics.Raycast(pos, d, out var hit, 40f, ~(1 << 2), QueryTriggerInteraction.Ignore)) continue;
                    var mr = hit.collider.GetComponent<MeshRenderer>();
                    if (mr == null) continue;
                    rays++;
                    bool pitMesh = hit.collider.gameObject.layer == RenderLayers.Pit;
                    if (!pitMesh) { onTunnelMat++; continue; }   // streamed tunnel chunk: tunnel material anyway
                    var mesh = hit.collider.GetComponent<MeshFilter>().sharedMesh;
                    int sub0 = (int)mesh.GetIndexCount(0) / 3;
                    if (mesh.subMeshCount > 1 && hit.triangleIndex >= sub0 && mr.sharedMaterials[1].shader == game.tunnelMaterial.shader) onTunnelMat++;
                }
                Check($"Tube mouth {along:0} m in: wall rays landing on the tunnel material", rays > 0 ? onTunnelMat * 100f / rays : -1, 90, 100, "%");
                game.Exposure.SnapToTarget();
                var st = TubeView(game, $"26_tube_mouth_{along:0}m_off", snap: false);
                Check($"Tube mouth {along:0} m in (lights off, exposure locked): bright pixels", st.bright, 0, 0, "px");
            }
            AllLamps(game, true);
        }

        /// <summary>Drive profile from the landing pad down the debris-mound ramp to 45 m inside the tube (3 lanes, 10 m wide).</summary>
        static void PitRampProfile(GameBootstrap game)
        {
            var cave = game.Cave; var f = cave.Field;
            float mouthU = math.dot(cave.Network.mouthFloor.xz - f.hf.pitCentre, f.hf.pitAxis);
            float uLand = math.dot(f.landingSpot - f.hf.pitCentre, f.hf.pitAxis), uEnd = mouthU + 45f;
            var endXZ = cave.RampXZ(uEnd);
            game.Tunnels.BuildNow(WorldOrigin.FrameToWorld(new double3(endXZ.x, f.rampEndY, endXZ.y)), 120f);
            Physics.SyncTransforms();
            var csv = new StringBuilder("u,v,h\n");
            float landSlope = 0, landStep = 0, rampSlope = 0, rampStep = 0, hLand = 0, hEnd = 0; int misses = 0;
            const float du = 0.25f;
            foreach (float v in new[] { -4f, 0f, 4f })
            {
                var hs = new List<float>();
                for (float u = uLand; u <= uEnd + 1e-3f; u += du)
                {
                    var xz = cave.RampXZ(u, v);
                    float top = math.max(f.RampY(u), f.rampEndY) + 3f;
                    var o = WorldOrigin.FrameToWorld(new double3(xz.x, top, xz.y));
                    float best = float.NaN, bestD = float.MaxValue;
                    foreach (var h in Physics.RaycastAll(o, Vector3.down, 12f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                        if (h.collider.transform.IsChildOf(cave.transform) && h.distance < bestD) { bestD = h.distance; best = top - h.distance; }
                    if (float.IsNaN(best)) { misses++; best = hs.Count > 0 ? hs[hs.Count - 1] : top - 3f; }
                    hs.Add(best);
                    csv.AppendLine($"{u:0.00},{v:0},{best:0.000}");
                }
                for (int i = 1; i < hs.Count - 1; i++)
                {
                    float u = uLand + i * du;
                    float step = Mathf.Abs(hs[i + 1] - 2 * hs[i] + hs[i - 1]);
                    int j0 = Mathf.Max(0, i - 2), j1 = Mathf.Min(hs.Count - 1, i + 2);
                    float slope = Mathf.Atan(Mathf.Abs(hs[j1] - hs[j0]) / ((j1 - j0) * du)) * Mathf.Rad2Deg;   // over 1 m
                    if (u < f.rampU0) { landSlope = Mathf.Max(landSlope, slope); landStep = Mathf.Max(landStep, step); }
                    else { rampSlope = Mathf.Max(rampSlope, slope); rampStep = Mathf.Max(rampStep, step); }
                }
                if (v == 0) { hLand = hs[0]; hEnd = hs[hs.Count - 1]; }
            }
            File.WriteAllText(Path.Combine(OutDir, "pit_ramp_profile.csv"), csv.ToString());
            Note($"Pit ramp: landing u={uLand:0.0} m, ramp {f.rampU0:0.0}..{f.rampU1:0.0} m (design grade {f.RampGradeDeg:0.0} deg), tube mouth u={mouthU:0.0} m, profile to u={uEnd:0.0} m; {misses} ray misses.");
            Check("Pit floor: debris mound height (landing pad above the tube floor 45 m in)", hLand - hEnd, 6, 14, "m");
            Check("Pit floor: landing zone + lane max slope (1 m baseline)", landSlope, 0, 8, "deg");
            Check("Pit floor: landing zone + lane max step", landStep * 100, 0, 5, "cm");
            Check("Pit floor: ramp + tube run-out max slope (1 m baseline, 10 m wide)", rampSlope, 0, 12, "deg");
            Check("Pit floor: ramp + tube run-out max step", rampStep * 100, 0, 5, "cm");
            Check("Pit floor: profile rays that found no floor", misses, 0, 0, "");
        }

        /// <summary>Visor glare: look 7 deg beside the Sun; halo must be finite and visible, patch over the Sun.</summary>
        static void SunGlareShot(GameBootstrap game)
        {
            var cam = game.cam; var eye = BuggyBuilder.DriverEye(game.Buggy);
            var sun = game.Sky.SunDir;
            var side = Vector3.Cross(sun, Vector3.up).normalized;
            var look = Quaternion.AngleAxis(7f, Vector3.up) * sun;
            cam.fieldOfView = 75f;
            cam.transform.SetPositionAndRotation(eye.position, Quaternion.LookRotation(look, Vector3.up));
            game.Sky.UpdateSky(true);
            var tex = Render(game, Path.Combine(OutDir, "19_sun_glare.png"), true);
            var sp = cam.WorldToViewportPoint(cam.transform.position + sun * 1000f);
            float pxPerDeg = tex.height / cam.fieldOfView;
            int cx = (int)(sp.x * tex.width), cy = (int)(sp.y * tex.height);
            int black = 0, n = 0; float ring = 0; int rn = 0;
            for (int dy = -(int)(10 * pxPerDeg); dy <= (int)(10 * pxPerDeg); dy += 2)
            for (int dx = -(int)(10 * pxPerDeg); dx <= (int)(10 * pxPerDeg); dx += 2)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= tex.width || y >= tex.height) continue;
                float deg = Mathf.Sqrt(dx * dx + dy * dy) / pxPerDeg;
                var c = tex.GetPixel(x, y);
                float v = c.r + c.g + c.b;
                if (float.IsNaN(v)) { black++; continue; }
                // skip the Earth disc (new Earth sits a few degrees from the Sun in the lunar morning)
                var pw = cam.ViewportPointToRay(new Vector3((float)x / tex.width, (float)y / tex.height, 0)).direction;
                if (Vector3.Angle(pw, game.Sky.EarthDir) < (float)game.Sky.State.earthAngularRadiusDeg + 0.4f) continue;
                if (deg > 3f && deg < 4.5f) { n++; if (v < 0.01f) black++; }
                if (deg > 5f && deg < 10f) { ring += v; rn++; }
            }
            UnityEngine.Object.DestroyImmediate(tex);
            Check("Sun glare: halo at 3-4.5 deg has no black / NaN pixels (Earth disc excluded)", n > 0 ? black : -1, 0, 0, "px");
            Check("Sun glare: halo visible 5-10 deg from the Sun (mean r+g+b)", rn > 0 ? ring / rn : -1, 0.02, 3, "");
            Check("Sun glare: visor local dimming patch active over the Sun", game.Sky.SunFilter, 0.99, 1, "");
        }

        /// <summary>Lunar night, full Earth, 8x zoom: the visor meters the Earth so the disc shows detail.</summary>
        static void NightEarthShot(GameBootstrap game)
        {
            bool had = SimClock.Instance != null;
            var clock = SimClock.Ensure();
            double keep = had ? clock.elapsedSeconds : 0;   // no clock = mission start
            clock.SetElapsed(SimClock.Presets[4].days * 86400.0);
            game.Sky.UpdateSky(true);
            var cam = game.cam; var eye = BuggyBuilder.DriverEye(game.Buggy);
            cam.transform.SetPositionAndRotation(eye.position, Quaternion.LookRotation(game.Sky.EarthDir, Vector3.up));
            cam.fieldOfView = PlayerControl.FovFor(8);
            game.Sky.UpdateSky(true);
            var tex = Render(game, Path.Combine(OutDir, "20_night_earth_8x.png"), true);
            var sp = cam.WorldToViewportPoint(cam.transform.position + game.Sky.EarthDir * 1000f);
            float rpx = Mathf.Tan((float)game.Sky.State.earthAngularRadiusDeg * Mathf.Deg2Rad) / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * tex.height * 0.5f;
            int cx = (int)(sp.x * tex.width), cy = (int)(sp.y * tex.height), disc = 0, clip = 0;
            for (int dy = -(int)rpx; dy <= (int)rpx; dy++)
            for (int dx = -(int)rpx; dx <= (int)rpx; dx++)
            {
                if (dx * dx + dy * dy > rpx * rpx * 0.9f) continue;
                var c = tex.GetPixel(cx + dx, cy + dy);
                disc++;
                if (c.r > 0.97f && c.g > 0.97f && c.b > 0.97f) clip++;
            }
            UnityEngine.Object.DestroyImmediate(tex);
            Note($"Night Earth: {game.Sky.State.earthLitFraction * 100:0}% lit, {disc} disc pixels, Earth metered: {game.Exposure.EarthMetered}.");
            Check("Night full Earth at 8x: clipped part of the disc", disc > 0 ? clip * 100f / disc : -1, 0, 5, "%");
            cam.fieldOfView = 75f;
            clock.SetElapsed(keep);
            game.Sky.UpdateSky(true);
        }

        /// <summary>Light-bleed probe: rays outward from inside the tube where it crosses the pit box face must all hit cave.</summary>
        static void PitSeamRays(GameBootstrap game)
        {
            var cave = game.Cave; var net = cave.Network;
            float3 a = net.mouthFloor, bb = net.NodePos(0, 0, 0);
            var box = cave.Box;
            float tc = -1;
            for (float t = 0; t <= 1; t += 0.0005f)
            {
                var q = math.lerp(a, bb, t);
                if (q.x < box.x || q.x > box.z || q.z < box.y || q.z > box.w) { tc = t; break; }
            }
            if (tc < 0) { Fail("Pit seam: the first tube never leaves the pit box"); return; }
            float len = math.length(bb - a);
            float3 axis = math.normalize(bb - a);
            var crossing = math.lerp(a, bb, tc);
            game.Tunnels.BuildNow(WorldOrigin.FrameToWorld((double3)crossing), 120f);
            Physics.SyncTransforms();
            int rays = 0, escapes = 0, escUp = 0, escDown = 0, escSide = 0; string probeNote = "";
            for (float off = -6f; off <= 6.01f; off += 1.5f)
            foreach (float hgt in new[] { 2f, 5f, 8f })
            {
                var o = WorldOrigin.FrameToWorld((double3)(math.lerp(a, bb, tc + off / len) + new float3(0, hgt, 0)));
                for (int i = 0; i < 300; i++)
                {
                    float y = 1 - 2 * (i + 0.5f) / 300f, r = Mathf.Sqrt(1 - y * y), ph = i * 2.39996f;
                    var dir = new Vector3(r * Mathf.Cos(ph), y, r * Mathf.Sin(ph));
                    if (Mathf.Abs(Vector3.Dot(dir, (Vector3)axis)) > 0.5f) continue;   // along the tube: open by design
                    rays++;
                    bool hit = false;
                    foreach (var h in Physics.RaycastAll(o, dir, 30f))
                        if (h.collider.transform.IsChildOf(cave.transform) || h.collider.transform.IsChildOf(game.Tunnels.transform)) { hit = true; break; }
                    if (!hit) { escapes++; if (dir.y > 0.3f) escUp++; else if (dir.y < -0.3f) escDown++; else escSide++; }
                }
                if (escapes > 0 && probeNote.Length < 600)
                {
                    var f0 = WorldOrigin.WorldToFrame(o); float fy = net.FloorY((float3)f0, 10f, 10f);
                    probeNote += $"[off {off:0.0} hgt {hgt}: y {f0.y:0.0}, SDF floor {fy:0.0}, escapes so far {escapes}] ";
                }
            }
            Note($"Pit seam probe: {rays} rays from 27 points within 6 m of the box face crossing; escapes up {escUp}, down {escDown}, sideways {escSide}. {probeNote}");
            Check("Pit/tunnel seam: rays escaping the tube within 30 m", escapes, 0, 0, "rays");
        }

        /// <summary>
        /// The drive players do first: from the lander straight towards the pit (~1.5 km), with floating-origin
        /// shifts exactly as in play (they don't run by themselves in edit mode) and terrain streaming.
        /// </summary>
        static void LanderToPitDrive(GameBootstrap game)
        {
            var b = game.Buggy;
            var log = new StringBuilder("t,frameX,frameZ,distToPit,speed,grounded,tilt,soc,shifts\n");
            int shifts = 0, recoveries = 0; float t = 0, stuckFor = 0, maxStuck = 0; bool diagDone = false;
            var pitFrame = new double3(0, 0, 0);
            double startDist = Unity.Mathematics.math.distance(WorldOrigin.WorldToFrame(b.transform.position).xz, pitFrame.xz);
            b.parkingBrake = false; b.brake = 0;
            for (int k = 0; k < 200 * 600; k++)
            {
                t += Dt;
                var f = WorldOrigin.WorldToFrame(b.transform.position);
                double dist = Unity.Mathematics.math.distance(f.xz, pitFrame.xz);
                if (dist < 110) break;   // reached the funnel edge
                // steer at the pit
                var to = WorldOrigin.FrameToWorld(new double3(0, f.y, 0)) - b.transform.position;
                float ang = Vector3.SignedAngle(Vector3.ProjectOnPlane(b.transform.forward, b.transform.up), Vector3.ProjectOnPlane(to, b.transform.up), b.transform.up);
                b.steering = Mathf.Clamp(ang / 60f, -0.6f, 0.6f);
                b.throttle = b.Speed < 2.8f ? 0.8f : 0f;        // a sensible driver: ~10 km/h over unknown ground
                if (Vector3.Angle(b.transform.up, -TerrainFrame.DownAt(b.transform.position)) > 70f && Mathf.Abs(b.Speed) < 0.5f)
                { b.Right(); recoveries++; Physics.SyncTransforms(); }
                if (b.HighCentred && stuckFor > 4f) { b.Right(); recoveries++; Physics.SyncTransforms(); stuckFor = 0; }
                LunarPhysics.AimGravity(b.transform.position); b.Step(Dt); game.Crane.Step(Dt); Physics.Simulate(Dt);
                if (k % 50 == 0)
                {
                    // floating origin, as WorldOrigin.LateUpdate does in play
                    var p = b.transform.position;
                    if (Mathf.Abs(p.x) > 1024f || Mathf.Abs(p.z) > 1024f || Mathf.Abs(p.y) > 1024f)
                    { WorldOrigin.ShiftBy(new Vector3(Mathf.Round(p.x), Mathf.Round(p.y), Mathf.Round(p.z))); shifts++; }
                    game.Streamer.BuildNow(b.transform.position, b.transform.position);
                }
                if (Mathf.Abs(b.Speed) < 0.3f) { stuckFor += Dt; maxStuck = Mathf.Max(maxStuck, stuckFor); } else stuckFor = 0;
                if (k % 200 == 0)
                {
                    int g = 0; foreach (var w in b.wheels) g += w.grounded ? 1 : 0;
                    log.AppendLine($"{t:0},{f.x:0.0},{f.z:0.0},{dist:0},{b.Speed:0.00},{g},{Vector3.Angle(b.transform.up, -TerrainFrame.DownAt(b.transform.position)):0},{b.stateOfCharge:0.000},{shifts}");
                }
                if (stuckFor > 5f && !diagDone)
                {
                    diagDone = true;
                    foreach (var w in b.wheels)
                    {
                        var m = b.transform.TransformPoint(w.mount);
                        var hits = Physics.RaycastAll(m + b.transform.up * 2f, -b.transform.up, 6f, ~(1 << 2));
                        var sbh = new StringBuilder();
                        foreach (var h in hits) sbh.Append($"{h.collider.name}@{Vector3.Dot(h.point - m, b.transform.up):0.00} ");
                        Note($"  stuck wheel {w.name}: grounded {w.grounded} load {w.load:0} x {w.x:0.00} omega {w.omega:0.0} slip {w.slip:0.00} | below: {sbh}");
                    }
                    var cols = Physics.OverlapBox(b.transform.TransformPoint(new Vector3(0, 0.6f, 0)), new Vector3(1.2f, 0.8f, 2.2f), b.transform.rotation, ~(1 << 2));
                    var sbc = new StringBuilder(); foreach (var c in cols) sbc.Append(c.name + " ");
                    Note($"  colliders touching the body volume: {sbc}");
                    var fs = WorldOrigin.WorldToFrame(b.transform.position);
                    Note($"  height function under rover {game.Height.Height(fs.x, fs.z, 0f):0.00}, rover frame y {fs.y:0.00}, hasCollider {game.Streamer.HasColliderAt(b.transform.position)}");
                    Shots(game, "22_stuck");
                }
                if (stuckFor > 20f) break;
            }
            File.WriteAllText(Path.Combine(OutDir, "lander_to_pit.csv"), log.ToString());
            var fe = WorldOrigin.WorldToFrame(b.transform.position);
            double end = Unity.Mathematics.math.distance(fe.xz, pitFrame.xz);
            Note($"Lander -> pit drive: {startDist:0} m -> {end:0} m from the pit in {t:0} s, {shifts} origin shifts, longest stop {maxStuck:0.0} s, {recoveries} recoveries (R).");
            Check("Lander -> pit: recoveries needed when driving blind in a straight line (R presses)", recoveries, 0, 8, "");
            Check("Lander -> pit: reached the funnel edge (no stall)", end, 0, 130, "m");
            b.throttle = 0; b.brake = 1; b.steering = 0;
        }

        static double3 RouteTarget(List<double3> route, double3 f, double look)
        {
            // closest point on the polyline, then walk ahead
            int bi = 0; double bt = 0, bd = double.MaxValue;
            for (int i = 0; i < route.Count - 1; i++)
            {
                var a = route[i].xz; var c = route[i + 1].xz; var ab = c - a;
                double t = Unity.Mathematics.math.saturate(Unity.Mathematics.math.dot(f.xz - a, ab) / Unity.Mathematics.math.max(1e-6, Unity.Mathematics.math.dot(ab, ab)));
                double d = Unity.Mathematics.math.distance(f.xz, a + ab * t);
                if (d < bd) { bd = d; bi = i; bt = t; }
            }
            double remaining = look;
            var p = Unity.Mathematics.math.lerp(route[bi], route[bi + 1], bt);
            for (int i = bi; i < route.Count - 1; i++)
            {
                var end = route[i + 1];
                double seg = Unity.Mathematics.math.distance(p.xz, end.xz);
                if (seg >= remaining) return p + (end - p) * (remaining / Unity.Mathematics.math.max(seg, 1e-6));
                remaining -= seg; p = end;
            }
            return route[route.Count - 1];
        }

        static double3 NearestJunctionAhead(TunnelNetwork net, double3 f)
        {
            var uv = net.ToUV((Unity.Mathematics.float2)f.xz);
            int i0 = Mathf.Max(0, Mathf.FloorToInt(uv.x / TunnelNetwork.Cell));
            int L = Mathf.Max(0, Mathf.RoundToInt((net.level0Floor - (float)f.y) / TunnelNetwork.LevelSpacing));
            double best = double.MaxValue; double3 bestP = f;
            for (int i = i0; i <= i0 + 1; i++)
            for (int j = Mathf.FloorToInt(uv.y / TunnelNetwork.Cell) - 1; j <= Mathf.FloorToInt(uv.y / TunnelNetwork.Cell) + 1; j++)
            {
                var n = net.NodePos(L, i, j);
                var nuv = net.ToUV(n.xz);
                if (nuv.x < uv.x + 20f) continue;
                double d = Unity.Mathematics.math.distance(n.xz, (Unity.Mathematics.float2)f.xz);
                if (d < best) { best = d; bestP = n; }
            }
            return bestP;
        }

        static void BaseShots(GameBootstrap game)
        {
            var cam = game.cam;
            var lander = game.LanderFrame.Value;
            var b = game.Buggy.transform;
            cam.fieldOfView = 60f;
            cam.transform.position = b.position + b.up * 3f - b.forward * 14f + b.right * 10f;
            cam.transform.LookAt(WorldOrigin.FrameToWorld(lander + new double3(0, 10, 0)));
            Render(game, Path.Combine(OutDir, "19_lander.png"));
            var cp = game.Crane.transform.position;
            cam.transform.position = cp + game.Crane.transform.up * 30f - game.Crane.transform.forward * 35f + game.Crane.transform.right * 30f;
            cam.transform.LookAt(cp + game.Crane.transform.forward * 25f);
            Render(game, Path.Combine(OutDir, "20_crane_overview.png"));
            cam.fieldOfView = 75f;
        }

        /// <summary>No large blown-out areas (the "super bright light ahead" bug): fraction of clipped pixels.</summary>
        static void CheckNoBlowout(GameBootstrap game, string what)
        {
            var cam = game.cam; var eye = BuggyBuilder.DriverEye(game.Buggy);
            cam.transform.SetPositionAndRotation(eye.position, eye.rotation);
            game.Sky.UpdateVisibility();
            var tex = Render(game, Path.Combine(OutDir, "23_blowout_" + (what.Contains("off") ? "dark" : "lit") + ".png"), true);
            if (!what.Contains("off"))
            {
                int g = 0; var all = tex.GetPixels();
                foreach (var c in all) if (c.r > 0.97f && c.g > 0.97f && c.b > 0.97f) g++;
                Check($"{what}: glint / clipped pixels (no strobing sparkle)", g * 100f / all.Length, 0, 0.05, "% of screen");
            }
            int clipped = 0, n = 0;
            for (int y = 0; y < tex.height; y += 4) for (int x = 0; x < tex.width; x += 4) { var c = tex.GetPixel(x, y); n++; if (c.r > 0.97f && c.g > 0.97f && c.b > 0.97f) clipped++; }
            UnityEngine.Object.DestroyImmediate(tex);
            Check($"{what}: blown-out screen area", clipped * 100f / n, 0, 3, "%");
        }

        static void CraneShot(GameBootstrap game, string name)
        {
            var cam = game.cam; var c = game.Crane;
            cam.fieldOfView = 60f;
            var pp = c.Platform.position;
            cam.transform.position = pp + Vector3.up * 8f + (pp - c.transform.position).normalized * 18f + c.transform.right * 12f;
            cam.transform.LookAt(pp + Vector3.up * 3f);
            Render(game, Path.Combine(OutDir, name + ".png"));
            cam.fieldOfView = 75f;
        }

        /// <summary>Round-8 features on the real world (own scene load, so -sections "Round 8" iterates fast).</summary>
        static void TestRound8()
        {
            EditorSceneManager.OpenScene("Assets/_Project/Scenes/Main.unity");
            var game = UnityEngine.Object.FindAnyObjectByType<GameBootstrap>();
            game.Initialise();
            var b = game.Buggy;
            Step(b, 600, i => { if (i % 200 == 0) game.Streamer.BuildNow(b.transform.position, b.transform.position); });
            Round8Render(game);
            DepositShots(game);
            PitBounceShots(game);
            VisorAssistShots(game);
            CockpitShot(game);
            MapShot(game);
            // ROUND8-MORE
        }

        /// <summary>1080p + FSR render scale per preset, and the frame cap default.</summary>
        static void Round8Render(GameBootstrap game)
        {
            Check("Render scale: 1440p screen renders the 3D view at 1080p (Medium)", QualityPresets.RenderScaleFor(QualityPresets.Level.Medium, 1440), 0.749, 0.751, "");
            Check("Render scale: 1440p screen, High preset also 1080p", QualityPresets.RenderScaleFor(QualityPresets.Level.High, 1440), 0.749, 0.751, "");
            Check("Render scale: 1080p screen renders native", QualityPresets.RenderScaleFor(QualityPresets.Level.Medium, 1080), 1, 1, "");
            Check("Render scale: Low preset on 1440p renders 900p", QualityPresets.RenderScaleFor(QualityPresets.Level.Low, 1440), 0.62, 0.63, "");
            var asset = UnityEngine.Object.Instantiate(UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset);
            QualityPresets.Configure(asset, QualityPresets.Level.High, 1440);
            Check("High preset: FSR upscaling filter", asset.upscalingFilter == UnityEngine.Rendering.Universal.UpscalingFilterSelection.FSR ? 1 : 0, 1, 1, "");
            Check("High preset keeps its quality (8k shadows, 4x MSAA)", asset.mainLightShadowmapResolution == 8192 && asset.msaaSampleCount == 4 ? 1 : 0, 1, 1, "");
            UnityEngine.Object.DestroyImmediate(asset);
        }

        /// <summary>
        /// Pit bounce: the sunlit wall relights the shadowed walls/floor, ~1/30 (high Sun) .. ~1/1000 (sliver) of a
        /// Sun-facing surface; nothing inside the tubes; the rim view keeps its look.
        /// </summary>
        static void PitBounceShots(GameBootstrap game)
        {
            Check("Pit bounce at 60 deg Sun: shadowed vs Sun-facing surface (~1/30)", 1f / PitBounce.RatioAt(60f), 15, 80, "x darker");
            Check("Pit bounce at 3 deg Sun (sliver lit): shadowed vs Sun-facing surface (~1/1000)", 1f / PitBounce.RatioAt(3f), 300, 3000, "x darker");
            PitBounce.Recompute(game.Sky);
            Note($"Pit bounce now: Sun el {game.Sky.SunElevationDeg:0.0} deg, lit fraction {PitBounce.LitFraction:0.00}, bounce {PitBounce.BounceLux:0} lux vs plain {PitBounce.SurfaceLux:0} lux (1/{PitBounce.SurfaceLux / Mathf.Max(PitBounce.BounceLux, 1e-3f):0}).");
            var tube = WorldOrigin.FrameToWorld(game.Cave.TubeFloorPoint(80f, out _));
            Check("Pit bounce: no light inside the tube (80 m in)", PitBounce.Irradiance(tube, Vector3.up), 0, 0, "lux");
            var cam = game.cam; cam.fieldOfView = 75f;
            var f = game.Cave.Field;
            var c = WorldOrigin.FrameToWorld(new double3(f.hf.pitCentre.x, f.rim, f.hf.pitCentre.y));
            var sunH = Vector3.ProjectOnPlane(game.Sky.SunDir, Vector3.up).normalized;
            if (sunH.sqrMagnitude < 0.1f) sunH = Vector3.forward;
            AllLamps(game, false);
            float Mean(Texture2D t) { double acc = 0; var px = t.GetPixels32(); foreach (var q in px) acc += q.r + q.g + q.b; UnityEngine.Object.DestroyImmediate(t); return (float)(acc / (px.Length * 3)); }
            // rim: stand on the Sun side, look across and down into the pit
            cam.transform.position = c + sunH * (Mathf.Sqrt(f.hf.innerA * f.hf.innerB) + 4f) + Vector3.up * 1.8f;
            cam.transform.LookAt(c - Vector3.up * (f.rim - f.pitFloor) * 0.8f);
            PitBounce.Enabled = false; float rimOff = Mean(Render(game, Path.Combine(OutDir, "25_pit_rim_nobounce.png"), keep: true));
            PitBounce.Enabled = true; float rimOn = Mean(Render(game, Path.Combine(OutDir, "25_pit_rim_bounce.png"), keep: true));
            // floor: look at the wall under the Sun-side rim (in shadow)
            var floorP = WorldOrigin.FrameToWorld(new double3(f.hf.pitCentre.x, f.pitFloor + 1.8f, f.hf.pitCentre.y));
            cam.transform.position = floorP - sunH * 6f;
            cam.transform.LookAt(floorP + sunH * 30f + Vector3.up * 8f);
            float MeanLow(Texture2D t) { double acc = 0; int n = 0; for (int y = 0; y < t.height * 0.3f; y += 3) for (int x = 0; x < t.width; x += 3) { var q = t.GetPixel(x, y); acc += q.r + q.g + q.b; n++; } UnityEngine.Object.DestroyImmediate(t); return (float)(acc / n * 85); }
            PitBounce.Enabled = false; var tOff = Render(game, Path.Combine(OutDir, "25_pit_floor_nobounce.png"), keep: true);
            PitBounce.Enabled = true; var tOn = Render(game, Path.Combine(OutDir, "25_pit_floor_bounce.png"), keep: true);
            float floorOff = MeanLow(UnityEngine.Object.Instantiate(tOff)), floorOn = MeanLow(UnityEngine.Object.Instantiate(tOn));
            float flOff = Mean(tOff), flOn = Mean(tOn);
            AllLamps(game, true);
            Note($"Pit bounce, mean pixel (0-255): rim view {rimOff:0.0} -> {rimOn:0.0}, pit floor towards the shadowed wall {flOff:0.0} -> {flOn:0.0}.");
            Check("Pit bounce keeps the rim view's look (brightness ratio)", rimOn / Mathf.Max(rimOff, 0.01f), 0.8, 1.35, "x");
            Note($"Pit floor itself (bottom 30% of that view): {floorOff:0.0} -> {floorOn:0.0}.");
            Check("Pit bounce keeps the pit floor's look (floor brightness ratio)", floorOn / Mathf.Max(floorOff, 0.01f), 0.8, 1.35, "x");
        }

        /// <summary>3D tube map: built from the explored tubes, rendered by its own camera; deposit colours on it.</summary>
        static void MapShot(GameBootstrap game)
        {
            // explore a little: scan along the first tubes of the network
            var net = game.Cave.Network;
            foreach (var n in net.ExplorePath(4, 1)) game.Nav.Scan((double3)net.NodePos(n.level, n.i, n.j));
            game.Nav.Scan(WorldOrigin.WorldToFrame(game.Buggy.transform.position));
            Check("Tube map: explored segments known", game.Nav.KnownEdges.Count, 5, 100000, "segments");
            var rt = game.Map.RenderOnce(net.mouthFloor, 900f);
            Note("Tube map: " + game.Map.Debug);
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(OutDir, "29_tube_map.png"), tex.EncodeToPNG());
            int lit = 0; foreach (var q in tex.GetPixels32()) if (q.r + q.g + q.b > 90) lit++;
            UnityEngine.Object.DestroyImmediate(tex);
            Check("Tube map: something drawn", lit, 500, 1e7, "px");
            var kinds = new Dictionary<RockDeposits.Kind, int>();
            foreach (var e in game.Nav.KnownEdges)
                for (int k = 0; k <= 10; k++)
                {
                    var p = math.lerp(e.a, e.b, k / 10.0);
                    var kd = RockDeposits.At((float3)p, new float3(0, 1, 0), game.Cave.Field.mouthFloorY, out _);
                    kinds[kd] = kinds.TryGetValue(kd, out var c) ? c + 1 : 1;
                }
            Note("Tube map floor samples by deposit: " + string.Join(", ", kinds.Select(kv => $"{kv.Key} {kv.Value}")));
        }

        /// <summary>Cockpit screens: live (redrawn), not blank, readable in a close-up from the driver's seat.</summary>
        static void CockpitShot(GameBootstrap game)
        {
            var sc = game.Buggy.GetComponent<CockpitScreens>();
            if (sc == null) { Fail("Cockpit screens missing"); return; }
            game.Nav.Scan(WorldOrigin.WorldToFrame(game.Buggy.transform.position));
            game.Nav.Track.Add(WorldOrigin.WorldToFrame(game.Buggy.transform.position));
            game.Nav.Track.Add(WorldOrigin.WorldToFrame(game.Buggy.transform.position - game.Buggy.transform.forward * 60f));
            int u0 = sc.Updates; sc.Redraw();
            Check("Cockpit screens redraw", sc.Updates - u0, 1, 1, "");
            for (int i = 0; i < 3; i++)
            {
                var px = sc.Screen(i).GetPixels32(); int lit = 0;
                foreach (var q in px) if (q.g > 60) lit++;
                Check($"Cockpit screen {i} has content (lit pixels)", lit, 100, CockpitScreens.W * CockpitScreens.H * 0.8, "px");
            }
            game.Teleport(2); Step(game.Buggy, 300);
            var eye = BuggyBuilder.DriverEye(game.Buggy);
            var console = game.Buggy.transform.Find("Body/Console");
            var cam = game.cam; cam.fieldOfView = 40f;
            cam.transform.position = eye.position;
            if (console != null) cam.transform.LookAt(console.position);
            sc.Redraw();
            Render(game, Path.Combine(OutDir, "28_cockpit_screens.png"));
            cam.fieldOfView = 75f;
        }

        /// <summary>
        /// Visor low-light assist: from the sunlit rim, looking down into the shadowed pit, the centre lifts by several EV
        /// within ~2 s while the surround keeps its brightness; looking away relaxes it.
        /// </summary>
        static void VisorAssistShots(GameBootstrap game)
        {
            var cam = game.cam; cam.fieldOfView = 75f;
            var f = game.Cave.Field; var va = game.Visor; var ex = game.Exposure;
            var c = WorldOrigin.FrameToWorld(new double3(f.hf.pitCentre.x, f.rim, f.hf.pitCentre.y));
            var sunH = Vector3.ProjectOnPlane(game.Sky.SunDir, Vector3.up).normalized;
            AllLamps(game, false);
            float R = Mathf.Sqrt(f.hf.innerA * f.hf.innerB);
            // stand back from the rim on the Sun side and look across, down at the shadowed foot of the far wall: the
            // sunlit plain and the lit top of the far wall fill the frame, the centre is the dark pit bottom
            float D = f.rim - f.pitFloor;
            cam.transform.position = c + sunH * (R + 14f) + Vector3.up * 1.8f;
            cam.transform.LookAt(c - sunH * R * 0.55f - Vector3.up * D * 0.92f);
            ex.SnapToTarget();
            va.Step(0); float evStart = va.Ev;
            float t = 0, t90 = -1;
            for (int i = 0; i < 40; i++) { va.Step(0.1f); t += 0.1f; if (t90 < 0 && va.TargetEv > 0.5f && va.Ev > 0.9f * va.TargetEv) t90 = t; }
            Note($"Visor assist from the rim: surround {ex.SurroundLum:0.###} vs centre {ex.CentreLum:0.####} ({Mathf.Log(ex.SurroundLum / Mathf.Max(ex.CentreLum, 1e-6f), 2):0.0} stops), lift {va.Ev:0.0} EV, 90% after {t90:0.0} s.");
            Check("Visor assist: lifts the dark pit centre (EV)", va.Ev, 2, 5.01, "EV");
            Check("Visor assist: ramps in over ~1-2 s (90%)", t90, 0.5, 2.5, "s");
            Check("Visor assist: starts from 0", evStart, 0, 0.01, "EV");
            float Mean(Texture2D tex, bool centre)
            {
                double acc = 0; int n = 0; int W = tex.width, H = tex.height;
                for (int y = 0; y < H; y += 4) for (int x = 0; x < W; x += 4)
                {
                    float dx = (x - W / 2f) / H, dy = (y - H / 2f) / H;
                    bool inC = dx * dx + dy * dy < 0.08f * 0.08f, outC = dx * dx + dy * dy > 0.4f * 0.4f;
                    if (centre ? !inC : !outC) continue;
                    var p = tex.GetPixel(x, y); acc += p.r + p.g + p.b; n++;
                }
                return n > 0 ? (float)(acc / n) : 0;
            }
            var on = Render(game, Path.Combine(OutDir, "26_visor_assist_on.png"), keep: true, snap: false);
            float keepEv = va.Ev;
            Tuning.VisorLift.Value = 0f; va.Step(10f);
            var off = Render(game, Path.Combine(OutDir, "26_visor_assist_off.png"), keep: true, snap: false);
            Tuning.VisorLift.Value = Tuning.VisorLift.Default;
            float cOn = Mean(on, true), cOff = Mean(off, true), sOn = Mean(on, false), sOff = Mean(off, false);
            UnityEngine.Object.DestroyImmediate(on); UnityEngine.Object.DestroyImmediate(off);
            Note($"Visor assist +{keepEv:0.0} EV: centre {cOff * 85:0.0} -> {cOn * 85:0.0}, surround {sOff * 85:0.0} -> {sOn * 85:0.0} (mean 0-255).");
            Check("Visor assist: centre brighter", cOn / Mathf.Max(cOff, 1e-4f), 1.5, 100, "x");
            Check("Visor assist: surround unchanged", sOn / Mathf.Max(sOff, 1e-4f), 0.9, 1.1, "x");
            // look away (out over the sunlit plain): relaxes
            cam.transform.rotation = Quaternion.LookRotation(sunH + Vector3.down * 0.15f);
            ex.SnapToTarget();
            for (int i = 0; i < 30; i++) va.Step(0.1f);
            Check("Visor assist: relaxes when looking away", va.Ev, 0, 0.3, "EV");
            AllLamps(game, true);
        }

        /// <summary>
        /// Round-7 bug: an orange glass-bead patch with straight, square edges. Finds a deposit patch on a level-0 tube
        /// floor that a 180 m colour-cell boundary cuts through (the old per-cell colour pick), and renders it with the
        /// old maths and the new (smooth colour drift + broken-up bead-spray edge).
        /// </summary>
        static void DepositShots(GameBootstrap game)
        {
            var net = game.Cave.Network;
            var path = net.ExplorePath(40, 0);
            float3? found = null;
            for (int s = 0; s + 1 < path.Count && found == null; s++)
            {
                var a = net.NodePos(path[s].level, path[s].i, path[s].j); var bb = net.NodePos(path[s + 1].level, path[s + 1].i, path[s + 1].j);
                float len = math.distance(a.xz, bb.xz);
                for (float d = 0; d < len && found == null; d += 3f)
                {
                    var c = math.lerp(a, bb, d / len);
                    for (float off = -12f; off <= 12f && found == null; off += 4f)
                    {
                        var dirv = math.normalize(bb.xz - a.xz);
                        var fp = c + new float3(-dirv.y * off, 0.3f, dirv.x * off);
                        float g0 = RockDeposits.Glass(fp, 1f, out float m0, legacy: true);
                        if (g0 < 0.3f) continue;
                        foreach (var o in new[] { new float3(8, 0, 0), new float3(-8, 0, 0), new float3(0, 0, 8), new float3(0, 0, -8) })
                        {
                            float g1 = RockDeposits.Glass(fp + o, 1f, out float m1, legacy: true);
                            if (g1 > 0.3f && m1 != m0) { found = fp; break; }
                        }
                    }
                }
            }
            if (found == null) { Note("Deposits: no glass patch cut by a colour-cell edge on the first 40 level-0 tubes (nothing to compare)."); return; }
            var target = WorldOrigin.FrameToWorld((double3)found.Value);
            game.Tunnels.BuildNow(target, 120f);
            Physics.SyncTransforms();
            if (Physics.Raycast(target + Vector3.up * 6f, Vector3.down, out var hit, 20f, ~(1 << 2), QueryTriggerInteraction.Ignore)) target = hit.point;
            var cam = game.cam;
            cam.fieldOfView = 60f;
            cam.transform.position = target + Vector3.up * 9f + new Vector3(6f, 0, -6f);
            cam.transform.LookAt(target);
            var lamp = new GameObject("TestLamp").AddComponent<Light>();
            lamp.type = LightType.Spot; lamp.spotAngle = 90; lamp.range = 60; lamp.transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
            lamp.gameObject.AddComponent<PhysicalLight>().physicalIntensity = 20000f;
            AllLamps(game, false);
            Shader.SetGlobalFloat("_MBA_DepositLegacy", 1f);
            Render(game, Path.Combine(OutDir, "24_deposit_before.png"));
            Shader.SetGlobalFloat("_MBA_DepositLegacy", 0f);
            Render(game, Path.Combine(OutDir, "24_deposit_after.png"));
            UnityEngine.Object.DestroyImmediate(lamp.gameObject);
            AllLamps(game, true);
            Note($"Deposits: glass patch on a colour-cell edge at frame {found.Value}; before/after shots 24_deposit_*.png.");
            // the new colour field has no straight edge: max colour change over 1 m anywhere on a 60 m line across the old edge
            float maxStep = 0;
            for (float x = -30; x < 30; x += 1f)
            {
                RockDeposits.Glass(found.Value + new float3(x, 0, 0), 1f, out float ma);
                RockDeposits.Glass(found.Value + new float3(x + 1, 0, 0), 1f, out float mb);
                maxStep = Mathf.Max(maxStep, Mathf.Abs(ma - mb));
            }
            Check("Deposits: orange/green colour changes smoothly (max change per metre)", maxStep, 0, 0.15, "");
        }

        static void CollapseShot(GameBootstrap game)
        {
            var net = game.Cave.Network;
            for (int i = 2; i < 40; i++)
            for (int j = -3; j <= 3; j++)
            {
                if (!net.HasCollapse(0, i, j)) continue;
                var top = net.CollapsePos(0, i, j);
                var below = new double3(top.x, net.LevelFloor(1, net.ToUV(top.xz).x) + 4f, top.z);
                var w = WorldOrigin.FrameToWorld(below + new double3(25, 0, 0));
                game.Tunnels.BuildNow(w, 140f);
                var cam = game.cam;
                cam.fieldOfView = 75f;
                cam.transform.position = w;
                cam.transform.LookAt(WorldOrigin.FrameToWorld(new double3(top.x, top.y - 10, top.z)));
                // light it: a helmet-lamp-like light at the camera
                var lamp = new GameObject("TestLamp").AddComponent<Light>();
                lamp.type = LightType.Spot; lamp.spotAngle = 70; lamp.range = 120; lamp.transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
                lamp.gameObject.AddComponent<PhysicalLight>().physicalIntensity = 30000f;
                Render(game, Path.Combine(OutDir, "21_collapse_from_below.png"));
                UnityEngine.Object.DestroyImmediate(lamp.gameObject);
                Note($"Collapse shaft found at level-0 junction ({i},{j}), {top.y - below.y:0} m drop to level 1.");
                return;
            }
            Note("No collapse shaft found in the scanned area.");
        }

        static void HelmetComposite(GameBootstrap game, string name)
        {
            // the helmet frame must leave the view clear: centre fully transparent, small average coverage
            var hm = MoonBaseAlpha.UI.HelmetOverlay.Texture;
            float centreA = hm.GetPixelBilinear(0.5f, 0.5f).a, sumA = 0; int cnt = 0;
            for (float y = 0.025f; y < 1f; y += 0.05f) for (float x = 0.025f; x < 1f; x += 0.05f) { sumA += hm.GetPixelBilinear(x, y).a; cnt++; }
            Check("Helmet frame: centre of view fully clear (alpha)", centreA, 0, 0.02, "");
            Check("Helmet frame: average screen coverage", sumA / cnt * 100, 0, 22, "%");
            var cam = game.cam; var w = game.Player.Walker;
            w.rig.SetVisible(false, false);
            cam.transform.SetPositionAndRotation(w.eye.position, w.transform.rotation);
            var tex = Render(game, Path.Combine(OutDir, name + "_raw.png"), true);
            var helm = MoonBaseAlpha.UI.HelmetOverlay.Texture;
            var px = tex.GetPixels();
            for (int y = 0; y < tex.height; y++)
            for (int x = 0; x < tex.width; x++)
            {
                var h = helm.GetPixelBilinear((x + 0.5f) / tex.width, (y + 0.5f) / tex.height);
                px[y * tex.width + x] = Color.Lerp(px[y * tex.width + x], new Color(h.r, h.g, h.b, 1), h.a);
            }
            tex.SetPixels(px); tex.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, name + ".png"), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            File.Delete(Path.Combine(OutDir, name + "_raw.png"));
        }

        static void VisorShots(GameBootstrap game)
        {
            var cam = game.cam;
            var eye = BuggyBuilder.DriverEye(game.Buggy);
            {
                var dir = (game.Sky.EarthDir + Vector3.down * 0.25f).normalized;
                cam.transform.SetPositionAndRotation(eye.position, Quaternion.LookRotation(dir, Vector3.up));
                cam.fieldOfView = 75f;
                var tex = Render(game, Path.Combine(OutDir, "08_earth_view.png"), true);
                var sp = cam.WorldToViewportPoint(cam.transform.position + game.Sky.EarthDir * 1000f);
                float peak = 0;
                int cx = (int)(sp.x * tex.width), cy = (int)(sp.y * tex.height);
                for (int dy = -12; dy <= 12; dy++)
                for (int dx = -12; dx <= 12; dx++)
                {
                    var c = tex.GetPixel(cx + dx, cy + dy);
                    peak = Mathf.Max(peak, c.r + c.g + c.b);
                }
                float sky = 0;   // brightest pixel in empty sky beside the Earth
                for (int dy = -12; dy <= 12; dy++)
                for (int dx = 60; dx <= 84; dx++) { var c = tex.GetPixel(cx + dx, cy + dy + 60); sky = Mathf.Max(sky, c.r + c.g + c.b); }
                UnityEngine.Object.DestroyImmediate(tex);
                Check("Smart visor: Earth stands out from the black sky (peak - sky, r+g+b)", peak - sky, 0.3, 3.01, "");
                Check("Smart visor: sky stays black (r+g+b)", sky, 0, 0.12, "");
            }
            // Earth at 8x visor zoom (lunar morning: thin crescent + earthshine-lit night side)
            cam.transform.SetPositionAndRotation(eye.position, Quaternion.LookRotation(game.Sky.EarthDir, Vector3.up));
            cam.fieldOfView = PlayerControl.FovFor(8);
            Render(game, Path.Combine(OutDir, "08_earth_zoom8x.png"));
            Note($"Earth at 8x: exposure metered the Earth disc: {game.Exposure.EarthMetered}.");
            cam.fieldOfView = 75f;
            cam.transform.SetPositionAndRotation(eye.position, eye.rotation);
            Render(game, Path.Combine(OutDir, "09_start_fp.png"));
            SunGlareShot(game);
            NightEarthShot(game);
            var b = game.Buggy.transform;
            cam.fieldOfView = 50f;
            cam.transform.position = b.position + b.right * 3.2f + b.forward * 2.8f + b.up * 1.6f;
            cam.transform.LookAt(b.position + b.up * 0.7f, b.up);
            Render(game, Path.Combine(OutDir, "10_rover_closeup.png"));
            cam.transform.position = b.position - b.right * 2.2f + b.up * 0.6f + b.forward * 1.45f;
            cam.transform.LookAt(b.TransformPoint(new Vector3(-1f, -0.15f, 1.45f)), b.up);
            Render(game, Path.Combine(OutDir, "11_wheel_closeup.png"));
            game.Player.ExitRover();
            var w = game.Player.Walker;
            for (int i = 0; i < 200; i++) { LunarPhysics.AimGravity(w.transform.position); w.Step(Dt); game.Buggy.Step(Dt); Physics.Simulate(Dt); }
            w.rig.SetVisible(true, true);
            var wt = w.transform;
            cam.transform.position = wt.position + wt.forward * 2.6f + wt.up * 1.5f + wt.right * 0.8f;
            cam.transform.LookAt(wt.position + wt.up * 1.1f, wt.up);
            Render(game, Path.Combine(OutDir, "12_astronaut_closeup.png"));
            cam.fieldOfView = 75f;
            game.Player.EnterRoverForced();
        }

        static void WalkerShots(GameBootstrap game, string name)
        {
            var cam = game.cam; var w = game.Player.Walker;
            w.rig.SetVisible(false, false);
            cam.transform.SetPositionAndRotation(w.eye.position, w.transform.rotation);
            Render(game, Path.Combine(OutDir, name + "_fp.png"));
            w.rig.SetVisible(true, true);
            cam.transform.position = w.transform.position - w.transform.forward * 4f + w.transform.up * 2.2f;
            cam.transform.LookAt(w.transform.position + w.transform.up * 1.2f + w.transform.forward * 3f, w.transform.up);
            Render(game, Path.Combine(OutDir, name + "_3p.png"));
        }

        static void Shots(GameBootstrap game, string name, bool fpOnly = false)
        {
            var cam = game.cam;
            var eye = BuggyBuilder.DriverEye(game.Buggy);
            game.Sky.UpdateSky(true);

            // first-person
            cam.transform.SetPositionAndRotation(eye.position, eye.rotation);
            cam.fieldOfView = 75f;
            Render(game, Path.Combine(OutDir, name + "_fp.png"));
            if (fpOnly) return;

            // third-person, behind-left and above
            var b = game.Buggy.transform;
            cam.transform.position = b.position - b.forward * 7f + b.up * 2.6f - b.right * 3f;
            cam.transform.LookAt(b.position + b.up * 0.6f, b.up);
            Render(game, Path.Combine(OutDir, name + "_3p.png"));

            if (name == "01_start")
            {
                // telephoto on the Earth
                cam.transform.SetPositionAndRotation(eye.position, Quaternion.LookRotation(game.Sky.EarthDir, Vector3.up));
                cam.fieldOfView = 4f;
                Render(game, Path.Combine(OutDir, name + "_earth_tele.png"));
                cam.fieldOfView = 75f;
                // look into the shadow side: exposure opens, stars/city lights?
                cam.transform.SetPositionAndRotation(eye.position, Quaternion.LookRotation(-game.Sky.SunDir + Vector3.up * 0.6f, Vector3.up));
                Render(game, Path.Combine(OutDir, name + "_west_sky.png"));
            }
        }

        static Texture2D Render(GameBootstrap game, string path, bool keep = false, bool snap = true)
        {
            var cam = game.cam;
            var ex = game.Exposure;
            game.Sky.UpdateSky(true);
            foreach (var lb in UnityEngine.Object.FindObjectsByType<LampBounce>()) lb.Refresh();
            LampBounce.PushGlobals(ex.Exposure);
            PitBounce.Recompute(game.Sky); PitBounce.PushGlobals(ex.Exposure);
            if (snap) ex.SnapToTarget();
            const int W = 1600, H = 900;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.Render();   // second pass: shadows/exposure globals settled
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            if (!keep) { UnityEngine.Object.DestroyImmediate(tex); tex = null; }
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            Note($"Screenshot `{Path.GetFileName(path)}` (EV {Mathf.Log(1f / ex.Exposure, 2):0.0}, metered {ex.MeteredLuminance:0.###}).");
            return tex;
        }
    }
}
