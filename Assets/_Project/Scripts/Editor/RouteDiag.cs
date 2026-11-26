using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MoonBaseAlpha.Cave;

namespace MoonBaseAlpha.Editor
{
    /// <summary>Debug: what is at the steep / floorless spots of a golden route (prims + floor profile).</summary>
    public static class RouteDiag
    {
        /// <summary>Tracked real floor along a straight link: worst 8 m slope and longest no-floor run.</summary>
        public static float3 WorstAt;
        public static string Profile;
        public static void LinkFloor(TunnelNetwork net, float3[] path, out float worst, out int nan)
        {
            worst = 0; nan = 0; WorstAt = default; int wi = 0;
            float prevY = float.NaN; var ys = new List<float>(); var ps = new List<float3>();
            for (int seg = 1; seg < path.Length; seg++)
            for (int s = 0, n = Mathf.Max(2, Mathf.CeilToInt(math.distance(path[seg - 1].xz, path[seg].xz) / 4f)); s <= n; s++)
            {
                var q = math.lerp(path[seg - 1], path[seg], s / (float)n);
                float y = float.NaN;
                if (!float.IsNaN(prevY)) { var qq = new float3(q.x, prevY, q.z); y = net.FloorY(qq, 3f, 6f); if (float.IsNaN(y)) y = net.FloorY(qq, 6f, 9f); }
                if (float.IsNaN(y)) y = net.FloorY(q, 10f, 16f);
                if (float.IsNaN(y)) nan++; else prevY = y;
                ys.Add(y); ps.Add(q);
            }
            for (int i = 0; i + 2 < ys.Count; i++)
                if (!float.IsNaN(ys[i]) && !float.IsNaN(ys[i + 2]))
                {
                    float dg = math.degrees(math.atan(math.abs(ys[i + 2] - ys[i]) / math.max(1f, math.distance(ps[i].xz, ps[i + 2].xz))));
                    if (dg > worst) { worst = dg; WorstAt = ps[i]; wi = i; }
                }
            var pb = new StringBuilder();
            for (int i = math.max(0, wi - 6); i < math.min(ys.Count, wi + 8); i++) pb.Append($"[{ps[i].x:0},{ps[i].z:0} nom {ps[i].y:0.0} floor {ys[i]:0.0}] ");
            Profile = pb.ToString();
        }

