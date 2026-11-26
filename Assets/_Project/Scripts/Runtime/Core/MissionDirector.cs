using System.Collections.Generic;
using System.Text;
using Unity.Mathematics;
using UnityEngine;
using MoonBaseAlpha.Base;
using MoonBaseAlpha.Buggy;
using MoonBaseAlpha.Cave;
using MoonBaseAlpha.Crew;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha
{
    /// <summary>
    /// The GIMLI-2 expedition story: the dialogue beats by trigger, the
    /// crew's tube-feature call-outs and deposit sightings (paced), status reports to Houston, then the drill, the
    /// vapour burst + spectrometer, the probe camera in the sealed chamber and the debrief screen.
    /// Every line goes through <see cref="Radio"/> by ID (one queue: lines never overlap). <see cref="Tick"/> is
    /// deterministic, driven by Update in play and by the verification in tests.
    /// </summary>
    [DefaultExecutionOrder(500)]
    public class MissionDirector : MonoBehaviour
    {
        public GameBootstrap game;
        public enum Phase { Idle, Drive, Venting, Probe, Debrief }
        public Phase phase = Phase.Idle;
        public float MissionTime { get; private set; }
        public float PhaseTime { get; private set; }

        public const float FeatureGap = 45f, FirstFeatureGap = 12f, SameTypeGap = 120f, StatusEvery = 210f, LookAhead = 30f;
        public const float DrillSeconds = 4f, VisualRange = 12f, DrillRange = 16f, DepositRange = 25f;
        public const float VentSeconds = 9f, ProbeMinSeconds = 14f, ProbeMaxSeconds = 45f;
        /// <summary>The probe camera's metering (cd/m^2): frost lit by its lamp from ~2-3 m.</summary>
        public const float ProbeLuminance = 12f;

        /// <summary>The HUD (DebugHud) hides itself during the probe camera and the debrief.</summary>
        public static bool HideHud { get; private set; }
        /// <summary>A new world: the HUD shows again.</summary>
        public static void ResetStatics() { HideHud = false; DebriefOpen = false; }
        /// <summary>The debrief screen (with its buttons) is up.</summary>
        public static bool DebriefOpen { get; private set; }

        readonly HashSet<string> once = new();
        /// <summary>Has a trigger (usually a line ID) fired?</summary>
        public bool Fired(string key) => once.Contains(key);
        bool Once(string key) => once.Add(key);
        /// <summary>"12.5 s junction" for every feature call-out (tests, timing report).</summary>
        public readonly List<string> FeatureLog = new();
        /// <summary>Deposit sightings tagged on the 3D map for the return trip.</summary>
        public readonly List<(double3 frame, RockDeposits.Kind kind)> Tags = new();

        public int Level { get; private set; } = -1;          // tube level now (-1 surface)
        public int MaxLevel { get; private set; } = -1;       // deepest tube level reached
        public bool InTubes { get; private set; }
        public bool TransponderOn => Fired("b2_touchdown") || MaxLevel >= 0;
        public float DrillProgress { get; private set; }
        public bool DrillReady { get; private set; }
        public float BatteryAtDiscovery { get; private set; } = -1f;
        public float TimeAtDiscovery { get; private set; } = -1f;

        float basaltT, statusT, lastFeature = -999f, sampleT, depositT, surfaceDriven;
        int cmdrReports, statusN, tagN, junctionN;
        readonly Dictionary<string, float> lastType = new();
        readonly HashSet<RockDeposits.Kind> seen = new();
        readonly List<TunnelPrim> buf = new();
        Vector3 lastRoverPos; bool havePos;
        bool drillKey;   // F held this frame (play: Input; tests: SetDrillKey)

        // ------------------------------------------------------------------ lifecycle

        public void Begin()
        {
            if (phase != Phase.Idle) return;
            phase = Phase.Drive; PhaseTime = 0; MissionTime = 0;
            HideHud = false;
        }

        void OnDestroy() { HideHud = false; }

        /// <summary>Tests: hold / release the drill key.</summary>
        public void SetDrillKey(bool held) => drillKey = held;

        void Update()
        {
            if (game == null || !game.Initialised) return;
            if (phase == Phase.Idle) return;
            if (Application.isPlaying) drillKey = Controls.Held(Controls.Btn.Use);   // F / pad A (hold)
            Tick(Time.deltaTime);
            if (phase >= Phase.Venting)
            {
                var b = game.Buggy; b.throttle = 0; b.brake = 1f;
            }
            if (phase == Phase.Debrief && (Cursor.lockState != CursorLockMode.None || !Cursor.visible)) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            // debrief on the pad: A = play again, Y = new mission, Back = exit (asks)
            if (phase == Phase.Debrief && PhaseTime > 1.5f && !MoonBaseAlpha.UI.QuitConfirm.Asking)
            {
                if (Controls.PadDown(Controls.Btn.Use)) MoonBaseAlpha.UI.PauseMenu.Restart();
                else if (Controls.PadDown(Controls.Btn.View)) MoonBaseAlpha.UI.PauseMenu.NewMission();
                else if (Controls.PadDown(Controls.Btn.Back)) MoonBaseAlpha.UI.QuitConfirm.Ask();
            }
        }

        // ------------------------------------------------------------------ the tick

        public void Tick(float dt)
        {
            if (phase == Phase.Idle || game == null) return;
            PhaseTime += dt;
            if (phase <= Phase.Drive) MissionTime += dt;
            var b = game.Buggy; var pc = game.Player;
            var focus = pc != null ? pc.Focus : b.transform;
            var f = WorldOrigin.WorldToFrame(focus.position);
            var p = (float3)f;
            var hf = game.Height; var field = game.Cave.Field;
            float below = hf.DataHeight(f.x, f.z) - p.y;
            bool underground = below > 15f;
            float pitDist = math.distance(p.xz, hf.pitCentre);
            InTubes = underground && pitDist > hf.innerA + 15f;
            Level = underground ? game.Cave.Network.LevelAt(p) : -1;
            if (InTubes) MaxLevel = math.max(MaxLevel, Level);
            var fw3 = Vector3.ProjectOnPlane(focus.forward, Vector3.up);
            var fwd = math.normalizesafe(new float2(fw3.x, fw3.z), new float2(0, 1));
            bool driving = pc == null || pc.State == PlayerControl.Mode.Driving;
            if (havePos && !underground && driving) surfaceDriven += Vector3.Distance(b.transform.position, lastRoverPos);
            lastRoverPos = b.transform.position; havePos = true;

            switch (phase)
            {
                case Phase.Drive:
                    Beat1(p, fwd, underground);
                    Beat2(underground);
                    Beat3(dt, p, driving, b);
                    Status(dt, driving, b);
                    sampleT += dt; depositT += dt;
                    if (InTubes && sampleT >= 0.5f) { sampleT = 0; Features(p, fwd); }
                    if (InTubes && depositT >= 0.25f) { depositT = 0; Deposits(b); }
                    Beat4(p, dt);
                    break;
                case Phase.Venting:
                    if (PhaseTime >= VentSeconds) EnterProbe();
                    break;
                case Phase.Probe:
                    if (PhaseTime >= ProbeMaxSeconds || (PhaseTime >= ProbeMinSeconds && Radio.Spoken.Contains("b5_houston") && !Radio.Busy)) EnterDebrief();
                    break;
            }
        }

        // ------------------------------------------------------------------ beat 1: surface

        static bool Ahead(float3 p, float2 fwd, float3 q, float cosMax = 0.5f)
        {
            var v = q.xz - p.xz; float l = math.length(v);
            return l < 8f || math.dot(v / l, fwd) > cosMax;
        }

        /// <summary>The rover is on the crane platform (the ride has its own lines).</summary>
        bool OnCrane => game.Crane != null && game.Crane.RoverOnPlatform;

        void Beat1(float3 p, float2 fwd, bool underground)
        {
            if (underground || OnCrane) return;
            if (game.GimliRoverFrame.HasValue && !Fired("b1_gimli1_spec"))
            {
                var g = (float3)game.GimliRoverFrame.Value;
                float d = math.distance(g.xz, p.xz);
                if (d < 170f && Ahead(p, fwd, g, 0.4f) && Once("b1_gimli1_spec"))
                {
                    Radio.Say("b1_gimli1_spec");
                    Radio.FromEarth("b1_gimli1_houston");
                    Radio.Say("b1_gimli1_name", Radio.RoundTrip + 1.4f);
                }
            }
            if (game.CargoFrame.HasValue && !Fired("b1_cargo_lander"))
            {
                var c = (float3)game.CargoFrame.Value;
                float d = math.distance(c.xz, p.xz);
                if ((d < 110f || (d < 220f && Ahead(p, fwd, c, 0.6f))) && !Radio.Busy && Once("b1_cargo_lander")) Radio.Say("b1_cargo_lander");
            }
            // optional: the first look back at our own tracks (camera facing backwards after a real drive)
            if (!Fired("b1_tracks") && surfaceDriven > 150f && game.cam != null && !Radio.Busy)
            {
                var look = Vector3.ProjectOnPlane(game.cam.transform.forward, Vector3.up).normalized;
                var rf = Vector3.ProjectOnPlane(game.Buggy.transform.forward, Vector3.up).normalized;
                if (Vector3.Dot(look, rf) < -0.4f && Once("b1_tracks")) Radio.Say("b1_tracks");
            }
        }

        // ------------------------------------------------------------------ beat 2: the crane

        void Beat2(bool underground)
        {
            var crane = game.Crane;
            if (crane == null) return;
            if (crane.state == CraneController.State.Parked && crane.RoverFullyOnDeck && Once("b2_platform")) Radio.Say("b2_platform");
            if (crane.state == CraneController.State.Hoisting && crane.GatesClosed && Once("b2_gates")) Radio.Say("b2_gates");
            if (crane.state == CraneController.State.Lowering && crane.RoverOnPlatform)
            {
                float depth = game.Cave.Field.rim - (float)WorldOrigin.WorldToFrame(crane.Platform.position).y;
                // story lines wait for an empty queue (no backlog behind the shudder call)
                if (depth > 3f && Radio.Pending == 0 && Once("b2_below_rim")) Radio.Say("b2_below_rim");
                if (depth > 14f && Fired("b2_below_rim") && Radio.Pending == 0 && Once("b2_bounce")) Radio.Say("b2_bounce");
                if (depth > 26f && Fired("b2_bounce") && Radio.Pending == 0 && Once("b2_pit_temp")) Radio.Say("b2_pit_temp");
            }
            if (crane.state == CraneController.State.AtBottom && crane.RoverOnPlatform && Once("b2_touchdown"))
            {
                if (!Fired("b2_pit_temp") && Once("b2_pit_temp")) { Once("b2_below_rim"); Once("b2_bounce"); Radio.Say("b2_pit_temp"); }   // [K] skipped the descent
                Radio.FromEarth(Mission.Tiered("b2_touchdown"));
            }
        }

        // ------------------------------------------------------------------ beat 3: in the tubes

        void Beat3(float dt, float3 p, bool driving, BuggyController b)
        {
            var crane = game.Crane;
            if (Fired("b2_touchdown") && crane != null && !crane.RoverOnPlatform && driving && Once("b3_offplatform")) Radio.Say("b3_offplatform");
            if (InTubes && math.distance(p, game.Cave.Network.mouthFloor) > 40f && Once("b3_tube_temp")) Radio.Say("b3_tube_temp");
            if (InTubes && driving && b.Speed > 1f)
            {
                bool allRock = true; int g = 0;
                foreach (var w in b.wheels) { if (!w.grounded) continue; g++; if (!w.soil.rigid) allRock = false; }
                if (g >= 3 && allRock) basaltT += dt;
            }
            if (basaltT > 15f && !Radio.Busy && Once("b3_basalt")) { Radio.Say("b3_basalt"); Radio.Say("b3_highway", 0.3f); }
            if (b.stateOfCharge < 0.3f && Once("b3_lowbatt")) Radio.Say("b3_lowbatt");
            // "on GIMLI-2's level now, signal maxed out just ahead": in the flow layout GIMLI-2 is often in the top flow,
            // so the call also waits until the route has < 500 m left
            if (InTubes && Level == GimliLevel && (!game.Cave.Network.IsFlow || game.Route == null || !game.Route.Valid || game.Route.Remaining(p, out _, out _) < 500f)
                && Once("b3_final_level"))
            {
                Radio.FromEarth("b3_final_level");
                Radio.Say("b3_final_cmdr", Radio.RoundTrip + 1.4f);
            }
        }

        void Status(float dt, bool driving, BuggyController b)
        {
            if (!driving || OnCrane) return;
            statusT += dt;
            if (statusT < StatusEvery || Radio.Busy) return;
            statusT = 0;
            int band = math.clamp((int)math.round(b.stateOfCharge * 10f) * 10, 10, 90);
            Radio.Say($"s_batt_{band}");
            Radio.FromEarth(statusN++ % 2 == 0 ? "s_ack_1" : "s_ack_2");
        }

        // ------------------------------------------------------------------ tube features

        public struct Feature { public string type; public float3 at; public float dist; }

        /// <summary>
        /// Tube features ~30 m ahead of p along fwd, on p's level (network prims): junction (a node chamber with 3+
        /// tubes), breakdown pile (tube mound), collapse ramp (kind 2), lava fall (drop), large chamber (hw > 60),
        /// dead-end spur (inside one, heading in), level ramp (kind 1, on it); a collapse shaft comes from the EDGE AHEAD
        /// warning (or CollapseNear ahead).
        /// </summary>
        public List<Feature> FeaturesAhead(float3 p, float2 fwd)
        {
            var list = new List<Feature>();
            var net = game.Cave.Network;
            net.Collect(p - new float3(70, 25, 70), p + new float3(70, 25, 70), 5f, buf);
            void Add(string t, float3 at) => list.Add(new Feature { type = t, at = at, dist = math.distance(at.xz, p.xz) });
            foreach (var pr in buf)
            {
                if (pr.type == TunnelPrim.Chamber)
                {
                    if (math.abs(pr.a.y - p.y) > 10f) continue;
                    float d = math.distance(pr.a.xz, p.xz);
                    if (pr.hw >= 60f) { if (d < pr.hw + LookAhead && (d < pr.hw * 0.9f || Ahead(p, fwd, pr.a))) Add("chamber", pr.a); continue; }
                    if (pr.moundH > 0) continue;   // talus cone under a collapse: the shaft call-out covers it
                    if (d > pr.hw + LookAhead || !Ahead(p, fwd, pr.a)) continue;
                    int n = 0;
                    foreach (var q in buf)
                        if (q.type == TunnelPrim.Tube && q.kind != 2 && (math.distance(q.a.xz, pr.a.xz) < 1f || math.distance(q.b.xz, pr.a.xz) < 1f) && math.abs(math.min(q.a.y, q.b.y) - pr.a.y) < 80f) n++;
                    if (n >= 3) Add("junction", pr.a);
                    continue;
                }
                if (pr.type != TunnelPrim.Tube) continue;
                float len = math.distance(pr.a.xz, pr.b.xz);
                if (len < 1f) continue;
                var ab = pr.b.xz - pr.a.xz;
                float t = math.saturate(math.dot(p.xz - pr.a.xz, ab) / (len * len));
                var c = math.lerp(pr.a, pr.b, t);
                float axis = math.distance(c.xz, p.xz);
                float floorHere = TunnelNetwork.FloorAt(pr, t);
                if (pr.kind == 1)
                {
                    if (axis < pr.hw && t > 0.05f && t < 0.95f && math.abs(floorHere - p.y) < 5f) Add("levelramp", c);
                    continue;
                }
                if (pr.kind == 2)
                {
                    if (axis < LookAhead && Ahead(p, fwd, c) && math.abs(floorHere - p.y) < 6f) Add("collapse_ramp", new float3(c.x, floorHere, c.z));
                    continue;
                }
                if (pr.moundH > 2f)
                {
                    var m = math.lerp(pr.a, pr.b, pr.moundT); m.y = TunnelNetwork.FloorAt(pr, pr.moundT);
                    if (math.distance(m.xz, p.xz) < LookAhead + pr.moundR * 0.5f && Ahead(p, fwd, m) && math.abs(m.y - p.y) < 8f) Add("breakdown", m);
                }
                if (pr.drop > 0f)
                {
                    var s = math.lerp(pr.a, pr.b, pr.dropT); s.y = TunnelNetwork.FloorAt(pr, pr.dropT);
                    if (math.distance(s.xz, p.xz) < LookAhead && Ahead(p, fwd, s) && math.abs(s.y - p.y) < 8f) Add("lavafall", s);
                }
                if (pr.deadEnd != 0)
                {
                    var dir = math.normalize(ab);
                    if (axis < pr.hw + 2f && t > 0.15f && t < 0.95f && math.abs(floorHere - p.y) < 5f && math.dot(dir, fwd) > 0.3f) Add("deadend", pr.b);
                }
            }
            if (!string.IsNullOrEmpty(game.EdgeWarning)) Add("shaft", p);
            else if (net.CollapseNear(p, LookAhead, out var top, out _, out _) && Ahead(p, fwd, top, 0.6f)) Add("shaft", top);
            list.Sort((x, y) => x.dist.CompareTo(y.dist));
            return list;
        }

        /// <summary>The ID(s) a feature type says, with replies. Returns false if the type has no lines.</summary>
        void SayFeature(string type)
        {
            bool first = !lastType.ContainsKey(type);
            switch (type)
            {
                case "junction":
                {
                    bool cmdr = junctionN++ % 2 == 0;
                    Radio.Say(cmdr ? "t_junction" : "t_junction_2");
                    if (cmdr && cmdrReports++ % 3 == 0) Radio.FromEarth("t_junction_houston");
                    break;
                }
                case "breakdown": Radio.Say("t_breakdown"); Radio.Say("t_breakdown_cmdr", 0.3f); break;
                case "collapse_ramp": Radio.Say("t_collapse_ramp"); Radio.Say("t_collapse_spec", 0.3f); cmdrReports++; break;
                case "lavafall": Radio.Say("t_lavafall"); break;
                case "chamber": Radio.Say("t_chamber"); break;
                case "deadend": Radio.Say("t_deadend"); cmdrReports++; break;
                case "shaft":
                    Radio.Say("t_shaft");
                    if (first || cmdrReports % 3 == 0) Radio.FromEarth("t_shaft_houston");
                    cmdrReports++;
                    break;
            }
        }

        void Features(float3 p, float2 fwd)
        {
            var list = FeaturesAhead(p, fwd);
            // beat 3 C: the first ramp down a level (a story beat, not a paced call-out)
            foreach (var ft in list)
                if (ft.type == "levelramp" && !Radio.Busy && Once("b3_ramp"))
                {
                    Radio.Say("b3_ramp"); Radio.Say("b3_breakdown", 0.3f);
                    lastFeature = MissionTime; FeatureLog.Add($"{MissionTime:0.0} s levelramp");
                    return;
                }
            if (Radio.Busy) return;   // never stale: only call out what is in front of us now
            foreach (var ft in list)
            {
                if (ft.type == "levelramp") continue;
                bool first = !lastType.TryGetValue(ft.type, out float lt);
                float since = MissionTime - lastFeature;
                if (first ? since < FirstFeatureGap : (since < FeatureGap || MissionTime - lt < SameTypeGap)) continue;
                SayFeature(ft.type);
                lastType[ft.type] = MissionTime; lastFeature = MissionTime;
                FeatureLog.Add($"{MissionTime:0.0} s {ft.type}");
                Once("feature_" + ft.type);
                return;
            }
        }

        // ------------------------------------------------------------------ deposits (trigger D)

        static readonly Vector2[] RayPattern = { new(0, 0), new(-0.45f, 0.1f), new(0.45f, 0.1f), new(0, -0.35f) };

        void Deposits(BuggyController b)
        {
            var d = b.GetComponent<BuggyDriver>();
            if (d == null || d.headlights == null) return;
            float mouthY = game.Cave.Field.mouthFloorY;
            foreach (var lamp in d.headlights)
            {
                if (lamp == null || !lamp.enabled) continue;
                var t = lamp.transform;
                float tanH = Mathf.Tan(lamp.spotAngle * 0.5f * Mathf.Deg2Rad);
                foreach (var o in RayPattern)
                {
                    var dir = (t.forward + t.right * o.x * tanH + t.up * o.y * tanH).normalized;
                    if (!Physics.Raycast(t.position, dir, out var hit, DepositRange, ~(1 << 2), QueryTriggerInteraction.Ignore)) continue;
                    if (hit.collider.attachedRigidbody != null) continue;
                    var fp = (float3)WorldOrigin.WorldToFrame(hit.point);
                    var k = RockDeposits.At(fp, (float3)hit.normal, mouthY, out float s);
                    if (k == RockDeposits.Kind.Dust) continue;
                    if (k == RockDeposits.Kind.Basalt) { if (Fired("b3_basalt") && !seen.Contains(k)) Sighting(k, (double3)fp); continue; }
                    if (s < 0.35f) continue;
                    Sighting(k, (double3)fp);
                    return;
                }
            }
        }

        /// <summary>A deposit type seen for the first time in the headlights: specialist line, commander reply, Houston (first only), map tag.</summary>
        public bool Sighting(RockDeposits.Kind k, double3 at)
        {
            if (seen.Contains(k) || Radio.Busy) return false;
            string id = k switch
            {
                RockDeposits.Kind.Glaze => "d_glaze",
                RockDeposits.Kind.OrangeGlass => "d_orange",
                RockDeposits.Kind.GreenGlass => "d_green",
                RockDeposits.Kind.Crust => "d_crust",
                RockDeposits.Kind.Basalt => "d_glints",
                _ => null,
            };
            if (id == null) return false;
            seen.Add(k);
            Radio.Say(id);
            Radio.Say(tagN++ % 2 == 0 ? "d_tag_1" : "d_tag_2", 0.3f);
            if (Once("d_houston")) Radio.FromEarth("d_houston", 1.2f, 4f);
            Tags.Add((at, k));
            Once(id);
            return true;
        }

        // ------------------------------------------------------------------ beat 4 + drill

        void Beat4(float3 p, float dt)
        {
            if (!game.ScoutFrame.HasValue) return;
            var sp = (float3)game.ScoutFrame.Value;
            if (math.distance(sp, p) < VisualRange && Once("b4_visual")) { Radio.Say("b4_visual"); Radio.Say("b4_radar", 0.3f); }
            DrillReady = Fired("b4_visual") && game.SealFrame.HasValue && math.distance((float3)game.SealFrame.Value, p) < DrillRange;
            if (!DrillReady) { DrillProgress = math.max(0f, DrillProgress - dt * 0.5f); return; }
            if (drillKey) DrillProgress += dt / DrillSeconds;
            else DrillProgress = math.max(0f, DrillProgress - dt * 0.5f);
            if (DrillProgress >= 1f) { DrillProgress = 1f; EnterVent(); }
        }

        ParticleSystem burst;
        Light probeLight;

        void EnterVent()
        {
            phase = Phase.Venting; PhaseTime = 0;
            TimeAtDiscovery = MissionTime; BatteryAtDiscovery = game.Buggy.stateOfCharge;
            if (game.SealObject != null)
            {
                var bore = game.SealObject.transform.Find("BoreHole");
                if (bore != null) bore.gameObject.SetActive(true);
                burst = MissionBuilder.VapourBurst(game.SealObject.transform, MissionBuilder.BoreLocal - new Vector3(0, 0, 0.15f), new Vector3(0, 0.25f, -1f));
                burst.Play();
            }
            CameraShake.Add(0.015f, 0.8f);
            Radio.Say("b5_gas", 0.4f);
        }

        void EnterProbe()
        {
            phase = Phase.Probe; PhaseTime = 0;
            HideHud = true;
            // S11: the probe camera has its own exposure: snap to it (no slow adaptation from the dark-adapted tube view,
            // which read as a white-out), and no visor lift on it (the visor is the crew's, not the probe's)
            if (game.Exposure != null) { game.Exposure.ForcedLuminance = ProbeLuminance; game.Exposure.NoFill = true; game.Exposure.SnapToTarget(); }
            game.Visor?.ResetLift();
            if (game.ChamberObject != null)
            {
                game.ChamberObject.SetActive(true);
                var lg = new GameObject("ProbeLamp");
                probeLight = lg.AddComponent<Light>();
                probeLight.type = LightType.Point; probeLight.range = 14f; probeLight.color = new Color(1f, 0.96f, 0.9f); probeLight.shadows = LightShadows.Soft;
                lg.AddComponent<PhysicalLight>().physicalIntensity = 220f;
            }
            Radio.Say("b5_probe");
            bool granite = Mission.TargetLevel >= 3;
            Radio.Say(granite ? "b5_granite" : "b5_frost_only", 2.5f);
            Radio.FromEarth("b5_houston", 1.2f, 3f);
        }

        void EnterDebrief()
        {
            phase = Phase.Debrief; PhaseTime = 0;
            HideHud = true; DebriefOpen = true;
            if (game.Exposure != null) { game.Exposure.ForcedLuminance = 0f; game.Exposure.NoFill = false; }
            if (probeLight != null) Util.Kill(probeLight.gameObject);
            if (game.ChamberObject != null) game.ChamberObject.SetActive(false);
        }

        /// <summary>The probe camera path: through the bore hole, a slow push towards the far wall (and the boulder).</summary>
        public bool ProbePose(float t, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = Quaternion.identity;
            if (game.ChamberObject == null) return false;
            var ch = game.ChamberObject.transform;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / ProbeMinSeconds));
            var local = Vector3.Lerp(new Vector3(-1.2f, 1.9f, -4.2f), new Vector3(-0.6f, 1.5f, -1.6f), k);
            var look = new Vector3(0.4f + Mathf.Sin(t * 0.3f) * 0.8f, 0.9f, 1.2f);
            pos = ch.TransformPoint(local);
            rot = Quaternion.LookRotation(ch.TransformPoint(look) - pos, ch.up);
            return true;
        }

        void LateUpdate()
        {
            if (phase != Phase.Probe || game == null || game.cam == null) return;
            if (!ProbePose(PhaseTime, out var pos, out var rot)) return;
            game.cam.transform.SetPositionAndRotation(pos, rot);
            if (probeLight != null) probeLight.transform.position = pos + rot * new Vector3(0.3f, 0.2f, 0.2f);
        }

        // ------------------------------------------------------------------ HUD / guidance

        /// <summary>
        /// Where the GIMLI-2 HUD marker points: on the route ~40 m ahead of the closest point (underground), else the
        /// pit mouth. Label "GIMLI-2 - level N, x m" (distance along the route).
        /// </summary>
        /// <summary>GIMLI-2's tube level: the route's last flow in the flow layout, else the tier's target level.</summary>
        public int GimliLevel => game.Cave.Network.IsFlow && game.Route != null && game.Route.Valid && game.Route.pointLevels.Count > 0
            ? game.Route.pointLevels[game.Route.pointLevels.Count - 1] : Mission.TargetLevel;

        public bool GuideTarget(Vector3 from, out Vector3 world, out string label)
        {
            world = default; label = null;
            var r = game.Route;
            int L = GimliLevel;
            if (r == null || !r.Valid)
            {
                if (!game.ScoutWorld.HasValue) return false;
                world = game.ScoutWorld.Value;
                label = $"GIMLI-2  {Dist(Vector3.Distance(from, world))}";
                return true;
            }
            var p = (float3)WorldOrigin.WorldToFrame(from);
            float below = game.Height.DataHeight(p.x, p.z) - p.y;
            if (below < 15f)
            {
                var mouth = r.points[0];
                world = WorldOrigin.FrameToWorld((double3)mouth);
                label = $"GIMLI-2  {Dist(math.distance(p, mouth) + r.Length)}";
                guideSeg = 0;
                return true;
            }
            float rem = r.Remaining(p, ref guideSeg, out int seg, out _);
            GuideRemaining = rem;
            // look-ahead along the polyline
            float ahead = 40f; var q = r.points[math.min(seg + 1, r.points.Count - 1)];
            {
                var a = r.points[seg]; var bpt = r.points[math.min(seg + 1, r.points.Count - 1)];
                var ab = bpt - a; float t = math.saturate(math.dot(p - a, ab) / math.max(1e-4f, math.lengthsq(ab)));
                var cur = a + ab * t;
                int k = seg + 1; var prev = cur;
                q = cur;
                while (k < r.points.Count)
                {
                    float l = math.distance(prev, r.points[k]);
                    if (l >= ahead) { q = prev + (r.points[k] - prev) * (ahead / l); break; }
                    ahead -= l; prev = r.points[k]; q = prev; k++;
                }
            }
            if (rem < 60f && game.ScoutFrame.HasValue) q = (float3)game.ScoutFrame.Value;
            world = WorldOrigin.FrameToWorld((double3)(q + new float3(0, 1.5f, 0)));
            label = $"GIMLI-2  {Dist(rem)}";
            return true;
        }

        /// <summary>
        /// Round 12 (P5): the CURRENT STEP line, from the beat state (key names from <see cref="Controls"/>).
        /// </summary>
        public string CurrentStep
        {
            get
            {
                if (game == null) return null;
                string use = Controls.Name(Controls.Btn.Use);
                switch (phase)
                {
                    case Phase.Idle: return null;
                    case Phase.Venting: return "Gas venting - read the spectrometer";
                    case Phase.Probe: return "Probe camera beyond the seal";
                    case Phase.Debrief: return "Mission debrief";
                }
                var b = game.Buggy; var crane = game.Crane;
                var f = WorldOrigin.WorldToFrame(b.transform.position);
                float below = game.Height.DataHeight(f.x, f.z) - (float)f.y;
                if (DrillReady) return $"Hold {use} to drill the seal";
                if (crane != null)
                {
                    bool onDeck = crane.RoverOnPlatform;
                    if (crane.Moving) return "Wait for the crane to stop";
                    if (crane.state == Base.CraneController.State.AtBottom && crane.GatesHeld && onDeck) return $"Press {use} to open the gates";
                    if (below < 15f)
                    {
                        if (onDeck && crane.state == Base.CraneController.State.Parked) return $"Stop in the middle of the deck, then {use} to lower";
                        var to = crane.Platform.position - b.transform.position;
                        float brg = (Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg + 360f) % 360f;
                        return to.magnitude < 25f ? "Drive onto the crane platform" : $"Drive to the crane (bearing {brg:000}, {Dist(to.magnitude)})";
                    }
                }
                if (Fired("b4_visual")) return "Drive up to the rubble seal";
                // round 13 (T4): GIMLI-2's way goes down a shaft ahead (its rope hangs from the rig's trolley)
                var rt = game.Route;
                if (rt != null && rt.ShaftCount > 0)
                    for (int i = 0; i < rt.ShaftCount; i++)
                    {
                        int k = rt.shaftAt[i];
                        if (guideSeg > k) continue;
                        var rim = rt.points[k]; var fp = (float3)f;
                        float d = math.distance(rim.xz, fp.xz);
                        if (d < 200f && math.abs(rim.y - fp.y) < 10f) return $"GIMLI-2 went down the shaft ahead - stop under the trolley ({Dist(d)})";
                        break;
                    }
                return GuideRemaining > 0 ? $"Drive to GIMLI-2 - follow the marker ({Dist(GuideRemaining)})" : "Drive to GIMLI-2 - follow the marker";
            }
        }

        /// <summary>Route progress hint (the segment the rover was last matched to): no jumps to a nearby other part of the route.</summary>
        int guideSeg;
        /// <summary>Distance left along the route at the last <see cref="GuideTarget"/> call (tests).</summary>
        public float GuideRemaining { get; private set; } = -1f;

        /// <summary>"882 m", "1.25 km" (no "level N": the flow layout has no levels, and "level 0, 882 m" read as a decimal).</summary>
        public static string Dist(float m) => m < 1000f ? $"{m:0} m" : $"{m / 1000f:0.00} km";

        GUIStyle big, body, small, button, prompt, barLabel;
        void Styles()
        {
            if (big != null) return;
            big = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true, richText = true };
            big.normal.textColor = new Color(1f, 0.93f, 0.8f);
            body = new GUIStyle(GUI.skin.label) { fontSize = 17, wordWrap = true, richText = true };
            body.normal.textColor = new Color(0.92f, 0.94f, 1f);
            small = new GUIStyle(body) { fontSize = 14 };
            button = new GUIStyle(GUI.skin.button) { fontSize = 20, fontStyle = FontStyle.Bold };
            prompt = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = true };
            prompt.normal.textColor = new Color(1f, 0.85f, 0.45f);
            barLabel = new GUIStyle(body) { fontSize = 16, alignment = TextAnchor.MiddleLeft };
        }

        static void Panel(Rect r, float a = 0.6f)
        {
            var prev = GUI.color; GUI.color = new Color(0, 0, 0, a); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = prev;
        }

        /// <summary>Spectrometer readings (fraction of the vented gas; O2 = none).</summary>
        public static readonly (string gas, float frac, string note)[] Spectrum =
        {
            ("H2O", 0.58f, "water vapour"), ("CO2", 0.24f, "carbon dioxide"), ("N2", 0.15f, "nitrogen"), ("Ar", 0.03f, "argon (trace)"), ("O2", 0f, "NONE"),
        };

        void OnGUI()
        {
            if (game == null || !game.Initialised || phase == Phase.Idle) return;
            Styles();
            float W = Screen.width, H = Screen.height;
            if (phase == Phase.Drive && DrillReady)
            {
                Panel(new Rect(W / 2 - 190, H - 118, 380, 40));
                GUI.Label(new Rect(W / 2 - 190, H - 118, 380, 40), DrillProgress > 0 ? $"Drilling the seal... {DrillProgress * 100:0}%" : $"Hold {Controls.Name(Controls.Btn.Use)} to drill the seal", prompt);
                if (DrillProgress > 0) Ring(new Vector2(W / 2, H / 2), 46f, DrillProgress);
            }
            if (phase == Phase.Venting || phase == Phase.Probe) Spectrometer(W, H);
            if (phase == Phase.Probe)
            {
                float fade = Mathf.Clamp01(1f - PhaseTime / 1.2f);
                if (fade > 0) Panel(new Rect(0, 0, W, H), fade);
                GUI.Label(new Rect(20, 16, 700, 30), $"<b>PROBE CAMERA - beyond the seal</b>   (pushed in through the bore hole)   T+{PhaseTime:00.0} s", body);
            }
            if (phase == Phase.Debrief) Debrief(W, H);
        }

        static void Ring(Vector2 c, float r, float frac)
        {
            int n = 48, lit = Mathf.RoundToInt(n * frac);
            var prev = GUI.color;
            for (int i = 0; i < n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                var p = c + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * r;
                GUI.color = i < lit ? new Color(1f, 0.8f, 0.3f, 0.95f) : new Color(1f, 1f, 1f, 0.2f);
                GUI.DrawTexture(new Rect(p.x - 3, p.y - 3, 6, 6), Texture2D.whiteTexture);
            }
            GUI.color = prev;
        }

        void Spectrometer(float W, float H)
        {
            float t = phase == Phase.Venting ? PhaseTime : VentSeconds + PhaseTime;
            var r = new Rect(W - 380, H * 0.22f, 360, 250);
            Panel(r, 0.7f);
            GUI.Label(new Rect(r.x + 14, r.y + 8, 340, 26), "<b>MASS SPECTROMETER</b> - bore-hole gas", body);
            float y = r.y + 44;
            for (int i = 0; i < Spectrum.Length; i++)
            {
                var (gas, frac, note) = Spectrum[i];
                float grow = Mathf.Clamp01((t - 0.6f - i * 0.4f) / 2.5f);
                bool o2 = gas == "O2";
                GUI.Label(new Rect(r.x + 14, y, 50, 26), $"<b>{gas}</b>", barLabel);
                var prev = GUI.color;
                GUI.color = new Color(1, 1, 1, 0.12f); GUI.DrawTexture(new Rect(r.x + 70, y + 6, 180, 14), Texture2D.whiteTexture);
                GUI.color = o2 ? new Color(1f, 0.35f, 0.25f) : new Color(0.45f, 0.85f, 1f);
                GUI.DrawTexture(new Rect(r.x + 70, y + 6, 180 * frac * grow / 0.6f, 14), Texture2D.whiteTexture);
                GUI.color = prev;
                string txt = o2 ? (grow > 0.5f ? "<color=#ff6a50><b>NONE</b></color>" : "...") : (grow > 0.05f ? $"{frac * 100f * grow:0}%" : "");
                GUI.Label(new Rect(r.x + 260, y, 100, 26), txt, barLabel);
                y += 38;
            }
        }

        void Debrief(float W, float H)
        {
            var r = new Rect(W / 2 - 460, H / 2 - 330, 920, 660);
            Panel(new Rect(0, 0, W, H), 0.55f);
            Panel(r, 0.8f);
            GUI.Label(new Rect(r.x, r.y + 14, r.width, 44), "MISSION DEBRIEF // GIMLI-2 EXPEDITION", big);
            var b = game.Buggy;
            float time = TimeAtDiscovery >= 0 ? TimeAtDiscovery : MissionTime;
            float batt = BatteryAtDiscovery >= 0 ? BatteryAtDiscovery : b.stateOfCharge;
            int kitsUsed = game.Winch != null ? RoverWinch.RigKitsPerMission - game.Winch.RigKits : 0;
            var sb = new StringBuilder();
            sb.AppendLine($"Mission seed  <b>{Mission.CurrentCode}</b>      Landing zone  <b>{Mission.Info.zone}</b> ({Mission.Info.rank})");
            var fnet = game.Cave.Network.Flow;
            if (fnet != null)
                sb.AppendLine($"Lava flows descended  <b>{math.max(0, MaxLevel)}</b>  (deepest tube floor about {fnet.FlowDepth(math.max(0, MaxLevel)):0} m below the rim)");
            else
                sb.AppendLine($"Levels descended  <b>{math.max(0, MaxLevel)}</b>  (GIMLI-2 on tube level {Mission.TargetLevel}, about {75 * math.max(0, MaxLevel) + 40} m below the rim)");
            sb.AppendLine($"Time  <b>{(int)(time / 60)}:{(int)(time % 60):00}</b>      Battery remaining  <b>{batt * 100:0}%</b>");
            sb.AppendLine($"Deposits tagged  <b>{Tags.Count}</b>      Shaft rigs installed  <b>{kitsUsed}</b>");
            GUI.Label(new Rect(r.x + 40, r.y + 72, r.width - 80, 120), sb.ToString(), body);
            GUI.Label(new Rect(r.x + 40, r.y + 190, r.width - 80, 24), "<b>REAL</b>", body);
            GUI.Label(new Rect(r.x + 40, r.y + 214, r.width - 80, 250),
                "- NASA's GIMLI mission: selected in 2026 to probe the Marius Hills Pit for a lava tube, a possible Moon-base shelter\n" +
                "- LRO data of the Marius Hills Pit (the terrain you drove)\n" +
                "- Lunar lava tubes: strong evidence from pits, radar and gravity; a cave at Mare Tranquillitatis radar-confirmed in 2024\n" +
                "- 1/6 g; pits about +17 C (sunlit walls), deep rock about -20 C (Apollo heat-flow probes)\n" +
                "- Earth rock on the Moon: Apollo 14 sample 14321 holds a ~4-billion-year-old fragment that formed on Earth\n" +
                "- The young Earth's air had no free oxygen", small);
            GUI.Label(new Rect(r.x + 40, r.y + 400, r.width - 80, 24), "<b>SPECULATIVE \"WHAT IF?\"</b>", body);
            GUI.Label(new Rect(r.x + 40, r.y + 424, r.width - 80, 110),
                "GIMLI having already flown and found the tubes; GIMLI-2; a sealed, airtight chamber keeping volcanic gas and ice for " +
                "billions of years" + (Mission.TargetLevel >= 3 ? "; and a whole Earth boulder inside it." : "."), small);
            if (GUI.Button(new Rect(r.x + r.width / 2 - 430, r.y + r.height - 80, 290, 54), Controls.PadActive ? "Play again (A)" : "Play again (same seed)", button)) MoonBaseAlpha.UI.PauseMenu.Restart();
            if (GUI.Button(new Rect(r.x + r.width / 2 - 125, r.y + r.height - 80, 250, 54), Controls.PadActive ? "New mission (Y)" : "New mission", button)) MoonBaseAlpha.UI.PauseMenu.NewMission();
            MoonBaseAlpha.UI.QuitConfirm.ExitButton(new Rect(r.x + r.width / 2 + 140, r.y + r.height - 80, 290, 54), button);
        }
    }
}
