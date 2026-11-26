using Unity.Mathematics;
using UnityEngine;
using MoonBaseAlpha.Base;
using MoonBaseAlpha.Buggy;
using MoonBaseAlpha.Cave;
using MoonBaseAlpha.Crew;
using MoonBaseAlpha.Rendering;
using MoonBaseAlpha.Sky;
using MoonBaseAlpha.Terrain;

namespace MoonBaseAlpha
{
    /// <summary>
    /// Brings the world up: terrain data -> pit + tunnel network -> terrain chunks -> base (landers, reactor,
    /// crane) -> rover -> sky/exposure. Keys: Backspace/F5 restart, 1 lander, 2 crane, 3 tube, 4 scout rover,
    /// 5 / Shift+5 next / previous special site, T mission time-scale.
    /// </summary>
    [DefaultExecutionOrder(-600)]
    public class GameBootstrap : MonoBehaviour
    {
        [Header("Scene references")]
        public Camera cam;
        public Light sun;
        public Material terrainMaterial, caveMaterial, sunMaterial, earthMaterial, starMaterial;
        public Material tunnelMaterial;

        [Header("Base layout (frame metres from the pit centre)")]
        public Vector2 landerXZ = new Vector2(500f, -230f);    // ~550 m ESE of the pit (exhibit pacing: 2-3 min drive)
        public Vector2 cargoXZ = new Vector2(260f, -260f);     // the uncrewed crane-delivery lander
        public Vector2 reactorXZ = new Vector2(820f, 60f);     // fission power unit ~0.4 km from the crew lander

        public TerrainStreamer Streamer { get; private set; }
        public CaveBuilder Cave { get; private set; }
        public CaveStreamer Tunnels { get; private set; }
        public CaveStreamer FarTunnels { get; private set; }
        public NavTracker Nav { get; private set; }
        public BuggyController Buggy { get; private set; }
        public SkyController Sky { get; private set; }
        public ExposureController Exposure { get; private set; }
        public PlayerControl Player { get; private set; }
        public CraneController Crane { get; private set; }
        public UnderRockShell Shell { get; private set; }
        public QualityPresets Quality { get; private set; }
        public VisorAssist Visor { get; private set; }
        public RoverWinch Winch { get; private set; }
        public MoonBaseAlpha.UI.TunnelMap Map { get; private set; }
        public HeightField Height => Streamer.Height;
        public bool Initialised { get; private set; }
        /// <summary>The landing zone the world was built for (the start screen rebuilds on a change).</summary>
        public Mission.Tier BuiltTier { get; private set; }

        /// <summary>Mission start: touchdown call, Houston gives the distance (tiered line), the specialist jokes.</summary>
        public void BeginMission()
        {
            Radio.Say("b1_touchdown", 1f);
            Radio.FromEarth(Mission.Tiered("b1_distance"), 1.2f, 1f);
            Radio.Say("b1_smell", 7f);
            Director?.Begin();
        }

        /// <summary>The mission story: triggers, drill, discovery, debrief.</summary>
        public MissionDirector Director { get; private set; }
        /// <summary>The live tyre tracks of the player's rover.</summary>
        public TyreTracks Tracks { get; private set; }
        /// <summary>The GIMLI-2 route from the pit mouth to the seal (invalid if none was found: old scout placement).</summary>
        public GoldenRoute Route { get; private set; }
        /// <summary>Relay beacons along the route (frame floor points).</summary>
        public readonly System.Collections.Generic.List<double3> Beacons = new();
        /// <summary>GIMLI-1's rover (parked on the rim) and lander, the cargo lander (frame).</summary>
        public double3? GimliRoverFrame { get; private set; }
        public double3? GimliLanderFrame { get; private set; }
        public double3? CargoFrame { get; private set; }
        public Vector3? GimliWorld => GimliRoverFrame.HasValue ? WorldOrigin.FrameToWorld(GimliRoverFrame.Value) : null;
        /// <summary>The seal (rubble wall face centre, floor) and its set pieces.</summary>
        public double3? SealFrame { get; private set; }
        public GameObject SealObject { get; private set; }
        public GameObject ChamberObject { get; private set; }
        public double3 ChamberFrame { get; private set; }
        public GameObject ScoutObject { get; private set; }
        /// <summary>Frame point where the rover starts (the landing zone of the tier).</summary>
        public double3 StartFrame => startFrame;
        double3 gimliApproach; float gimliHeading;

        public double3? LanderFrame { get; private set; }
        public double3? ScoutFrame { get; private set; }
        public Vector3? ScoutWorld => ScoutFrame.HasValue ? WorldOrigin.FrameToWorld(ScoutFrame.Value) : null;
        public Vector3? CraneWorld => Crane != null ? Crane.Platform.position : null;
        double3 startFrame; float startHeading;
        double3 scoutApproach; float scoutHeading; float2 scoutDir;

        /// <summary>Special sites in the network (key 5 / Shift+5).</summary>
        public SiteCatalog Sites { get; private set; }
        public int SiteIndex { get; private set; } = -1;
        public string SiteLabel { get; private set; }
        /// <summary>"EDGE AHEAD - 75 m drop" while the player is within 15 m of a collapse edge and facing it (no barrier).</summary>
        public string EdgeWarning { get; private set; }

        /// <summary>
        /// EDGE AHEAD (within <see cref="BuggyDriver.StopDistance"/> = 15 m of a drop the player is heading into):
        /// collapse shafts and the pit rim. Also used by tests.
        /// </summary>
        public string EdgeWarningAt(Vector3 world, Vector3 forward)
        {
            float d = EdgeDistanceAhead(world, forward, BuggyDriver.StopDistance, out float drop);
            return d < 0f ? null : $"EDGE AHEAD - {drop:0} m drop";
        }

        public float EdgeDistanceAhead(Vector3 world, Vector3 forward, float range) => EdgeDistanceAhead(world, forward, range, out _);

        /// <summary>
        /// Distance (m) along the heading to a drop the rover's footprint would run into: a collapse shaft (its circle
        /// widened by half the rover's width, so a lane beside it is free), or the pit's inner shaft wall from the
        /// funnel. -1 = none within range. Lava falls are capped at 4 m (rubble slopes) and talus ramps are no edge.
        /// No assist on / beside the crane platform (it hangs over the pit).
        /// </summary>
        public float EdgeDistanceAhead(Vector3 world, Vector3 forward, float range, out float drop)
        {
            drop = 0f;
            if (Cave == null || Cave.Network == null) return -1f;
            if (Crane != null && Vector3.Distance(world, Crane.Platform.position) < 14f) return -1f;
            var f = (float3)WorldOrigin.WorldToFrame(world);
            var fw = math.normalizesafe(new float2(forward.x, forward.z));
            if (math.lengthsq(fw) < 0.5f) return -1f;
            const float halfWidth = 1.5f;
            float best = -1f;
            if (Cave.Network.CollapseNear(f, range + 25f, out var top, out float radius, out float sDrop))
            {
                var rel = top.xz - f.xz; float along = math.dot(rel, fw);
                float perp = math.abs(rel.x * fw.y - rel.y * fw.x), R = radius + halfWidth;
                if (math.length(rel) < R) { best = 0f; drop = sDrop; }
                else if (along > 0f && perp < R)
                {
                    float d = along - math.sqrt(R * R - perp * perp);
                    if (d <= range) { best = math.max(0f, d); drop = sDrop; }
                }
            }
            var hf = Cave.Field.hf;
            if (hf.DataHeight(f.x, f.z) - f.y < 15f)
            {
                // on the surface: march the heading to the inner shaft ellipse (+ half the rover's width)
                float A = hf.innerA + halfWidth, B = hf.innerB + halfWidth;
                for (float s = 0f; s <= range; s += 1f)
                {
                    var q = f.xz + fw * s - hf.pitCentre;
                    float u = math.dot(q, hf.pitAxis), v = q.x * hf.pitAxis.y - q.y * hf.pitAxis.x;
                    if (u * u / (A * A) + v * v / (B * B) < 1f)
                    {
                        if (best < 0f || s < best) { best = s; drop = Cave.Field.rim - Cave.Field.pitFloor; }
                        break;
                    }
                }
            }
            return best;
        }
        public float SiteLabelTime { get; private set; } = -100f;
        /// <summary>Round 12 (S8): the Cadet crane ride as a time-lapse (2.8 min of physics in under 1 min).</summary>
        public const float CadetCraneTimeScale = 3f;
        public bool CraneTimeLapse { get; private set; }
        /// <summary>Round 12 (P6): the 2 crew's suit consumables (status block on the helmet HUD).</summary>
        public readonly Crew.LifeSupport Life = new();
        public Transform BaseCam { get; private set; }

