using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha
{
    /// <summary>
    /// Automated frame-time profile of the player (exe flag <c>-perfbench</c>): measures the start site and a tube
    /// with costs switched on/off one at a time (render resolution, lamp bounce, far tube ring, crew lamps), logs a
    /// table ("[PerfBench]" lines) and quits. Used to size the exhibit hardware and to find the top costs.
    /// </summary>
    public class PerfBench : MonoBehaviour
    {
        public GameBootstrap game;
        public float settle = 2f, measure = 6f;

        ProfilerRecorder drawCalls, batches, setPass, tris;
        readonly StringBuilder table = new();

        public static bool Requested => System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-perfbench") >= 0;

        void OnEnable()
        {
            drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            tris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        }

        void OnDisable() { drawCalls.Dispose(); batches.Dispose(); setPass.Dispose(); tris.Dispose(); }

        IEnumerator Start()
        {
            QualityPresets.SetFrameCap(false);
            Application.runInBackground = true;   // keep measuring when the window loses focus
            yield return new WaitForSecondsRealtime(15f);   // streaming + exposure settle
            table.AppendLine("| case | fps | frame ms | 1% ms | GPU ms | draw calls | batches | setpass | tris (k) | lights |");
            table.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            yield return Case("start, 1080p FSR");
            yield return Native("start, native res");

            // tyre tracks: the crane pad (cargo-lander tracks + GIMLI-1's rim traverses in view), then with every track hidden
            game.Teleport(1);
            yield return new WaitForSecondsRealtime(6f);
            yield return Case("crane pad, tracks in view");
            var trackRenderers = new List<Renderer>();
            foreach (var r in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                if (r.sharedMaterial == TyreTracks.Mat) trackRenderers.Add(r);
            foreach (var r in trackRenderers) r.enabled = false;
            yield return Case($"crane pad, {trackRenderers.Count} track meshes hidden");
            foreach (var r in trackRenderers) r.enabled = true;
            // live tracks: drive 10 s on regolith, CPU cost of laying them
            var drv = game.Buggy.GetComponent<Buggy.BuggyDriver>(); bool drvOn = drv.enabled; drv.enabled = false;
            float tEnd = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < tEnd) { game.Buggy.throttle = 0.6f; game.Buggy.brake = 0; game.Buggy.parkingBrake = false; game.Buggy.steering = 0.2f; yield return null; }
            game.Buggy.throttle = 0; game.Buggy.brake = 1; drv.enabled = drvOn;
            table.AppendLine($"| live tyre tracks: {game.Tracks.LaidMetres:0} m laid, {game.Tracks.ChunkCount} chunks, CPU {TyreTracks.AvgMs:0.000} ms/frame | | | | | | | | | |");

            game.Teleport(2);
            yield return new WaitForSecondsRealtime(8f);
            yield return Case("tube, 1080p FSR");
            yield return Native("tube, native res");
            LampBounce.LegacyPointLights = true;
            yield return Case("tube, old point-light bounce");
            LampBounce.LegacyPointLights = false;
            game.FarTunnels.SetChunksVisible(false);
            yield return Case("tube, far ring hidden");
            game.FarTunnels.SetChunksVisible(true);
            var heads = game.Buggy.GetComponent<Crew.CrewHeads>();
            heads?.SetLamps(false);
            yield return Case("tube, crew lamps off");
            game.SetLights(false);
            yield return Case("tube, all lamps off");
            game.SetLights(true); heads?.SetLamps(true);

            Debug.Log("[PerfBench] results (" + Screen.width + "x" + Screen.height + ", quality " + game.Quality.level + ")\n" + table);
            yield return new WaitForSecondsRealtime(0.5f);
            Application.Quit();
        }

        IEnumerator Native(string name)
        {
            QualityPresets.ForceNative = true; game.Quality.Apply();
            yield return Case(name);
            QualityPresets.ForceNative = false; game.Quality.Apply();
        }

        IEnumerator Case(string name)
        {
            yield return new WaitForSecondsRealtime(settle);
            var ms = new List<float>(); double gpu = 0; int gpuN = 0;
            long dc = 0, bt = 0, sp = 0, tr = 0; int n = 0;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < measure)
            {
                yield return null;
                ms.Add(Time.unscaledDeltaTime * 1000f);
                if (PerfStats.GpuMs > 0) { gpu += PerfStats.GpuMs; gpuN++; }
                dc += drawCalls.LastValue; bt += batches.LastValue; sp += setPass.LastValue; tr += tris.LastValue; n++;
            }
            float avg = 0; foreach (var m in ms) avg += m; avg /= Mathf.Max(1, ms.Count);
            ms.Sort();
            float worst = ms.Count > 0 ? ms[Mathf.Min(ms.Count - 1, (int)(ms.Count * 0.99f))] : 0;
            int lights = 0;
            foreach (var pl in PhysicalLight.All) if (pl != null && pl.Light != null && pl.Light.enabled && pl.Light.type != LightType.Directional) lights++;
            n = Mathf.Max(1, n);
            string row = $"| {name} | {1000f / Mathf.Max(avg, 0.01f):0} | {avg:0.0} | {worst:0.0} | {(gpuN > 0 ? (gpu / gpuN).ToString("0.0") : "n/a")} | {dc / n} | {bt / n} | {sp / n} | {tr / n / 1000} | {lights} |";
            table.AppendLine(row);
            Debug.Log("[PerfBench] " + row);
        }
    }
}
