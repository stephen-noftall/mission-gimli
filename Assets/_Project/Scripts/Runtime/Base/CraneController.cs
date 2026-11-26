using System.Collections.Generic;
using UnityEngine;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha.Base
{
    /// <summary>
    /// Pit crane, after NASA Langley's Lunar Surface Manipulation System (LSMS) concept: a truss mast and
    /// slewing truss boom, cable winch, and a vehicle platform on slings. Delivered by the cargo lander and
    /// assembled on the rim (pre-set-up here).
    ///
    /// Physics: the platform is a rigid body hanging from the boom tip on a rope (max-distance joint), so it
    /// swings like a real pendulum in 1/6 g (period 2*pi*sqrt(L/g) ~ 30 s at 40 m); slewing is slow with
    /// smooth acceleration and the joint carries light anti-sway damping (tag-line / controller).
    /// Cycle: on the load pad -> [L] close the end gates, hoist, slew over the shaft, lower ~45 m to the pit
    /// floor; [G] opens the gates (manual); [U] the same back up to the pad. [K] skips a ride to its end state.
    /// Winch: jerk-limited (accel 0.25 m/s^2, jerk 0.3 m/s^3), creeping for the last metres before touchdown.
    /// Slew: input-shaped - each accel/decel ramp lasts exactly one pendulum period, so no residual swing.
    /// </summary>
    public class CraneController : MonoBehaviour
    {
        public enum State { Parked, Hoisting, SlewingOut, Lowering, AtBottom, RaisingUp, SlewingIn, Settling, ClosingGates }

        public float boomLength = 46f;
        public float mastHeight = 18f;
        public float slingHeight = 4.5f;
        /// <summary>Half the spreader truss length along the deck (P4: ~4.5 m on the 6.5 m deck: near-vertical slings).</summary>
        public const float SpreaderHalf = 2.25f;
        public float winchSpeed = 2.4f;       // m/s (exhibit pacing)
        public float slewRate = 2.0f;         // deg/s (max): centripetal + ramp offsets of the load stay < 1 m
        public float winchAccel = 0.25f;      // m/s^2 (rover vertical accel stays well under 0.3 g)
        public float winchJerk = 0.6f;        // m/s^3
        public float touchdownSpeed = 0.1f;   // m/s creep for the last metre (touchdown stays under 0.3 g)
        public float gateOpenDeg = 100f, gateTime = 2f;
        public float padAngle, pitAngle;      // slew angles (deg, about local up)
        /// <summary>World direction the platform's exit ramp should face at the bottom (the tube mouth).</summary>
        public Vector3 bottomExitDir;
        Quaternion jointStartLocal;           // platform rotation relative to the hook when the joint was made
        float rotatorYaw;                     // current load-rotator yaw (deg)

        // \ tuning: "crane speed" scales the whole ride; "crane sway" scales the anti-sway damping
        static float K => Tuning.CraneSpeed.Value;
        float WinchSpeedNow => winchSpeed * K;
        float WinchAccelNow => Mathf.Min(winchAccel * K, 0.27f);  // rover stays < 0.3 g (0.49 m/s^2) at any setting (its suspension amplifies ~1.7x)
        float WinchJerkNow => Mathf.Min(winchJerk * K, 0.6f);     // round-7 jerk: a soft lift-off
        // the slew's centripetal offset grows with the square of the rate: capped at x1.3 so the load stays < 1 m off the vertical
        float SlewRateNow => slewRate * Mathf.Min(K, 1.15f);
        float GateTimeNow => gateTime / K;
        float appliedSway = -1f, settleT;
        /// <summary>Horizontal offset of the sling apex from the vertical under the boom tip (m).</summary>
        float SwayNow(Vector3 anchorW) => Vector3.ProjectOnPlane(anchorW - TipWorld, transform.up).magnitude;

        public State state = State.Parked;
        public Rigidbody Platform { get; private set; }
        public string Prompt { get; private set; }
        public float CableLength => joint != null ? joint.linearLimit.limit : 0f;

        Transform boom, mast;
        Rigidbody hook;
        ConfigurableJoint joint;
        LineRenderer cable;
        LineRenderer[] slings;
        float slew, slewVel;
        float winchVel, winchAcc;              // + = paying out
        float slewFrom, slewTo, slewT, slewDur, slewRamp;   // shaped slew plan
        bool slewPlanned;
        State afterGates;
        float gateAngle = 100f;                // current (0 = closed)
        public float GateAngle => gateAngle;
        public bool GatesClosed => gateAngle <= 0.5f;
        public bool GatesOpen => gateAngle >= gateOpenDeg - 0.5f;
        /// <summary>At the bottom the gates stay shut until the crew releases them ([G]; manual since round 9).</summary>
        public bool GatesHeld { get; private set; }
        /// <summary>[G] at the bottom: swing the end gates open.</summary>
        public void OpenGates() { if (state == State.AtBottom) GatesHeld = false; }
        /// <summary>Radio.Now when the platform came to rest on the pit floor (-1: not yet).</summary>
        public float BottomTime { get; private set; } = -1f;
        public float WinchSpeed => winchVel;
        float rimClearY;                       // local Y the platform must clear when slewing (rim + margin)
        Transform roverT;
        readonly RaycastHit[] hits = new RaycastHit[8];
        readonly List<Transform> gates = new();
        public IReadOnlyList<Transform> Gates => gates;

        public static CraneController Build(Transform parent, Vector3 basePos, Quaternion baseRot, float padAngleDeg, float pitAngleDeg, float rimClearLocalY)
        {
            var root = new GameObject("PitCrane (LSMS-type)");
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(basePos, baseRot);
            var c = root.AddComponent<CraneController>();
            c.padAngle = padAngleDeg; c.pitAngle = pitAngleDeg; c.slew = padAngleDeg; c.rimClearY = rimClearLocalY;
            c.BuildVisuals();
            c.BuildPlatform();
            return c;
        }

        void BuildVisuals()
        {
            var steel = Mats.Make("CraneTruss", new Color(0.75f, 0.76f, 0.78f), 0.8f, 0.45f, Mats.Brushed, 0.3f);
            var yellow = Mats.Make("CraneYellow", new Color(0.85f, 0.68f, 0.12f), 0.2f, 0.4f);
            var dark = Mats.Make("CraneDark", new Color(0.08f, 0.08f, 0.09f), 0.5f, 0.4f);
            var bag = Mats.Make("RegolithBags", new Color(0.5f, 0.48f, 0.44f), 0f, 0.15f, Mats.Fabric, 1f);
            mast = new GameObject("Mast").transform;
            mast.SetParent(transform, false);
            // tripod base with outriggers + regolith-bag ballast
            for (int i = 0; i < 4; i++)
            {
                var q = Quaternion.Euler(0, i * 90 + 45, 0);
                BaseBuilder.Tube(mast, "Outrigger", 0.12f, steel, q * new Vector3(0, 1.2f, 0.6f), q * new Vector3(0, 0.2f, 5.5f));
                BaseBuilder.Box(mast, "Pad", q * new Vector3(0, 0.12f, 5.5f), new Vector3(1.2f, 0.24f, 1.2f), dark);
                for (int k = 0; k < 3; k++)
                    BaseBuilder.Box(mast, "Ballast", q * new Vector3(0, 0.35f + k * 0.45f, 4.2f), new Vector3(1.3f, 0.42f, 0.9f), bag, new Vector3(0, i * 90 + 45 + k * 7, 0));
            }
            // lattice mast
            float h = mastHeight, w = 0.9f;
            var corners = new[] { new Vector3(-w, 0, -w), new Vector3(w, 0, -w), new Vector3(w, 0, w), new Vector3(-w, 0, w) };
            foreach (var cn in corners) BaseBuilder.Tube(mast, "MastChord", 0.07f, steel, cn + Vector3.up * 0.8f, cn * 0.6f + Vector3.up * h);
            for (float y = 1.5f; y < h; y += 2.2f)
                for (int i = 0; i < 4; i++)
                {
                    float s = Mathf.Lerp(1f, 0.6f, y / h);
                    BaseBuilder.Tube(mast, "MastLace", 0.035f, steel, corners[i] * s + Vector3.up * y, corners[(i + 1) % 4] * s + Vector3.up * (y + 1.1f));
                }
            BaseBuilder.Box(mast, "SlewBearing", new Vector3(0, h, 0), new Vector3(1.6f, 0.5f, 1.6f), yellow);
            BaseBuilder.Box(mast, "Winch", new Vector3(0, 1.4f, -1.3f), new Vector3(1.4f, 1.0f, 1.0f), yellow);
            BaseBuilder.Box(mast, "PowerBox", new Vector3(1.3f, 0.9f, -1.1f), new Vector3(0.7f, 0.9f, 0.6f), dark);
            // slewing boom: triangular truss
            boom = new GameObject("Boom").transform;
            boom.SetParent(transform, false);
            boom.localPosition = new Vector3(0, h + 0.3f, 0);
            float L = boomLength;
            var b0 = new Vector3(-0.5f, 0, 0); var b1 = new Vector3(0.5f, 0, 0); var b2 = new Vector3(0, 0.9f, 0);
            foreach (var cn in new[] { b0, b1, b2 }) BaseBuilder.Tube(boom, "BoomChord", 0.06f, steel, cn + new Vector3(0, 0, -6f), cn * 0.5f + new Vector3(0, 0, L));
            for (float z = -5.5f; z < L - 1; z += 2f)
            {
                float s = Mathf.Lerp(1f, 0.5f, (z + 6f) / (L + 6f));
                BaseBuilder.Tube(boom, "BoomLace", 0.03f, steel, b0 * s + new Vector3(0, 0, z), b2 * s + new Vector3(0, 0, z + 1f));
                BaseBuilder.Tube(boom, "BoomLace", 0.03f, steel, b1 * s + new Vector3(0, 0, z), b2 * s + new Vector3(0, 0, z + 1f));
                BaseBuilder.Tube(boom, "BoomLace", 0.03f, steel, b0 * s + new Vector3(0, 0, z), b1 * s + new Vector3(0, 0, z + 1f));
            }
            // LSMS-style tension stays from a king post
            BaseBuilder.Tube(boom, "KingPost", 0.08f, steel, Vector3.zero, new Vector3(0, 5f, 0));
            BaseBuilder.Tube(boom, "Stay", 0.025f, dark, new Vector3(0, 5f, 0), new Vector3(0, 0.45f, L));
            BaseBuilder.Tube(boom, "Stay", 0.025f, dark, new Vector3(0, 5f, 0), new Vector3(0, 0.6f, -6f));
            BaseBuilder.Box(boom, "Counterweight", new Vector3(0, -0.4f, -6.5f), new Vector3(2.2f, 1.2f, 1.6f), bag);
            BaseBuilder.Cyl(boom, "Sheave", new Vector3(0, -0.2f, L), 0.6f, 0.2f, yellow, new Vector3(0, 0, 90));

            cable = NewLine("Cable", 0.035f);
            slings = new LineRenderer[4];
            for (int i = 0; i < 4; i++) slings[i] = NewLine("Sling", 0.02f);
        }

        LineRenderer NewLine(string n, float width)
        {
            var go = new GameObject(n);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = Mats.Make("CableSteel", new Color(0.15f, 0.15f, 0.16f), 0.8f, 0.4f);
            lr.startWidth = lr.endWidth = width;
            lr.positionCount = 2;
            lr.useWorldSpace = true;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return lr;
        }

        void BuildPlatform()
        {
            var steel = Mats.Make("PlatformDeck", new Color(0.6f, 0.61f, 0.63f), 0.7f, 0.4f, Mats.Brushed, 0.5f);
            var yellow = Mats.Textured(Mats.Make("PlatformRails", Color.white, 0.2f, 0.4f), ProcTex.Paint("CraneYellowPaint", new Color(0.85f, 0.68f, 0.12f), 41, 0.45f, 0.4f));
            // round 12 (P4): steel plate with weld seams, bolt rows, scuffs and dust (deck sides / underside, beams)
            var plate = Mats.Textured(Mats.Make("PlatformPlate", Color.white, 0.7f, 0.35f), ProcTex.SteelPlate(42));
            var beamSteel = Mats.Textured(Mats.Make("PlatformBeam", Color.white, 0.75f, 0.35f), ProcTex.Paint("BeamSteel", new Color(0.42f, 0.43f, 0.45f), 43, 0.35f, 0.55f));
            var aluTube = Mats.Textured(Mats.Make("SpreaderAlu", Color.white, 0.9f, 0.55f, Mats.Brushed, 0.4f), ProcTex.Paint("SpreaderAluTex", new Color(0.78f, 0.79f, 0.81f), 44, 0.1f, 0.25f));
            var mesh = Mats.Cutout(Mats.Make("GateMeshWire", Color.white, 0.8f, 0.45f), Mats.Wire, new Vector2(6, 1.4f));
            // hook (kinematic, follows the boom tip)
            var hookGo = new GameObject("Hook");
            hookGo.transform.SetParent(transform, false);
            hook = hookGo.AddComponent<Rigidbody>();
            hook.isKinematic = true;
            hook.interpolation = RigidbodyInterpolation.Interpolate;
            BaseBuilder.Box(hookGo.transform, "HookBlock", Vector3.zero, new Vector3(0.5f, 0.7f, 0.4f), yellow);
            BaseBuilder.Box(hookGo.transform, "HookSheaveCover", new Vector3(0, 0.2f, 0), new Vector3(0.56f, 0.3f, 0.12f), beamSteel);
            BaseBuilder.Box(hookGo.transform, "Hook", new Vector3(0, -0.5f, 0), new Vector3(0.1f, 0.35f, 0.16f), beamSteel);

            var pGo = new GameObject("CranePlatform");
            pGo.transform.SetParent(transform, false);
            Platform = pGo.AddComponent<Rigidbody>();
            Platform.mass = 700f;
            Platform.linearDamping = 0f; Platform.angularDamping = 0.3f;
            Platform.interpolation = RigidbodyInterpolation.Interpolate;
            Platform.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            // deck 6.5 x 4.2 m, rails along the long sides, ramps at both ends
            // deck: brushed tread on top, steel plate on the sides and underneath (P4)
            BaseBuilder.Box(pGo.transform, "DeckTop", new Vector3(0, 0.232f, 0), new Vector3(4.2f, 0.016f, 6.5f), steel);
            var deckBody = BaseBuilder.Box(pGo.transform, "DeckBody", new Vector3(0, 0.112f, 0), new Vector3(4.2f, 0.224f, 6.5f), plate);
            AddBox(pGo, new Vector3(0, 0.12f, 0), new Vector3(4.2f, 0.24f, 6.5f));
            // underside structure: two longitudinal I-beams + cross members (visual; inside the deck's collider footprint)
            foreach (float bx in new[] { -1.25f, 1.25f })
            {
                BaseBuilder.Box(pGo.transform, "IBeamTopFlange", new Vector3(bx, 0.0f, 0), new Vector3(0.22f, 0.025f, 6.3f), beamSteel);
                BaseBuilder.Box(pGo.transform, "IBeamWeb", new Vector3(bx, -0.12f, 0), new Vector3(0.025f, 0.22f, 6.3f), beamSteel);
                BaseBuilder.Box(pGo.transform, "IBeamBottomFlange", new Vector3(bx, -0.24f, 0), new Vector3(0.22f, 0.025f, 6.3f), beamSteel);
            }
            for (int k = 0; k < 5; k++)
                BaseBuilder.Box(pGo.transform, "CrossMember", new Vector3(0, -0.07f, -2.8f + k * 1.4f), new Vector3(4.0f, 0.14f, 0.1f), beamSteel);
            foreach (float s in new[] { -1f, 1f })
            {
                BaseBuilder.Box(pGo.transform, "SideRail", new Vector3(s * 2.15f, 0.85f, 0), new Vector3(0.1f, 1.3f, 6.5f), yellow);
                AddBox(pGo, new Vector3(s * 2.15f, 0.85f, 0), new Vector3(0.1f, 1.3f, 6.5f));
                // ramp: outer end down on the ground, inner end level with the deck top (0.24 m)
                BaseBuilder.Box(pGo.transform, "Ramp", new Vector3(0, 0.12f, s * 3.95f), new Vector3(3.6f, 0.06f, 1.6f), steel, new Vector3(s * 8.5f, 0, 0));
                AddBox(pGo, new Vector3(0, 0.12f, s * 3.95f), new Vector3(3.6f, 0.06f, 1.6f), new Vector3(s * 8.5f, 0, 0));
            }
            // end gates: hinged on a side post, swing 100 deg outward (clear of the ramp); closed while the crane moves
            foreach (float s in new[] { -1f, 1f })
            {
                var hinge = new GameObject("EndGateHinge");
                hinge.transform.SetParent(pGo.transform, false);
                hinge.transform.localPosition = new Vector3(s * 2.15f, 0.8f, s * 3.2f);
                BaseBuilder.Box(hinge.transform, "HingePost", new Vector3(0, 0.05f, 0), new Vector3(0.1f, 1.0f, 0.1f), yellow);
                var gate = new GameObject("EndGate").transform;
                gate.SetParent(hinge.transform, false);
                gate.localPosition = new Vector3(-s * 2.1f, 0, 0);
                BaseBuilder.Box(gate, "GateBar", new Vector3(0, 0.35f, 0), new Vector3(4.2f, 0.12f, 0.12f), yellow);
                BaseBuilder.Box(gate, "GateBar", new Vector3(0, -0.25f, 0), new Vector3(4.2f, 0.12f, 0.12f), yellow);
                BaseBuilder.Box(gate, "GateMesh", new Vector3(0, 0.05f, 0), new Vector3(4.1f, 0.9f, 0.02f), mesh);
                var gc = gate.gameObject.AddComponent<BoxCollider>();
                gc.center = new Vector3(0, 0.1f, 0); gc.size = new Vector3(4.2f, 1.6f, 0.25f);
                gates.Add(hinge.transform);
            }
            ApplyGates();
            // spreader (P4): an aluminium truss along the deck (top + bottom chords, diagonals), steel end fittings with
            // lifting lugs (two slings each to the deck corners: near-vertical), a centre lug for the main cable
            {
                var sp = new GameObject("Spreader").transform; sp.SetParent(pGo.transform, false);
                float yt = slingHeight - 0.05f, yb = slingHeight - 0.45f, L = SpreaderHalf;
                foreach (float cx in new[] { -0.12f, 0.12f })
                {
                    BaseBuilder.Tube(sp, "TopChord", 0.035f, aluTube, new Vector3(cx, yt, -L), new Vector3(cx, yt, L));
                    BaseBuilder.Tube(sp, "BottomChord", 0.035f, aluTube, new Vector3(cx, yb, -L), new Vector3(cx, yb, L));
                    for (int k = 0; k < 8; k++)
                    {
                        float z0 = -L + 2f * L * k / 8f, z1 = -L + 2f * L * (k + 1) / 8f;
                        BaseBuilder.Tube(sp, "Diagonal", 0.02f, aluTube, new Vector3(cx, k % 2 == 0 ? yb : yt, z0), new Vector3(cx, k % 2 == 0 ? yt : yb, z1));
                    }
                }
                for (int k = 0; k <= 8; k += 2) BaseBuilder.Tube(sp, "Strut", 0.018f, aluTube, new Vector3(-0.12f, yb, -L + 2f * L * k / 8f), new Vector3(0.12f, yt, -L + 2f * L * k / 8f));
                foreach (float s in new[] { -1f, 1f })
                {
                    BaseBuilder.Box(sp, "EndFitting", new Vector3(0, (yt + yb) / 2f, s * L), new Vector3(0.34f, 0.5f, 0.12f), beamSteel);
                    BaseBuilder.Box(sp, "LugBeam", new Vector3(0, yb - 0.1f, s * L), new Vector3(2.0f, 0.12f, 0.1f), beamSteel);   // short cross beam: lugs at +-0.9 m
                }
                BaseBuilder.Box(sp, "CentreLug", new Vector3(0, yt + 0.1f, 0), new Vector3(0.06f, 0.2f, 0.16f), beamSteel);
            }
            Platform.automaticCenterOfMass = false;
            Platform.centerOfMass = new Vector3(0, 0.15f, 0);

            CreateJoint(2f);
        }


        /// <summary>(Re)create the rope + anti-sway joint so its rest frame is the platform's current orientation.</summary>
        void CreateJoint(float cableLength)
        {
            if (joint != null) Util.Kill(joint);
            var pGo = Platform.gameObject;
            jointStartLocal = Quaternion.Inverse(hook.transform.rotation) * Platform.transform.rotation;
            rotatorYaw = 0f;
            joint = pGo.AddComponent<ConfigurableJoint>();
            joint.connectedBody = hook;
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = new Vector3(0, slingHeight, 0);
            joint.connectedAnchor = Vector3.zero;
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Limited;
            joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.linearLimit = new SoftJointLimit { limit = cableLength, bounciness = 0f, contactDistance = 0.05f };
            joint.linearLimitSpring = new SoftJointLimitSpring { spring = 0f, damper = 0f };
            // light anti-sway damping relative to the hook
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.targetRotation = Quaternion.identity;
            appliedSway = -1f;
            ApplySway();
            joint.enableCollision = false;
        }

        /// <summary>
        /// Anti-sway control strength (x the "crane sway" tuning). 1 = the round-7 damping (swing dies within a cycle);
        /// ~0.4 = a lazy pendulum swing that decays over a few cycles (~33 s period on 45 m of cable).
        /// The cable-pendulum damping scales fully; the deck-levelling sling spring / damping (the rocking mode) only a
        /// little, so the deck does not rock.
        /// </summary>
        void ApplySway()
        {
            float s = Tuning.CraneSway.Value;
            if (joint == null || Mathf.Approximately(s, appliedSway)) return;
            appliedSway = s;
            // stability limit (tested at the slowest setting): the pendulum damping never drops below 0.6x round 7
            joint.xDrive = joint.zDrive = new JointDrive { positionSpring = 0f, positionDamper = 6000f * Mathf.Lerp(0.6f, 1f, s), maximumForce = 15000f };
            // deck rocking (sling geometry + tag lines) is damped harder than in round 7: with a lazier cable pendulum the
            // two modes couple, and the rocking would otherwise grow to ~10 deg during the slew
            joint.slerpDrive = new JointDrive { positionSpring = 60000f, positionDamper = 60000f, maximumForce = 1e6f };
            if (Platform != null) Platform.angularDamping = 0.6f;
        }

        static void AddBox(GameObject go, Vector3 c, Vector3 s, Vector3? euler = null)
        {
            // child collider so the ramps can be rotated
            var child = new GameObject("Col");
            child.transform.SetParent(go.transform, false);
            child.transform.localPosition = c;
            child.transform.localRotation = Quaternion.Euler(euler ?? Vector3.zero);
            var b = child.AddComponent<BoxCollider>();
            b.size = s;
            b.sharedMaterial = new PhysicsMaterial("Deck") { dynamicFriction = 0.7f, staticFriction = 0.8f };
        }

        Vector3 TipLocal(float slewDeg) => Quaternion.Euler(0, slewDeg, 0) * new Vector3(0, mastHeight + 0.1f, boomLength);
        public Vector3 TipWorld => transform.TransformPoint(TipLocal(slew));
        public Vector3 PadWorld => transform.TransformPoint(Quaternion.Euler(0, padAngle, 0) * new Vector3(0, 0, boomLength));

        /// <summary>Put the platform on the load pad (resting on the ground, cable slack).</summary>
        public void ResetToPad()
        {
            TieDown(false);
            slew = padAngle; slewVel = 0;
            boom.localRotation = Quaternion.Euler(0, slew, 0);
            var tip = TipWorld;
            hook.position = tip; hook.transform.position = tip;
            var up = transform.up;
            var pad = PadWorld;
            // the ground under the pad: skip the crane's own parts (platform rails, gates, deck) and any
            // dynamic body (the rover may sit on the deck), else the platform lands on its own rails
            float best = float.MaxValue;
            foreach (var h in Physics.RaycastAll(pad + up * 10f, -up, 30f, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.IsChildOf(transform)) continue;
                var arb = h.collider.attachedRigidbody;
                if (arb != null && !arb.isKinematic) continue;
                if (h.distance < best) { best = h.distance; pad = h.point; }
            }
            Platform.transform.SetPositionAndRotation(pad + up * 0.05f, transform.rotation * Quaternion.Euler(0, padAngle, 0));
            Platform.position = Platform.transform.position; Platform.rotation = Platform.transform.rotation;
            Platform.linearVelocity = Vector3.zero; Platform.angularVelocity = Vector3.zero;
            CreateJoint(Vector3.Distance(tip, Platform.transform.TransformPoint(new Vector3(0, slingHeight, 0))) + 0.6f);
            state = State.Parked;
            winchVel = winchAcc = 0; slewPlanned = false;
            gateAngle = gateOpenDeg; ApplyGates(); GatesHeld = false;
        }

        void SetCable(float len)
        {
            var l = joint.linearLimit; l.limit = Mathf.Max(0.5f, len); joint.linearLimit = l;
        }

        public void SetRover(Transform rover) => roverT = rover;

        public bool RoverOnPlatform
        {
            get
            {
                if (roverT == null) return false;
                var local = Platform.transform.InverseTransformPoint(roverT.position);
                return Mathf.Abs(local.x) < 1.5f && Mathf.Abs(local.z) < 1.6f && local.y > 0f && local.y < 1.6f;
            }
        }

        /// <summary>Safety interlock: only move with the rover fully on the deck, or well clear of it.</summary>
        public bool SafeToMove => roverT == null || RoverOnPlatform || Vector3.Distance(roverT.position, Platform.position) > 9f;
        public bool RoverFullyOnDeck
        {
            get
            {
                if (roverT == null) return false;
                var local = Platform.transform.InverseTransformPoint(roverT.position);
                return Mathf.Abs(local.x) < 1.2f && Mathf.Abs(local.z) < 1.4f && local.y > 0f && local.y < 1.5f;
            }
        }
        // no wait after L / U: the winch starts at once and the gates close while the slack is taken up;
        // the platform only lifts off once both gates are shut (interlock in the hoist)
        public void CommandLower() { if (state == State.Parked && SafeToMove) { CommandTime = Time.time; afterGates = State.Hoisting; state = State.Hoisting; TieDown(true); descentFloor0 = -1f; shudderDone = false; shudderT = -1f; } }
        public void CommandRaise() { if (state == State.AtBottom && SafeToMove) { CommandTime = Time.time; afterGates = State.RaisingUp; state = State.RaisingUp; TieDown(true); } }
        public float CommandTime { get; private set; } = -1f;

        // scripted event: a brake slip at ~60% of the descent (5 cm, safe), then Mission Control
        float descentFloor0 = -1f, shudderT = -1f;
        bool shudderDone;
        public const float ShudderSlip = 0.05f, ShudderDuration = 0.4f;
        public bool ShudderActive => shudderT >= 0f && shudderT < 2.5f;
        public float ShudderProgress { get; private set; } = -1f;   // fraction of the descent when it happened
        public float ShudderTime { get; private set; } = -1f;       // Radio.Now when it happened

        ConfigurableJoint tie;
        public bool TiedDown => tie != null;
        /// <summary>Wheel tie-downs: hold the rover's wheels against sliding on the deck while the crane moves (vertical free: it rides on its own suspension).</summary>
        void TieDown(bool on)
        {
            if (!on) { if (tie != null) Util.Kill(tie); tie = null; return; }
            if (tie != null || roverT == null || !RoverOnPlatform) return;
            var rb = roverT.GetComponent<Rigidbody>();
            if (rb == null) return;
            tie = rb.gameObject.AddComponent<ConfigurableJoint>();
            tie.connectedBody = Platform;
            tie.axis = Vector3.right; tie.secondaryAxis = Vector3.up;
            tie.autoConfigureConnectedAnchor = true;
            tie.xMotion = ConfigurableJointMotion.Locked;
            tie.yMotion = ConfigurableJointMotion.Free;
            tie.zMotion = ConfigurableJointMotion.Locked;
            tie.angularXMotion = tie.angularYMotion = tie.angularZMotion = ConfigurableJointMotion.Free;
            tie.enableCollision = true;
        }

        bool GoingDown => state == State.Hoisting || state == State.SlewingOut || state == State.Lowering || (state == State.ClosingGates && afterGates == State.Hoisting);
        public bool Moving => state != State.Parked && state != State.AtBottom;

        /// <summary>[K] skip the ride: jump to its end state (platform on the ground, gates open, rover at rest on the deck).</summary>
        public void SkipToEnd()
        {
            if (!Moving) return;
            bool down = GoingDown;
            TieDown(false);
            var up = transform.up;
            Vector3 rel = Vector3.zero; Quaternion relRot = Quaternion.identity;
            bool carry = roverT != null && Vector3.Distance(roverT.position, Platform.position) < 6f;
            if (carry) { rel = Platform.transform.InverseTransformPoint(roverT.position); relRot = Quaternion.Inverse(Platform.rotation) * roverT.rotation; }
            if (!down) ResetToPad();
            else
            {
                slew = pitAngle; slewVel = 0; slewPlanned = false;
                boom.localRotation = Quaternion.Euler(0, slew, 0);
                var tip = TipWorld;
                hook.position = tip; hook.transform.position = tip;
                // the floor under the tip (skip the crane's own parts)
                Vector3 ground = tip - up * 45f; float best = float.MaxValue;
                foreach (var h in Physics.RaycastAll(tip, -up, 120f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                    if (!h.collider.transform.IsChildOf(transform) && h.distance < best) { best = h.distance; ground = h.point; }
                var r0 = transform.rotation * Quaternion.Euler(0, padAngle, 0);
                Platform.transform.SetPositionAndRotation(ground + up * 0.05f, r0);
                Platform.position = Platform.transform.position; Platform.rotation = r0;
                Platform.linearVelocity = Vector3.zero; Platform.angularVelocity = Vector3.zero;
                CreateJoint(Vector3.Distance(tip, Platform.transform.TransformPoint(new Vector3(0, slingHeight, 0))) + 1.0f);
                // load rotator to its end position (same rule as during the descent)
                bool backwards = carry && Vector3.Dot(r0 * relRot * Vector3.forward, r0 * Vector3.forward) < 0f;
                float a = RotatorTarget(up, backwards);
                rotatorYaw = a;
                var desiredLocal = Quaternion.Inverse(hook.transform.rotation) * Quaternion.AngleAxis(rotatorYaw, up) * (hook.transform.rotation * jointStartLocal);
                joint.targetRotation = Quaternion.Inverse(desiredLocal) * jointStartLocal;
                Platform.rotation = Quaternion.AngleAxis(a, up) * r0; Platform.transform.rotation = Platform.rotation;
                state = State.AtBottom; GatesHeld = true; BottomTime = Radio.Now;
            }
            winchVel = winchAcc = 0;
            gateAngle = GatesHeld && state == State.AtBottom ? 0f : gateOpenDeg; ApplyGates();
            if (carry)
            {
                var rb = roverT.GetComponent<Rigidbody>();
                var p = Platform.transform.TransformPoint(rel) + up * 0.05f; var q = Platform.rotation * relRot;
                roverT.SetPositionAndRotation(p, q);
                if (rb != null) { rb.position = p; rb.rotation = q; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            }
            Physics.SyncTransforms();
        }

        float RotatorTarget(Vector3 up, bool roverBackwards)
        {
            var platFwd0 = Vector3.ProjectOnPlane(transform.rotation * Quaternion.Euler(0, padAngle, 0) * Vector3.forward, up);
            var fwdNow = roverBackwards ? -platFwd0 : platFwd0;
            return Vector3.SignedAngle(fwdNow, Vector3.ProjectOnPlane(bottomExitDir, up), up);
        }

        void ApplyGates()
        {
            foreach (var g in gates) g.localRotation = Quaternion.Euler(0, gateAngle, 0);
        }

        /// <summary>Jerk-limited winch towards a target payout speed (+ out, - in).</summary>
        void Winch(float targetVel, float dt)
        {
            float wantAcc = Mathf.Clamp((targetVel - winchVel) / 0.6f, -WinchAccelNow, WinchAccelNow);
            winchAcc = Mathf.MoveTowards(winchAcc, wantAcc, WinchJerkNow * dt);
            float nv = winchVel + winchAcc * dt;
            if ((winchVel - targetVel) * (nv - targetVel) < 0) { nv = targetVel; winchAcc = 0; }   // no overshoot
            winchVel = nv;
            SetCable(CableLength + winchVel * dt);
        }

        /// <summary>Speed that can still stop within <paramref name="dist"/> at the winch's deceleration.</summary>
        float StopSpeed(float dist) => Mathf.Sqrt(2f * WinchAccelNow * 0.8f * Mathf.Max(0f, dist));

        float FloorBelowPlatform()
        {
            var up = transform.up;
            float best = 999f;
            foreach (var h in Physics.RaycastAll(Platform.position + up * 0.05f, -up, 60f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                if (!h.collider.transform.IsChildOf(transform) && h.distance < best) best = h.distance;
            return best - 0.05f;
        }

        static string Use => Controls.Name(Controls.Btn.Use);

        /// <summary>The context action at the crane: lower (parked, rover on the deck), open the gates, raise.</summary>
        public void UseAction()
        {
            if (state == State.Parked && RoverOnPlatform) CommandLower();
            else if (state == State.AtBottom && GatesHeld) OpenGates();
            else if (state == State.AtBottom && RoverOnPlatform) CommandRaise();
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.L)) CommandLower();
            if (Input.GetKeyDown(KeyCode.U)) CommandRaise();
            if (Input.GetKeyDown(KeyCode.K)) SkipToEnd();
            if (Input.GetKeyDown(KeyCode.G)) OpenGates();
            bool near = roverT != null && Vector3.Distance(roverT.position, Platform.position) < 12f;
            // round 12 (P3): USE (F / pad A) does whatever the crane offers here
            if (near && Controls.Down(Controls.Btn.Use)) UseAction();
            Prompt = null;
            if (near)
            {
                switch (state)
                {
                    case State.Parked: Prompt = RoverOnPlatform ? $"{Use} crane: lower to the pit floor (~45 m)" : SafeToMove ? $"Crane platform: drive on, stop in the middle, then {Use}" : "Crane interlock: drive fully onto the deck (or clear of it)"; break;
                    case State.AtBottom: Prompt = GatesHeld ? $"{Use} open the gates" : RoverOnPlatform ? $"Drive off towards the tube mouth   {Use} raise" : "On the pit floor - drive off towards the tube mouth"; break;
                    case State.ClosingGates: Prompt = "Crane: closing the end gates..."; break;
                    case State.Hoisting: case State.SlewingOut: case State.Lowering: Prompt = "Crane lowering... (keep the brakes on)  [K] skip"; break;
                    default: Prompt = "Crane raising...  [K] skip"; break;
                }
            }
        }

        void FixedUpdate() => Step(Time.fixedDeltaTime);

        public void Step(float dt)
        {
            if (joint == null) return;
            ApplySway();
            var up = transform.up;
            var anchorW = Platform.transform.TransformPoint(joint.anchor);
            float len = CableLength;
            if (shudderT >= 0f)
            {
                // brake slip: the drum lets 5 cm go over 0.4 s (smooth, so the rover just feels a shudder)
                float s0 = Mathf.SmoothStep(0f, 1f, shudderT / ShudderDuration);
                shudderT += dt;
                float s1 = Mathf.SmoothStep(0f, 1f, shudderT / ShudderDuration);
                if (s1 > s0) SetCable(CableLength + ShudderSlip * (s1 - s0));
                if (shudderT > 3f) shudderT = -1f;
            }
            float gateTarget = Moving || (state == State.AtBottom && GatesHeld) ? 0f : gateOpenDeg;
            if (!Mathf.Approximately(gateAngle, gateTarget))
            {
                // smooth hinge motion: ~2 s for the full swing
                gateAngle = Mathf.MoveTowards(gateAngle, gateTarget, gateOpenDeg / GateTimeNow * dt);
                ApplyGates();
            }
            switch (state)
            {
                case State.ClosingGates:
                    if (GatesClosed) state = afterGates;
                    break;
                case State.Hoisting:
                case State.RaisingUp:
                {
                    // shorten until the platform clears the rim by ~2 m; take up the slack at a creep first
                    float clearLen = (mastHeight + 0.1f) - (rimClearY + slingHeight);
                    float taut = Vector3.Distance(TipWorld, anchorW);
                    float slack = len - taut;
                    // stopping distance includes the jerk-limited lag (accel can't flip instantly)
                    float lag = Mathf.Abs(winchVel) * WinchAccelNow / WinchJerkNow;
                    // slack: quick, then a creep for the last 15 cm so the cable takes the load gently (no yank at lift-off)
                    float v = slack > 0.15f ? Mathf.Min(0.3f * K, StopSpeed(slack - 0.15f - lag) + 0.04f)
                            : slack > 0.02f ? 0.04f
                            : Mathf.Min(WinchSpeedNow, StopSpeed(len - clearLen - lag) + 0.02f);
                    if (len <= clearLen) v = 0f;
                    if (slack <= 0.05f && !GatesClosed) v = 0f;   // interlock: no lift-off before both gates are shut
                    Winch(-v, dt);
                    if (CableLength <= clearLen + 0.05f && Mathf.Abs(winchVel) < 0.03f)
                    {
                        winchVel = winchAcc = 0; slewPlanned = false;
                        // operator practice: let the load settle before slewing (< 0.3 m off the vertical, at most 10 s)
                        settleT += dt;
                        if (SwayNow(anchorW) < 0.3f || settleT > 10f) { settleT = 0; state = state == State.Hoisting ? State.SlewingOut : State.SlewingIn; }
                    }
                    break;
                }
                case State.SlewingOut:
                case State.SlewingIn:
                {
                    float target = state == State.SlewingOut ? pitAngle : padAngle;
                    if (!slewPlanned)
                    {
                        // input shaping: accel and decel ramps of exactly two pendulum periods -> zero residual cable swing,
                        // and half the tangential acceleration of a one-period ramp (the loaded deck tips less)
                        float L = len + slingHeight;
                        float T = 2f * 2f * Mathf.PI * Mathf.Sqrt(L / Mathf.Max(0.1f, Physics.gravity.magnitude));
                        float total = Mathf.Abs(Mathf.DeltaAngle(slew, target));
                        float vmax = Mathf.Min(SlewRateNow, total / T);   // cruise phase >= 0
                        slewFrom = slew; slewTo = slew + Mathf.DeltaAngle(slew, target);
                        slewRamp = T; slewDur = vmax > 1e-4f ? total / vmax + T : 0f;
                        slewT = 0; slewPlanned = true;
                    }
                    slewT += dt;
                    float dir = Mathf.Sign(slewTo - slewFrom), totalA = Mathf.Abs(slewTo - slewFrom);
                    float vm = slewDur > slewRamp ? totalA / (slewDur - slewRamp) : 0f;
                    float tt = Mathf.Min(slewT, slewDur);
                    float pos;
                    if (tt < slewRamp) pos = 0.5f * vm / slewRamp * tt * tt;
                    else if (tt < slewDur - slewRamp) pos = 0.5f * vm * slewRamp + vm * (tt - slewRamp);
                    else { float r = slewDur - tt; pos = totalA - 0.5f * vm / slewRamp * r * r; }
                    float prev = slew;
                    slew = slewFrom + dir * pos;
                    slewVel = (slew - prev) / dt;
                    if (slewT >= slewDur)
                    {
                        slew = slewTo; slewVel = 0;
                        // and again before lowering / setting down
                        settleT += dt;
                        if (SwayNow(anchorW) < 0.3f || settleT > 10f) { settleT = 0; slewPlanned = false; state = state == State.SlewingOut ? State.Lowering : State.Settling; }
                    }
                    break;
                }
                case State.Lowering:
                case State.Settling:
                {
                    // pay out until the platform rests on the ground (cable goes slack); creep for the last metres
                    float taut = Vector3.Distance(TipWorld, anchorW);
                    bool resting = len > taut + 0.6f && Platform.linearVelocity.magnitude < 0.08f && Platform.angularVelocity.magnitude < 0.05f;
                    if (resting) { winchVel = winchAcc = 0; if (state == State.Lowering) { GatesHeld = true; BottomTime = Radio.Now; } state = state == State.Lowering ? State.AtBottom : State.Parked; TieDown(false); break; }
                    float floor = FloorBelowPlatform();
                    if (state == State.Lowering)
                    {
                        if (descentFloor0 < 0f) descentFloor0 = floor;
                        if (!shudderDone && descentFloor0 > 10f && floor < 0.4f * descentFloor0)
                        {
                            shudderDone = true; shudderT = 0f;
                            ShudderProgress = 1f - floor / descentFloor0; ShudderTime = Radio.Now;
                            CameraShake.Add(0.025f, 1.0f);
                            Radio.Say("crane_shudder_crew", 0.9f, true);
                            Radio.FromEarth("crane_shudder_capcom", 1.4f, 0f, true);
                        }
                    }
                    float lag = Mathf.Abs(winchVel) * WinchAccelNow / WinchJerkNow;
                    float v = len > taut + 0.3f ? touchdownSpeed : Mathf.Min(WinchSpeedNow, StopSpeed(floor - 1.0f - lag) + touchdownSpeed);
                    Winch(v, dt);
                    break;
                }
            }
            // load rotator: during the descent turn the platform so one ramp faces the tube mouth
            if (bottomExitDir.sqrMagnitude > 0.1f)
            {
                float target = 0f;
                // which way does the rover face on the deck? turn so its nose points at the tube mouth
                if (state == State.Lowering || state == State.AtBottom)
                    target = RotatorTarget(up, roverT != null && Vector3.Dot(roverT.forward, Platform.transform.forward) < 0f);
                float yaw = Mathf.MoveTowards(rotatorYaw, target, 10f * dt);
                if (!Mathf.Approximately(yaw, rotatorYaw))
                {
                    rotatorYaw = yaw;
                    var desiredLocal = Quaternion.Inverse(hook.transform.rotation) * Quaternion.AngleAxis(rotatorYaw, up) * (hook.transform.rotation * jointStartLocal);
                    joint.targetRotation = Quaternion.Inverse(desiredLocal) * jointStartLocal;
                }
            }
            boom.localRotation = Quaternion.Euler(0, slew, 0);
            var tip = TipWorld;
            // kinematic target (never a teleport): the hook must carry its velocity, or the anti-sway damper
            // would drag the platform backwards while slewing
            if ((hook.position - tip).sqrMagnitude > 25f) { hook.position = tip; hook.transform.position = tip; }
            else hook.MovePosition(tip);
            // cable visuals
            cable.SetPosition(0, tip);
            cable.SetPosition(1, anchorW);
            var pt = Platform.transform;
            // slings (P4): from the spreader's end lugs, two per end, to the deck corners (the cable meets the centre lug)
            var corners = new[] { new Vector3(-2f, 0.3f, -3.1f), new Vector3(2f, 0.3f, -3.1f), new Vector3(2f, 0.3f, 3.1f), new Vector3(-2f, 0.3f, 3.1f) };
            for (int i = 0; i < 4; i++)
            {
                var lug = new Vector3(Mathf.Sign(corners[i].x) * 0.9f, slingHeight - 0.55f, Mathf.Sign(corners[i].z) * SpreaderHalf);
                slings[i].SetPosition(0, pt.TransformPoint(lug)); slings[i].SetPosition(1, pt.TransformPoint(corners[i]));
            }
        }
    }
}