        bool autoLights = true;

        void Awake() => Initialise();

        public void Initialise()
        {
            if (Initialised) return;
            var initClock = System.Diagnostics.Stopwatch.StartNew();
            LunarPhysics.Configure();
            WorldOrigin.ResetOrigin();
            MissionDirector.ResetStatics();

            var meta = TerrainMeta.Load();
            TerrainFrame.R0 = meta.site.R0;
            TerrainFrame.SiteLatDeg = meta.site.lat;
            TerrainFrame.SiteLonDeg = meta.site.lon;
            var hf = HeightField.Load(meta);
            hf.detailSeed = WorldSeed.For("surface-detail");

            var caveGo = new GameObject("Cave");
            Cave = caveGo.AddComponent<CaveBuilder>();
            Cave.material = caveMaterial;
            Cave.tunnelMaterial = tunnelMaterial;
            Cave.Plan(ref hf);

            // crane site + levelled load pad (must exist before any terrain or pit mesh is built)
            ChooseCraneSite(ref hf);
            Cave.SetHeightField(hf);

            var terrainGo = new GameObject("Terrain");
            Streamer = terrainGo.AddComponent<TerrainStreamer>();
            Streamer.material = terrainMaterial;
            Streamer.Initialise(hf);

            Cave.Build();
            Tunnels = caveGo.AddComponent<CaveStreamer>();
            Tunnels.material = tunnelMaterial != null ? tunnelMaterial : caveMaterial;
            Tunnels.Initialise(Cave.Network, Cave);
            // render-only coarse ring from the stream radius to 2x (3 m voxels): the tube recedes into the dark
            var farGo = new GameObject("FarTunnels");
            farGo.transform.SetParent(caveGo.transform, false);
            FarTunnels = farGo.AddComponent<CaveStreamer>();
            FarTunnels.renderOnly = true; FarTunnels.voxel = 3f; FarTunnels.maxJobsPerFrame = 3;
            FarTunnels.radius = Tunnels.radius * 2f; FarTunnels.innerRadius = Tunnels.radius;
            FarTunnels.material = new Material(Tunnels.material) { name = "CaveRockFar" };
            FarTunnels.material.SetFloat("_FarRing", 1f);
            FarTunnels.Initialise(Cave.Network, Cave);
            Tunnels.far = FarTunnels;
            Shader.SetGlobalFloat("_MBA_MouthFloorY", Cave.Field.mouthFloorY);

            var cave = Cave;
            PitBounce.Configure(cave.Field.hf.pitCentre, cave.Field.hf.innerA, cave.Field.hf.innerB, cave.Field.rim, cave.Field.pitFloor, cave.Field.hf.pitAxis);
            GroundMaterial.Provider = world =>
            {
                var f = WorldOrigin.WorldToFrame(world);
                var field = cave.Field;
                float depth = field.hf.DataHeight(f.x, f.z) - (float)f.y;
                if (depth < 15f) return Soil.Regolith;
                float r = math.length((float2)f.xz - field.hf.pitCentre);
                if (r < field.hf.innerA * 1.6f) return Soil.Talus;                   // pit floor / mouth: fallen regolith + blocks
                return Soil.Basalt;                                                   // tube floors: bare rock
            };

            // rover recovery: nearest level, boulder-free footprint within ~10 m (prefer the way we came)
            var hfr = hf;
            BuggyController.FindRecoverySpot = (world, prefer) =>
            {
                var f = WorldOrigin.WorldToFrame(world);
                if (hfr.DataHeight(f.x, f.z) - (float)f.y > 8f)
                {
                    // underground (pit floor / tubes): probe the actual cave floor with rays
                    var upv = -TerrainFrame.DownAt(world);
                    Vector3? bestW = null; float bestS = float.MaxValue;
                    for (int k = 0; k < 60; k++)
                    {
                        float a = k * 2.39996f, r = 1.5f + 0.8f * math.sqrt(k);
                        var c = world + Quaternion.AngleAxis(a * Mathf.Rad2Deg, upv) * Vector3.ProjectOnPlane(Vector3.forward, upv).normalized * r;
                        float hmin = float.MaxValue, hmax = float.MinValue; bool ok = true; Vector3 centre = c;
                        foreach (var o in new[] { new Vector2(0, 0), new Vector2(1.2f, 1.6f), new Vector2(-1.2f, 1.6f), new Vector2(1.2f, -1.6f), new Vector2(-1.2f, -1.6f) })
                        {
                            var q = c + new Vector3(o.x, 0, o.y);
                            if (!Physics.Raycast(q + upv * 3f, -upv, out var hit, 8f, ~(1 << 2), QueryTriggerInteraction.Ignore)) { ok = false; break; }
                            float h = Vector3.Dot(hit.point - world, upv);
                            hmin = Mathf.Min(hmin, h); hmax = Mathf.Max(hmax, h);
                            if (o == Vector2.zero) centre = hit.point;
                        }
                        if (!ok) continue;
                        float score = (hmax - hmin) * 4f + r * 0.1f - 0.5f * Vector3.Dot((c - world).normalized, prefer.normalized);
                        if (score < bestS && hmax - hmin < 0.6f) { bestS = score; bestW = centre; }
                    }
                    return bestW;
                }
                double3? best = null; float bestScore = float.MaxValue;
                for (int k = 0; k < 120; k++)
                {
                    float a = k * 2.39996f, r = 2f + 0.75f * math.sqrt(k);
                    if (r > 11f) break;
                    double x = f.x + r * math.cos(a), z = f.z + r * math.sin(a);
                    float s = SiteScore(hfr, new float2((float)x, (float)z), 2.6f);
                    float dirBonus = -2f * math.dot(math.normalize(new float2(math.cos(a), math.sin(a))), math.normalizesafe(new float2(prefer.x, prefer.z)));
                    float score = s + dirBonus + r * 0.15f;
                    if (score < bestScore) { bestScore = score; best = new double3(x, hfr.Height(x, z, 0f), z); }
                }
                return best.HasValue ? (Vector3?)WorldOrigin.FrameToWorld(best.Value) : null;
            };

            // base hardware
            var baseRoot = new GameObject("Base").transform;
            BuildBase(baseRoot, hf);

            // rover
            Buggy = BuggyBuilder.Build();
            var eye = BuggyBuilder.DriverEye(Buggy);
            Crane.SetRover(Buggy.transform);

            // origin + physics focus
            var originGo = new GameObject("WorldOrigin");
            var origin = originGo.AddComponent<WorldOrigin>();
            origin.target = Buggy.transform;
            var physicsGo = new GameObject("LunarPhysics");
            var lunarPhysics = physicsGo.AddComponent<LunarPhysics>();
            lunarPhysics.focus = Buggy.transform;
            if (SimClock.Instance == null) new GameObject("SimClock").AddComponent<SimClock>();
            Radio.Clear();
            if (GetComponent<Radio>() == null) gameObject.AddComponent<Radio>();
            if (GetComponent<MoonBaseAlpha.UI.PauseMenu>() == null) gameObject.AddComponent<MoonBaseAlpha.UI.PauseMenu>();
            if (GetComponent<MoonBaseAlpha.UI.QuitConfirm>() == null) gameObject.AddComponent<MoonBaseAlpha.UI.QuitConfirm>();
            var tuning = GetComponent<TuningController>() ?? gameObject.AddComponent<TuningController>();

            // camera
            if (cam == null) cam = Camera.main;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50000f;
            cam.fieldOfView = 75f;
            var dc = cam.GetComponent<DriverCamera>() ?? cam.gameObject.AddComponent<DriverCamera>();
            dc.seatEye = eye;
            Streamer.viewer = cam.transform;
            Streamer.physicsFocus = Buggy.transform;
            Tunnels.viewer = cam.transform;
            Tunnels.physicsFocus = Buggy.transform;
            FarTunnels.viewer = cam.transform;

            // sky + exposure
            if (sun.GetComponent<PhysicalLight>() == null) sun.gameObject.AddComponent<PhysicalLight>();
            Sky = cam.GetComponent<SkyController>() ?? cam.gameObject.AddComponent<SkyController>();
            Sky.cam = cam; Sky.sun = sun;
            Sky.sunMaterial = sunMaterial; Sky.earthMaterial = earthMaterial; Sky.starMaterial = starMaterial;
            Exposure = cam.GetComponent<ExposureController>() ?? cam.gameObject.AddComponent<ExposureController>();
            Exposure.cam = cam; Exposure.sky = Sky;
            GetComponent<TuningController>().exposure = Exposure;
            Exposure.darknessAt = world => cave.Darkness(WorldOrigin.WorldToFrame(world));
            Sky.skyVisibility = world => cave.SkyVisibility(WorldOrigin.WorldToFrame(world));
            Shell = cam.GetComponent<UnderRockShell>() ?? cam.gameObject.AddComponent<UnderRockShell>();
            Shell.cam = cam; Shell.sky = Sky;
            var tunnels = Tunnels;
            Shell.streamRadius = () => tunnels.DrawRadius;
            // S3: camera down in the pit (inside the funnel, > 4 m below the plain): black shell below the rim plane
            Shell.pitClipY = () =>
            {
                var cf = WorldOrigin.WorldToFrame(cam.transform.position);
                if (cave.Field.hf.PitRho(new float2((float)cf.x, (float)cf.z)) > 1.0f || cf.y > cave.Field.rim - 4f) return null;
                return (float)WorldOrigin.FrameToWorld(new double3(cf.x, cave.Field.rim - 2f, cf.z)).y;
            };
            Shell.Init();

            Visor = cam.GetComponent<VisorAssist>() ?? cam.gameObject.AddComponent<VisorAssist>();
            Visor.cam = cam; Visor.exposure = Exposure;
            Visor.active = () => (Player == null || Player.FirstPersonView) && !MissionDirector.HideHud;   // not on the probe camera / debrief

            Player = gameObject.GetComponent<PlayerControl>() ?? gameObject.AddComponent<PlayerControl>();
            Player.cam = cam; Player.rover = Buggy; Player.streamer = Streamer; Player.exposure = Exposure;
            Winch = Buggy.GetComponent<RoverWinch>() ?? Buggy.gameObject.AddComponent<RoverWinch>();
            Winch.rover = Buggy; Winch.network = Cave.Network;
            var driver = Buggy.GetComponent<BuggyDriver>();
            if (driver != null) driver.edgeAhead = (pos, fwd) => EdgeDistanceAhead(pos, fwd, 120f);
            if (Cave.Network.IsFlow)
            {
                // rope length: the deepest shaft + margin
                float deepest = 40f; foreach (var s in Cave.Network.Flow.Shafts) deepest = Mathf.Max(deepest, s.drop);
                Winch.RopeLength = deepest + 20f;
                RigRouteShafts();
                // the advance team's rigs where a shaft bottom has no driving way back up: span + trolley, no rope (T3)
                foreach (var s in Cave.Network.Flow.ShaftsNeedingLines()) Winch.PreRig(s.top, s.radius, s.drop, false);
            }
            Winch.canUse = () => Player != null && Player.State == PlayerControl.Mode.Driving;
            var screens = Buggy.GetComponent<CockpitScreens>();
            if (screens != null) { screens.nav = () => Nav; screens.winch = () => Winch; }
            Player.origin = origin; Player.physics = lunarPhysics; Player.tunnels = Tunnels;
            Player.Init();

            Player.crane = Crane; Player.baseCam = BaseCam;
            var heads = Buggy.GetComponent<CrewHeads>();
            if (heads != null)
            {
                var sky = Sky; var crane = Crane;
                heads.earthDir = () => sky.SkyShown ? sky.EarthDir : (Vector3?)null;
                heads.craneWorld = () => crane != null ? crane.Platform.position : (Vector3?)null;
                heads.scoutWorld = () => ScoutWorld;
            }

            Quality = gameObject.GetComponent<QualityPresets>() ?? gameObject.AddComponent<QualityPresets>();
            Quality.terrain = Streamer; Quality.tunnels = Tunnels; Quality.farTunnels = FarTunnels;

            Map = gameObject.GetComponent<MoonBaseAlpha.UI.TunnelMap>() ?? gameObject.AddComponent<MoonBaseAlpha.UI.TunnelMap>();
            Map.game = this;
            Nav = gameObject.GetComponent<NavTracker>() ?? gameObject.AddComponent<NavTracker>();
            Nav.network = Cave.Network;

            Sites = new SiteCatalog(Cave.Network, (float3)ScoutFrame.Value, scoutDir, Mission.TargetLevel, GimliRoverFrame.HasValue ? (float3?)(float3)GimliRoverFrame.Value : null);
            Tracks = FindAnyObjectByType<TyreTracks>() ?? TyreTracks.Attach(Buggy);
            Tracks.rover = Buggy;
            Director = gameObject.GetComponent<MissionDirector>() ?? gameObject.AddComponent<MissionDirector>();
            Director.game = this;
            Teleport(0);
            Sky.Build();
            Sky.UpdateSky(true);
            Exposure.SnapToTarget();
            Initialised = true;
            BuiltTier = Mission.Current;
            if (Application.isPlaying && !Application.isBatchMode && !PerfBench.Requested && !PlayerShots.Requested && Mission.ShowStartScreen)
            {
                var ss = gameObject.GetComponent<MoonBaseAlpha.UI.StartScreen>() ?? gameObject.AddComponent<MoonBaseAlpha.UI.StartScreen>();
                ss.game = this;
            }
            else if (Application.isPlaying) BeginMission();
            if (Application.isPlaying && PerfBench.Requested && GetComponent<PerfBench>() == null) gameObject.AddComponent<PerfBench>().game = this;
            if (Application.isPlaying && PlayerShots.Requested && GetComponent<PlayerShots>() == null) gameObject.AddComponent<PlayerShots>().game = this;
            Debug.Log($"[Game] world ready in {initClock.Elapsed.TotalSeconds:0.0} s: {Sites.Sites.Count} special sites, seed {WorldSeed.Value}");
        }

