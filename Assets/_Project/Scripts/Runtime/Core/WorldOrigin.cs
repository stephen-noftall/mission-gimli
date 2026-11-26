using System;
using Unity.Mathematics;
using UnityEngine;

namespace MoonBaseAlpha
{
    /// <summary>
    /// Floating origin. "Frame" coordinates are the double-precision tangent-plane frame centred on the
    /// Marius Hills Pit (+X east, +Y up, +Z north, metres). Unity world = frame - Origin.
    /// When the tracked object gets far from the Unity origin, every root object is shifted back so
    /// float precision stays sub-millimetre anywhere in the 65 km play area.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class WorldOrigin : MonoBehaviour
    {
        public static double3 Origin { get; private set; }
        public static event Action<Vector3> Shifted;   // argument: world-space delta applied to everything

        [Tooltip("Object to keep near the Unity origin (usually the buggy or camera).")]
        public Transform target;
        public float threshold = 1024f;

        public static Vector3 FrameToWorld(double3 p) => (float3)(p - Origin);
        public static double3 WorldToFrame(Vector3 p) => (double3)(float3)p + Origin;

        /// <summary>Moon centre in Unity world space.</summary>
        public static Vector3 MoonCentreWorld => FrameToWorld(new double3(0, -TerrainFrame.R0, 0));

        public static void ResetOrigin() => Origin = double3.zero;

        void Awake() => Origin = double3.zero;

        void LateUpdate()
        {
            if (target == null) return;
            var p = target.position;
            if (Mathf.Abs(p.x) < threshold && Mathf.Abs(p.z) < threshold && Mathf.Abs(p.y) < threshold) return;
            ShiftBy(new Vector3(Mathf.Round(p.x), Mathf.Round(p.y), Mathf.Round(p.z)));
        }

        /// <summary>Move the origin by <paramref name="worldOffset"/> (everything moves by -offset).</summary>
        public static void ShiftBy(Vector3 worldOffset)
        {
            Origin += (double3)(float3)worldOffset;
            var delta = -worldOffset;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.GetComponent<WorldOriginIgnore>() != null) continue;
                var rbs = root.GetComponentsInChildren<Rigidbody>();
                root.transform.position += delta;
                foreach (var rb in rbs) rb.position = rb.transform.position;
            }
            foreach (var ps in UnityEngine.Object.FindObjectsByType<ParticleSystem>())
            {
                if (ps.main.simulationSpace != ParticleSystemSimulationSpace.World) continue;
                var parts = new ParticleSystem.Particle[ps.particleCount];
                int n = ps.GetParticles(parts);
                for (int i = 0; i < n; i++) parts[i].position += delta;
                ps.SetParticles(parts, n);
            }
            Physics.SyncTransforms();
            Shifted?.Invoke(delta);
        }
    }

    /// <summary>Marks a root object that must not be moved by origin shifts (e.g. camera-following sky).</summary>
    public class WorldOriginIgnore : MonoBehaviour { }

    /// <summary>Static description of the terrain frame (set when terrain metadata loads).</summary>
    public static class TerrainFrame
    {
        /// <summary>Radius of the frame origin from the Moon's centre, metres.</summary>
        public static double R0 = Lunar.ReferenceRadius - 1500.0;
        public static double SiteLatDeg = 14.0917;
        public static double SiteLonDeg = 303.2299;

        /// <summary>Unit "down" (towards the Moon's centre) at a Unity world position.</summary>
        public static Vector3 DownAt(Vector3 worldPos)
        {
            var c = WorldOrigin.MoonCentreWorld;
            return (c - worldPos).normalized;
        }
    }
}
