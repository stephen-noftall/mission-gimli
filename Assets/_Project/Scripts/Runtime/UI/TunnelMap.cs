using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using MoonBaseAlpha.Cave;

namespace MoonBaseAlpha.UI
{
    /// <summary>
    /// 3D tube map (key M; Shift+M = the old flat nav map): a rotatable (drag) and zoomable (wheel) holographic model of
    /// the tubes you have been near (NavTracker), the pit, collapse shafts you passed, rigged fixed lines, your position
    /// and your track (the route back). Tube floors/walls are coloured by the rock deposits the CaveRock shader draws
    /// (same noise, ported to C# in RockDeposits), with a legend. Own layer, own camera, RenderTexture; costs nothing
    /// while closed. The mouse is free while it is open.
    /// </summary>
    public class TunnelMap : MonoBehaviour
    {
        public const int Layer = 30;
        public const float Scale = 0.1f;                      // map units per metre
        public static bool IsOpen { get; private set; }
        public string Debug { get; private set; } = "";
        public static int ClosedFrame { get; private set; } = -1;
        public GameBootstrap game;

        Camera mapCam;
        RenderTexture rt;
        Transform root;
        Material holo, solid;
        Mesh tubes, track, marks, solidTubes, flowLines;
        int builtFlow = -1;
        bool Flow => game != null && game.Cave != null && game.Cave.Network != null && game.Cave.Network.IsFlow;
        int builtEdges = -1, builtTrack = -1, builtLines = -1;
        float yaw = 30f, pitch = 35f, dist = 120f, nextBuild;
        GUIStyle legend, title;

        static readonly (RockDeposits.Kind k, string label, Color c)[] Legend =
        {
            (RockDeposits.Kind.Basalt, "bare basalt (tube floor / walls)", new Color(0.35f, 0.45f, 0.55f)),
            (RockDeposits.Kind.Dust, "talus + dust near openings", new Color(0.75f, 0.72f, 0.65f)),
            (RockDeposits.Kind.Glaze, "lava glaze (glassy wall lining)", new Color(0.2f, 0.6f, 1f)),
            (RockDeposits.Kind.OrangeGlass, "orange glass beads (speculative)", new Color(1f, 0.5f, 0.1f)),
            (RockDeposits.Kind.GreenGlass, "green glass beads (speculative)", new Color(0.4f, 0.9f, 0.3f)),
            (RockDeposits.Kind.Crust, "Zn/S volatile crusts (speculative)", new Color(1f, 0.95f, 0.4f)),
        };

        static Color KindColour(RockDeposits.Kind k) { foreach (var l in Legend) if (l.k == k) return l.c; return Color.gray; }

        void Init()
        {
            if (mapCam != null) return;
            var go = new GameObject("TunnelMapRoot");
            go.AddComponent<WorldOriginIgnore>();
            go.transform.position = new Vector3(0, -30000f, 0);    // far below everything; only the map camera sees this layer
            root = go.transform;
            var sh = Shader.Find("MoonBaseAlpha/Holo");
            holo = new Material(sh != null ? sh : Shader.Find("Hidden/Internal-Colored")) { name = "Holo" };
            tubes = NewChild("Tubes"); track = NewChild("Track"); marks = NewChild("Marks"); flowLines = NewChild("FlowLines");
            var ss = Shader.Find("MoonBaseAlpha/MapSolid");
            solid = new Material(ss != null ? ss : Shader.Find("Hidden/Internal-Colored")) { name = "MapSolid" };
            solidTubes = NewChild("SolidTubes", solid);
            var cg = new GameObject("TunnelMapCam");
            cg.AddComponent<WorldOriginIgnore>();
            mapCam = cg.AddComponent<Camera>();
            mapCam.cullingMask = 1 << Layer;
            mapCam.clearFlags = CameraClearFlags.SolidColor;
            // flow layout: a cool slate background (contrast for the grey rock tubes); the old holo map stays near-black
            mapCam.backgroundColor = Flow ? new Color(0.149f, 0.188f, 0.235f, 1f) : new Color(0.01f, 0.02f, 0.04f, 1f);
            mapCam.nearClipPlane = 0.5f; mapCam.farClipPlane = 5000f; mapCam.fieldOfView = 40f;
            mapCam.enabled = false;
            rt = new RenderTexture(1280, 800, 24) { name = "TunnelMapRT", antiAliasing = 4 };
            mapCam.targetTexture = rt;
            if (game != null && game.cam != null) game.cam.cullingMask &= ~(1 << Layer);
        }