        bool runningLogged;

        void BuildBase(Transform root, HeightField hf)
        {
            Vector3 Ground(double3 p, out Vector3 up)
            {
                var w = WorldOrigin.FrameToWorld(new double3(p.x, hf.Height(p.x, p.z, 0f), p.z));
                up = -TerrainFrame.DownAt(w);
                return w;
            }
            var pit = new double3(hf.pitCentre.x, 0, hf.pitCentre.y);

            // crew lander on a levelled spot; blast zone recorded for the regolith shader
            var lp = FindClearSpot(hf, new double3(landerXZ.x, 0, landerXZ.y), 4.5f);
            LanderFrame = new double3(lp.x, hf.Height(lp.x, lp.z, 0f), lp.z);
            var lw = Ground(lp, out var lup);
            var toPit = Vector3.ProjectOnPlane(WorldOrigin.FrameToWorld(pit) - lw, lup).normalized;
            var lander = BaseBuilder.CrewLander(root);
            lander.transform.SetPositionAndRotation(lw - lup * 0.3f, Quaternion.LookRotation(toPit, lup));
            Shader.SetGlobalVector("_MBA_Blast", new Vector4((float)lp.x, (float)lp.z, 140f, 0));

            // start: the landing zone of the tier, on bearing 045 from the pit (as Houston calls it), facing the pit.
            // The crew lander, cargo lander and base stay where they are; only the rover's start moves.
            {
                float brg = math.radians(Mission.StartBearing);
                var sxz = hf.pitCentre + new float2(math.sin(brg), math.cos(brg)) * Mission.Info.startDistance;
                startFrame = FindClearSpot(hf, new double3(sxz.x, 0, sxz.y), 3f);
                var toPitS = math.normalizesafe(hf.pitCentre - (float2)startFrame.xz);
                startHeading = math.degrees(math.atan2(toPitS.x, toPitS.y));
            }

            var cp = FindClearSpot(hf, new double3(cargoXZ.x, 0, cargoXZ.y), 3.5f);
            var cw = Ground(cp, out var cup);
            CargoFrame = new double3(cp.x, hf.Height(cp.x, cp.z, 0f), cp.z);
            var cargo = BaseBuilder.CargoLander(root);
            cargo.transform.SetPositionAndRotation(cw - cup * 0.2f, Quaternion.LookRotation(Vector3.ProjectOnPlane(WorldOrigin.FrameToWorld(pit) - cw, cup), cup));

            var rp = FindClearSpot(hf, new double3(reactorXZ.x, 0, reactorXZ.y), 2.5f);
            var rw = Ground(rp, out var rup);
            BaseBuilder.Reactor(root).transform.SetPositionAndRotation(rw - rup * 0.2f, Quaternion.identity);

            var landing = Cave.Field.landingSpot;
            const float boom = 46f;
            var baseXZ = craneBaseXZ; float padSide = cranePadSide;
            var bw = Ground(new double3(baseXZ.x, 0, baseXZ.y), out var bup);
            float rimLocal = Cave.Field.rim - hf.Height(baseXZ.x, baseXZ.y, 0f);
            var towardPit = Vector3.ProjectOnPlane(WorldOrigin.FrameToWorld(new double3(landing.x, 0, landing.y)) - bw, bup).normalized;
            var craneRot = Quaternion.LookRotation(towardPit, bup);
            // slew 0 = pointing at the landing spot; the load pad is 90 deg to the side on the plain
            // hoist height: clear the highest ground (incl. boulders) under the whole slew arc by 3 m
            float baseH = hf.Height(baseXZ.x, baseXZ.y, 0f), arcMax = rimLocal;
            for (float ang = Mathf.Min(padSide, 0f); ang <= Mathf.Max(padSide, 0f); ang += 1f)
                for (float r = boom - 5f; r <= boom + 5f; r += 1f)
                {
                    var wpt = bw + craneRot * (Quaternion.Euler(0, ang, 0) * new Vector3(0, 0, r));
                    var fpt = WorldOrigin.WorldToFrame(wpt);
                    arcMax = Mathf.Max(arcMax, hf.Height(fpt.x, fpt.z, 0f) - baseH);
                }
            Crane = CraneController.Build(root, bw, craneRot, padSide, 0f, arcMax + 3f);
            Physics.SyncTransforms();
            Crane.ResetToPad();
            var mouthW = WorldOrigin.FrameToWorld((double3)Cave.Network.mouthFloor);
            var landW = WorldOrigin.FrameToWorld(new double3(landing.x, Cave.Field.pitFloor, landing.y));
            Crane.bottomExitDir = (mouthW - landW).normalized;

            // BASE CAM: a camera mast behind the crane, watching the mast, the load pad and the pit rim
            {
                var awayW = -towardPit;
                var padW = Crane.PadWorld;
                var padDir = Vector3.ProjectOnPlane(padW - bw, bup).normalized;
                var rimW = bw + towardPit * 28f;
                var look = (bw + 2f * padW + rimW) / 4f + bup * 3f;
                Vector3 cg = bw, cu = bup;
                foreach (var (a, b) in new[] { (22f, 14f), (28f, 6f), (16f, 22f), (34f, 0f), (22f, 30f) })
                {
                    var camGround = padW + padDir * a + awayW * b;
                    var cgf = WorldOrigin.WorldToFrame(camGround);
                    cg = Ground(new double3(cgf.x, 0, cgf.z), out cu);
                    var eye = cg + cu * 9.4f;
                    bool Clear(Vector3 to) => !Physics.Linecast(eye, to, out var lh, ~(1 << 2), QueryTriggerInteraction.Ignore) || lh.distance > Vector3.Distance(eye, to) - 6f;
                    if (Clear(padW + cu * 1.5f) && Clear(bw + cu * 6f)) break;
                }
                BaseCam = BaseBuilder.CameraMast(root, 9f);
                var mastRoot = BaseCam.parent;
                mastRoot.SetPositionAndRotation(cg - cu * 0.1f, Quaternion.LookRotation(Vector3.ProjectOnPlane(look - cg, cu).normalized, cu));
                BaseCam.rotation = Quaternion.LookRotation((look - BaseCam.position).normalized, cu);
            }

            // pre-made tracks: the robots that hauled the crane sections from the GIMLI-2 cargo lander to the pad
            {
                var padF = WorldOrigin.WorldToFrame(Crane.PadWorld);
                var a = new double3(cp.x, 0, cp.z); var bpt = new double3(padF.x, 0, padF.z);
                var dirCP = math.normalizesafe((bpt - a).xz);
                var fw = new double3(dirCP.x, 0, dirCP.y);
                var a1 = a + fw * 7.0;    // from the foot of the cargo ramp
                var perp = new double3(-dirCP.y, 0, dirCP.x);
                TyreTracks.BuildPath(root, "Tracks: cargo lander -> crane pad", hf, new[] { a1, math.lerp(a1, bpt, 0.5) + perp * 6.0, bpt - fw * 9.0 }, 2.0f, 0.32f, 4f, 17);
                TyreTracks.BuildPath(root, "Tracks: crane pad -> cargo lander", hf, new[] { bpt - fw * 9.0 + perp * 3.0, math.lerp(a1, bpt, 0.5) + perp * 2.0, a1 + perp * 3.0 }, 2.0f, 0.32f, 5f, 29);
            }
            BuildGimliOne(root, hf, baseXZ);

            // GIMLI-2: at the end of the golden route on the target level, facing the sealed tube end
            var net = Cave.Network;
            Route = net.GoldenPath(Mission.TargetLevel);
            var scout = BaseBuilder.Scout(root);
            ScoutObject = scout;
            if (Route.Valid)
            {
                var dir = Route.sealDir;
                var dir3 = new float3(dir.x, 0, dir.y);
                scoutDir = dir;
                float fy = net.FloorY(Route.scout);
                var sp = Route.scout; if (!float.IsNaN(fy)) sp.y = fy;
                ScoutFrame = new double3(sp.x, sp.y + 0.05f, sp.z);
                var ap = Route.scout - dir3 * 22f;
                float ay = net.FloorY(ap);
                scoutApproach = new double3(ap.x, (float.IsNaN(ay) ? ap.y : ay) + 1.5f, ap.z);
                scoutHeading = math.degrees(math.atan2(dir.x, dir.y));
                scout.transform.position = WorldOrigin.FrameToWorld(ScoutFrame.Value);
                scout.transform.rotation = Quaternion.LookRotation(new Vector3(dir.x, 0, dir.y), Vector3.up);
                RadarSweep.Add(scout);
                // the seal: a fused rubble wall across the closed tube end
                float sy = net.FloorY(Route.seal);
                var sealP = Route.seal; if (!float.IsNaN(sy)) sealP.y = sy;
                SealFrame = (double3)sealP;
                float hwS = Route.sealHalfWidth;
                float sealH = 1.55f * Tunnel.HeightFor(hwS / (Route.CaveIn ? 1.2f : 1f)) * 1.05f;
                SealObject = MissionBuilder.RubbleSeal(root, hwS, sealH, (int)(net.seed % 100000), tunnelMaterial != null ? tunnelMaterial : caveMaterial);
                SealObject.transform.SetPositionAndRotation(WorldOrigin.FrameToWorld(SealFrame.Value) - Vector3.up * 0.3f, Quaternion.LookRotation(new Vector3(dir.x, 0, dir.y), Vector3.up));
                SealObject.AddComponent<ScoutAnchor>().frame = SealFrame.Value;
                if (Route.BackSeal)
                {
                    // a through tube was sealed (Cadet): the far side of the hollow part caved in too (its pile faces back)
                    var back = MissionBuilder.RubbleSeal(root, hwS, sealH, (int)(net.seed % 100000) + 7, tunnelMaterial != null ? tunnelMaterial : caveMaterial);
                    back.name = "Rubble seal (far side)";
                    back.transform.SetPositionAndRotation(WorldOrigin.FrameToWorld((double3)Route.backSeal) - Vector3.up * 0.3f, Quaternion.LookRotation(new Vector3(-dir.x, 0, -dir.y), Vector3.up));
                }
                // the sealed chamber in the rock beyond the tube end (inside-out shell: invisible from outside)
                // beyond the pile (it reaches ~1.4x the tube height back from its toe) and beyond a dead end's closed end
                var cxz = Route.seal.xz + dir * math.max(math.distance(Route.spur.b.xz, Route.seal.xz) + 8f, MissionBuilder.PileDepth(sealH) + 6f);
                ChamberFrame = new double3(cxz.x, sealP.y, cxz.y);
                ChamberObject = MissionBuilder.FrostChamber(root, 5.5f, Mission.TargetLevel >= 3);
                ChamberObject.transform.SetPositionAndRotation(WorldOrigin.FrameToWorld(ChamberFrame), Quaternion.LookRotation(new Vector3(dir.x, 0, dir.y), Vector3.up));
                ChamberObject.AddComponent<ScoutAnchor>().frame = ChamberFrame;
                ChamberObject.SetActive(false);
                // relay beacons at the route's junctions (beside the way on, on the real floor)
                Beacons.Clear();
                for (int k = 1; k < Route.points.Count - 2; k++)
                {
                    if (Route.pointIsJunction.Count == Route.points.Count && !Route.pointIsJunction[k]) continue;   // flow layout: junctions only
                    var n = Route.points[k]; var nx = Route.points[k + 1];
                    var od = math.normalizesafe((nx - n).xz);
                    var side = new float2(-od.y, od.x);
                    foreach (float off in new[] { 7f, 4f, 0f })
                    {
                        var q = n + new float3(od.x * 14f + side.x * off, 0, od.y * 14f + side.y * off);
                        float qy = net.FloorY(q, 8f, 14f);
                        if (float.IsNaN(qy)) continue;
                        q.y = qy;
                        Beacons.Add((double3)q);
                        var bgo = MissionBuilder.Beacon(root);
                        bgo.transform.SetPositionAndRotation(WorldOrigin.FrameToWorld((double3)q), Quaternion.LookRotation(new Vector3(od.x, 0, od.y), Vector3.up));
                        break;
                    }
                }
            }
            else
            {
                // fallback (no sealable spur found): the old placement, deep along the backbone
                var path = net.ExplorePath(14, Mission.TargetLevel);
                var last = path[path.Count - 1];
                var prev = path[path.Count - 2];
                var sp = net.NodePos(last.level, last.i, last.j);
                var pp = net.NodePos(prev.level, prev.i, prev.j);
                var dir = math.normalize(sp.xz - pp.xz);
                scoutDir = dir;
                ScoutFrame = new double3(sp.x, sp.y + 0.6f, sp.z);
                scoutApproach = new double3(sp.x - dir.x * 22f, sp.y + 1.5f, sp.z - dir.y * 22f);
                scoutHeading = math.degrees(math.atan2(dir.x, dir.y));
                scout.transform.position = WorldOrigin.FrameToWorld(ScoutFrame.Value);
                scout.transform.rotation = Quaternion.LookRotation(new Vector3(-dir.x, 0, -dir.y), Vector3.up);
                Debug.LogWarning($"[Game] no golden route to level {Mission.TargetLevel}: scout on the backbone");
            }
            var wo = scout.AddComponent<ScoutAnchor>();
            wo.frame = ScoutFrame.Value;
            if (root.GetComponent<BeaconBlink>() == null) root.gameObject.AddComponent<BeaconBlink>();
        }

