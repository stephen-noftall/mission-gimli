using System;
using Unity.Mathematics;
using UnityEngine;

namespace MoonBaseAlpha.Buggy
{
    /// <summary>
    /// MR-4 "Marius Rover": a stretched 4-seat derivative of the Apollo Lunar Roving Vehicle.
    ///
    /// Physics per wheel (200 Hz):
    ///  - unsprung mass (wheel + hub motor + arms, 25 kg) on its own vertical DOF, so wheels can hop;
    ///  - torsion-bar spring + asymmetric hydraulic damper (rebound ~1.6x compression, overall ~0.65
    ///    critical for the sprung corner mass) + progressive bump stops + droop limit;
    ///  - flexible wire-mesh tyre as a stiff spring to the ground (sphere-cast contact);
    ///  - Bekker sinkage / Janosi-Hanamoto shear / compaction resistance for traction and rolling
    ///    resistance, with a bristle (stick) model at walking speeds so it can park on slopes;
    ///  - wheel spin with hub-motor torque/power limits, regen + friction brakes, solved semi-implicitly.
    /// Gravity on the body is Unity's (re-aimed at the Moon's centre each step); no aerodynamic forces.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class BuggyController : MonoBehaviour
    {
        [Serializable]
        public class Wheel
        {
            public string name;
            public Vector3 mount;          // local, top of suspension travel
            public bool front, left;
            public Transform steerPivot;   // visual
            public Transform spinner;      // visual

            // state
            [NonSerialized] public float x;          // extension below mount (0 = full bump, travel = full droop)
            [NonSerialized] public float vAxis;      // world velocity of the wheel along -up (m/s)
            [NonSerialized] public float omega;      // spin, rad/s (+ = forward)
            [NonSerialized] public float spinAngle;
            [NonSerialized] public float steer;      // rad
            [NonSerialized] public float bristleX, bristleY;
            [NonSerialized] public float prevDefl;

            // diagnostics
            [NonSerialized] public bool grounded;
            [NonSerialized] public float load, sinkage, slip, slipAngle, fx, fy, springForce, motorTorque;
            [NonSerialized] public string limit = "";   // what limits the drive force now: grip / torque / power / governor
            [NonSerialized] public Vector3 contactPoint, contactNormal;
            [NonSerialized] public Soil soil;
        }

        [Header("Geometry (LRV-derived)")]
        public float wheelRadius = 0.41f;       // LRV 81.8 cm wire-mesh wheel
        public float wheelWidth = 0.28f;        // wider than LRV's 23 cm for the 4-seat load
        public float travel = 0.40f;            // more than the LRV: 4 crew on rough ground
        public Wheel[] wheels;

        [Header("Mass")]
        public float totalMass = 1640f;         // 650 kg vehicle + 4 x 210 kg suited crew + 150 kg cargo
        public float unsprungMass = 25f;        // per corner
        public Vector3 centreOfMass = new Vector3(0f, 0.55f, -0.05f);

        [Header("Suspension")]
        public float springRate = 5550f;        // N/m at the wheel -> 0.6 Hz ride in 1/6 g, 12 cm sag
        public float springFreeExtension = 0.393f;
        public float dampingRatio = 0.65f;      // minimal overshoot
        public float reboundBias = 1.6f;        // rebound/compression damping ratio
        public float bumpStopStart = 0.035f;
        public float tyreRate = 30000f;         // wire-mesh tyre radial stiffness
        public float antiRollRate = 4500f;      // N/m per axle (torsion-bar stabiliser linking left/right)
        public float tyreDamping = 180f;

        [Header("Rollover safety (no 'pinball' relaunch after a flip)")]
        public float maxSuspensionForce = 9000f;   // N per corner (~14x the static corner load)
        public float maxTyreForce = 7000f;         // N per tyre
        public float maxTyreDeflection = 0.1f;     // m: beyond this the sphere cast started inside the ground (no real tyre squashes 0.6 m)
        public float maxStrokeRate = 3f;           // m/s, wheel travel relative to the body
        public float maxContactAngle = 60f;        // deg between ground normal and suspension axis
        public float angularDampingMild = 0.12f;   // 1/s (crew shifting, structural losses)
        public float contactFadeStart = 40f;       // deg: tyre force fades out from here to maxContactAngle
        public float hullPloughAngular = 2.5f;     // 1/s while the hull scrapes the ground tilted (loose regolith is very lossy)
        public float hullPloughLinear = 0.8f;      // 1/s

        BoxCollider[] hull;
        static readonly Collider[] overlap = new Collider[4];
        /// <summary>The hull (not a tyre) is touching the ground (overlap test, 3 cm skin; works in edit-mode tests too).</summary>
        public bool HullContact { get; private set; }
        bool TouchingHull()
        {
            hull ??= GetComponentsInChildren<BoxCollider>();
            foreach (var b in hull)
            {
                var t = b.transform;
                var half = Vector3.Scale(b.size * 0.5f, t.lossyScale) + Vector3.one * 0.03f;
                if (Physics.OverlapBoxNonAlloc(t.TransformPoint(b.center), half, overlap, t.rotation, ~(1 << 2), QueryTriggerInteraction.Ignore) > 0) return true;
            }
            return false;
        }

        [Header("Drive")]
        public float peakTorque = 220f;         // N m at the wheel (hub motor + harmonic drive)
        public float peakPowerPerWheel = 2000f; // W
        public float maxSpeed = 15f / 3.6f;     // governed, m/s (x the \ "rover top speed" tuning)
        /// <summary>Drive ranges (round 12, P2): SLOW = the round-11 top speed with full torque; FAST = x2, half torque.</summary>
        public enum DriveRange { Slow, Fast }
        public DriveRange RangeMode = DriveRange.Slow;
        /// <summary>FAST top speed factor (provisional: ~37 km/h; the user judges it in play) and its torque share.</summary>
        public const float FastFactor = 2f, FastTorque = 0.5f;
        /// <summary>Extra limit from the driver's edge assist (m/s; infinity = none).</summary>
        [NonSerialized] public float speedCap = float.PositiveInfinity;
        /// <summary>Governed top speed of the range (base x tuning x range), before the edge assist's cap.</summary>
        public float MaxSpeedUncapped => maxSpeed * Tuning.RoverSpeed.Value * (RangeMode == DriveRange.Fast ? FastFactor : 1f) * (Damaged ? DamagedSpeedFactor : 1f);
        /// <summary>Governed top speed now (base x tuning x range, edge assist cap).</summary>
        public float MaxSpeedNow => Mathf.Min(MaxSpeedUncapped, speedCap);
        /// <summary>After a fall into a shaft without a line (RoverWinch mercy): bent suspension, top speed capped.</summary>
        public bool Damaged;
        public const float DamagedSpeedFactor = 0.45f;
        public float PeakTorqueNow => peakTorque * Tuning.MotorTorque.Value * (RangeMode == DriveRange.Fast ? FastTorque : 1f);
        public float brakeTorque = 300f;        // N m per wheel
        public float wheelInertia = 1.4f;       // kg m^2 (rim + mesh + motor rotor reflected)
        public float minTurnRadius = 3.1f;      // LRV: 3.1 m with double Ackermann
        public float steerRate = math.PI / 3f;  // rad/s servo limit
        public bool fourWheelSteer = true;

        [Header("Power")]
        public float batteryCapacityWh = 40000f;
        public float stateOfCharge = 1f;
        public float avionicsWatts = 150f;
        public float motorEfficiency = 0.85f;
        public float regenEfficiency = 0.55f;

        [Header("Input (set by driver / tests)")]
        [Range(-1, 1)] public float throttle;
        [Range(-1, 1)] public float steering;
        [Range(0, 1)] public float brake;
        public bool parkingBrake;
        public float extraLoadWatts;            // lights etc.

        // bristle parameters (static friction model at low speed)
        const float BristleK = 25000f, BristleC = 2500f;
        const float SlipEps = 0.4f;

        public Rigidbody Body { get; private set; }
        /// <summary>Shock sensor: hard-landing warnings (see <see cref="ImpactMonitor"/>).</summary>
        public readonly ImpactMonitor Impact = new();
        public float Speed { get; private set; }            // signed forward speed, m/s
        public float PowerDraw { get; private set; }        // W from the battery
        public double EnergyUsedWh { get; private set; }
        public double DistanceM { get; private set; }
        public float SprungMass => totalMass - 4f * unsprungMass;

        void Awake() => Setup();

        public void Setup()
        {
            Body = GetComponent<Rigidbody>();
            Body.mass = SprungMass;
            Body.linearDamping = 0f;              // vacuum
            Body.angularDamping = angularDampingMild;
            Body.useGravity = true;
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            Body.maxAngularVelocity = 30f;
            Body.automaticCenterOfMass = false;
            Body.centerOfMass = centreOfMass;
            Body.automaticInertiaTensor = false;
            // Box approximation 2.2 x 1.3 x 3.9 m, crew mass at the extremities.
            float m = SprungMass;
            Body.inertiaTensor = new Vector3(m / 12f * (1.3f * 1.3f + 3.9f * 3.9f),
                                             m / 12f * (2.2f * 2.2f + 3.9f * 3.9f),
                                             m / 12f * (2.2f * 2.2f + 1.3f * 1.3f));
            Body.inertiaTensorRotation = Quaternion.identity;
            if (wheels != null)
                foreach (var w in wheels) { w.x = springFreeExtension - SprungMass * Lunar.Gravity / 4f / springRate; w.vAxis = 0; }
        }

        /// <summary>Place the buggy so its wheels rest on the ground below <paramref name="world"/>.</summary>
        public void PlaceAt(Vector3 world, Quaternion rot)
        {
            var up = -TerrainFrame.DownAt(world);
            // ground directly below the requested point (start just above it so cave roofs aren't hit)
            if (Physics.Raycast(world + up * 1.0f, -up, out var hit, 30f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                world = hit.point;
            // chassis origin sits ~0.34 m above the ground at static sag; drop in from a few cm higher
            transform.SetPositionAndRotation(world + up * 0.45f, rot);
            Body.position = transform.position; Body.rotation = rot;
            Body.linearVelocity = Vector3.zero; Body.angularVelocity = Vector3.zero;
            Impact.Reset();
            foreach (var w in wheels) { w.x = springFreeExtension - SprungMass * Lunar.Gravity / 4f / springRate; w.vAxis = 0; w.omega = 0; w.bristleX = w.bristleY = 0; }
        }

        /// <summary>Stabiliser bar: resists the left and right wheel on the same axle moving differently.</summary>
        float AntiRoll(Wheel w)
        {
            foreach (var o in wheels)
                if (o != w && o.front == w.front) return antiRollRate * (o.x - w.x);   // this side compressed more -> extra lift here (and less on the partner)
            return 0f;
        }

        /// <summary>True when the rover is high-centred: belly down, wheels hanging, not moving.</summary>
        public bool HighCentred
        {
            get
            {
                if (wheels == null || Body == null) return false;
                if (Body.linearVelocity.magnitude > 0.2f) return false;
                int air = 0; foreach (var w in wheels) if (!w.grounded || w.load < 150f) air++;
                if (air >= 2) return true;
                // belly on a rim / rock: very little clearance under the chassis
                var up = transform.up;
                foreach (var p in new[] { new Vector3(0, 0.1f, 0), new Vector3(0, 0.1f, 0.9f), new Vector3(0, 0.1f, -0.9f) })
                    if (Physics.Raycast(transform.TransformPoint(p) + up * 0.3f, -up, 0.42f, ~(1 << 2), QueryTriggerInteraction.Ignore)) return true;
                return false;
            }
        }

        /// <summary>
        /// Recovery: lift the rover clear (as high as the space above allows, e.g. in the tube), set it
        /// level on the local vertical keeping its heading, and stop it.
        /// </summary>
        public void Right()
        {
            var up = -TerrainFrame.DownAt(transform.position);
            var pos = transform.position;
            // stuck (not flipped): move to the nearest level, clear patch within ~10 m, preferring behind us
            float tilt = Vector3.Angle(transform.up, up);
            if (tilt < 40f && FindRecoverySpot != null)
            {
                var spot = FindRecoverySpot(pos, -transform.forward);
                if (spot.HasValue) pos = spot.Value;
            }
            // find the ground below (cast from a bit above, in case we're on our side / roof)
            if (Physics.Raycast(pos + up * 2.5f, -up, out var g, 10f, ~(1 << 2), QueryTriggerInteraction.Ignore)) pos = g.point;
            float headroom = 3f;
            if (Physics.Raycast(pos + up * 0.2f, up, out var ceil, 6f, ~(1 << 2), QueryTriggerInteraction.Ignore)) headroom = ceil.distance;
            float lift = Mathf.Clamp(headroom - 2.0f, 0.5f, 1.2f);
            var fwd = Vector3.ProjectOnPlane(transform.forward, up);
            if (fwd.sqrMagnitude < 1e-3f) fwd = Vector3.ProjectOnPlane(transform.up, up);
            PlaceAt(pos + up * lift, Quaternion.LookRotation(fwd.normalized, up));
            transform.position = pos + up * (lift + 0.45f);
            Body.position = transform.position;
        }

        /// <summary>Set by the game: (stuck position, preferred direction) -> a clear level spot nearby (world).</summary>
        public static System.Func<Vector3, Vector3, Vector3?> FindRecoverySpot;

        void FixedUpdate() => Step(Time.fixedDeltaTime);

        public void Step(float dt)
        {
            if (Body == null) Setup();
            Impact.Sample(Body.linearVelocity, Physics.gravity, dt);
            var up = transform.up;
            {
                // hull scraping on regolith while tilted: ploughing losses (keeps a flipped rover from tumbling on)
                float tilt = Vector3.Angle(up, -Physics.gravity.normalized);
                HullContact = tilt > 30f && TouchingHull();
                int onGround = 0; foreach (var w in wheels) if (w.grounded) onGround++;
                // tilted on two wheels: the tyres bulldoze sideways through the regolith (also lossy)
                bool edge = tilt > 25f && onGround > 0 && onGround <= 2;
                Body.angularDamping = HullContact || edge ? hullPloughAngular : angularDampingMild;
                Body.linearDamping = HullContact || edge ? hullPloughLinear : 0f;
            }
            var fwd = transform.forward;
            Vector3 gWorld = Physics.gravity;
            float gAxis = Vector3.Dot(gWorld, -up);     // gravity along the suspension axis (down)

            Speed = Vector3.Dot(Body.linearVelocity, fwd);
            float speedAbs = Mathf.Abs(Speed);

            // ---- steering: curvature command, double Ackermann
            float kMax = 1f / minTurnRadius;
            // lateral acceleration budget (rollover margin at 1/6 g); FAST: a smaller budget, the steering angle shrinks with speed
            float aLat = RangeMode == DriveRange.Fast ? 0.4f : 0.55f;
            if (speedAbs > 1f) kMax = Mathf.Min(kMax, aLat * Lunar.Gravity / (speedAbs * speedAbs) + (RangeMode == DriveRange.Fast ? 0.02f : 0.05f));
            float kappa = steering * kMax;
            float halfBase = 0.5f * Mathf.Abs(wheels[0].mount.z - wheels[2].mount.z);

            float power = 0;
            float cCompBase = 2f * dampingRatio * Mathf.Sqrt(springRate * SprungMass / 4f);
            float cComp = cCompBase * 2f / (1f + reboundBias);
            float cReb = cComp * reboundBias;

            foreach (var w in wheels)
            {
                // steering target and servo rate limit
                float zl = w.mount.z, xl = w.mount.x;
                float target;
                if (fourWheelSteer) target = Mathf.Atan2(zl * kappa, 1f - xl * kappa);
                else target = w.front ? Mathf.Atan2((zl + halfBase) * kappa, 1f - xl * kappa) : 0f;
                w.steer = Mathf.MoveTowards(w.steer, target, steerRate * dt);

                Vector3 mountW = transform.TransformPoint(w.mount);
                Vector3 axis = -up;
                float vMount = Vector3.Dot(Body.GetPointVelocity(mountW), axis);

                // ---- ground contact (sphere = tyre)
                const float castBack = 0.6f;
                bool hitGround = Physics.SphereCast(mountW - axis * castBack, wheelRadius, axis, out var hit,
                    castBack + travel + 0.25f, ~(1 << 2), QueryTriggerInteraction.Ignore);
                // ground far off the suspension axis (rover on its side / roof): the hull takes it, not the tyre
                if (hitGround && Vector3.Angle(hit.normal, up) > maxContactAngle) hitGround = false;
                float defl = 0;
                if (hitGround)
                {
                    float centreAtContact = hit.distance - castBack;    // wheel-centre extension where the tyre just touches
                    defl = Mathf.Min(w.x - centreAtContact, maxTyreDeflection);
                }
                w.grounded = hitGround && defl > 0;

                // ---- forces along the suspension axis
                float spring = springRate * (springFreeExtension - w.x);                  // + pushes wheel down / body up
                float relV = w.vAxis - vMount;                                            // + = extending (rebound)
                if (w.x < bumpStopStart)
                {
                    // hydraulic bump stop: full force in compression, little on the way back out (dissipative)
                    float d = bumpStopStart - w.x;
                    spring += (60000f * d + 4.0e6f * d * d) * (relV > 0 ? 0.3f : 1f);
                }
                float damp = -(relV > 0 ? cReb : cComp) * relV;
                float suspension = Mathf.Clamp(spring + damp + AntiRoll(w), -maxSuspensionForce, maxSuspensionForce);

                float tyreN = 0;
                if (w.grounded)
                {
                    float deflRate = (defl - w.prevDefl) / dt;
                    tyreN = Mathf.Clamp(tyreRate * defl + tyreDamping * deflRate, 0f, maxTyreForce);
                    // ground well off the suspension axis: the tyre edge can't push the rover back up
                    float cosN = Vector3.Dot(hit.normal, up);
                    tyreN *= Mathf.Clamp01((cosN - Mathf.Cos(maxContactAngle * Mathf.Deg2Rad)) /
                                           (Mathf.Cos(contactFadeStart * Mathf.Deg2Rad) - Mathf.Cos(maxContactAngle * Mathf.Deg2Rad)));
                    w.prevDefl = defl;
                }
                else w.prevDefl = 0;
                float nAxis = w.grounded ? Mathf.Max(0.05f, Vector3.Dot(hit.normal, up)) : 1f;
                float tyreAxis = tyreN * nAxis;     // component pushing the wheel up the axis

                // unsprung mass along the axis (semi-implicit)
                float acc = (suspension - tyreAxis) / unsprungMass + gAxis;
                w.vAxis += acc * dt;
                w.vAxis = vMount + Mathf.Clamp(w.vAxis - vMount, -maxStrokeRate, maxStrokeRate);
                w.x += (w.vAxis - vMount) * dt;
                if (w.x > travel)
                {
                    // droop limit: wheel hangs on the arms, momentum goes into the body
                    float relLimit = w.vAxis - vMount;
                    if (relLimit > 0) { Body.AddForceAtPosition(axis * (unsprungMass * relLimit / dt), mountW, ForceMode.Force); w.vAxis = vMount; }
                    w.x = travel;
                }
                if (w.x < 0f) { w.x = 0f; float relLimit = w.vAxis - vMount; if (relLimit < 0) { Body.AddForceAtPosition(axis * (unsprungMass * relLimit / dt), mountW, ForceMode.Force); w.vAxis = vMount; } }

                Body.AddForceAtPosition(up * suspension, mountW, ForceMode.Force);
                w.springForce = suspension;
                w.load = tyreN;

                // ---- wheel frame on the contact plane
                Vector3 steerFwd = Quaternion.AngleAxis(w.steer * Mathf.Rad2Deg, up) * fwd;
                Vector3 n = w.grounded ? hit.normal : up;
                Vector3 f = Vector3.ProjectOnPlane(steerFwd, n).normalized;
                Vector3 l = Vector3.Cross(n, f);
                Vector3 P = w.grounded ? hit.point : mountW + axis * (w.x + wheelRadius);
                w.contactPoint = P; w.contactNormal = n;

                // what we're standing on may itself move (crane platform): forces react on it (Newton 3)
                Rigidbody ground = w.grounded && hit.rigidbody != null && !hit.rigidbody.isKinematic ? hit.rigidbody : null;
                Vector3 groundVel = ground != null ? ground.GetPointVelocity(P) : Vector3.zero;
                if (ground != null) ground.AddForceAtPosition(-tyreN * n, P, ForceMode.Force);

                // tyre normal force component not along the axis is carried by the arms into the body
                if (w.grounded) Body.AddForceAtPosition(tyreN * (n - up * Vector3.Dot(n, up)), P, ForceMode.Force);

                // ---- drive torque
                float cmd = throttle;
                float tMotor = cmd * PeakTorqueNow;
                float wAbs = Mathf.Abs(w.omega);
                string lim = Mathf.Abs(cmd) > 0.98f ? "torque" : "throttle";
                if (wAbs * Mathf.Abs(tMotor) > peakPowerPerWheel) { tMotor = Mathf.Sign(tMotor) * peakPowerPerWheel / Mathf.Max(wAbs, 1e-3f); lim = "power"; }
                float vWheel = w.omega * wheelRadius;
                if (cmd * Speed > 0)
                {
                    float gov = Mathf.Clamp01((MaxSpeedNow - Mathf.Abs(Speed)) / 0.15f);   // speed governor (navigation ground speed)
                    if (gov < 0.99f) lim = "governor";
                    tMotor *= gov;
                }
                float tBrake = Mathf.Max(brake, parkingBrake ? 1f : 0f) * brakeTorque;

                // ---- soil contact
                float Fx = 0, Fy = 0;
                if (w.grounded)
                {
                    w.soil = GroundMaterial.At(P);
                    var tm = Terramechanics.Contact(w.soil, tyreN, wheelRadius, wheelWidth);
                    w.sinkage = tm.sinkage;
                    float rEff = wheelRadius - tm.sinkage * 0.5f;
                    Vector3 vP = Body.GetPointVelocity(P) - groundVel;   // slip relative to the ground under the wheel
                    float vx = Vector3.Dot(vP, f), vy = Vector3.Dot(vP, l);
                    float K = w.soil.K, L = Mathf.Max(0.02f, tm.contactLength), Fmax = tm.maxShear * Tuning.WheelGrip.Value;
                    float lo = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.8f, Mathf.Max(Mathf.Abs(vx), Mathf.Abs(w.omega * rEff))));

                    // longitudinal force as a function of wheel speed (for the implicit spin update)
                    float LongForce(float om, out float bx)
                    {
                        float vs = om * rEff - vx;                              // slip velocity (+ = driving)
                        float denom = Mathf.Max(Mathf.Max(Mathf.Abs(om * rEff), Mathf.Abs(vx)), SlipEps);
                        float s = Mathf.Clamp(vs / denom, -1f, 1f);
                        float fSlip = Mathf.Sign(s) * Fmax * Terramechanics.ShearFraction(s * L, K);
                        bx = w.bristleX + vs * dt;
                        float fBr = BristleK * bx + BristleC * vs;
                        if (Mathf.Abs(fBr) > Fmax) { fBr = Mathf.Sign(fBr) * Fmax; bx = (fBr - BristleC * vs) / BristleK; }
                        return Mathf.Lerp(fSlip, fBr, lo);
                    }

                    // implicit spin update with linearised soil reaction
                    float f0 = LongForce(w.omega, out _);
                    const float eps = 0.05f;
                    float f1 = LongForce(w.omega + eps, out _);
                    float G = Mathf.Max(0f, (f1 - f0) / eps);
                    float denomI = wheelInertia + dt * rEff * G;
                    float omegaFree = w.omega + dt * (tMotor - rEff * f0) / denomI;
                    float brakeDelta = tBrake * dt / denomI;
                    float omegaNew = Mathf.Abs(omegaFree) <= brakeDelta ? 0f : omegaFree - Mathf.Sign(omegaFree) * brakeDelta;
                    Fx = LongForce(omegaNew, out float bxNew);
                    w.bristleX = lo > 0.001f ? bxNew : 0f;
                    w.omega = omegaNew;
                    w.slip = Mathf.Clamp((omegaNew * rEff - vx) / Mathf.Max(Mathf.Max(Mathf.Abs(omegaNew * rEff), Mathf.Abs(vx)), SlipEps), -1f, 1f);
                    if (Mathf.Abs(w.slip) > 0.35f) lim = "grip";   // wheelspin: the soil shear limit, whatever the motor does

                    // compaction (rolling) resistance opposes travel
                    float rr = tm.compaction * (float)math.tanh(vx / 0.05f);

                    // lateral
                    float tanA = vy / Mathf.Max(Mathf.Abs(vx), SlipEps);
                    w.slipAngle = Mathf.Atan(tanA);
                    float fySlip = -Mathf.Sign(vy) * Fmax * Terramechanics.ShearFraction(L * tanA, K);
                    float by = w.bristleY + vy * dt;
                    float fyBr = -(BristleK * by + BristleC * vy);
                    if (Mathf.Abs(fyBr) > Fmax) { fyBr = Mathf.Sign(fyBr) * Fmax; by = -(fyBr + BristleC * vy) / BristleK; }
                    float loY = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.8f, Mathf.Abs(vx)));
                    Fy = Mathf.Lerp(fySlip, fyBr, loY);
                    w.bristleY = loY > 0.001f ? by : 0f;

                    // friction ellipse
                    float mag = Mathf.Sqrt(Fx * Fx + Fy * Fy);
                    if (mag > Fmax && mag > 1e-3f) { float k = Fmax / mag; Fx *= k; Fy *= k; }

                    var Ft = f * (Fx - rr) + l * Fy;
                    Body.AddForceAtPosition(Ft, P, ForceMode.Force);
                    if (ground != null) ground.AddForceAtPosition(-Ft, P, ForceMode.Force);
                }
                else
                {
                    w.sinkage = 0; w.slip = 0; w.slipAngle = 0;
                    float omegaFree = w.omega + dt * tMotor / wheelInertia;
                    float brakeDelta = tBrake * dt / wheelInertia + 0.5f * dt / wheelInertia;   // + bearing drag
                    w.omega = Mathf.Abs(omegaFree) <= brakeDelta ? 0f : omegaFree - Mathf.Sign(omegaFree) * brakeDelta;
                    w.bristleX = w.bristleY = 0;
                }
                w.fx = Fx; w.fy = Fy; w.motorTorque = tMotor; w.limit = Mathf.Abs(cmd) < 0.02f ? "" : lim;

                // electrical power: motoring draws, braking with the motor regenerates
                float pm = tMotor * w.omega;
                power += pm > 0 ? pm / motorEfficiency : pm * regenEfficiency;
                w.spinAngle += w.omega * dt;
            }

            PowerDraw = power + avionicsWatts + extraLoadWatts;
            double wh = PowerDraw * dt / 3600.0;
            EnergyUsedWh += wh;
            stateOfCharge = Mathf.Clamp01(stateOfCharge - (float)(wh / batteryCapacityWh));
            DistanceM += speedAbs * dt;
            if (stateOfCharge <= 0f) throttle = 0f;
        }

        void LateUpdate()
        {
            // visuals
            if (wheels == null) return;
            foreach (var w in wheels)
            {
                if (w.steerPivot != null)
                {
                    w.steerPivot.localPosition = w.mount - Vector3.up * (w.x + w.sinkage * 0.5f);
                    w.steerPivot.localRotation = Quaternion.Euler(0, w.steer * Mathf.Rad2Deg, 0);
                }
                if (w.spinner != null) w.spinner.localRotation = Quaternion.Euler(w.spinAngle * Mathf.Rad2Deg, 0, 0);
            }
        }
    }
}
