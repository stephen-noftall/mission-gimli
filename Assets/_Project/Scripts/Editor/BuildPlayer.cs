using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MoonBaseAlpha.Editor
{
    /// <summary>
    /// Builds the Windows x64 player to Builds/MoonBaseAlpha/MoonBaseAlpha.exe (keep the folder together:
    /// the exe needs MoonBaseAlpha_Data/ and UnityPlayer.dll beside it).
    /// Menu: Tools > Moon Base Alpha > Build Player
    /// Batch: Unity -batchmode -quit -projectPath . -executeMethod MoonBaseAlpha.Editor.BuildPlayer.Run
    /// </summary>
    public static class BuildPlayer
    {
        const string ScenePath = "Assets/_Project/Scenes/Main.unity";
        const string OutDir = "Builds/MoonBaseAlpha";
        const string ExeName = "MoonBaseAlpha.exe";

        [MenuItem("Tools/Moon Base Alpha/Build Player")]
        public static void Run()
        {
            if (!Directory.Exists("Assets/StreamingAssets/Terrain"))
                Debug.LogWarning("[BuildPlayer] Assets/StreamingAssets/Terrain missing - run Tools/terrain/build_terrain.py");

            EnsureShaderRefs();
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(OutDir, ExeName),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            Debug.Log($"[BuildPlayer] {s.result}: {s.outputPath} ({s.totalSize / (1024 * 1024)} MB, {s.totalTime.TotalSeconds:F0} s, {s.totalErrors} errors)");
            if (Application.isBatchMode)
                EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }

        const string RefDir = "Assets/_Project/Resources/ShaderRefs";

        /// <summary>
        /// Shaders used only via Shader.Find at runtime (Rendering/Mats, WheelDust, DebugHud) are not shipped unless
        /// something in the build references them. These Resources materials do, one per keyword combination that
        /// Mats enables at runtime (_NORMALMAP, _ALPHATEST_ON, _EMISSION), so the needed variants are not stripped.
        /// When you add a new Shader.Find or runtime keyword, add it here.
        /// </summary>
        public static void EnsureShaderRefs()
        {
            Directory.CreateDirectory(RefDir);
            string[] kw = { "_NORMALMAP", "_ALPHATEST_ON", "_EMISSION" };
            for (int mask = 0; mask < 8; mask++)
            {
                var name = "Lit";
                for (int i = 0; i < 3; i++) if ((mask & (1 << i)) != 0) name += kw[i];
                var m = Ref(name, "Universal Render Pipeline/Lit");
                for (int i = 0; i < 3; i++)
                    if ((mask & (1 << i)) != 0) m.EnableKeyword(kw[i]); else m.DisableKeyword(kw[i]);
                if ((mask & 2) != 0) { m.SetFloat("_AlphaClip", 1f); m.SetFloat("_Cull", 0f); }
                EditorUtility.SetDirty(m);
            }
            Ref("Dust", "MoonBaseAlpha/Dust");
            Ref("UnderRock", "MoonBaseAlpha/UnderRock");
            Ref("SkySunGlare", "MoonBaseAlpha/SkySunGlare");
            Ref("InternalColored", "Hidden/Internal-Colored");
            Ref("VisorGain", "Hidden/MoonBaseAlpha/VisorGain");
            Ref("Holo", "MoonBaseAlpha/Holo");
            Ref("MapSolid", "MoonBaseAlpha/MapSolid");
            Ref("TrackDecal", "MoonBaseAlpha/TrackDecal");
            {
                // the drill's vapour burst (MissionBuilder.VapourBurst): URP particles, unlit, transparent
                var pm = Ref("ParticlesUnlitTransparent", "Universal Render Pipeline/Particles/Unlit");
                pm.SetFloat("_Surface", 1f);
                pm.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                EditorUtility.SetDirty(pm);
            }
            AssetDatabase.SaveAssets();
        }

        static Material Ref(string name, string shader)
        {
            var path = $"{RefDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            var sh = Shader.Find(shader);
            if (sh == null) throw new System.Exception($"[BuildPlayer] shader not found: {shader}");
            if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); }
            else m.shader = sh;
            return m;
        }
    }
}