        /// <summary>
        /// GIMLI-1 on the rim: its rover parked at the rim on level ground away from the crane (outside the slew circle),
        /// near the drive from the landing zone to the crane; the small CLPS lander ~40 m further out; a line of
        /// seismometer stakes on the plain; the rover's radar-traverse tracks along the rim.
        /// </summary>
        void BuildGimliOne(Transform root, HeightField hf, float2 craneBase)
        {
            var padF = WorldOrigin.WorldToFrame(Crane.PadWorld);
            var pad = new float2((float)padF.x, (float)padF.z);
            var st = new float2((float)startFrame.x, (float)startFrame.z);
            float best = float.MaxValue; float2 pick = hf.pitCentre + new float2(60, 60);
            for (int k = 0; k < 72; k++)
            {
                float a = math.radians(k * 5f);
                var dir = new float2(math.sin(a), math.cos(a));
                for (float r = 40f; r <= 160f; r += 5f)
                {
                    var c = hf.pitCentre + dir * r;
                    float rho = hf.PitRho(c);
                    if (rho < 1.65f || rho > 2.3f) continue;
                    float dBase = math.distance(c, craneBase), dPad = math.distance(c, pad);
                    if (dBase < 68f || dPad < 45f) continue;
                    // distance from the straight drive landing zone -> crane pad (aim: 50-100 m, seen on the way)
                    var ab = pad - st; float t = math.saturate(math.dot(c - st, ab) / math.max(1f, math.lengthsq(ab)));
                    float dLine = math.distance(c, st + ab * t);
                    float score = SiteScore(hf, c, 3f) * 1.5f + math.abs(dLine - 70f) / 12f + (rho - 1.65f) * 3f;
                    if (dLine < 30f) score += 20f;
                    if (score < best) { best = score; pick = c; }
                }
            }
            var rp = FindClearSpot(hf, new double3(pick.x, 0, pick.y), 2.2f);
            var radial = math.normalizesafe((float2)rp.xz - hf.pitCentre);
            var tangent = new float2(-radial.y, radial.x);
            GimliRoverFrame = new double3(rp.x, hf.Height(rp.x, rp.z, 0f), rp.z);
            var rw = WorldOrigin.FrameToWorld(GimliRoverFrame.Value);
            var rup = -TerrainFrame.DownAt(rw);
            var rover = MissionBuilder.GimliRover(root);
            rover.transform.SetPositionAndRotation(rw - rup * 0.05f, Quaternion.LookRotation(Vector3.ProjectOnPlane(new Vector3(tangent.x, 0, tangent.y), rup), rup));
            // the lander a short walk out on the plain
            var lp = FindClearSpot(hf, rp + new double3(radial.x, 0, radial.y) * 40.0 + new double3(tangent.x, 0, tangent.y) * 12.0, 2.6f);
            GimliLanderFrame = new double3(lp.x, hf.Height(lp.x, lp.z, 0f), lp.z);
            var lw = WorldOrigin.FrameToWorld(GimliLanderFrame.Value);
            var lup = -TerrainFrame.DownAt(lw);
            var lander = MissionBuilder.GimliLander(root);
            lander.transform.SetPositionAndRotation(lw - lup * 0.1f, Quaternion.LookRotation(Vector3.ProjectOnPlane(new Vector3(-radial.x, 0, -radial.y), lup), lup));
            // seismometer stakes: a line out on the plain from the lander
            for (int i = 1; i <= 6; i++)
            {
                var sp = (float2)lp.xz + radial * (i * 22f) + tangent * (i % 2 == 0 ? 3f : -3f);
                var sw = WorldOrigin.FrameToWorld(new double3(sp.x, hf.Height(sp.x, sp.y, 0f), sp.y));
                var stake = MissionBuilder.SeismoStake(root);
                stake.transform.SetPositionAndRotation(sw, Quaternion.FromToRotation(Vector3.up, -TerrainFrame.DownAt(sw)));
            }
            // radar traverses along the rim: arcs at the rover's radius, clear of the crane, ending at the rover
            float rr = math.distance((float2)rp.xz, hf.pitCentre);
            float a0 = math.atan2(radial.x, radial.y);
            foreach (var (span, dr, seed) in new[] { (0.9f, 0f, 3), (-0.7f, 6f, 5), (0.5f, -5f, 7) })
            {
                var pts = new System.Collections.Generic.List<double3>();
                for (int k = 0; k <= 24; k++)
                {
                    float a = a0 + span * k / 24f;
                    var c = hf.pitCentre + new float2(math.sin(a), math.cos(a)) * (rr + dr);
                    if (math.distance(c, craneBase) < 62f || math.distance(c, pad) < 12f || hf.PitRho(c) < 1.55f) break;
                    pts.Add(new double3(c.x, 0, c.y));
                }
                pts.Reverse();   // the last point is at the rover's end
                if (pts.Count >= 3) TyreTracks.BuildPath(root, "Tracks: GIMLI-1 radar traverse", hf, pts, 1.84f, 0.26f, 1.5f, seed, 0.75f);
            }
            gimliApproach = rp + new double3(tangent.x, 0, tangent.y) * 28.0 + new double3(radial.x, 0, radial.y) * 10.0;
            var toR = math.normalizesafe((float2)(rp.xz - gimliApproach.xz));
            gimliHeading = math.degrees(math.atan2(toR.x, toR.y));
        }

