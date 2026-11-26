using UnityEngine;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha.Crew
{
    /// <summary>
    /// Suited astronaut on foot in 1/6 g.
    ///  - Mass 205 kg (80 kg crew member + ~125 kg xEMU suit/PLSS): weighs 333 N on the Moon.
    ///  - Horizontal control is a ground reaction force limited by boot-regolith friction (mu ~0.6 of the
    ///    normal force), so starting, stopping and turning are slow and slidey, as in the Apollo footage.
    ///  - Above ~1.3 m/s the gait becomes the lunar "lope": each stride is a small ballistic hop.
    ///  - Space = jump (suited push-off ~1.25 m/s -> ~0.5 m). No air control.
    ///  - Two helmet lamps (H). Mouse look; body yaws with the view.
    /// </summary>
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    public class AstronautController : MonoBehaviour
    {
        public const float Mass = 205f;
        public float walkSpeed = 1.1f, lopeSpeed = 2.2f;
        public float friction = 0.6f;
        public float jumpSpeed = 1.25f;
        public float lopeHop = 0.55f;
        public float eyeHeight = 1.72f;
        public AstronautRig rig;
        public Light[] lamps;
        public Transform eye;

        [Header("Input (set by the player controller or tests)")]
        public Vector2 move;          // x strafe, y forward (-1..1)
        public bool run;
        public bool jumpRequest;
        public float yawDelta;        // degrees this frame

        public Rigidbody Body { get; private set; }
        public bool Grounded { get; private set; }
        public Vector3 GroundNormal { get; private set; } = Vector3.up;
        public float Speed { get; private set; }
        public float AirTime { get; private set; }

        float stridePhase, lastLand, hopCooldown;
        bool wasGrounded;

        void Awake() => Setup();

        public void Setup()
        {
            Body = GetComponent<Rigidbody>();
            Body.mass = Mass;
            Body.linearDamping = 0f; Body.angularDamping = 0f;
            Body.freezeRotation = true;
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var col = GetComponent<CapsuleCollider>();
            col.height = 1.9f; col.radius = 0.33f; col.center = new Vector3(0, 0.95f, 0);
            col.sharedMaterial = new PhysicsMaterial("Suit") { dynamicFriction = 0f, staticFriction = 0f, frictionCombine = PhysicsMaterialCombine.Minimum, bounciness = 0f };
        }

        void FixedUpdate() => Step(Time.fixedDeltaTime);

        public void Step(float dt)
        {
            if (Body == null) Setup();
            var up = -TerrainFrame.DownAt(transform.position);
            // keep upright with respect to local vertical, apply yaw
            var fwd = Vector3.ProjectOnPlane(transform.forward, up).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
            fwd = Quaternion.AngleAxis(yawDelta, up) * fwd;
            yawDelta = 0;
            Body.MoveRotation(Quaternion.LookRotation(fwd, up));

            // ground probe (feet)
            Grounded = Physics.SphereCast(transform.position + up * 0.5f, 0.28f, -up, out var hit, 0.5f - 0.28f + 0.08f, ~(1 << 2), QueryTriggerInteraction.Ignore);
            GroundNormal = Grounded ? hit.normal : up;
            var groundBody = Grounded && hit.rigidbody != null && !hit.rigidbody.isKinematic ? hit.rigidbody : null;
            var v = Body.linearVelocity - (groundBody != null ? groundBody.GetPointVelocity(hit.point) : Vector3.zero);
            var vTan = Vector3.ProjectOnPlane(v, GroundNormal);
            Speed = vTan.magnitude;

            if (Grounded)
            {
                if (!wasGrounded) { lastLand = Time.time; hopCooldown = 0.12f; }
                AirTime = 0;
                float target = (run ? lopeSpeed : walkSpeed) * Tuning.WalkSpeed.Value;   // \ tuning: walk speed
                var right = Vector3.Cross(up, fwd);
                var desired = Vector3.ProjectOnPlane(fwd * move.y + right * move.x, GroundNormal);
                if (desired.sqrMagnitude > 1) desired.Normalize();
                desired *= target;
                // ground reaction needed to reach the desired velocity in ~0.25 s, limited by friction
                float g = Physics.gravity.magnitude;
                float normal = Mass * g * Mathf.Max(0.1f, Vector3.Dot(GroundNormal, up));
                // + static friction: the boots also carry the downhill pull of gravity, so an idle astronaut
                // stands still on a slope (the suit collider itself is frictionless)
                var gTan = Vector3.ProjectOnPlane(Physics.gravity, GroundNormal);
                var F = (desired - vTan) * (Mass / 0.25f) - Mass * gTan;
                float fmax = friction * normal;
                if (F.magnitude > fmax) F = F.normalized * fmax;
                Body.AddForce(F, ForceMode.Force);
                if (groundBody != null) groundBody.AddForceAtPosition(-F, hit.point, ForceMode.Force);

                hopCooldown -= dt;
                // lunar lope: at speed, every stride launches a small hop
                if (Speed > 1.3f && desired.sqrMagnitude > 0.5f && hopCooldown <= 0)
                {
                    Body.AddForce(up * (Mass * lopeHop), ForceMode.Impulse);
                    hopCooldown = 0.12f;
                }
                if (jumpRequest)
                {
                    Body.AddForce(up * (Mass * jumpSpeed), ForceMode.Impulse);
                    hopCooldown = 0.3f;
                }
                stridePhase += dt * Mathf.Lerp(0.9f, 1.6f, Mathf.InverseLerp(0, lopeSpeed, Speed)) * (Speed > 0.05f ? 1 : 0);
            }
            else AirTime += dt;
            jumpRequest = false;
            wasGrounded = Grounded;

            if (rig != null)
            {
                float amt = Mathf.Clamp01(Speed / lopeSpeed) * 1.2f;
                rig.SetLocomotion(stridePhase % 1f, Mathf.Min(1, amt), !Grounded, 6f * Mathf.Clamp01(Speed / lopeSpeed));
            }
        }

        public void SetLamps(bool on)
        {
            if (lamps == null) return;
            foreach (var l in lamps) if (l) l.enabled = on;
        }
        public bool LampsOn => lamps != null && lamps.Length > 0 && lamps[0].enabled;

        /// <summary>Create a walker (model, physics, lamps, eye) at a world position.</summary>
        public static AstronautController Create(Vector3 world, Quaternion rot, int crewIndex = 0)
        {
            var go = new GameObject("Astronaut (player)");
            go.layer = 2;
            go.transform.SetPositionAndRotation(world, rot);
            go.AddComponent<Rigidbody>();
            go.AddComponent<CapsuleCollider>();
            var a = go.AddComponent<AstronautController>();
            a.rig = AstronautBuilder.Build(go.transform, crewIndex);
            a.eye = new GameObject("Eye").transform;
            a.eye.SetParent(go.transform, false);
            a.eye.localPosition = new Vector3(0, a.eyeHeight, 0.12f);
            // xEMU-style helmet lights: ~1200 lm each in a 40 deg beam -> ~3200 cd
            var lamps = new Light[2];
            for (int i = 0; i < 2; i++)
            {
                var l = new GameObject("HelmetLamp").AddComponent<Light>();
                l.transform.SetParent(a.rig.head, false);
                l.transform.localPosition = new Vector3(i == 0 ? -0.21f : 0.21f, 0.2f, 0.16f);
                l.transform.localRotation = Quaternion.Euler(6, 0, 0);
                l.type = LightType.Spot; l.spotAngle = 45; l.innerSpotAngle = 25; l.range = 80;
                l.color = new Color(1f, 0.97f, 0.93f);
                l.shadows = i == 0 ? LightShadows.Soft : LightShadows.None;
                l.shadowBias = 0.1f; l.shadowNormalBias = 1.5f;
                l.shadowNearPlane = 0.6f;     // the lamp's own helmet / shoulders must not shadow its beam
                l.gameObject.AddComponent<PhysicalLight>().physicalIntensity = 3200f;
                l.enabled = false;
                lamps[i] = l;
            }
            a.lamps = lamps;
            go.AddComponent<LampBounce>().lamps = lamps;
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;
            a.Setup();
            return a;
        }
    }
}