        public static void Links()
        {
            var sb = new StringBuilder();
            EditorSceneManager.OpenScene("Assets/_Project/Scenes/Main.unity");
            var game = Object.FindAnyObjectByType<GameBootstrap>();
            game.Initialise();
            var b0 = game.Cave.Network;
            var args = System.Environment.GetCommandLineArgs();
            int ai = System.Array.IndexOf(args, "-diagSeed");
            uint seed = ai >= 0 ? Mission.SeedFromCode(args[ai + 1]) : b0.seed;
            var net = new TunnelNetwork(seed, b0.origin, b0.dirU, game.Cave.Field.rim, b0.level0Floor, math.distance(b0.mouthFloor.xz, b0.origin)) { surfaceAt = b0.surfaceAt, maxLevels = b0.maxLevels };
            var links = new List<(int3 to, byte kind)>();
            int[] bad = new int[2], tot = new int[2];
            for (int L = 0; L < 4; L++)
            for (int i = 0; i < 10; i++)
            for (int j = -3; j <= 3; j++)
            {
                links.Clear(); net.Links(L, i, j, links);
                var pa = net.NodePos(L, i, j);
                foreach (var (to, kind) in links)
                {
                    LinkFloor(net, net.LinkPath(new int3(L, i, j), to), out float worst, out int nan);
                    int k = kind == 1 ? 1 : 0;
                    tot[k]++;
                    bool fail = worst > 30f || nan > 0;
                    if (fail) bad[k]++;
                    if (fail && bad[k] <= 25) sb.AppendLine($"{(k == 1 ? "RAMP" : "link")} L{L} ({i},{j}) -> L{to.x} ({to.y},{to.z}): worst {worst:0} deg at {WorstAt}, {nan} no-floor samples");
                    if (fail && k == 1 && bad[k] <= 2)
                    {
                        sb.AppendLine("   profile: " + Profile);
                        var buf = new List<TunnelPrim>();
                        net.Collect(WorstAt - new float3(30, 30, 30), WorstAt + new float3(30, 30, 30), 2f, buf);
                        foreach (var p in buf) sb.AppendLine($"   {(p.type == 0 ? "Tube" : p.type == 1 ? "Chamber" : "Shaft")} L{p.level} kind{p.kind} a {p.a} b {p.b} hw {p.hw:0.0}/{p.hwEnd:0.0} hv {p.hv:0.0} drop {p.drop:0.0} mound {p.moundH:0.0}@{p.moundT:0.00} dead {p.deadEnd}");
                    }
                }
            }
            sb.Insert(0, $"links failing (worst 8 m slope > 30 deg or no floor): flat {bad[0]}/{tot[0]}, level ramps {bad[1]}/{tot[1]}\n\n");
            Directory.CreateDirectory("Docs/verification"); File.WriteAllText("Docs/verification/link_diag.md", sb.ToString());
            Debug.Log("[RouteDiag] links done");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        public static void Run()
        {
            var sb = new StringBuilder();
            EditorSceneManager.OpenScene("Assets/_Project/Scenes/Main.unity");
            var game = Object.FindAnyObjectByType<GameBootstrap>();
            game.Initialise();
            var b = game.Cave.Network;
            var cases = new (string code, int level, float3 at)[]
            {
                ("MOON", 3, new float3(-1277, -59, -114)),
            };
            foreach (var (code, level, at) in cases)
            {
                uint seed = code == "" ? b.seed : Mission.SeedFromCode(code);
                var net = new TunnelNetwork(seed, b.origin, b.dirU, game.Cave.Field.rim, b.level0Floor, math.distance(b.mouthFloor.xz, b.origin)) { surfaceAt = b.surfaceAt, maxLevels = b.maxLevels };
                var r = net.GoldenPath(level);
                sb.AppendLine($"## {(code == "" ? "default" : code)} level {level} at {at}");
                // nearest route segment
                int seg = 0; float best = 1e9f;
                for (int k = 0; k < r.points.Count - 1; k++)
                {
                    var a = r.points[k]; var c = r.points[k + 1]; var ab = c - a;
                    float t = math.saturate(math.dot(at - a, ab) / math.lengthsq(ab));
                    float d = math.distance(at.xz, (a + ab * t).xz);
                    if (d < best) { best = d; seg = k; }
                }
                sb.AppendLine($"route seg {seg}: {r.points[seg]} -> {r.points[seg + 1]} (levels {r.pointLevels[seg]} -> {r.pointLevels[seg + 1]}), {best:0.0} m off");
                var buf = new List<TunnelPrim>();
                net.Collect(at - new float3(40, 40, 40), at + new float3(40, 40, 40), 2f, buf);
                foreach (var p in buf.OrderBy(p => p.level))
                {
                    string ty = p.type == 0 ? "Tube" : p.type == 1 ? "Chamber" : "Shaft";
                    sb.AppendLine($"  {ty} L{p.level} kind{p.kind} a {p.a} b {p.b} hw {p.hw:0.0}/{p.hwEnd:0.0} hv {p.hv:0.0} drop {p.drop:0.0}@{p.dropT:0.00} mound {p.moundH:0.0}@{p.moundT:0.00} r{p.moundR:0} dead {p.deadEnd}");
                }
                // floor profile along the segment around the spot
                var a0 = r.points[seg]; var a1 = r.points[seg + 1];
                float len = math.distance(a0.xz, a1.xz);
                float t0 = math.saturate(math.dot((at - a0).xz, (a1 - a0).xz) / (len * len));
                sb.Append("  profile (s, nominal y, FloorY 10/16): ");
                for (float s = -24; s <= 24; s += 2)
                {
                    float t = math.saturate(t0 + s / len);
                    var q = math.lerp(a0, a1, t);
                    sb.Append($"[{s:0}: {q.y:0.0} {net.FloorY(q, 10f, 16f):0.0}] ");
                }
                sb.AppendLine();
            }
            Directory.CreateDirectory("Docs/verification"); File.WriteAllText("Docs/verification/route_diag.md", sb.ToString());
            Debug.Log("[RouteDiag] done");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
