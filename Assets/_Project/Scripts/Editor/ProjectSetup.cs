using System;
using System.IO;
using System.Reflection;
using Unity.CodeEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MoonBaseAlpha.Editor
{
    /// <summary>
    /// One-shot project setup: URP pipeline asset + renderer (Forward+, SSAO, soft shadows, HDR),
    /// a post-processing volume profile, a starter lunar scene, and Visual Studio as script editor.
    /// Safe to re-run: existing assets are reused and re-configured.
    /// Menu: Tools > Moon Base Alpha > Run Project Setup
    /// Batch: Unity -batchmode -quit -projectPath . -executeMethod MoonBaseAlpha.Editor.ProjectSetup.Run
    /// </summary>
    public static class ProjectSetup
    {
        const string SettingsDir = "Assets/_Project/Settings";
        const string PipelinePath = SettingsDir + "/URP_Pipeline.asset";
        const string RendererPath = SettingsDir + "/URP_Pipeline_Renderer.asset";
        const string VolumePath = SettingsDir + "/PostProcess_Default.asset";
        const string ScenePath = "Assets/_Project/Scenes/Main.unity";
        const string GroundMatPath = "Assets/_Project/Materials/LunarRegolith.mat";
        const string VisualStudioPath = @"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe";

        [MenuItem("Tools/Moon Base Alpha/Run Project Setup")]
        public static void Run()
        {
            Directory.CreateDirectory(SettingsDir);
            var pipeline = SetupPipeline();
            var profile = SetupVolumeProfile();
            SetupScene(profile);
            SetupScriptEditor();
            AssetDatabase.SaveAssets();
            Debug.Log($"[ProjectSetup] Done. Pipeline: {AssetDatabase.GetAssetPath(pipeline)}");
        }

        static UniversalRenderPipelineAsset SetupPipeline()
        {
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer == null)
            {
                // URP's internal helper is what the "Create > Rendering > URP Asset" menu uses;
                // it wires post-process data and shader resources correctly.
                var create = typeof(UniversalRenderPipelineAsset).GetMethod("CreateRendererAsset",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var rendererTypeEnum = typeof(UniversalRenderPipelineAsset).Assembly
                    .GetType("UnityEngine.Rendering.Universal.RendererType");
                var universal = Enum.Parse(rendererTypeEnum, "UniversalRenderer");
                renderer = (UniversalRendererData)create.Invoke(null,
                    new object[] { RendererPath, universal, false, "Renderer" });
            }

            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
            }

            // Renderer: Forward+ (many lights per object - good for base interiors) + SSAO.
            var rso = new SerializedObject(renderer);
            rso.FindProperty("m_RenderingMode").intValue = (int)RenderingMode.ForwardPlus;
            rso.ApplyModifiedPropertiesWithoutUndo();
            EnsureSsao(renderer);

            // Pipeline quality.
            var pso = new SerializedObject(pipeline);
            pso.FindProperty("m_SupportsHDR").boolValue = true;
            pso.FindProperty("m_MSAA").intValue = (int)MsaaQuality._4x;
            pso.FindProperty("m_RequireDepthTexture").boolValue = true;
            pso.FindProperty("m_RequireOpaqueTexture").boolValue = true;
            pso.FindProperty("m_MainLightShadowsSupported").boolValue = true;
            pso.FindProperty("m_MainLightShadowmapResolution").intValue = (int)UnityEngine.Rendering.Universal.ShadowResolution._4096;
            pso.FindProperty("m_AdditionalLightsRenderingMode").intValue = (int)LightRenderingMode.PerPixel;
            pso.FindProperty("m_AdditionalLightShadowsSupported").boolValue = true;
            pso.FindProperty("m_ShadowDistance").floatValue = 150f;
            pso.FindProperty("m_ShadowCascadeCount").intValue = 4;
            pso.FindProperty("m_SoftShadowsSupported").boolValue = true;
            pso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);

            GraphicsSettings.defaultRenderPipeline = pipeline;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = pipeline;
            }
            QualitySettings.SetQualityLevel(current, false);
            return pipeline;
        }

        static void EnsureSsao(UniversalRendererData renderer)
        {
            foreach (var f in renderer.rendererFeatures)
                if (f != null && f.GetType().Name == "ScreenSpaceAmbientOcclusion") return;

            var ssaoType = typeof(UniversalRendererData).Assembly
                .GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion");
            var feature = (ScriptableRendererFeature)ScriptableObject.CreateInstance(ssaoType);
            feature.name = "ScreenSpaceAmbientOcclusion";
            AssetDatabase.AddObjectToAsset(feature, renderer);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            var so = new SerializedObject(renderer);
            var features = so.FindProperty("m_RendererFeatures");
            var map = so.FindProperty("m_RendererFeatureMap");
            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedPropertiesWithoutUndo();
            renderer.SetDirty();
            EditorUtility.SetDirty(renderer);
        }

        static VolumeProfile SetupVolumeProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, VolumePath);
            }

            T Get<T>() where T : VolumeComponent
            {
                if (!profile.TryGet(out T c))
                {
                    c = profile.Add<T>(true);
                    c.name = typeof(T).Name;
                    AssetDatabase.AddObjectToAsset(c, profile);
                }
                return c;
            }

            var tone = Get<Tonemapping>();
            tone.mode.Override(TonemappingMode.ACES);

            var bloom = Get<Bloom>();
            bloom.threshold.Override(1.1f);
            bloom.intensity.Override(0.6f);
            bloom.scatter.Override(0.6f);

            var color = Get<ColorAdjustments>();
            color.postExposure.Override(0.2f);
            color.contrast.Override(12f);
            color.saturation.Override(-10f);

            var vignette = Get<Vignette>();
            vignette.intensity.Override(0.25f);
            vignette.smoothness.Override(0.4f);

            EditorUtility.SetDirty(profile);
            return profile;
        }

        static void SetupScene(VolumeProfile profile)
        {
            if (File.Exists(ScenePath)) return; // never clobber a scene someone has edited

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Harsh, unfiltered sunlight and a black sky.
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.05f, 0.055f, 0.07f);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 2.2f;
            sun.color = new Color(1f, 0.98f, 0.94f);
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(25f, -40f, 0f);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.farClipPlane = 2000f;
            camGo.transform.SetPositionAndRotation(new Vector3(0f, 12f, -18f), Quaternion.Euler(30f, 0f, 0f));
            var camData = cam.GetUniversalAdditionalCameraData();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            camData.antialiasingQuality = AntialiasingQuality.High;

            var volume = new GameObject("Global Volume").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = profile;

            var mat = AssetDatabase.LoadAssetAtPath<Material>(GroundMatPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.SetColor("_BaseColor", new Color(0.42f, 0.41f, 0.40f));
                mat.SetFloat("_Smoothness", 0.08f);
                AssetDatabase.CreateAsset(mat, GroundMatPath);
            }
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            ground.GetComponent<MeshRenderer>().sharedMaterial = mat;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        static void SetupScriptEditor()
        {
            if (!File.Exists(VisualStudioPath))
            {
                Debug.LogWarning($"[ProjectSetup] Visual Studio not found at {VisualStudioPath}; set it in Preferences > External Tools.");
                return;
            }
            CodeEditor.SetExternalScriptEditor(VisualStudioPath);
            CodeEditor.CurrentEditor.SyncAll(); // generate .sln / .csproj
        }
    }
}