        /// <summary>0 = mission start (landing zone), 1 = crane load pad, 2 = tube mouth, 3 = GIMLI-2, 4 = GIMLI-1 on the rim.</summary>
        /// <summary>
        /// Round 13 (T3/T4): the shafts GIMLI-2 went down on its way to the seal keep its rope hanging from the trolley;
        /// the route's drop then runs from the rig's trolley spot on the upper floor to the foot below.
        /// </summary>
        void RigRouteShafts()
        {
            if (Route == null || !Route.Valid || Route.ShaftCount == 0) return;
            var F = Cave.Network.Flow;
            for (int i = 0; i < Route.ShaftCount; i++)
            {
                var s = F.Shafts[Route.shaftIds[i]];
                var rig = Winch.PreRig(s.top, s.radius, s.drop, true);
                int k = Route.shaftAt[i];
                if (k + 1 >= Route.points.Count) continue;
                Route.points[k] = (float3)rig.rimSpot;
                Route.points[k + 1] = (float3)rig.foot;
                // the lower points bunched round the rubble cone (the crossing of the two tube axes): drop them, so the
                // way on from the foot leads straight off the cone
                int j = k + 2;
                while (j < Route.points.Count - 2 && math.distance(Route.points[j].xz, ((float3)rig.foot).xz) < 20f && math.abs(Route.points[j].y - (float)rig.foot.y) < 10f)
                {
                    Route.points.RemoveAt(j); Route.pointLevels.RemoveAt(j); Route.pointIsJunction.RemoveAt(j);
                    for (int q = 0; q < Route.ShaftCount; q++) if (Route.shaftAt[q] > j) Route.shaftAt[q]--;
                }
            }
            Route.Recompute();
            Debug.Log($"[Game] route: {Route.ShaftCount} shaft(s) down (tier wants {Route.ShaftsWanted}), {Route.Length:0} m");
        }

