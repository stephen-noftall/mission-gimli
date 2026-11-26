using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha.Buggy
{
    /// <summary>
    /// The rover console's three small screens under the dials (round 8): DRIVE (speed, heading, pitch/roll, battery),
    /// NAV (your track + explored tubes, north up, rover arrow) and SYS (power, temperatures, comms delay, kits) in tiny
    /// text. Drawn on the CPU into 160x100 textures at ~5 Hz with a built-in 3x5 pixel font: no cameras, no extra
    /// draw passes. Emissive but dim, so they read at night exposure without lighting the cabin.
    /// </summary>
    public class CockpitScreens : MonoBehaviour
    {
        public const int W = 160, H = 100;
        public const float Rate = 5f;
        public BuggyController rover;
        public System.Func<Cave.NavTracker> nav;
        public System.Func<RoverWinch> winch;

        readonly Texture2D[] tex = new Texture2D[3];
        readonly Color32[][] px = new Color32[3][];
        public Texture2D Screen(int i) => tex[i];
        float next;
        public int Updates { get; private set; }

        static readonly Color32 Bg = new(4, 10, 8, 255), Fg = new(120, 255, 170, 255), Dim = new(40, 110, 70, 255),
                                Amber = new(255, 190, 60, 255), Red = new(255, 80, 60, 255), Blue = new(110, 190, 255, 255);

        /// <summary>Build the three screens on the console face (local frame of the console box).</summary>
        public void Build(Transform console)
        {
            for (int i = 0; i < 3; i++)
            {
                tex[i] = new Texture2D(W, H, TextureFormat.RGBA32, false) { name = $"CockpitScreen{i}", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                px[i] = new Color32[W * H];
                var m = Mats.Emissive(Mats.Make($"CockpitScreen{i}", Color.black, 0f, 0.8f), new Color(0.42f, 0.42f, 0.42f), tex[i]);
                m.SetColor("_BaseColor", new Color(0.05f, 0.05f, 0.05f));
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.DestroyImmediate(q.GetComponent<Collider>());   // at once: a concave collider must not live a frame on the rover's dynamic body
                q.name = $"Screen{i}";
                q.transform.SetParent(console, false);
                // lower half of the console face, three side by side (console box is 0.46 x 0.26 m, face at z -0.5)
                q.transform.localPosition = new Vector3(-0.31f + i * 0.31f, -0.27f, -0.56f);
                q.transform.localScale = new Vector3(0.29f, 0.33f, 1f);
                q.GetComponent<MeshRenderer>().sharedMaterial = m;
                q.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            Redraw();
        }

        void Update()
        {
            if (tex[0] == null || rover == null) return;
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 1f / Rate;
            Redraw();
        }

        public void Redraw()
        {
            if (tex[0] == null || rover == null) return;
            DrawDrive(px[0]); DrawNav(px[1]); DrawSys(px[2]);
            for (int i = 0; i < 3; i++) { tex[i].SetPixels32(px[i]); tex[i].Apply(false); }
            Updates++;
        }

        // ------------------------------------------------------------------ pages

        void DrawDrive(Color32[] p)
        {
            Clear(p);
            Text(p, 3, H - 8, "DRIVE", Dim);
            var up = -TerrainFrame.DownAt(rover.transform.position);
            var fwd = Vector3.ProjectOnPlane(rover.transform.forward, up);
            float heading = (Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg + 360f) % 360f;
            float kmh = Mathf.Abs(rover.Speed) * 3.6f;
            Text(p, 6, H - 34, $"{kmh:0.0}", Fg, 4);
            Text(p, 6 + 4 * 4 * 4 + 8, H - 34, "KM/H", Dim);
            Text(p, 3, 44, $"HDG {heading:000}", Fg, 2);
            float pitch = 90f - Vector3.Angle(rover.transform.forward, up);
            float roll = 90f - Vector3.Angle(rover.transform.right, up);
            Text(p, 3, 30, $"PITCH {pitch:+0;-0;0}", Fg);
            Text(p, 3, 22, $"ROLL  {roll:+0;-0;0}", Fg);
            // battery bar
            float soc = Mathf.Clamp01(rover.stateOfCharge);
            Text(p, 3, 10, $"BATT {soc * 100:0}%", soc > 0.2f ? Fg : Red);
            Rect(p, 70, 9, 86, 7, Dim, false);
            Rect(p, 71, 10, Mathf.RoundToInt(84 * soc), 5, soc > 0.2f ? Fg : Red, true);
            // artificial horizon (small)
            int cx = 130, cy = 36;
            float rr = roll * Mathf.Deg2Rad;
            for (int k = -16; k <= 16; k++) Set(p, cx + Mathf.RoundToInt(k * Mathf.Cos(rr)), cy + Mathf.RoundToInt(-k * Mathf.Sin(rr) - pitch * 0.4f), Blue);
            Set(p, cx, cy, Amber); Set(p, cx - 1, cy, Amber); Set(p, cx + 1, cy, Amber);
            if (rover.Damaged) Text(p, 70, H - 8, "DAMAGED", Red);
            // drive range (P2) and the edge assist (S5)
            var drv = rover.GetComponent<BuggyDriver>();
            bool fast = rover.RangeMode == BuggyController.DriveRange.Fast;
            Text(p, 112, H - 8, fast ? "FAST" : "SLOW", fast ? Amber : Fg);
            if (drv != null && drv.AssistLabel != null) Text(p, 3, 2, drv.AssistLabel, Amber);
        }

        void DrawNav(Color32[] p)
        {
            Clear(p);
            Text(p, 3, H - 8, "NAV", Dim);
            var n = nav?.Invoke();
            var f = WorldOrigin.WorldToFrame(rover.transform.position);
            const float mPerPx = 4f;
            int cx = W / 2, cy = H / 2 - 4;
            if (n != null)
            {
                foreach (var e in n.KnownEdges)
                    Line(p, cx + (float)((e.a.x - f.x) / mPerPx), cy + (float)((e.a.z - f.z) / mPerPx), cx + (float)((e.b.x - f.x) / mPerPx), cy + (float)((e.b.z - f.z) / mPerPx), e.level == 0 ? Dim : new Color32(30, 70, 110, 255));
                var tr = n.Track;
                for (int i = Mathf.Max(1, tr.Count - 1500); i < tr.Count; i++)
                    Line(p, cx + (float)((tr[i - 1].x - f.x) / mPerPx), cy + (float)((tr[i - 1].z - f.z) / mPerPx), cx + (float)((tr[i].x - f.x) / mPerPx), cy + (float)((tr[i].z - f.z) / mPerPx), Fg);
            }
            // pit
            int px0 = cx + Mathf.RoundToInt((float)(-f.x / mPerPx)), py0 = cy + Mathf.RoundToInt((float)(-f.z / mPerPx));
            for (int a = 0; a < 24; a++) Set(p, px0 + Mathf.RoundToInt(6 * Mathf.Cos(a * 0.26f)), py0 + Mathf.RoundToInt(6 * Mathf.Sin(a * 0.26f)), Amber);
            // fixed lines
            var w = winch?.Invoke();
            if (w != null) foreach (var l in w.Rigs) { int lx = cx + (int)((l.roller.x - f.x) / mPerPx), ly = cy + (int)((l.roller.z - f.z) / mPerPx); Rect(p, lx - 1, ly - 1, 3, 3, Red, true); }
            // rover arrow
            var up = -TerrainFrame.DownAt(rover.transform.position);
            var fwd = Vector3.ProjectOnPlane(rover.transform.forward, up).normalized;
            for (int k = 0; k < 7; k++) Set(p, cx + Mathf.RoundToInt(fwd.x * k), cy + Mathf.RoundToInt(fwd.z * k), Amber);
            Rect(p, cx - 1, cy - 1, 3, 3, Amber, true);
            Text(p, W - 10, H - 8, "N", Dim);
            Text(p, 3, 3, $"{mPerPx * 40:0} M", Dim);
            Line(p, 3, 10, 43, 10, Dim);
        }

        void DrawSys(Color32[] p)
        {
            Clear(p);
            Text(p, 3, H - 8, "SYS", Dim);
            float kw = rover.PowerDraw / 1000f;
            float motorT = 20f + Mathf.Clamp(kw, 0, 9) * 6f;
            var w = winch?.Invoke();
            var lines = new List<(string, Color32)>
            {
                ($"PWR  {kw:0.00} KW", Fg),
                ($"BATT {rover.stateOfCharge * 100:0.0}%  {rover.batteryCapacityWh * rover.stateOfCharge / 1000f:0.0} KWH", Fg),
                ($"TRIP {rover.DistanceM / 1000.0:0.00} KM", Fg),
                ($"MOTORS {motorT:0} C", motorT > 60 ? Amber : Fg),
                ($"AVIONICS {18 + kw:0} C", Fg),
                ($"COMMS DELAY {Radio.RoundTrip:0.0} S", Fg),
                (w != null ? $"ROPES {w.Ropes}/{RoverWinch.RopesPerMission}  RIGS {w.RigKits}" : "ROPES -", Fg),
                (rover.Damaged ? "SUSP FAULT  SPEED LIM" : "SUSP OK", rover.Damaged ? Red : Fg),
            };
            int y = H - 18;
            foreach (var (s, c) in lines) { Text(p, 3, y, s, c); y -= 10; }
        }

        // ------------------------------------------------------------------ drawing

        static void Clear(Color32[] p) { for (int i = 0; i < p.Length; i++) p[i] = Bg; }
        static void Set(Color32[] p, int x, int y, Color32 c) { if (x >= 0 && y >= 0 && x < W && y < H) p[y * W + x] = c; }
        static void Rect(Color32[] p, int x, int y, int w, int h, Color32 c, bool fill)
        {
            for (int j = 0; j < h; j++) for (int i = 0; i < w; i++)
                if (fill || i == 0 || j == 0 || i == w - 1 || j == h - 1) Set(p, x + i, y + j, c);
        }
        static void Line(Color32[] p, float x0, float y0, float x1, float y1, Color32 c)
        {
            if ((x0 < 0 && x1 < 0) || (y0 < 0 && y1 < 0) || (x0 >= W && x1 >= W) || (y0 >= H && y1 >= H)) return;
            int n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0))), 1, 400);
            for (int k = 0; k <= n; k++) Set(p, Mathf.RoundToInt(Mathf.Lerp(x0, x1, k / (float)n)), Mathf.RoundToInt(Mathf.Lerp(y0, y1, k / (float)n)), c);
        }

        /// <summary>Draw text with the bottom-left corner at (x, y), pixel scale s.</summary>
        public static void Text(Color32[] p, int x, int y, string s, Color32 c, int scale = 1) => PixelFont.Draw(p, W, H, x, y, s, c, scale);
    }
}
