using UnityEngine;

namespace MoonBaseAlpha.Buggy
{
    /// <summary>
    /// Player input -> LRV-style hand controller. Forward = drive, back = brake (then reverse once stopped),
    /// left/right = steer. Gamepad works through the default "Horizontal"/"Vertical" axes.
    ///  - Drive ranges (round 12, P2): double-tap forward = FAST, double-tap back = SLOW (0.35 s window, read from the
    ///    "Vertical" axis so W/S, arrows, a stick or the D-pad all work). Starts in SLOW. A short chime on change.
    ///  - Edge assist (round 12, S5): a lidar edge-stop. Within the braking distance of a drop ahead (pit rim, collapse
    ///    shaft) the rover drops to SLOW and slows along a braking curve ("SLOW - EDGE"); at 15 m it brakes to a stop
    ///    facing the edge ("STOP - EDGE"). Release and press forward again = creep at walking pace (to rig a winch, or
    ///    to drive in on purpose). Reversing away is always allowed. Not on talus ramps (no edge) or the crane deck.
    /// </summary>
    public class BuggyDriver : MonoBehaviour
    {
        public BuggyController buggy;
        public Light[] headlights;
        public float throttleRamp = 2.5f;
        public float steerRamp = 2.0f;

        bool reverseLatched;
        /// <summary>False while the player is on foot (H then controls the helmet lamps).</summary>
        public bool LampKeyEnabled = true;

        // ---- drive ranges (P2)
        public const float DoubleTapWindow = 0.35f;
        float lastUpTap = -10f, lastDownTap = -10f, noReverseUntil = -10f;
        bool upHeld, downHeld;
        /// <summary>Range changes (tests count them; the HUD shows the range).</summary>
        public int RangeChanges { get; private set; }

        // ---- edge assist (S5)
        public const float StopDistance = 15f, CreepSpeed = 1.0f, AssistDecel = 0.6f, MinSlowDistance = 30f;
        public enum EdgeState { None, Slowing, Stopped, Creep }
        public EdgeState Edge { get; private set; }
        /// <summary>Distance (m) from the rover to the drop ahead along its heading, or &lt; 0 for none (set by the game).</summary>
        public System.Func<Vector3, Vector3, float> edgeAhead;
        /// <summary>"SLOW - EDGE" / "STOP - EDGE" / "CREEP - EDGE", null when the assist is idle.</summary>
        public string AssistLabel { get; private set; }
        public float EdgeDistance { get; private set; } = -1f;
        bool releasedAfterStop;
        /// <summary>Auto-stops so far (tests).</summary>
        public int AutoStops { get; private set; }

        /// <summary>Speed limit of the edge assist at distance d from the drop: a braking curve ending at creep at 15 m.</summary>
        public static float CapAt(float d) => Mathf.Sqrt(CreepSpeed * CreepSpeed + 2f * AssistDecel * Mathf.Max(0f, d - StopDistance));
        /// <summary>Where the assist starts slowing for a top speed: >= 30 m, more in FAST (~85 m at 37 km/h).</summary>
        public static float SlowDistanceFor(float vmax) => Mathf.Max(MinSlowDistance, StopDistance + (vmax * vmax - CreepSpeed * CreepSpeed) / (2f * AssistDecel));

        AudioSource chime;
        AudioClip chimeUp, chimeDown;

        void Update()
        {
            if (buggy == null) return;
            var mv = Controls.Move;
            Drive(mv.y, mv.x, Controls.Held(Controls.Btn.Brake), Time.deltaTime, Time.time);
            if (Input.GetKeyDown(KeyCode.Tab)) buggy.fourWheelSteer = !buggy.fourWheelSteer;
            if (Controls.Down(Controls.Btn.Lights) && headlights != null && LampKeyEnabled) ToggleLights();
            if (Controls.Down(Controls.Btn.Recover)) buggy.Right();
        }

        public void ToggleLights()
        {
            bool on = !(headlights.Length > 0 && headlights[0].enabled);
            foreach (var l in headlights) if (l) l.enabled = on;
            buggy.extraLoadWatts = on ? 4 * 60f : 0f;
        }

        /// <summary>One frame of the hand controller (tests call it directly with scripted input).</summary>
        public void Drive(float v, float h, bool hardBrake, float dt, float now)
        {
            DetectTaps(v, now);
            float targetThrottle = 0f, targetBrake = 0f;
            float speed = buggy.Speed;
            if (v > 0.05f)
            {
                reverseLatched = false;
                if (speed < -0.3f) targetBrake = v; else targetThrottle = v;
            }
            else if (v < -0.05f)
            {
                if ((speed > 0.3f && !reverseLatched) || now < noReverseUntil) targetBrake = -v;
                else { reverseLatched = true; targetThrottle = v * 0.5f; }   // reverse is limited, like the LRV
            }
            if (Mathf.Abs(v) < 0.05f && Mathf.Abs(speed) > 0.3f) targetBrake = 0.12f;   // regen coast-down (~0.4 m/s^2)

            EdgeAssist(v, speed, ref targetThrottle, ref targetBrake);
            if (hardBrake) { targetBrake = 1f; targetThrottle = 0f; }

            buggy.throttle = Mathf.MoveTowards(buggy.throttle, targetThrottle, throttleRamp * dt);
            buggy.brake = targetBrake;
            buggy.steering = Mathf.MoveTowards(buggy.steering, h, steerRamp * dt);
            buggy.parkingBrake = Mathf.Abs(speed) < 0.05f && Mathf.Abs(v) < 0.05f;
        }

