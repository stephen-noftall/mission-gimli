using UnityEngine;

namespace MoonBaseAlpha
{
    /// <summary>
    /// Global physics setup for the Moon: 200 Hz fixed step, no air, and gravity that always points at
    /// the Moon's centre from the focus object (the buggy). Over the 25 km play area the local vertical
    /// tilts by up to ~0.4 degrees, so gravity is re-aimed each step instead of being a constant.
    /// </summary>
    [DefaultExecutionOrder(-900)]
    public class LunarPhysics : MonoBehaviour
    {
        public const float FixedDt = 1f / 200f;
        public Transform focus;

        public static void Configure()
        {
            // In Play the physics must step itself (the editor test harness uses Script mode).
            if (Application.isPlaying) Physics.simulationMode = SimulationMode.FixedUpdate;
            Time.fixedDeltaTime = FixedDt;
            Time.maximumDeltaTime = 0.1f;
            Physics.gravity = new Vector3(0, -Lunar.Gravity, 0);
            Physics.defaultSolverIterations = 12;
            Physics.defaultSolverVelocityIterations = 4;
            Physics.bounceThreshold = 0.5f;
            Physics.defaultMaxDepenetrationVelocity = 2f;
            Physics.sleepThreshold = 0.0005f;   // low g -> low energies; don't let things freeze mid-slide
        }

        public static void AimGravity(Vector3 atWorldPos)
        {
            Physics.gravity = TerrainFrame.DownAt(atWorldPos) * Lunar.Gravity;
        }

        void Awake() => Configure();

        void FixedUpdate()
        {
            if (focus != null) AimGravity(focus.position);
        }
    }
}
