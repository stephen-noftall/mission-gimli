using UnityEngine;
using UnityEngine.Rendering;
using MoonBaseAlpha.Sky;

namespace MoonBaseAlpha.Rendering
{
    /// <summary>Render layers used to cull the surface while the camera is under rock.</summary>
    public static class RenderLayers
    {
        public const int SurfaceTerrain = 8;   // heightfield render chunks + far ring (incl. skirts)
        public const int Pit = 9;              // the pit mesh (stays visible: the cave-in look from the tube)
    }

    /// <summary>
    /// While the camera is under rock (sky hidden): a black camera-centred sphere at 0.9x the tunnel stream
    /// radius, and the surface-terrain layer culled. Stops the far end of a tube (open chunk ends, sunlit
    /// terrain skirts seen from below) from glowing at the draw distance.
    /// </summary>
    [DefaultExecutionOrder(250)]   // after SkyController (200) decided the sky visibility
    public class UnderRockShell : MonoBehaviour
    {
        public Camera cam;
        public SkyController sky;
        public System.Func<float> streamRadius;
        /// <summary>Round 12 (S3): the camera is down in the pit (sky shown): the shell goes on below this world height.</summary>
        public System.Func<float?> pitClipY;
        public bool PitMode { get; private set; }
        public const float Factor = 0.9f;
        public float radiusFactor = Factor;

        GameObject shell;
        int fullMask = -1;
        public bool Active => shell != null && shell.activeSelf;

        public void Init()
        {
            if (shell != null) return;
            if (cam == null) cam = GetComponent<Camera>();
            fullMask = cam.cullingMask | (1 << RenderLayers.SurfaceTerrain) | (1 << RenderLayers.Pit);
            shell = new GameObject("UnderRockShell");
            shell.transform.SetParent(cam.transform, false);
            shell.AddComponent<MeshFilter>().sharedMesh = Sphere();
            var mr = shell.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(Shader.Find("MoonBaseAlpha/UnderRock")) { name = "UnderRock" };
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            shell.SetActive(false);
        }

        static Mesh Sphere()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var m = go.GetComponent<MeshFilter>().sharedMesh;
            Util.Kill(go);
            return m;
        }

        void LateUpdate() => Refresh();

        public void Refresh()
        {
            if (shell == null) Init();
            if (cam == null || sky == null) return;
            bool under = !sky.SkyShown;
            float? clipY = !under && pitClipY != null ? pitClipY() : null;
            PitMode = clipY.HasValue;
            bool on = under || PitMode;
            if (shell.activeSelf != on) shell.SetActive(on);
            Shader.SetGlobalFloat("_MBA_ShellClipY", PitMode ? clipY.Value : 1e9f);
            // the built-in sphere has radius 0.5
            float r = radiusFactor * (streamRadius != null ? streamRadius() : 240f);
            shell.transform.localScale = Vector3.one * (2f * r);
            cam.cullingMask = under ? fullMask & ~(1 << RenderLayers.SurfaceTerrain) : fullMask;
        }
    }
}