        Mesh NewChild(string n, Material mat = null)
        {
            var go = new GameObject(n);
            go.layer = Layer;
            go.transform.SetParent(root, false);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat != null ? mat : holo;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var m = new Mesh { name = n, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mf.sharedMesh = m;
            return m;
        }

        public void SetOpen(bool open)
        {
            Init();
            IsOpen = open;
            mapCam.enabled = open;
            if (open) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; builtEdges = -1; builtFlow = -1; Rebuild(); }
            else { ClosedFrame = Time.frameCount; Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        }

        void Update()
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Controls.Down(Controls.Btn.Map) && !shift && !PauseMenu.IsOpen) SetOpen(!IsOpen);
            if (!IsOpen) return;
            if (Input.GetKeyDown(KeyCode.Escape) || Controls.PadDown(Controls.Btn.Brake)) { SetOpen(false); return; }
            if (Input.GetMouseButton(0)) { yaw += Input.GetAxis("Mouse X") * 4f; pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 3f, -10f, 89f); }
            // the pad: right stick rotates, triggers zoom
            var rs = Controls.RightStick; float dt = Time.unscaledDeltaTime;
            if (rs.sqrMagnitude > 0.04f) { yaw += rs.x * 90f * dt; pitch = Mathf.Clamp(pitch - rs.y * 60f * dt, -10f, 89f); }
            dist = Mathf.Clamp(dist * Mathf.Pow(0.88f, Input.mouseScrollDelta.y) * Mathf.Pow(0.3f, Controls.TriggerZoom * dt), 8f, 1500f);
            if (Time.unscaledTime > nextBuild) { nextBuild = Time.unscaledTime + 1f; Rebuild(); }
            PlaceCamera();
        }

        /// <summary>Map-root local position of a frame point (mesh vertices).</summary>
        Vector3 MapPos(double3 frame) => (Vector3)(float3)((frame - new double3(0, game.Cave.Field.mouthFloorY, 0)) * Scale);
        Vector3 MapWorld(double3 frame) => root.position + MapPos(frame);

        void PlaceCamera()
        {
            var focus = MapWorld(WorldOrigin.WorldToFrame(game.Player.Focus.position));
            var rot = Quaternion.Euler(pitch, yaw, 0);
            mapCam.transform.SetPositionAndRotation(focus - rot * Vector3.forward * dist, rot);
        }

        /// <summary>Rebuild the meshes from what the nav log knows (only when it changed).</summary>
        public void Rebuild()
        {
            Init();
            var nav = game.Nav; var w = game.Winch;
            int lines = w != null ? w.RiggedCount : 0;
            int tags = game.Director != null ? game.Director.Tags.Count : 0;
            if (Flow) { if (nav.Track.Count / 20 != builtFlow) { builtFlow = nav.Track.Count / 20; BuildFlow(nav); } }
            else if (nav.KnownEdges.Count != builtEdges) { builtEdges = nav.KnownEdges.Count; BuildTubes(nav); }
            if (nav.Track.Count != builtTrack || lines != builtLines || tags != builtTags) { builtTrack = nav.Track.Count; builtLines = lines; builtTags = tags; BuildTrackAndMarks(nav); }
        }

        void BuildTubes(NavTracker nav)
        {
            var v = new List<Vector3>(); var c = new List<Color>(); var idx = new List<int>();
            float mouthY = game.Cave.Field.mouthFloorY;
            foreach (var e in nav.KnownEdges)
            {
                float len = (float)math.distance(e.a, e.b);
                if (e.chamber)
                {
                    // chamber: a ring of the floor outline
                    int n = 24; int start = v.Count;
                    for (int k = 0; k <= n; k++)
                    {
                        float a = k * Mathf.PI * 2f / n;
                        var p = e.a + new double3(Mathf.Cos(a) * e.hw, 0.5, Mathf.Sin(a) * e.hw);
                        v.Add(MapPos(p)); c.Add(KindColour(Sample(p, Vector3.up, mouthY)) * 0.8f);
                        if (k > 0) { idx.Add(start + k - 1); idx.Add(start + k); }
                    }
                    continue;
                }
                // tube: floor centre line + two wall lines at mid height, coloured by the deposits there
                var dir = math.normalizesafe(e.b - e.a);
                var side = math.normalizesafe(math.cross(new double3(0, 1, 0), dir));
                int steps = Mathf.Clamp(Mathf.CeilToInt(len / 12f), 1, 60);
                for (int line = 0; line < 3; line++)
                {
                    int start = v.Count;
                    for (int k = 0; k <= steps; k++)
                    {
                        var p = math.lerp(e.a, e.b, k / (double)steps);
                        double3 q; Vector3 nrm;
                        if (line == 0) { q = p + new double3(0, 0.5, 0); nrm = Vector3.up; }
                        else { float s = line == 1 ? -1 : 1; q = p + side * (e.hw * s) + new double3(0, e.hv * 0.55, 0); nrm = (Vector3)(float3)(-side * s); }
                        v.Add(MapPos(q)); c.Add(KindColour(Sample(q, nrm, mouthY)) * (line == 0 ? 1f : 0.55f));
                        if (k > 0) { idx.Add(start + k - 1); idx.Add(start + k); }
                    }
                }
                // a few ribs to read as a tube
                for (int k = 0; k <= steps; k += 3)
                {
                    var p = math.lerp(e.a, e.b, k / (double)steps);
                    int start = v.Count;
                    for (int r = 0; r <= 8; r++)
                    {
                        float a = r * Mathf.PI / 8f;
                        var q = p + side * (e.hw * Mathf.Cos(a)) + new double3(0, e.hv * 0.55 + e.hv * Mathf.Sin(a), 0);
                        v.Add(MapPos(q)); c.Add(new Color(0.1f, 0.25f, 0.35f));
                        if (r > 0) { idx.Add(start + r - 1); idx.Add(start + r); }
                    }
                }
            }
            tubes.Clear();
            tubes.SetVertices(v); tubes.SetColors(c); tubes.SetIndices(idx, MeshTopology.Lines, 0);
            tubes.RecalculateBounds();
        }

        static RockDeposits.Kind Sample(double3 fp, Vector3 n, float mouthY) => RockDeposits.At((float3)fp, (float3)n, mouthY, out _);

        // ------------------------------------------------------------------ flow layout: what the crew would know
        // GIMLI-1 (rim rover, ground-penetrating radar) gives only a coarse estimate of the tubes within its radar reach;
        // GIMLI-2 drove its route with lidar, so the route and the tubes beside it are mapped precisely; your own rover
        // maps what its lamps / lidar see as it goes. Everything else is unknown (not drawn).
        public const float RadarReach = 700f, Lidar = 80f, RoverSees = 120f;
        static readonly Color RouteCol = new Color(0.37f, 0.9f, 1f), RadarCol = new Color(0.45f, 0.6f, 0.8f), Gimli1Col = new Color(0.72f, 0.65f, 1f),
                              TalusCol = new Color(0.56f, 0.75f, 0.62f), ShaftCol = new Color(0.79f, 0.6f, 0.71f);
        public int MappedCount { get; private set; }
        public int RadarCount { get; private set; }

        void BuildFlow(NavTracker nav)
        {
            var F = game.Cave.Network.Flow; var net = game.Cave.Network;
            float mouthY = game.Cave.Field.mouthFloorY;
            // where the route and your track have been (40 m cells)
            var routeCells = new HashSet<int2>(); var trackCells = new HashSet<int2>();
            int2 Cell(float3 p) => (int2)math.floor(p.xz / 40f);
            if (game.Route != null) foreach (var p in game.Route.points) routeCells.Add(Cell(p));
            for (int i = 0; i < nav.Track.Count; i += 3) trackCells.Add(Cell((float3)nav.Track[i]));
            bool Near(float3[] pts, HashSet<int2> cells, float range)
            {
                int r = (int)math.ceil(range / 40f);
                foreach (var p in pts)
                {
                    var c = Cell(p);
                    for (int dx = -r; dx <= r; dx++) for (int dz = -r; dz <= r; dz++) if (cells.Contains(c + new int2(dx, dz))) return true;
                }
                return false;
            }
            float3 g1 = game.GimliRoverFrame.HasValue ? (float3)game.GimliRoverFrame.Value : new float3(net.origin.x, net.rimY, net.origin.y);
            float dMin = F.FlowDepth(0) - 5f, dMax = F.FlowDepth(F.L.flows.Count - 1) + 30f;
            var v = new List<Vector3>(); var c = new List<Color>(); var idx = new List<int>();
            var lv = new List<Vector3>(); var lc = new List<Color>(); var li = new List<int>();
            void Line(double3 a, double3 b, Color col) { li.Add(lv.Count); lv.Add(MapPos(a)); lc.Add(col); li.Add(lv.Count); lv.Add(MapPos(b)); lc.Add(col); }
            var light = math.normalize(new float3(0.4f, 0.85f, 0.3f));
            int mapped = 0, radar = 0;
            for (int k = 0; k < F.L.edges.Count; k++)
            {
                var e = F.L.edges[k]; var pts = F.WayPts[k];
                if (pts == null || pts.Length < 2) continue;
                bool isMapped = e.a == 0 || e.b == 0 || Near(pts, routeCells, Lidar) || Near(pts, trackCells, RoverSees);
                if (isMapped)
                {
                    mapped++;
                    // a low-poly arch at the tube's real width and height, basalt grey (lighter near the surface)
                    float hw = (float)e.w / 2, h = (float)e.h; const int RS = 7; int start = v.Count;
                    for (int i = 0; i < pts.Length; i++)
                    {
                        var q0 = pts[math.max(0, i - 1)]; var q1 = pts[math.min(pts.Length - 1, i + 1)];
                        var t = math.normalizesafe(new float3(q1.x - q0.x, 0, q1.z - q0.z), new float3(1, 0, 0));
                        var side = new float3(-t.z, 0, t.x);
                        float depth = net.rimY - pts[i].y, td = math.saturate((depth - dMin) / math.max(1f, dMax - dMin));
                        var baseCol = Color.Lerp(new Color(0.77f, 0.75f, 0.72f), new Color(0.35f, 0.37f, 0.41f), td);
                        for (int j = 0; j <= RS; j++)
                        {
                            float a = j / (float)RS * math.PI * 2f, ca = math.cos(a), sa = math.sin(a);
                            var pos = pts[i] + side * (ca * hw) + new float3(0, (sa + 1f) * h * 0.5f, 0);
                            var nrm = math.normalize(side * ca * h + new float3(0, sa * hw, 0));
                            float shade = 0.45f + 0.55f * math.abs(math.dot(nrm, light));
                            float jit = 0.85f + 0.25f * ((math.hash(new int3(k, i, j)) % 1000) / 1000f);
                            v.Add(MapPos((double3)pos)); c.Add(baseCol * (shade * jit));
                            if (i > 0 && j < RS) { int A = start + (i - 1) * (RS + 1) + j, B = start + i * (RS + 1) + j; idx.Add(A); idx.Add(B); idx.Add(A + 1); idx.Add(B); idx.Add(B + 1); idx.Add(A + 1); }
                        }
                    }
                }
                else
                {
                    bool inRadar = false; foreach (var p in pts) if (math.distance(p.xz, g1.xz) < RadarReach) { inRadar = true; break; }
                    if (!inRadar) continue;
                    radar++;
                    // radar estimate: a coarse, slightly wrong outline through the ends and the middle
                    var q = new[] { pts[0], pts[pts.Length / 2], pts[pts.Length - 1] };
                    for (int i = 0; i < 3; i++)
                    {
                        uint hsh = math.hash(new int2(k, i));
                        q[i] += new float3(((hsh & 255) / 255f - 0.5f) * 30f, (((hsh >> 8) & 255) / 255f - 0.5f) * 12f + (float)e.h * 0.5f, (((hsh >> 16) & 255) / 255f - 0.5f) * 30f);
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        var dir = math.normalizesafe(new float3(q[i + 1].x - q[i].x, 0, q[i + 1].z - q[i].z)); var side = new float3(-dir.z, 0, dir.x) * ((float)e.w / 2);
                        Line(q[i], q[i + 1], RadarCol); Line(q[i] + side, q[i + 1] + side, RadarCol * 0.6f); Line(q[i] - side, q[i + 1] - side, RadarCol * 0.6f);
                    }
                }
            }
            MappedCount = mapped; RadarCount = radar;
            solidTubes.Clear(); solidTubes.SetVertices(v); solidTubes.SetColors(c); solidTubes.SetTriangles(idx, 0); solidTubes.RecalculateBounds();
            // GIMLI-2's route on the floor
            if (game.Route != null) for (int i = 1; i < game.Route.points.Count; i++) Line(game.Route.points[i - 1] + new float3(0, 2.5f, 0), game.Route.points[i] + new float3(0, 2.5f, 0), RouteCol);
            // GIMLI-2, GIMLI-1 and the reach of its radar
            if (game.ScoutFrame.HasValue) Diamond(game.ScoutFrame.Value + new double3(0, 14, 0), 18f, RouteCol, Line);
            Box(g1 + new float3(0, 7, 0), 12f, Gimli1Col, Line);
            for (int k = 0; k < 64; k++)
            {
                if (k % 2 == 1) continue;   // dashed
                float a0 = k / 64f * math.PI * 2f, a1 = (k + 1) / 64f * math.PI * 2f;
                Line(new double3(g1.x + math.cos(a0) * RadarReach, g1.y + 1, g1.z + math.sin(a0) * RadarReach), new double3(g1.x + math.cos(a1) * RadarReach, g1.y + 1, g1.z + math.sin(a1) * RadarReach), Gimli1Col * 0.7f);
            }
            // collapses: talus ramps (sage) and shafts (rose)
            foreach (var rp in F.RampPath) if (rp != null) for (int i = 1; i < rp.Length; i++) Line(rp[i - 1] + new float3(0, 3, 0), rp[i] + new float3(0, 3, 0), TalusCol);
            foreach (var sh in F.Shafts)
            {
                Line(sh.top, sh.top - new float3(0, sh.drop, 0), ShaftCol);
                for (int k = 0; k < 12; k++) { float a0 = k / 12f * math.PI * 2f, a1 = (k + 1) / 12f * math.PI * 2f; Line(sh.top + new float3(math.cos(a0), 0, math.sin(a0)) * sh.radius, sh.top + new float3(math.cos(a1), 0, math.sin(a1)) * sh.radius, ShaftCol); }
            }
            flowLines.Clear(); flowLines.SetVertices(lv); flowLines.SetColors(lc); flowLines.SetIndices(li, MeshTopology.Lines, 0); flowLines.RecalculateBounds();
            tubes.Clear();
        }

        static void Diamond(double3 p, float r, Color col, System.Action<double3, double3, Color> line)
        {
            var pts = new[] { new double3(r, 0, 0), new double3(-r, 0, 0), new double3(0, 0, r), new double3(0, 0, -r) };
            foreach (var q in pts) { line(p + new double3(0, r, 0), p + q, col); line(p - new double3(0, r, 0), p + q, col); }
            line(p + pts[0], p + pts[2], col); line(p + pts[2], p + pts[1], col); line(p + pts[1], p + pts[3], col); line(p + pts[3], p + pts[0], col);
        }

        static void Box(float3 p, float r, Color col, System.Action<double3, double3, Color> line)
        {
            for (int k = 0; k < 4; k++)
            {
                float a0 = k * math.PI / 2f + math.PI / 4f, a1 = a0 + math.PI / 2f;
                var b0 = p + new float3(math.cos(a0), 0, math.sin(a0)) * r; var b1 = p + new float3(math.cos(a1), 0, math.sin(a1)) * r;
                line(b0 - new float3(0, r * 0.5f, 0), b1 - new float3(0, r * 0.5f, 0), col); line(b0 + new float3(0, r * 0.5f, 0), b1 + new float3(0, r * 0.5f, 0), col);
                line(b0 - new float3(0, r * 0.5f, 0), b0 + new float3(0, r * 0.5f, 0), col);
            }
        }

        /// <summary>A label at a frame point on the map (screen rect of the drawn map texture).</summary>
        void MapLabel(Rect r, double3 frame, string text, Color col)
        {
            var sp = mapCam.WorldToScreenPoint(MapWorld(frame));
            if (sp.z <= 0) return;
            float scale = Mathf.Max(r.width / rt.width, r.height / rt.height);
            float ox = r.x + (r.width - rt.width * scale) / 2f, oy = r.y + (r.height - rt.height * scale) / 2f;
            float x = ox + sp.x * scale, y = oy + (rt.height - sp.y) * scale;
            if (x < r.x || x > r.xMax || y < r.y || y > r.yMax) return;
            var prev = GUI.color; GUI.color = new Color(0, 0, 0, 0.75f);
            GUI.Label(new Rect(x - 99, y - 33, 200, 24), text, labelStyle); GUI.color = col;
            GUI.Label(new Rect(x - 100, y - 34, 200, 24), text, labelStyle); GUI.color = prev;
        }
        GUIStyle labelStyle;

        static readonly Color TagColour = new Color(0.45f, 1f, 0.4f);
        int builtTags = -1;

        void BuildTrackAndMarks(NavTracker nav)
        {
            var v = new List<Vector3>(); var c = new List<Color>(); var idx = new List<int>();
            for (int i = 0; i < nav.Track.Count; i++)
            {
                v.Add(MapPos(nav.Track[i] + new double3(0, 1.5, 0))); c.Add(new Color(1f, 1f, 1f));
                if (i > 0) { idx.Add(i - 1); idx.Add(i); }
            }
            track.Clear(); track.SetVertices(v); track.SetColors(c); track.SetIndices(idx, MeshTopology.Lines, 0); track.RecalculateBounds();

            v.Clear(); c.Clear(); idx.Clear();
            void Seg(double3 a, double3 b, Color col) { idx.Add(v.Count); v.Add(MapPos(a)); c.Add(col); idx.Add(v.Count); v.Add(MapPos(b)); c.Add(col); }
            void Cross(double3 p, float r, Color col) { Seg(p - new double3(r, 0, 0), p + new double3(r, 0, 0), col); Seg(p - new double3(0, 0, r), p + new double3(0, 0, r), col); Seg(p - new double3(0, r, 0), p + new double3(0, r, 0), col); }
            // the pit: rim + floor circles and four verticals
            var f = game.Cave.Field;
            for (int k = 0; k < 32; k++)
            {
                float a0 = k * Mathf.PI / 16f, a1 = (k + 1) * Mathf.PI / 16f;
                foreach (double y in new double[] { f.rim, f.pitFloor })
                    Seg(new double3(Mathf.Cos(a0) * f.hf.innerA, y, Mathf.Sin(a0) * f.hf.innerB), new double3(Mathf.Cos(a1) * f.hf.innerA, y, Mathf.Sin(a1) * f.hf.innerB), new Color(1f, 0.8f, 0.5f));
            }
            for (int k = 0; k < 4; k++) { float a = k * Mathf.PI / 2f; Seg(new double3(Mathf.Cos(a) * f.hf.innerA, f.rim, Mathf.Sin(a) * f.hf.innerB), new double3(Mathf.Cos(a) * f.hf.innerA, f.pitFloor, Mathf.Sin(a) * f.hf.innerB), new Color(0.6f, 0.45f, 0.3f)); }
            // collapse shafts near the track
            var seen = new HashSet<string>();
            for (int i = 0; i < nav.Track.Count; i += 10)
                if (game.Cave.Network.CollapseNear((float3)nav.Track[i], 60f, out var top, out float rs, out float drop) && seen.Add($"{top.x:0},{top.z:0}"))
                {
                    for (int k = 0; k < 16; k++) { float a0 = k * Mathf.PI / 8f, a1 = (k + 1) * Mathf.PI / 8f; Seg((double3)top + new double3(Mathf.Cos(a0) * rs, 0, Mathf.Sin(a0) * rs), (double3)top + new double3(Mathf.Cos(a1) * rs, 0, Mathf.Sin(a1) * rs), new Color(1f, 0.3f, 0.3f)); }
                    Seg((double3)top, (double3)top - new double3(0, drop, 0), new Color(0.6f, 0.15f, 0.15f));
                }
            // fixed lines
            if (game.Winch != null) foreach (var l in game.Winch.Rigs) { Seg(l.anchorA, l.anchorB, new Color(0.8f, 0.8f, 0.85f)); if (l.RopeHanging) Seg(l.roller, l.foot, new Color(1f, 0.5f, 0.1f)); }
            // deposits tagged "for the return trip": a green post with a cross on top
            if (game.Director != null)
                foreach (var t in game.Director.Tags)
                {
                    Seg(t.frame, t.frame + new double3(0, 14, 0), TagColour);
                    Cross(t.frame + new double3(0, 14, 0), 5f, TagColour);
                }
            // you
            Cross(WorldOrigin.WorldToFrame(game.Player.Focus.position), 12f, new Color(0.3f, 1f, 1f));
            marks.Clear(); marks.SetVertices(v); marks.SetColors(c); marks.SetIndices(idx, MeshTopology.Lines, 0); marks.RecalculateBounds();
        }

        /// <summary>Tests: render the map once to a texture.</summary>
        public RenderTexture RenderOnce(float3? focusFrame = null, float distance = -1f)
        {
            Init(); builtEdges = -1; builtTrack = -1; builtFlow = -1; Rebuild(); PlaceCamera();
            if (focusFrame.HasValue)
            {
                float d = distance > 0 ? distance : dist;
                var rot = Quaternion.Euler(pitch, yaw, 0);
                mapCam.transform.SetPositionAndRotation(MapWorld((double3)focusFrame.Value) - rot * Vector3.forward * d, rot);
            }
            bool was = mapCam.enabled; mapCam.enabled = true;
            mapCam.Render();
            mapCam.enabled = was;
            Debug = $"{tubes.vertexCount + solidTubes.vertexCount} tube verts ({MappedCount} mapped, {RadarCount} radar), bounds {tubes.bounds.center + root.position} size {tubes.bounds.size}, cam {mapCam.transform.position} fwd {mapCam.transform.forward}, mask {mapCam.cullingMask}, shader {holo.shader.name} ok {holo.shader.isSupported}";
            return rt;
        }

        void OnGUI()
        {
            if (!IsOpen || rt == null) return;
            legend ??= new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, normal = { textColor = new Color(0.8f, 0.95f, 1f) } };
            title ??= new GUIStyle(legend) { fontSize = 18, fontStyle = FontStyle.Bold };
            float W = Screen.width, H = Screen.height;
            var r = new Rect(W * 0.08f, H * 0.08f, W * 0.84f, H * 0.84f);
            GUI.depth = -50;
            GUI.DrawTexture(r, rt, ScaleMode.ScaleAndCrop, false);
            if (Flow)
            {
                labelStyle ??= new GUIStyle(legend) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                GUI.Label(new Rect(r.x + 12, r.y + 8, 760, 26), "TUBE MAP   drag: rotate   wheel: zoom   M / Esc: close", title);
                float yy = r.y + 40;
                foreach (var (label, col) in new[] { ("mapped tube (GIMLI-2 lidar, your rover)", new Color(0.7f, 0.69f, 0.66f)), ("GIMLI-1 radar estimate (within the dashed circle)", RadarCol),
                    ("GIMLI-2's route", RouteCol), ("your track (the route back)", Color.white), ("collapse: talus ramp (drivable)", TalusCol), ("collapse: shaft (winch)", ShaftCol),
                    ("fixed line (anchor kit)", new Color(1f, 0.5f, 0.1f)), ("deposit tagged for the return trip", TagColour) })
                {
                    var prev = GUI.color; GUI.color = col; GUI.DrawTexture(new Rect(r.x + 14, yy + 5, 22, 4), Texture2D.whiteTexture); GUI.color = prev;
                    GUI.Label(new Rect(r.x + 44, yy - 2, 460, 22), label, legend); yy += 20;
                }
                GUI.Label(new Rect(r.x + 14, yy + 4, 560, 22), $"{MappedCount} tubes mapped, {RadarCount} radar estimates. Unknown tubes are not shown.", legend);
                var f0 = game.Cave.Field;
                MapLabel(r, new double3(f0.hf.pitCentre.x, f0.rim + 30, f0.hf.pitCentre.y), "PIT", new Color(1f, 0.8f, 0.5f));
                if (game.GimliRoverFrame.HasValue) MapLabel(r, game.GimliRoverFrame.Value + new double3(0, 40, 0), "GIMLI-1 radar", Gimli1Col);
                if (game.ScoutFrame.HasValue) MapLabel(r, game.ScoutFrame.Value + new double3(0, 50, 0), "GIMLI-2", RouteCol);
                MapLabel(r, WorldOrigin.WorldToFrame(game.Player.Focus.position) + new double3(0, 30, 0), "YOU", new Color(0.3f, 1f, 1f));
                return;
            }
            GUI.Label(new Rect(r.x + 12, r.y + 8, 600, 26), "TUBE MAP (explored)   drag: rotate   wheel: zoom   M / Esc: close", title);
            float y = r.y + 40;
            foreach (var (k, label, c) in Legend)
            {
                var prev = GUI.color; GUI.color = c; GUI.DrawTexture(new Rect(r.x + 14, y + 5, 22, 4), Texture2D.whiteTexture); GUI.color = prev;
                GUI.Label(new Rect(r.x + 44, y - 2, 420, 22), label, legend); y += 20;
            }
            foreach (var (label, c) in new[] { ("your track (the route back)", Color.white), ("you", new Color(0.3f, 1f, 1f)), ("pit", new Color(1f, 0.8f, 0.5f)), ("collapse shaft", new Color(1f, 0.3f, 0.3f)), ("fixed line (anchor kit)", new Color(1f, 0.5f, 0.1f)), ("deposit tagged for the return trip", TagColour) })
            {
                var prev = GUI.color; GUI.color = c; GUI.DrawTexture(new Rect(r.x + 14, y + 5, 22, 4), Texture2D.whiteTexture); GUI.color = prev;
                GUI.Label(new Rect(r.x + 44, y - 2, 420, 22), label, legend); y += 20;
            }
            GUI.Label(new Rect(r.x + 14, y + 4, 520, 40), "Ilmenite / iron-metal glints: tiny, everywhere in fresh basalt (not mapped).", legend);
        }
    }
}