        public void Teleport(int where)
        {
            if (Player != null && Player.State == PlayerControl.Mode.OnFoot) Player.EnterRoverForced();
            double3 p; float headingDeg;
            var hf = Streamer.Height;
            bool underground = false;
            switch (where)
            {
                case 1:
                {
                    // crane back on its load pad first (it may be down in the pit), then 18 m short of the
                    // platform's outer ramp, lined up with it (the deck runs along the boom)
                    var padW = Crane.PadWorld;
                    Streamer.BuildNow(padW, padW);
                    Physics.SyncTransforms();
                    Crane.ResetToPad();
                    Physics.SyncTransforms();
                    var pad = WorldOrigin.WorldToFrame(Crane.Platform.position);
                    var craneBase = WorldOrigin.WorldToFrame(Crane.transform.position);
                    var outward = math.normalize((pad - craneBase).xz);
                    p = new double3(pad.x + outward.x * 18, 0, pad.z + outward.y * 18);
                    headingDeg = (float)math.degrees(math.atan2(-outward.x, -outward.y));
                    break;
                }
                case 2:
                {
                    p = Cave.TubeFloorPoint(60f, out float2 dir);
                    p.y += 0.5f;
                    headingDeg = math.degrees(math.atan2(dir.x, dir.y));
                    underground = true;
                    break;
                }
                case 3:
                    p = scoutApproach; headingDeg = scoutHeading; underground = true;
                    break;
                case 4:
                    p = gimliApproach; headingDeg = gimliHeading;
                    break;
                default:
                    p = startFrame; headingDeg = startHeading;
                    // restart: anchor kits back, fixed lines gone, rover repaired
                    if (Winch != null) Winch.ResetAll();
                    Buggy.Damaged = false;
                    break;
            }
            if (!underground)
            {
                if (where != 1) p = FindClearSpot(hf, p, 3f);
                p.y = hf.Height(p.x, p.z, 0f) + 1.5f;
            }
            var world = WorldOrigin.FrameToWorld(p);
            Streamer.BuildNow(world, world);
            if (underground) Tunnels.BuildNow(world, 120f);   // the far ring streams in by itself (dark, distant)
            var up = -TerrainFrame.DownAt(world);
            var fwd = Quaternion.AngleAxis(headingDeg, up) * Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
            Buggy.PlaceAt(world, Quaternion.LookRotation(fwd, up));
            Buggy.stateOfCharge = where == 0 ? 1f : Buggy.stateOfCharge;
            Buggy.throttle = 0; Buggy.brake = 0; Buggy.steering = 0;
            autoLights = true;
            SetLights(underground);
            Physics.SyncTransforms();
        }