        void DetectTaps(float v, float now)
        {
            bool up = v > 0.5f, down = v < -0.5f;
            if (up && !upHeld)
            {
                if (now - lastUpTap < DoubleTapWindow) { SetRange(BuggyController.DriveRange.Fast); lastUpTap = -10f; }
                else lastUpTap = now;
            }
            if (down && !downHeld)
            {
                if (now - lastDownTap < DoubleTapWindow) { SetRange(BuggyController.DriveRange.Slow); lastDownTap = -10f; noReverseUntil = now + 0.4f; }
                else lastDownTap = now;
            }
            upHeld = up; downHeld = down;
        }

        public void SetRange(BuggyController.DriveRange r, bool sound = true)
        {
            if (buggy.RangeMode == r) return;
            // no FAST while the edge assist holds the rover back
            if (r == BuggyController.DriveRange.Fast && Edge != EdgeState.None) return;
            buggy.RangeMode = r;
            RangeChanges++;
            if (sound) Chime(r == BuggyController.DriveRange.Fast);
        }

        void EdgeAssist(float v, float speed, ref float targetThrottle, ref float targetBrake)
        {
            float d = edgeAhead != null && speed > -0.3f ? edgeAhead(buggy.transform.position, buggy.transform.forward) : -1f;
            EdgeDistance = d;
            buggy.speedCap = float.PositiveInfinity;
            float vmaxFree = buggy.MaxSpeedUncapped;
            float slowAt = SlowDistanceFor(vmaxFree);
            if (d < 0f || d > slowAt + 10f)
            {
                if (Edge != EdgeState.None) MessageLog.Clear("edge-stop");
                Edge = EdgeState.None; AssistLabel = null; releasedAfterStop = false;
                return;
            }
            if (d > slowAt) { if (Edge == EdgeState.Slowing) { Edge = EdgeState.None; AssistLabel = null; } return; }
            // inside the slow-down distance: SLOW range and the braking curve
            if (buggy.RangeMode == BuggyController.DriveRange.Fast) { buggy.RangeMode = BuggyController.DriveRange.Slow; RangeChanges++; Chime(false); }
            if (Edge == EdgeState.None) Edge = EdgeState.Slowing;
            if (Edge == EdgeState.Slowing)
            {
                float cap = CapAt(d);
                buggy.speedCap = cap;
                // follow the braking curve: off the throttle near the cap, brake firmly above it (no lag behind the curve)
                if (speed > cap - 0.3f) targetThrottle = Mathf.Min(targetThrottle, 0f);
                if (speed > cap) targetBrake = Mathf.Max(targetBrake, Mathf.Clamp01(0.35f + (speed - cap) / 0.6f));
                AssistLabel = "SLOW - EDGE";
                if (d <= StopDistance)
                {
                    Edge = EdgeState.Stopped; releasedAfterStop = false; AutoStops++;
                    MessageLog.AddOnce("edge-stop", "Auto-stop: edge ahead. Release and press forward again to creep on.", MessageLog.Kind.Warning);
                    Debug.Log($"[Driver] edge assist: auto-stop {d:0.0} m from the edge");
                }
            }
            if (Edge == EdgeState.Stopped)
            {
                AssistLabel = "STOP - EDGE";
                buggy.speedCap = CreepSpeed;
                if (v > 0.05f && releasedAfterStop && Mathf.Abs(speed) < 0.3f) { Edge = EdgeState.Creep; }
                else
                {
                    if (v <= 0.05f) releasedAfterStop = Mathf.Abs(speed) < 0.3f || releasedAfterStop;
                    if (targetThrottle > 0f) targetThrottle = 0f;            // forward is held back (reverse still works)
                    if (speed > 0.05f) targetBrake = 1f;
                }
            }
            if (Edge == EdgeState.Creep)
            {
                AssistLabel = "CREEP - EDGE";
                buggy.speedCap = CreepSpeed;
                if (speed > CreepSpeed + 0.2f) targetBrake = Mathf.Max(targetBrake, 0.5f);
            }
        }

        void Chime(bool up)
        {
            if (!Application.isPlaying) return;
            if (chime == null)
            {
                chime = gameObject.AddComponent<AudioSource>();
                chime.playOnAwake = false; chime.spatialBlend = 0f; chime.volume = 0.35f;
                chimeUp = Tone("RangeFast", 880f, 1320f); chimeDown = Tone("RangeSlow", 1320f, 880f);
            }
            chime.PlayOneShot(up ? chimeUp : chimeDown);
        }

        static AudioClip Tone(string name, float f0, float f1)
        {
            int rate = 44100, n = (int)(rate * 0.22f);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate, f = i < n / 2 ? f0 : f1;
                d[i] = 0.3f * Mathf.Sin(2 * Mathf.PI * f * t) * Mathf.Clamp01(Mathf.Min(i, n - i) / 300f);
            }
            var c = AudioClip.Create(name, n, 1, rate, false); c.SetData(d, 0); return c;
        }
    }
}
