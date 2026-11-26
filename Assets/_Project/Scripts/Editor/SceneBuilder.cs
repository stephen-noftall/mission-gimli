using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using MoonBaseAlpha.Rendering;
using MoonBaseAlpha.UI;

namespace MoonBaseAlpha.Editor
{
    /// <summary>
    /// Builds Main.unity for the first pass: bootstrap, camera, Sun, post-processing and materials.
    /// Everything else (terrain, cave, rover, sky) is generated at runtime from data.
    /// Menu: Tools > Moon Base Alpha > Build Main Scene
    /// Batch: Unity -batchmode -quit -projectPath . -executeMethod MoonBaseAlpha.Editor.SceneBuilder.Build
    /// </summary>
    public static class SceneBuilder
    {
        const string ScenePath = "Assets/_Project/Scenes/Main.unity";
        const string MatDir = "Assets/_Project/Materials";
        const string PipelinePath = "Assets/_Project/Settings/URP_Pipeline.asset";
        const string RendererPath = "Assets/_Project/Settings/URP_Pipeline_Renderer.asset";
        const string VolumePath = "Assets/_Project/Settings/PostProcess_Default.asset";

        [MenuItem("Tools/Moon Base Alpha/Build Main Scene")]
        public static void Build()
        {
            ConfigurePipeline();
            ConfigureVolume();
            PlayerSettings.enableFrameTimingStats = true;   // GPU time for the F3 readout
            var mats = Materials();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.reflectionIntensity = 0f;
            RenderSettings.fog = false;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.985f, 0.96f);   // solar spectrum outside an atmosphere: very slightly warm-white
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 1f;
            sun.shadowBias = 0.05f;
            sun.shadowNormalBias = 0.4f;
            sun.gameObject.AddComponent<PhysicalLight>().physicalIntensity = Lunar.SolarIlluminance;

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50000f;
            cam.fieldOfView = 75f;
            var cd = cam.GetUniversalAdditionalCameraData();
            cd.renderPostProcessing = true;
            cd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            cd.antialiasingQuality = AntialiasingQuality.High;
            cd.renderShadows = true;

            var volume = new GameObject("Global Volume").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumePath);

            var game = new GameObject("Game").AddComponent<GameBootstrap>();
            game.cam = cam; game.sun = sun;
            game.terrainMaterial = mats.terrain; game.caveMaterial = mats.cave; game.tunnelMaterial = mats.tunnel;
            game.sunMaterial = mats.sun; game.earthMaterial = mats.earth; game.starMaterial = mats.stars;
            game.gameObject.AddComponent<DebugHud>().game = game;

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneBuilder] Main scene built.");
        }

        public struct Mats { public Material terrain, cave, tunnel, sun, earth, stars; }

        public static Mats Materials()
        {
            Directory.CreateDirectory(MatDir);
            var m = new Mats
            {
                terrain = Mat("LunarRegolith", "MoonBaseAlpha/LunarRegolith"),
                cave = Mat("LavaTubeRock", "MoonBaseAlpha/LunarRegolith"),
                tunnel = Mat("TunnelRock", "MoonBaseAlpha/CaveRock"),
                sun = Mat("SunDisc", "MoonBaseAlpha/SkySun"),
                earth = Mat("Earth", "MoonBaseAlpha/SkyEarth"),
                stars = Mat("Stars", "MoonBaseAlpha/SkyStars"),
            };
            // Tube walls: fresh basalt is darker than mature regolith, with more relief
            m.cave.SetFloat("_Albedo", 0.075f);
            m.cave.SetFloat("_AlbedoVariation", 0.3f);
            m.cave.SetFloat("_Rocky", 1f);
            m.cave.SetFloat("_DetailNormal", 0.7f);
            m.cave.SetColor("_Tint", new Color(0.98f, 0.97f, 0.96f, 1));
            m.earth.SetTexture("_DayTex", Tex("Assets/_Project/Textures/Earth/2k_earth_daymap.jpg"));
            m.earth.SetTexture("_NightTex", Tex("Assets/_Project/Textures/Earth/2k_earth_nightmap.jpg"));
            m.earth.SetTexture("_CloudTex", Tex("Assets/_Project/Textures/Earth/2k_earth_clouds.jpg"));
            foreach (var x in new[] { m.terrain, m.cave, m.tunnel, m.sun, m.earth, m.stars }) EditorUtility.SetDirty(x);
            AssetDatabase.SaveAssets();
            return m;
        }

        static Texture2D Tex(string path)
        {
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            if (imp != null && (imp.wrapModeU != TextureWrapMode.Repeat || imp.mipmapEnabled))
            {
                imp.wrapModeU = TextureWrapMode.Repeat;
                imp.wrapModeV = TextureWrapMode.Clamp;
                imp.mipmapEnabled = false;    // sampled with LOD 0; avoids the seam at lon = 180
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Material Mat(string name, string shader)
        {
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var sh = Shader.Find(shader);
            if (sh == null) throw new System.Exception($"Shader not found: {shader}");
            if (mat == null) { mat = new Material(sh); AssetDatabase.CreateAsset(mat, path); }
            else mat.shader = sh;
            return mat;
        }

        static void ConfigurePipeline()
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (pipeline == null || renderer == null) { ProjectSetup.Run(); pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath); renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath); }

            // SSAO darkens ambient light; on the Moon ambient is ~0, so it only costs time. Remove it.
            var so = new SerializedObject(renderer);
            var feats = so.FindProperty("m_RendererFeatures");
            var map = so.FindProperty("m_RendererFeatureMap");
            for (int i = feats.arraySize - 1; i >= 0; i--)
            {
                var f = feats.GetArrayElementAtIndex(i).objectReferenceValue;
                if (f != null && f.GetType().Name == "ScreenSpaceAmbientOcclusion")
                {
                    feats.DeleteArrayElementAtIndex(i);
                    if (i < map.arraySize) map.DeleteArrayElementAtIndex(i);
                    Object.DestroyImmediate(f, true);
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(renderer);

            var pso = new SerializedObject(pipeline);
            void SetF(string n, float v) { var p = pso.FindProperty(n); if (p != null) p.floatValue = v; }
            void SetI(string n, int v) { var p = pso.FindProperty(n); if (p != null) p.intValue = v; }
            void SetB(string n, bool v) { var p = pso.FindProperty(n); if (p != null) p.boolValue = v; }
            SetF("m_ShadowDistance", 500f);           // low sun -> long shadows
            SetI("m_ShadowCascadeCount", 4);
            SetF("m_Cascade4Split.x", 0.03f);
            SetB("m_SupportsHDR", true);
            SetI("m_HDRColorBufferPrecision", 1);     // 64-bit: room for sunlight and lamp-lit darkness
            SetB("m_AdditionalLightShadowsSupported", true);
            SetI("m_AdditionalLightsShadowmapResolution", 4096);
            SetI("m_MaxPixelLights", 8);
            pso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
        }

        static void ConfigureVolume()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumePath);
            if (profile == null) return;
            if (profile.TryGet(out Bloom bloom)) { bloom.intensity.Override(0f); bloom.active = false; }
            if (profile.TryGet(out ColorAdjustments ca)) { ca.postExposure.Override(0f); ca.contrast.Override(0f); ca.saturation.Override(0f); }
            if (profile.TryGet(out Vignette vig)) vig.intensity.Override(0.12f);
            if (profile.TryGet(out Tonemapping tm)) tm.mode.Override(TonemappingMode.ACES);
            EditorUtility.SetDirty(profile);
        }
    }
}