        /// <summary>
        /// Site cycle: put the rover on the floor ~25 m before special site k, facing it (collapse shafts,
        /// lava-fall steps, ramps, large chambers; the scout rover last). Returns false if no spot was found.
        /// </summary>
        public bool GotoSite(int k)
        {
            if (Sites == null || Sites.Sites.Count == 0) return false;
            k = ((k % Sites.Sites.Count) + Sites.Sites.Count) % Sites.Sites.Count;
            SiteIndex = k;
            var s = Sites.Sites[k];
            SiteLabel = $"SITE {k + 1}/{Sites.Sites.Count} - {s.label}";
            SiteLabelTime = Time.unscaledTime;
            if (s.kind == SiteCatalog.Kind.Scout) { Teleport(3); return true; }
            if (s.kind == SiteCatalog.Kind.Gimli1) { Teleport(4); return true; }
            if (Player != null && Player.State == PlayerControl.Mode.OnFoot) Player.EnterRoverForced();
            var featW = WorldOrigin.FrameToWorld((double3)s.feature);
            Streamer.BuildNow(featW, featW);
            Tunnels.BuildNow(featW, 120f);
            Physics.SyncTransforms();
            var up = -TerrainFrame.DownAt(featW);
            Vector3? spot = null; Vector3 face = Vector3.forward; float bestScore = float.MaxValue;
            foreach (float f in new[] { 1f, 0.8f, 1.25f, 0.6f, 1.6f, 0.4f })
            {
                foreach (float ang in new[] { 0f, 20f, -20f, 40f, -40f, 65f, -65f, 90f, -90f })
                {
                    float a = math.radians(ang);
                    var d = new float2(s.approach.x * math.cos(a) - s.approach.y * math.sin(a), s.approach.x * math.sin(a) + s.approach.y * math.cos(a));
                    var cxz = s.feature.xz - d * s.standoff * f;
                    var probe = WorldOrigin.FrameToWorld(new double3(cxz.x, s.feature.y + 14f, cxz.y));
                    if (!Physics.Raycast(probe, -up, out var hit, 45f, ~(1 << 2), QueryTriggerInteraction.Ignore)) continue;
                    if (Vector3.Dot(hit.normal, up) < 0.9f) continue;
                    if (Physics.Raycast(hit.point + up * 0.3f, up, 3.5f, ~(1 << 2), QueryTriggerInteraction.Ignore)) continue;   // headroom
                    var fwd = Vector3.ProjectOnPlane(featW - hit.point, up);
                    if (fwd.sqrMagnitude < 1f) continue;
                    var rot = Quaternion.LookRotation(fwd.normalized, up);
                    // wheel contact points (+ a margin) and the centre: twist, bump and slope of the footprint
                    var hs = new float[5]; bool ok = true; int idx = 0;
                    foreach (var o in new[] { new Vector3(-1.2f, 0, 1.6f), new Vector3(1.2f, 0, 1.6f), new Vector3(-1.2f, 0, -1.6f), new Vector3(1.2f, 0, -1.6f), Vector3.zero })
                    {
                        var q = hit.point + rot * o;
                        if (!Physics.Raycast(q + up * 2.5f, -up, out var h2, 5f, ~(1 << 2), QueryTriggerInteraction.Ignore)) { ok = false; break; }
                        hs[idx++] = Vector3.Dot(h2.point - hit.point, up);
                    }
                    if (!ok) continue;
                    float twist = Mathf.Abs((hs[0] - hs[1]) - (hs[2] - hs[3]));
                    float bump = Mathf.Abs(hs[4] - (hs[0] + hs[1] + hs[2] + hs[3]) / 4f);
                    float pitchDeg = Mathf.Atan(Mathf.Abs((hs[0] + hs[1]) - (hs[2] + hs[3])) / 2f / 3.2f) * Mathf.Rad2Deg;
                    float rollDeg = Mathf.Atan(Mathf.Abs((hs[0] + hs[2]) - (hs[1] + hs[3])) / 2f / 2.4f) * Mathf.Rad2Deg;
                    if (twist > 0.3f || bump > 0.3f || pitchDeg > 16f || rollDeg > 12f) continue;
                    float score = twist * 3f + bump * 3f + (pitchDeg + rollDeg) / 10f + Mathf.Abs(f - 1f) * 0.6f + Mathf.Abs(ang) / 150f;
                    if (score < bestScore) { bestScore = score; spot = hit.point; face = fwd.normalized; }
                }
                if (spot.HasValue && bestScore < 0.6f) break;   // good enough at this distance
            }
            if (!spot.HasValue) { SiteLabel += "  (no level spot found)"; return false; }
            Streamer.BuildNow(spot.Value, spot.Value);
            Tunnels.BuildNow(spot.Value, 120f);
            Buggy.PlaceAt(spot.Value + up * 0.5f, Quaternion.LookRotation(face, up));
            Buggy.throttle = 0; Buggy.brake = 0; Buggy.steering = 0;
            autoLights = true;
            SetLights(true);
            Physics.SyncTransforms();
            return true;
        }

        /// <summary>Change the world seed and rebuild the world (reloads the scene in play mode).</summary>
        public void Reseed(uint seed)
        {
            WorldSeed.Set(seed);
            if (Application.isPlaying)
                UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
        }

        float2 craneBaseXZ; float cranePadSide;

