using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using MoonBaseAlpha.Cave;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha.Buggy
{
    /// <summary>
    /// Shaft rigs (round 12, S6): a span cable + motorised trolley across the tube over a collapse shaft, and the
    /// rover's own powered rope ascender hanging from it.
    ///  - At each rig (the advance team's kit, stays there): two rock-bolted wall anchors high on the tube walls, a
    ///    permanent span cable wall to wall across the tube over the hole, and a small motorised trolley on it. The
    ///    shafts are off-centre with a >= 12 m lane beside them: the trolley parks over that lane (solid floor) and runs
    ///    across to over the hole. Round 13 (T3): the advance team's rigs carry no rope (the rope stays on the rover
    ///    until it is used); only where GIMLI-2 itself went down on its way to the seal does its rope still hang from
    ///    the trolley out over the hole: there the crew go out, pull the trolley in and clip GIMLI-2's rope to the
    ///    cage hook (a short fade, +10 min) instead of using one of their own.
    ///  - The rover carries a stock of ropes (<see cref="RopesPerMission"/>) and rig kits (<see cref="RigKitsPerMission"/>)
    ///    and a lift cage (4 roll-bar poles meeting above the centre = one hook point). Everything clips on
    ///    automatically (no EVA step).
    ///  - Down: stop under the parked trolley, [J]: the cage hook clips a rope (GIMLI-2's, else one from stock), the
    ///    winch lifts the rover clear, the trolley runs out over the hole, the winch lowers it; at the bottom it unclips
    ///    and leaves the rope hanging (the way back).
    ///  - Up: stop under a hanging rope, [J]: clip on, winch up until clear of the lip, the trolley carries the rover over
    ///    solid floor, set down, unclip; the rover reels its own rope back in (stock +1; GIMLI-2's ropes stay).
    ///  - An unrigged shaft: [J] at its edge installs one of the rover's rig kits (the crew's work is skipped: a short
    ///    fade, "+40 min", the mission clock advances), then as above.
    ///  - At the bottom of a rig with no rope hanging (fell in, or rigged without a rope): [J] sends one of the rover's
    ///    ropes up on the trolley's light messenger line (fade, +15 min, SPECULATIVE kit), then the ride up as above.
    ///  - Falling in with no rope is survivable (game mercy, SPECULATIVE): roll cage + 4-point harnesses, the rover is
    ///    guided upright onto a glancing talus landing; it ends up "ROVER DAMAGED" with a capped top speed.
    /// Winch speed on \ ("rover winch speed").
    /// </summary>
    public class RoverWinch : MonoBehaviour
    {
        public const int RopesPerMission = 3, RigKitsPerMission = 2;
        /// <summary>Round 12: the ride 50% faster than round 11 (0.75 m/s); acceleration and jerk scaled with it.</summary>
        public const float BaseWinchSpeed = 1.125f, Accel = 0.18f, Jerk = 0.375f, CreepSpeed = 0.15f;
        /// <summary>The lift cage's hook point above the chassis origin (local), ~3 m above the ground.</summary>
        public static readonly Vector3 CageHook = new Vector3(0f, 2.7f, -0.2f);
        /// <summary>Span cable height above the upper floor at the trolley: rover + cage + 1 m clearance + rope + sag.</summary>
        public const float SpanHeight = 6.0f;
        /// <summary>Rope between trolley and cage hook when fully reeled in.</summary>
        public const float MinRope = 0.6f;
        public const float ClipTime = 1.0f, SettleTime = 1.0f;
        /// <summary>Rig install (skipped crew work): game minutes added to the mission clock, fade timings (s).</summary>
        public const float RigSetupMinutes = 40f, FadeOut = 0.8f, FadeHold = 1.8f, FadeIn = 0.8f;
        /// <summary>Round 13 (T3): crew out to clip GIMLI-2's rope at the rim; a rope sent up on the messenger line.</summary>
        public const float GimliClipMinutes = 10f, MessengerMinutes = 15f;
        enum Pending { Rig, GimliClip, Messenger }
        Pending pending;
        /// <summary>Park spot: the trolley's solid-floor end, this far from the hole's edge (centre of the >= 12 m lane).</summary>
        public const float ParkFromEdge = 6.5f;

        public enum State { Free, Fetch, Clip, Lift, TrolleyOut, Lowering, Down, Raising, TrolleyIn, SetDown, Unclip, Installing }

        public BuggyController rover;
        public TunnelNetwork network;
        public System.Func<bool> canUse;             // the player is driving
        public int Ropes { get; private set; } = RopesPerMission;
        public int RigKits { get; private set; } = RigKitsPerMission;
        public State state = State.Free;
        public string Prompt { get; private set; }
        public float CableOut { get; private set; }
        public float WinchVel => winchVel;
        public bool Damaged => rover != null && rover.Damaged;
        /// <summary>Rope length (deepest shaft + margin; the game sets it from the network).</summary>
        public float RopeLength = 100f;
        /// <summary>Fade-to-black alpha of a rig install (0..1) and its caption.</summary>
        public float FadeAlpha { get; private set; }
        public string FadeText { get; private set; }
        /// <summary>Tests: the fade alpha at the moment the rig appeared (1 = hidden).</summary>
        public float LastSpawnAlpha { get; private set; } = -1f;

        /// <summary>A span rig at one shaft (frame coordinates).</summary>
        public class Rig
        {
            public string key;
            public float3 top; public float radius, drop;
            public double3 anchorA, anchorB;      // wall bolts, A on the lane side
            public double3 park, roller;          // trolley at its solid-floor end / over the hole (at span height)
            public double3 rimSpot;               // rover spot on the lane under the parked trolley (floor)
            public double3 foot;                  // floor below the hole (where a hanging rope ends)
            public float trolleyT = 1f;           // 0 = parked over the lane, 1 = over the hole
            public bool gimliRope, roverRope;     // ropes hanging from the trolley
            public bool preRigged;
            public GameObject go;
            public LineRenderer span, rope;
            public Transform trolley;
            public bool RopeHanging => gimliRope || roverRope;
            public double3 Trolley => math.lerp(park, roller, (double)trolleyT);
        }
        readonly Dictionary<string, Rig> rigs = new();
        public int RiggedCount => rigs.Count;
        public IEnumerable<Rig> Rigs => rigs.Values;
        Rig active;                                   // the rig the rover hangs on
        bool usingOwnRope;                            // the rope on the hook is the rover's (else GIMLI-2's)
        bool goingDown;

        Rigidbody hook;
        ConfigurableJoint joint;
        float winchVel, winchAcc, moveT, moveDur, stateT;
        Vector3 moveFrom, moveTo;
        Quaternion jointRest;
        Material ropeMat, spanMat, steelMat, trolleyMat;
        AudioSource sfx; AudioClip click;

        static float Speed => BaseWinchSpeed * Tuning.WinchSpeed.Value;

        // ------------------------------------------------------------------ queries

        /// <summary>Shaft near the rover's level (within <paramref name="range"/> of its edge): key, top centre, radius, drop.</summary>
        public bool ShaftNear(Vector3 world, float range, out string key, out float3 top, out float radius, out float drop)
        {
            key = null; top = default; radius = 0; drop = 0;
            if (network == null) return false;
            var f = (float3)WorldOrigin.WorldToFrame(world);
            if (!network.CollapseNear(f, range, out top, out radius, out drop)) return false;
            key = Key(top);
            return true;
        }

        static string Key(float3 top) => $"{top.x:0},{top.z:0}";

        public bool Attached => joint != null;
        bool RoverStopped => rover.Body.linearVelocity.magnitude < 0.3f;
        Vector3 Hook => rover.transform.TransformPoint(CageHook);

        /// <summary>A rig whose trolley park spot the rover is at (upper level).</summary>
        Rig RigAtPark(Vector3 world, double range = 5.0)
        {
            var f = WorldOrigin.WorldToFrame(world);
            foreach (var r in rigs.Values)
                if (math.distance(r.rimSpot.xz, f.xz) < range && math.abs(r.rimSpot.y - f.y) < 4.0) return r;
            return null;
        }

        /// <summary>A rig whose hanging rope ends near the rover (lower level).</summary>
        Rig RigAtFoot(Vector3 world, double range = 12.0)
        {
            var f = WorldOrigin.WorldToFrame(world);
            foreach (var r in rigs.Values)
                if (math.distance(r.foot.xz, f.xz) < range && math.abs(r.foot.y - f.y) < 8.0) return r;
            return null;
        }

        /// <summary>Horizontal distance from the rover to the foot of the nearest hanging rope (m), or -1.</summary>
        public float FootDistance()
        {
            var r = RigAtFoot(rover.transform.position);
            return r == null || !r.RopeHanging ? -1f : (float)math.distance(r.foot.xz, WorldOrigin.WorldToFrame(rover.transform.position).xz);
        }

        public Rig RigAt(float3 top) => rigs.TryGetValue(Key(top), out var r) ? r : null;

        // ------------------------------------------------------------------ rig geometry

        List<TunnelPrim> sdfPrims; float3 sdfLo, sdfHi;
        float Sdf(float3 p)
        {
            if (sdfPrims == null || math.any(p < sdfLo) || math.any(p > sdfHi))
            {
                sdfLo = p - 90f; sdfHi = p + 90f;
                sdfPrims ??= new List<TunnelPrim>();
                network.Collect(sdfLo, sdfHi, 8f, sdfPrims);
            }
            if (sdfPrims.Count == 0) return 1e9f;
            var arr = new NativeArray<TunnelPrim>(sdfPrims.ToArray(), Allocator.Temp);
            float d = TunnelSdf.Eval(arr, arr.Length, p, out _, out _);
            arr.Dispose();
            return d;
        }

        /// <summary>The lane direction at a shaft: from the hole towards the upper tube's axis (across the tube).</summary>
        float2 LaneDir(float3 top, float radius)
        {
            float2 best = new float2(1, 0); float bd = float.MaxValue;
            var buf = new List<TunnelPrim>();
            network.Collect(top - 80f, top + 80f, 8f, buf);
            foreach (var p in buf)
            {
                if (p.type != TunnelPrim.Tube || p.kind != 0 || math.abs(math.lerp(p.a.y, p.b.y, 0.5f) - top.y) > 10f) continue;
                var ab = p.b.xz - p.a.xz; float t = math.saturate(math.dot(top.xz - p.a.xz, ab) / math.max(1e-3f, math.lengthsq(ab)));
                var c = p.a.xz + ab * t; float d = math.distance(c, top.xz);
                if (d < bd)
                {
                    bd = d;
                    var across = math.normalizesafe(new float2(-ab.y, ab.x));
                    best = d > 1f ? math.normalizesafe(c - top.xz) : across;
                    if (math.dot(best, across) < 0) across = -across;
                    best = math.normalizesafe(across * math.sign(math.dot(best, across) + 1e-3f));
                }
            }
            return best;
        }

        /// <summary>Build a rig's geometry at a shaft: wall anchors across the tube, park / hole trolley points, foot.</summary>
        Rig MakeRig(float3 top, float radius, float drop)
        {
            var r = new Rig { key = Key(top), top = top, radius = radius, drop = drop };
            var lane = LaneDir(top, radius);
            float floorY = network.FloorY(top + new float3(lane.x, 0, lane.y) * (radius + ParkFromEdge), 4f, 6f);
            if (float.IsNaN(floorY)) floorY = top.y;
            float h = SpanHeight;
            // roof check over the span line: keep >= 1.5 m of air above the trolley (else lower to >= 5 m)
            for (float hh = SpanHeight; hh >= 5f; hh -= 0.25f)
            {
                bool ok = true;
                for (float s = -radius; s <= radius + ParkFromEdge + 2f && ok; s += 2f)
                    if (Sdf(new float3(top.x + lane.x * s, floorY + hh + 1.5f, top.z + lane.y * s)) > -0.2f) ok = false;
                h = hh; if (ok) break;
            }
            float y = floorY + h;
            float Wall(float2 dir)
            {
                for (float s = radius; s < 90f; s += 0.5f)
                    if (Sdf(new float3(top.x + dir.x * s, y, top.z + dir.y * s)) > 0f) return s - 0.3f;
                return 60f;
            }
            float sa = Wall(lane), sb = Wall(-lane);
            r.anchorA = new double3(top.x + lane.x * sa, y + 0.15f, top.z + lane.y * sa);
            r.anchorB = new double3(top.x - lane.x * sb, y + 0.15f, top.z - lane.y * sb);
            float parkS = math.min(radius + ParkFromEdge, sa - 1.5f);
            r.park = new double3(top.x + lane.x * parkS, y, top.z + lane.y * parkS);
            r.roller = new double3(top.x, y, top.z);
            r.rimSpot = new double3(r.park.x, floorY, r.park.z);
            float footY = network.FloorY(new float3(top.x, top.y - drop + 14f, top.z), 4f, 30f);
            r.foot = new double3(top.x, float.IsNaN(footY) ? top.y - drop : footY, top.z);
            return r;
        }

        // ------------------------------------------------------------------ commands

        /// <summary>[J] under the parked trolley: clip a rope, lift, trolley out, lower.</summary>
        public bool RideDown(out string why)
        {
            why = null;
            if (state != State.Free) { why = "winch busy"; return false; }
            var r = RigAtPark(rover.transform.position);
            if (r == null) { why = "not under a trolley"; return false; }
            if (!RoverStopped) { why = "stop first"; return false; }
            if (!r.RopeHanging && Ropes <= 0) { why = "no ropes left"; MessageLog.AddOnce("no-rope", "No ropes left: this shaft can't be descended.", MessageLog.Kind.Warning); return false; }
            if (r.drop > RopeLength - 5f) { why = $"drop {r.drop:0} m: longer than a rope"; return false; }
            active = r; goingDown = true;
            usingOwnRope = !r.gimliRope;
            if (!r.RopeHanging) { Ropes--; r.roverRope = true; }
            stateT = 0;
            rover.parkingBrake = true;
            if (r.gimliRope && r.trolleyT > 0.01f)
            {
                // GIMLI-2's rope hangs from the trolley out over the hole: the crew go out, pull it in and clip on (fade)
                pending = Pending.GimliClip; state = State.Installing;
                FadeText = $"+{GimliClipMinutes:0} min: crew out - trolley pulled in, GIMLI-2's rope clipped to the cage hook";
                MessageLog.Add("Crew out at the rim: pulling the trolley in to clip on GIMLI-2's rope.", MessageLog.Kind.System);
                Debug.Log($"[Winch] ride down {r.key} on GIMLI-2's rope: {r.drop:0} m");
                return true;
            }
            state = r.trolleyT > 0.01f ? State.Fetch : State.Clip;
            if (state == State.Fetch) StartMove(r.Trolley, r.park, out _);
            Radio.Say("winch_lowering", 0.3f);
            MessageLog.Add($"Shaft rig: clipping on{(usingOwnRope ? "" : " to GIMLI-2's rope")} - lowering {r.drop:0} m.", MessageLog.Kind.System);
            Debug.Log($"[Winch] ride down {r.key}: {r.drop:0} m, ropes {Ropes}/{RopesPerMission}");
            return true;
        }

        /// <summary>[J] under a hanging rope at the bottom: clip on and winch up, trolley back over the lane, set down.</summary>
        public bool RideUp()
        {
            if (state != State.Free) return false;
            var r = RigAtFoot(rover.transform.position, 5.0);
            if (r == null || !r.RopeHanging || !RoverStopped) return false;
            active = r; goingDown = false;
            usingOwnRope = r.roverRope;
            r.trolleyT = 1f;
            state = State.Clip; stateT = 0;
            rover.parkingBrake = true;
            Radio.Say("winch_hooked", 0.3f);
            MessageLog.Add("Shaft rig: clipped on - winching up.", MessageLog.Kind.System);
            return true;
        }

        /// <summary>
        /// Round 13 (T3): at the foot of a rig with no rope hanging, send one of the rover's ropes up: the trolley runs out
        /// over the hole and lowers its light messenger line, the crew tie the rope on, the trolley hauls it up and makes it
        /// fast (fade, +15 min), then the normal ride up. SPECULATIVE (a plausible kit feature, not flown hardware).
        /// </summary>
        public bool SendRopeUp(out string why)
        {
            why = null;
            if (state != State.Free) { why = "winch busy"; return false; }
            var r = RigAtFoot(rover.transform.position, 6.0);
            if (r == null) { why = "not at the foot of a rig"; return false; }
            if (r.RopeHanging) { why = "a rope is hanging here"; return false; }
            if (Ropes <= 0) { why = "no ropes left"; MessageLog.AddOnce("no-rope", "No ropes left: no way to send one up.", MessageLog.Kind.Warning); return false; }
            if (!RoverStopped) { why = "stop first"; return false; }
            active = r; goingDown = false; usingOwnRope = true;
            pending = Pending.Messenger; state = State.Installing; stateT = 0;
            rover.parkingBrake = true;
            FadeText = $"+{MessengerMinutes:0} min: rope sent up on the trolley's messenger line";
            MessageLog.Add("Messenger line down from the trolley: sending a rope up.", MessageLog.Kind.System);
            return true;
        }

        /// <summary>Can a rig kit go in here: stopped near an unrigged shaft edge on the upper level, a kit left.</summary>
        public bool CanInstall(out string why)
        {
            why = null;
            if (state != State.Free) { why = "winch busy"; return false; }
            if (!ShaftNear(rover.transform.position, 15f, out var key, out _, out _, out float drop)) { why = "no shaft edge here"; return false; }
            if (rigs.ContainsKey(key)) { why = "rigged - drive under the trolley"; return false; }
            if (RigKits <= 0) { why = "no rig kits left"; return false; }
            if (drop > RopeLength - 5f) { why = $"drop {drop:0} m: longer than a rope"; return false; }
            if (!RoverStopped) { why = "stop first"; return false; }
            return true;
        }

        /// <summary>
        /// [J] at an unrigged shaft: a rig kit goes in. The crew's setup is skipped: fade to black, the rig appears and
        /// the rover is parked under the trolley while the screen is black, "+40 min: span rig installed", fade back.
        /// instant = tests (no fade timing; the spawn is recorded as hidden).
        /// </summary>
        public bool InstallRig(bool instant = false)
        {
            if (!CanInstall(out string why)) { if (why == "no rig kits left") MessageLog.AddOnce("no-rig", "No rig kits left: this shaft can't be rigged.", MessageLog.Kind.Warning); return false; }
            ShaftNear(rover.transform.position, 15f, out _, out pendingTop, out pendingR, out pendingDrop);
            state = State.Installing; stateT = 0; pending = Pending.Rig;
            rover.parkingBrake = true;
            FadeText = $"+{RigSetupMinutes:0} min: span rig installed";
            if (instant) { FadeAlpha = 1f; SpawnPendingRig(); FadeAlpha = 0f; state = State.Free; }
            return true;
        }

        float3 pendingTop; float pendingR, pendingDrop;

        void SpawnPendingRig()
        {
            RigKits--;
            var r = MakeRig(pendingTop, pendingR, pendingDrop);
            r.trolleyT = 0f;
            rigs[r.key] = r;
            MakeRigVisual(r);
            LastSpawnAlpha = FadeAlpha;
            var clock = SimClock.Instance ?? (Application.isPlaying ? null : SimClock.Ensure());
            if (clock != null) clock.SetElapsed(clock.elapsedSeconds + RigSetupMinutes * 60.0);
            // the rover parked under the trolley, facing the hole
            var spot = WorldOrigin.FrameToWorld(r.rimSpot);
            var up = -TerrainFrame.DownAt(spot);
            var face = Vector3.ProjectOnPlane(WorldOrigin.FrameToWorld((double3)r.top) - spot, up);
            rover.PlaceAt(spot + up * 0.5f, Quaternion.LookRotation(face.sqrMagnitude > 0.1f ? face.normalized : rover.transform.forward, up));
            MessageLog.Add($"Span rig installed (+{RigSetupMinutes:0} min). Rig kits left: {RigKits}.", MessageLog.Kind.System);
            Debug.Log($"[Winch] rig installed at {r.key}: kits {RigKits}/{RigKitsPerMission}");
        }

        void Attach(float length, Vector3 hookPos)
        {
            if (hook == null)
            {
                var go = new GameObject("WinchHook");
                go.AddComponent<WorldOriginIgnore>();   // moved explicitly (origin shifts handled below)
                hook = go.AddComponent<Rigidbody>();
                hook.isKinematic = true;
                WorldOrigin.Shifted += d => { if (hook != null) { hook.position += d; hook.transform.position += d; } moveFrom += d; moveTo += d; };
            }
            hook.position = hookPos; hook.transform.position = hookPos;
            var body = rover.Body;
            joint = body.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = hook;
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = CageHook;
            joint.connectedAnchor = Vector3.zero;
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Limited;
            joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.linearLimit = new SoftJointLimit { limit = Mathf.Max(0.2f, length) };
            // stiff rope; the cage keeps the rover level (a stiff rotational spring to the upright pose), light swing damping
            joint.xDrive = joint.zDrive = new JointDrive { positionDamper = 900f, maximumForce = 4000f };
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            var upNow = -TerrainFrame.DownAt(body.position);
            var fwd = Vector3.ProjectOnPlane(rover.transform.forward, upNow).normalized;
            var level = Quaternion.LookRotation(fwd, upNow);
            var startLocal = Quaternion.Inverse(hook.rotation) * body.rotation;
            jointRest = Quaternion.Inverse(hook.rotation) * level;
            joint.targetRotation = Quaternion.Inverse(jointRest) * startLocal;
            cageT = 0f;
            ApplyCage();
            joint.enableCollision = false;
            CableOut = length;
            winchVel = winchAcc = 0;
        }

        float cageT;

        /// <summary>Cage stiffness ramps in over 2 s after clipping on (no kick when the rover sits a little tilted).</summary>
        void ApplyCage()
        {
            if (joint == null) return;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(cageT / 2f));
            joint.slerpDrive = new JointDrive { positionSpring = 45000f * k, positionDamper = 22000f * Mathf.Max(k, 0.2f), maximumForce = 1e6f };
        }

        void Release()
        {
            if (joint != null) Util.Kill(joint);
            joint = null;
            rover.parkingBrake = false;
        }

        void SetCable(float len)
        {
            CableOut = Mathf.Clamp(len, 0.2f, RopeLength);
            var l = joint.linearLimit; l.limit = CableOut; joint.linearLimit = l;
        }

        void Winch(float target, float dt)
        {
            float wantAcc = Mathf.Clamp((target - winchVel) / 0.6f, -Accel, Accel);
            winchAcc = Mathf.MoveTowards(winchAcc, wantAcc, Jerk * dt);
            float nv = winchVel + winchAcc * dt;
            if ((winchVel - target) * (nv - target) < 0) { nv = target; winchAcc = 0; }
            winchVel = nv;
            SetCable(CableOut + winchVel * dt);
        }

        /// <summary>Trolley run: smoothstep, peak acceleration 6 d / T^2 kept under ~0.45 m/s^2 (a gentle swing).</summary>
        static float MoveDuration(Vector3 a, Vector3 b) => Mathf.Max(4f, Mathf.Sqrt(6f * Vector3.Distance(a, b) / 0.45f));

        void StartMove(double3 fromF, double3 toF, out float dur)
        {
            moveFrom = WorldOrigin.FrameToWorld(fromF); moveTo = WorldOrigin.FrameToWorld(toF);
            moveT = 0; moveDur = dur = MoveDuration(moveFrom, moveTo);
        }

        float StopSpeed(float dist) => Mathf.Sqrt(2f * Accel * 0.7f * Mathf.Max(0f, dist));

        /// <summary>Clearance under the lowest wheel (rays under each wheel hub: on a talus slope one wheel lands first).</summary>
        float GroundBelow()
        {
            var up = -TerrainFrame.DownAt(rover.transform.position);
            float best = 999f;
            foreach (var w in rover.wheels)
            {
                var hub = rover.transform.TransformPoint(w.mount - Vector3.up * 0.18f);
                foreach (var h in Physics.RaycastAll(hub + up * 0.2f, -up, 200f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                    if (h.rigidbody != rover.Body && h.distance - 0.2f - rover.wheelRadius - 0.1f < best) best = h.distance - 0.2f - rover.wheelRadius - 0.1f;
            }
            return best;
        }

        // ------------------------------------------------------------------ loop

        void Update()
        {
            Prompt = null;
            if (rover == null || (canUse != null && !canUse())) return;
            bool j = Input.GetKeyDown(KeyCode.J) || Controls.Down(Controls.Btn.Use);
            string J = Controls.Name(Controls.Btn.Use);
            if (state == State.Free)
            {
                var atPark = RigAtPark(rover.transform.position);
                var atFoot = RigAtFoot(rover.transform.position);
                if (atPark != null)
                {
                    Prompt = !RoverStopped ? "Shaft rig here - stop under the trolley"
                           : !atPark.RopeHanging && Ropes <= 0 ? "Shaft rig: no ropes left"
                           : atPark.gimliRope && atPark.trolleyT > 0.01f ? $"{J} clip on GIMLI-2's rope and ride down ({atPark.drop:0} m, crew out {GimliClipMinutes:0} min)"
                           : $"{J} ride down the shaft ({atPark.drop:0} m)   ROPES {Ropes}";
                    if (j) RideDown(out _);
                }
                else if (atFoot != null && atFoot.RopeHanging)
                {
                    float fd = FootDistance();
                    Prompt = fd > 4f ? $"Rope {fd:0} m away - drive under it" : RoverStopped ? $"{J} clip on the rope - winch up" : "Rope here - stop to clip on";
                    if (j) RideUp();
                }
                else if (atFoot != null)
                {
                    float fd = (float)math.distance(atFoot.foot.xz, WorldOrigin.WorldToFrame(rover.transform.position).xz);
                    Prompt = fd > 5f ? $"Shaft rig above, no rope - drive under it ({fd:0} m)"
                           : Ropes <= 0 ? "Shaft rig above: no ropes left"
                           : RoverStopped ? $"{J} send a rope up on the messenger line ({MessengerMinutes:0} min)   ROPES {Ropes}" : "Shaft rig above - stop under it";
                    if (j) SendRopeUp(out _);
                }
                else if (ShaftNear(rover.transform.position, 15f, out var key, out _, out _, out _))
                {
                    if (rigs.TryGetValue(key, out var r0)) Prompt = $"Shaft rig: drive to the trolley ({math.distance(r0.rimSpot.xz, WorldOrigin.WorldToFrame(rover.transform.position).xz):0} m)";
                    else if (CanInstall(out string why)) { Prompt = $"{J} install a span rig ({RigSetupMinutes:0} min)   RIGS {RigKits}"; if (j) InstallRig(); }
                    else Prompt = $"Span rig: {why}";
                }
            }
            else Prompt = state switch
            {
                State.Installing => null,
                State.Fetch or State.Clip => "Shaft rig: clipping on",
                State.Lift or State.TrolleyOut => "Shaft rig: trolley running out over the shaft",
                State.Lowering => $"Winch: lowering, {CableOut:0} m out",
                State.Raising => $"Winch: hauling up, {CableOut:0} m out",
                State.TrolleyIn or State.SetDown => "Shaft rig: trolley back over solid floor",
                State.Down or State.Unclip => "Shaft rig: unclipping",
                _ => null,
            };
        }

        void FixedUpdate() => Step(Time.fixedDeltaTime);

        public void Step(float dt)
        {
            if (rover == null) return;
            FallMercy(dt);
            stateT += dt;
            if (state == State.Installing) { StepFade(dt); UpdateVisuals(); return; }
            if (state == State.Fetch)
            {
                // the trolley comes back to the lane end (a hanging rope reels up on its spool)
                moveT += dt / moveDur;
                active.trolleyT = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(moveT));
                if (moveT >= 1f) { active.trolleyT = 0f; state = State.Clip; stateT = 0; }
                UpdateVisuals(); return;
            }
            if (state == State.Clip)
            {
                if (stateT >= ClipTime)
                {
                    var tw = WorldOrigin.FrameToWorld(active.Trolley);
                    Attach(Vector3.Distance(tw, Hook) + 0.1f, tw);
                    Click();
                    state = goingDown ? State.Lift : State.Raising; stateT = 0;
                }
                UpdateVisuals(); return;
            }
            if (state == State.Unclip)
            {
                if (stateT >= ClipTime) FinishUnclip();
                UpdateVisuals(); return;
            }
            UpdateVisuals();
            if (joint == null || hook == null) return;
            if (cageT < 2f) { cageT += dt; ApplyCage(); }
            var hp = Hook;
            float taut = Vector3.Distance(hook.position, hp);
            switch (state)
            {
                case State.Lift:
                case State.Raising:
                {
                    // take up the slack at a creep, lift off gently, then reel in until the cage hook is just under the trolley
                    float lag = Mathf.Abs(winchVel) * Accel / Jerk;
                    float left = CableOut - MinRope;
                    float slack = CableOut - taut;
                    float v = slack > 0.03f ? Mathf.Min(0.1f, StopSpeed(slack) + 0.02f) : Mathf.Min(Speed, StopSpeed(left - lag) + 0.03f);
                    if (cageT < 2f) v = Mathf.Min(v, 0.04f);
                    if (left <= 0.02f) v = 0;
                    Winch(-v, dt);
                    if (left <= 0.05f && Mathf.Abs(winchVel) < 0.02f)
                    {
                        winchVel = winchAcc = 0;
                        if (state == State.Lift) { StartMove(active.park, active.roller, out _); state = State.TrolleyOut; }
                        else { StartMove(active.roller, active.park, out _); state = State.TrolleyIn; }
                        stateT = 0;
                    }
                    break;
                }
                case State.TrolleyOut:
                case State.TrolleyIn:
                {
                    moveT += dt / moveDur;
                    float s = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(moveT));
                    hook.MovePosition(Vector3.Lerp(moveFrom, moveTo, s));
                    active.trolleyT = state == State.TrolleyOut ? s : 1f - s;
                    Winch(0f, dt);
                    if (moveT >= 1f) { state = state == State.TrolleyOut ? State.Lowering : State.SetDown; winchVel = winchAcc = 0; stateT = 0; }
                    break;
                }
                case State.Lowering:
                case State.SetDown:
                {
                    float ground = GroundBelow();
                    bool slack = CableOut > taut + 0.4f;
                    if (slack && rover.Body.linearVelocity.magnitude < 0.08f)
                    {
                        winchVel = winchAcc = 0;
                        state = state == State.Lowering ? State.Down : State.Unclip; stateT = 0;
                        Radio.Say(state == State.Down ? "winch_on_talus" : "winch_on_rim", 0.3f);
                        break;
                    }
                    float lag = Mathf.Abs(winchVel) * Accel / Jerk;
                    float v = slack ? CreepSpeed * 0.5f : Mathf.Min(Speed, StopSpeed(ground - 2.0f - lag) + (ground < 0.6f ? CreepSpeed * 0.5f : CreepSpeed));
                    Winch(v, dt);
                    break;
                }
                case State.Down:
                    // settle, then unclip and leave the rope hanging (the way back up)
                    if (stateT >= SettleTime) { state = State.Unclip; stateT = 0; }
                    break;
            }
        }

        void FinishUnclip()
        {
            Release();
            Click();
            if (goingDown)
            {
                active.foot = WorldOrigin.WorldToFrame(rover.transform.position);
                MessageLog.Add("Rope left hanging from the trolley: the way back up.", MessageLog.Kind.System);
                Radio.Say("winch_unhooked", 0.3f);
            }
            else
            {
                if (usingOwnRope) { active.roverRope = false; Ropes = Mathf.Min(RopesPerMission, Ropes + 1); MessageLog.Add($"Rope reeled back in. ROPES {Ropes}.", MessageLog.Kind.System); }
                // a rope still hanging (GIMLI-2's): the trolley goes back over the hole so it hangs down the shaft again
                active.trolleyT = active.RopeHanging ? 1f : 0f;
            }
            state = State.Free; active = null; stateT = 0;
        }

        void StepFade(float dt)
        {
            float t = stateT;
            if (t < FadeOut) FadeAlpha = t / FadeOut;
            else if (t < FadeOut + FadeHold)
            {
                bool done = FadeAlpha >= 1f;
                FadeAlpha = 1f;
                if (!done) AtBlack();
            }
            else if (t < FadeOut + FadeHold + FadeIn) FadeAlpha = 1f - (t - FadeOut - FadeHold) / FadeIn;
            else
            {
                FadeAlpha = 0f;
                // the rope is on the hook now: straight into the ride (down on GIMLI-2's rope, up on the one sent up)
                if (pending == Pending.Rig) { state = State.Free; rover.parkingBrake = false; }
                else { state = State.Clip; stateT = 0; }
            }
        }

        void AtBlack()
        {
            if (pending == Pending.Rig) { SpawnPendingRig(); return; }
            var clock = SimClock.Instance ?? (Application.isPlaying ? null : SimClock.Ensure());
            float minutes = pending == Pending.GimliClip ? GimliClipMinutes : MessengerMinutes;
            if (clock != null) clock.SetElapsed(clock.elapsedSeconds + minutes * 60.0);
            if (pending == Pending.GimliClip) active.trolleyT = 0f;
            else { Ropes--; active.roverRope = true; active.trolleyT = 1f; }
            LastSpawnAlpha = FadeAlpha;
            MessageLog.Add(pending == Pending.GimliClip ? $"GIMLI-2's rope clipped on (+{GimliClipMinutes:0} min)." : $"Rope up and made fast (+{MessengerMinutes:0} min). ROPES {Ropes}.", MessageLog.Kind.System);
        }

        // ------------------------------------------------------------------ no-rope fall (game mercy)

        bool falling; float fallTop, fallPeakV;

        /// <summary>
        /// Free fall into a shaft with no rope: keep the rover upright (roll cage, crew bracing) and turn the landing into
        /// a glancing slide down the talus (vertical speed bled off over the first metres of contact). SPECULATIVE.
        /// </summary>
        void FallMercy(float dt)
        {
            if (joint != null) { falling = false; return; }
            var body = rover.Body;
            var up = -TerrainFrame.DownAt(body.position);
            float vDown = -Vector3.Dot(body.linearVelocity, up);
            int grounded = 0; foreach (var w in rover.wheels) if (w.grounded) grounded++;
            if (!falling)
            {
                if (vDown > 4f && grounded == 0 && ShaftNear(body.position, 2f, out _, out _, out _, out _))
                { falling = true; fallTop = Vector3.Dot(body.position, up); fallPeakV = 0; }
                return;
            }
            fallPeakV = Mathf.Max(fallPeakV, vDown);
            var axis = Vector3.Cross(rover.transform.up, up);
            body.AddTorque(axis * 4000f - body.angularVelocity * 2500f, ForceMode.Force);
            float reach = Mathf.Max(2.5f, (vDown * vDown - 9f) / (2f * 14f) + 2f);
            if (grounded > 0 || Physics.Raycast(body.position, -up, reach, ~(1 << 2 | 1 << body.gameObject.layer), QueryTriggerInteraction.Ignore))
            {
                if (vDown > 3f)
                {
                    float shed = Mathf.Min(vDown - 3f, 14f * dt);
                    body.AddForce(up * shed / dt * body.mass, ForceMode.Force);
                }
                else if (body.linearVelocity.magnitude < 1.5f)
                {
                    falling = false;
                    float h = fallTop - Vector3.Dot(body.position, up);
                    if (h > 10f && !rover.Damaged)
                    {
                        rover.Damaged = true;
                        Radio.Say("fall_crew_1", 0.6f, true);
                        Radio.Say("fall_crew_2", 4f, true);
                        Radio.FromEarth("fall_capcom", 1.5f, 0f, true);
                        MessageLog.Add("ROVER DAMAGED: fell into a shaft - top speed capped.", MessageLog.Kind.Warning);
                        Debug.Log($"[Winch] fell {h:0} m without a rope, peak {fallPeakV:0.0} m/s: ROVER DAMAGED");
                    }
                }
            }
        }

        // ------------------------------------------------------------------ visuals

        void EnsureMats()
        {
            ropeMat ??= Rendering.Mats.Make("RigRopeOrange", new Color(1f, 0.42f, 0.05f), 0f, 0.5f);
            spanMat ??= Rendering.Mats.Make("RigSpanSteel", new Color(0.55f, 0.56f, 0.58f), 0.9f, 0.45f);
            steelMat ??= Rendering.Mats.Make("RigSteel", new Color(0.42f, 0.43f, 0.45f), 0.85f, 0.4f);
            trolleyMat ??= Rendering.Mats.Make("RigTrolleyYellow", new Color(0.95f, 0.7f, 0.1f), 0.2f, 0.45f);
        }

        static GameObject Part(Transform parent, string name, PrimitiveType t, Vector3 scale, Material m)
        {
            var g = GameObject.CreatePrimitive(t);
            Object.DestroyImmediate(g.GetComponent<Collider>());
            g.name = name; g.transform.SetParent(parent, false); g.transform.localScale = scale;
            g.GetComponent<MeshRenderer>().sharedMaterial = m;
            return g;
        }

        void MakeRigVisual(Rig r)
        {
            EnsureMats();
            r.go = new GameObject($"ShaftRig {r.key}");
            var t = r.go.transform;
            foreach (var (n, a) in new[] { ("AnchorA", r.anchorA), ("AnchorB", r.anchorB) })
            {
                // a bolted wall plate with an eye
                var plate = Part(t, n, PrimitiveType.Cube, new Vector3(0.45f, 0.45f, 0.08f), steelMat);
                plate.AddComponent<ScoutAnchor>().frame = a;
                plate.transform.position = WorldOrigin.FrameToWorld(a);
                var other = n == "AnchorA" ? r.anchorB : r.anchorA;
                var d = (Vector3)(float3)(other - a); d.y = 0;
                if (d.sqrMagnitude > 0.01f) plate.transform.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
                var eye = Part(plate.transform, "Eye", PrimitiveType.Cylinder, new Vector3(0.35f, 0.6f, 0.35f), steelMat);
                eye.transform.localPosition = new Vector3(0, 0, 0.8f); eye.transform.localRotation = Quaternion.Euler(90, 0, 0);
            }
            var spanGo = new GameObject("SpanCable"); spanGo.transform.SetParent(t, false);
            r.span = spanGo.AddComponent<LineRenderer>();
            r.span.sharedMaterial = spanMat; r.span.startWidth = r.span.endWidth = 0.035f; r.span.positionCount = 9;
            var tr = Part(t, "Trolley", PrimitiveType.Cube, new Vector3(0.5f, 0.32f, 0.3f), trolleyMat).transform;
            var motor = Part(tr, "Motor", PrimitiveType.Cylinder, new Vector3(0.45f, 0.35f, 0.45f), steelMat);
            motor.transform.localPosition = new Vector3(0, -0.2f, 0.9f); motor.transform.localRotation = Quaternion.Euler(90, 0, 0);
            foreach (float x in new[] { -0.3f, 0.3f })
            {
                var wheel = Part(tr, "Sheave", PrimitiveType.Cylinder, new Vector3(0.5f, 0.15f, 0.5f), steelMat);
                wheel.transform.localPosition = new Vector3(x, 0.55f, 0); wheel.transform.localRotation = Quaternion.Euler(0, 0, 90);
            }
            r.trolley = tr;
            var ropeGo = new GameObject("Rope"); ropeGo.transform.SetParent(t, false);
            r.rope = ropeGo.AddComponent<LineRenderer>();
            r.rope.sharedMaterial = ropeMat; r.rope.startWidth = r.rope.endWidth = 0.03f; r.rope.positionCount = 2;
            UpdateRig(r);
        }

        void UpdateRig(Rig r)
        {
            if (r.span == null) return;
            var a = WorldOrigin.FrameToWorld(r.anchorA); var b = WorldOrigin.FrameToWorld(r.anchorB);
            var tp = WorldOrigin.FrameToWorld(r.Trolley);
            // span: a gentle catenary-ish sag, deeper at the trolley when the rover hangs on it
            bool loaded = r == active && joint != null;
            float span = Vector3.Distance(a, b), sag = span * (loaded ? 0.012f : 0.006f);
            for (int i = 0; i < 9; i++)
            {
                float s = i / 8f; var p = Vector3.Lerp(a, b, s);
                p.y -= sag * 4f * s * (1 - s);
                r.span.SetPosition(i, p);
            }
            if (r.trolley != null)
            {
                r.trolley.position = tp + Vector3.down * 0.15f;
                var d = b - a; d.y = 0; if (d.sqrMagnitude > 0.01f) r.trolley.rotation = Quaternion.LookRotation(d.normalized, Vector3.up) * Quaternion.Euler(0, 90, 0);
            }
            // rope: to the cage hook while clipped on (tweened during the clip), else hanging to the foot or coiled
            Vector3 end;
            if (r == active && (state == State.Clip || state == State.Unclip))
            {
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(stateT / ClipTime));
                if (state == State.Unclip) k = 1f - k;
                var restEnd = r.RopeHanging && r.trolleyT > 0.99f && !goingDown ? (Vector3)WorldOrigin.FrameToWorld(r.foot) : tp + Vector3.down * 1f;
                end = Vector3.Lerp(restEnd, Hook, k);
                if (state == State.Unclip && goingDown) end = Vector3.Lerp(WorldOrigin.FrameToWorld(r.foot) + Vector3.up * 0.3f, Hook, k);
            }
            else if (r == active && joint != null) end = Hook;
            else if (r.RopeHanging && r.trolleyT > 0.99f) end = WorldOrigin.FrameToWorld(r.foot) + Vector3.up * 0.2f;
            else end = tp + Vector3.down * (r.RopeHanging ? 1.2f : 0.4f);
            r.rope.enabled = r.RopeHanging || r == active;
            r.rope.SetPosition(0, tp); r.rope.SetPosition(1, end);
        }

        void UpdateVisuals() { foreach (var r in rigs.Values) UpdateRig(r); }

        void Click()
        {
            if (!Application.isPlaying) return;
            if (sfx == null)
            {
                sfx = gameObject.AddComponent<AudioSource>(); sfx.playOnAwake = false; sfx.spatialBlend = 0f; sfx.volume = 0.5f;
                int rate = 44100, n = rate / 25; var d = new float[n]; var rnd = new System.Random(7);
                for (int i = 0; i < n; i++) d[i] = (float)(rnd.NextDouble() * 2 - 1) * Mathf.Exp(-i / (rate * 0.004f)) * 0.8f;
                click = AudioClip.Create("RigClick", n, 1, rate, false); click.SetData(d, 0);
            }
            sfx.PlayOneShot(click);
        }

        GUIStyle fadeStyle;
        void OnGUI()
        {
            if (FadeAlpha <= 0f) return;
            GUI.depth = -150;
            GUI.color = new Color(0, 0, 0, FadeAlpha);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            fadeStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 26, alignment = TextAnchor.MiddleCenter };
            fadeStyle.normal.textColor = new Color(0.9f, 0.92f, 1f, Mathf.Clamp01(FadeAlpha * 2f - 1f));
            GUI.Label(new Rect(0, Screen.height / 2f - 30, Screen.width, 60), FadeText, fadeStyle);
        }

        // ------------------------------------------------------------------ restart / advance team

        /// <summary>Restart: stock back, only the advance team's rigs (GIMLI-2's ropes hanging again).</summary>
        public void ResetAll()
        {
            Release();
            foreach (var r in rigs.Values) if (r.go) Util.Kill(r.go);
            rigs.Clear(); active = null; state = State.Free; Ropes = RopesPerMission; RigKits = RigKitsPerMission;
            FadeAlpha = 0f;
            foreach (var (top, rad, drop, gimli) in preRigged) PreRig(top, rad, drop, gimli, false);
        }

        readonly List<(float3 top, float radius, float drop, bool gimli)> preRigged = new();

        /// <summary>Tests: no rigs at all (not even the advance team's).</summary>
        public void ClearAllLines() { preRigged.Clear(); ResetAll(); }

        /// <summary>
        /// The advance team's rig at a shaft (span + trolley). Round 13 (T3): with no rope (trolley parked over the lane)
        /// unless GIMLI-2 went down here on its way to the seal (<paramref name="gimliRope"/>: its rope hangs from the
        /// trolley out over the hole). Uses no kit or rope from the rover's stock. Returns the rig.
        /// </summary>
        public Rig PreRig(float3 top, float radius, float drop, bool gimliRope, bool remember = true)
        {
            string key = Key(top);
            if (rigs.TryGetValue(key, out var have)) return have;
            if (remember) preRigged.Add((top, radius, drop, gimliRope));
            var r = MakeRig(top, radius, drop);
            r.preRigged = true; r.gimliRope = gimliRope; r.trolleyT = gimliRope ? 1f : 0f;
            rigs[key] = r;
            MakeRigVisual(r);
            return r;
        }
    }
}