        /// <summary>
        /// Crane on the rim: the boom (46 m) reaches the landing spot on the pit floor. The crew chose the
        /// orientation where the mast base and the load pad (90 deg to the side) are on the flattest ground,
        /// then levelled the pad (a 6 m radius graded patch, as any work site would be prepared).
        /// </summary>
        void ChooseCraneSite(ref HeightField hf)
        {
            const float boom = 46f;
            var landing = Cave.Field.landingSpot;
            float bestScore = float.MaxValue; craneBaseXZ = landing; cranePadSide = -90f; float2 padXZ = landing;
            for (int k = 0; k < 72; k++)
            {
                float a = k * 5f * Mathf.Deg2Rad;
                var dirToBase = new float2(Mathf.Sin(a), Mathf.Cos(a));
                var bxz = landing + dirToBase * boom;
                foreach (float side in new[] { -90f, 90f })
                {
                    var towardLanding = -dirToBase;
                    float sa = side * Mathf.Deg2Rad;
                    var padDir = new float2(towardLanding.x * Mathf.Cos(sa) + towardLanding.y * Mathf.Sin(sa), -towardLanding.x * Mathf.Sin(sa) + towardLanding.y * Mathf.Cos(sa));
                    var pxz = bxz + padDir * boom;
                    float score = SiteScore(hf, bxz, 4f) + 2f * SiteScore(hf, pxz, 6f);
                    if (hf.PitRho(pxz) < 1.35f || hf.PitRho(bxz) < 1.05f) score += 100f;
                    if (score < bestScore) { bestScore = score; craneBaseXZ = bxz; cranePadSide = side; padXZ = pxz; }
                }
            }
            float padH = 0; int n = 0;
            for (float dx = -5; dx <= 5; dx += 1) for (float dz = -5; dz <= 5; dz += 1) { padH += hf.Height(padXZ.x + dx, padXZ.y + dz, 0f); n++; }
            hf.pad0 = new float4(padXZ.x, padXZ.y, 6.5f, padH / n);
        }

        /// <summary>Slope (deg) + roughness score of a footprint (lower is flatter).</summary>
        static float SiteScore(HeightField hf, float2 c, float radius)
        {
            float hx = hf.Height(c.x + radius, c.y, 0f) - hf.Height(c.x - radius, c.y, 0f);
            float hz = hf.Height(c.x, c.y + radius, 0f) - hf.Height(c.x, c.y - radius, 0f);
            float slope = math.degrees(math.atan(math.sqrt(hx * hx + hz * hz) / (2 * radius)));
            float h0 = hf.Height(c.x, c.y, 0f), worst = 0;
            for (int i = 0; i < 8; i++)
            {
                float a = i * math.PI / 4;
                float h = hf.Height(c.x + math.cos(a) * radius * 0.7f, c.y + math.sin(a) * radius * 0.7f, 0f);
                float plane = h0 + (hx * math.cos(a) + hz * math.sin(a)) / (2 * radius) * radius * 0.7f;
                worst = math.max(worst, math.abs(h - plane));
            }
            return slope + worst * 20f;
        }

        /// <summary>
        /// Spiral search for a spot where a footprint of the given radius is near-level and free of boulders:
        /// least-squares plane over the disc, scored by slope and worst deviation. Returns the best found.
        /// </summary>
        static double3 FindClearSpot(HeightField hf, double3 p, float radius = 3f)
        {
            double3 best = p; float bestScore = float.MaxValue;
            int g = 13; float step = radius * 2f / (g - 1);
            var hs = new float[g * g];
            for (int k = 0; k < 600; k++)
            {
                float a = k * 2.39996f, r = 1.2f * math.sqrt(k) * math.max(1f, radius / 3f);
                double x = p.x + r * math.cos(a), z = p.z + r * math.sin(a);
                float sh = 0, sx = 0, sz = 0, sxx = 0, szz = 0; int n = 0;
                for (int j = 0; j < g; j++)
                for (int i = 0; i < g; i++)
                {
                    float dx = -radius + i * step, dz = -radius + j * step;
                    if (dx * dx + dz * dz > radius * radius * 1.01f) { hs[j * g + i] = float.NaN; continue; }
                    float h = hf.Height(x + dx, z + dz, 0f);
                    hs[j * g + i] = h;
                    sh += h; sx += h * dx; sz += h * dz; sxx += dx * dx; szz += dz * dz; n++;
                }
                float c = sh / n, gx = sx / sxx, gz = sz / szz;
                float worst = 0;
                for (int j = 0; j < g; j++)
                for (int i = 0; i < g; i++)
                {
                    float h = hs[j * g + i];
                    if (float.IsNaN(h)) continue;
                    worst = math.max(worst, math.abs(h - (c + gx * (-radius + i * step) + gz * (-radius + j * step))));
                }
                float slope = math.degrees(math.atan(math.sqrt(gx * gx + gz * gz)));
                float score = slope / 4f + worst / 0.15f + r / 60f;
                if (score < bestScore) { bestScore = score; best = new double3(x, 0, z); }
                if (slope < 4f && worst < 0.15f) break;
            }
            return best;
        }

        public void SetLights(bool on)
        {
            var d = Buggy.GetComponent<BuggyDriver>();
            foreach (var l in d.headlights) if (l) l.enabled = on;
            Buggy.extraLoadWatts = on ? 4 * 60f : 0f;
        }

        void Update()
        {
            if (!Initialised) return;
            if (!runningLogged && Time.realtimeSinceStartup > 10f)
            {
                runningLogged = true;
                Debug.Log($"[Game] running: {1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):0} fps, {Tunnels.ChunkCount} tunnel chunks, {FarTunnels.ChunkCount} far-ring chunks, quality {Quality.level}");
            }
            if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.F5) || Input.GetKeyDown(KeyCode.Alpha1)) Teleport(0);
            if (Input.GetKeyDown(KeyCode.Alpha2)) Teleport(1);
            if (Input.GetKeyDown(KeyCode.Alpha3)) Teleport(2);
            if (Input.GetKeyDown(KeyCode.Alpha4)) Teleport(3);
            if (Input.GetKeyDown(KeyCode.Alpha5))
            {
                bool back = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                GotoSite(SiteIndex < 0 ? (back ? -1 : 0) : SiteIndex + (back ? -1 : 1));
            }
            if (Input.GetKeyDown(KeyCode.T)) SimClock.Instance?.CycleScale();
            if (Controls.Down(Controls.Btn.Lights)) autoLights = false;
            Nav.focus = Player.Focus;
            if (Director != null && Director.phase != MissionDirector.Phase.Idle)
                Life.Step(Time.deltaTime, Player.State == PlayerControl.Mode.OnFoot ? 0 : -1, Player.State == PlayerControl.Mode.OnFoot && Player.Walker != null && Player.Walker.LampsOn);
            EdgeWarning = EdgeWarningAt(Player.Focus.position, Player.Focus.forward);
            if (EdgeWarning != null) MessageLog.AddOnce("edge", EdgeWarning, MessageLog.Kind.Warning); else MessageLog.Clear("edge");
            // Cadet: the crane ride runs as a time-lapse (game timing for the short run; physics unchanged, x3 per frame)
            bool lapse = Mission.Current == Mission.Tier.Cadet && Crane.Moving && Crane.RoverOnPlatform;
            if (!MoonBaseAlpha.UI.PauseMenu.AnyMenuOpen && Time.timeScale > 0f)
            {
                if (lapse) { Time.timeScale = CadetCraneTimeScale; MessageLog.AddOnce("lapse", $"Crane ride: time-lapse x{CadetCraneTimeScale:0} (Cadet)", MessageLog.Kind.System); }
                else if (CraneTimeLapse) { Time.timeScale = 1f; MessageLog.Clear("lapse"); }
            }
            CraneTimeLapse = lapse;
            Crane.SetRover(Buggy.transform);
            if (autoLights && Exposure.Darkness > 0.5f)
            {
                SetLights(true);
                if (Player.State == PlayerControl.Mode.OnFoot) Player.Walker.SetLamps(true);
                autoLights = false;
            }
        }
    }

    /// <summary>Keeps a static object at a fixed frame position (it is re-placed after origin shifts).</summary>
    public class ScoutAnchor : MonoBehaviour
    {
        public double3 frame;
    }
}
