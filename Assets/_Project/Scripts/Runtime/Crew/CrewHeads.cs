using System.Collections.Generic;
using UnityEngine;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha.Crew
{
    /// <summary>
    /// The three seated passengers: helmet lamps (one ~3200 cd spot each, no shadows), always on (scaled by
    /// the "helmet lamps" tuning), and heads that look around - a soft spring bob from the
    /// rover's motion plus look-at targets re-picked every 2-6 s (nearest wall, the crane when close, the
    /// Earth, the scout rover, the way ahead), turned at ~60 deg/s within what a suited neck + torso can do.
    /// </summary>
    public class CrewHeads : MonoBehaviour
    {
        public Buggy.BuggyDriver driver;
        public System.Func<Vector3?> earthDir, craneWorld, scoutWorld;
        public const float LampCandela = 3200f, TurnRate = 60f, YawLimit = 80f, PitchMin = -40f, PitchMax = 50f;

        class Head
        {
            public AstronautRig rig;
            public Light lamp;
            public float yaw, pitch;            // current (deg, chest frame; +pitch = look down)
            public Vector3 target;              // world point
            public bool hasTarget;
            public float nextPick;
            public int picks;
            public string what = "";
            public Vector3 bob, bobVel;
        }

        readonly List<Head> heads = new();
        System.Random rng = new(1234);
        float t;
        Vector3 lastPos, lastVel; bool first = true;

        public int Count => heads.Count;
        public Light Lamp(int i) => heads[i].lamp;
        public Transform HeadTransform(int i) => heads[i].rig.head;
        public int Picks(int i) => heads[i].picks;
        public string Looking(int i) => heads[i].what;

        public void Init(IEnumerable<AstronautRig> passengers)
        {
            foreach (var rig in passengers)
            {
                var l = new GameObject("HelmetLamp").AddComponent<Light>();
                l.transform.SetParent(rig.head, false);
                l.transform.localPosition = new Vector3(0f, 0.22f, 0.18f);
                l.transform.localRotation = Quaternion.Euler(6, 0, 0);
                l.type = LightType.Spot; l.spotAngle = 45; l.innerSpotAngle = 25; l.range = 80;
                l.color = new Color(1f, 0.97f, 0.93f);
                l.shadows = LightShadows.None;
                l.gameObject.layer = 2;
                l.gameObject.AddComponent<PhysicalLight>().physicalIntensity = LampCandela;
                l.enabled = true;
                heads.Add(new Head { rig = rig, lamp = l, nextPick = 0.5f + heads.Count * 0.9f });
            }
        }

        void LateUpdate() => Step(Time.deltaTime);

        public void SetLamps(bool on) { foreach (var h in heads) if (h.lamp) h.lamp.enabled = on; }

        public void Step(float dt)
        {
            if (dt <= 0) return;
            t += dt;
            // rover motion -> head lag on a soft spring (rover frame)
            var pos = transform.position;
            if (first) { lastPos = pos; lastVel = Vector3.zero; first = false; }
            var vel = (pos - lastPos) / dt; var acc = (vel - lastVel) / dt;
            lastPos = pos; lastVel = vel;
            if (acc.sqrMagnitude > 400f) acc = Vector3.zero;   // teleports / origin shifts
            var accLocal = transform.InverseTransformDirection(acc);
            float w = 2f * Mathf.PI * 1.6f;
            foreach (var h in heads)
            {
                if (h.rig == null || !h.rig.gameObject.activeInHierarchy) continue;
                if (t >= h.nextPick || !h.hasTarget) Pick(h);
                // desired yaw/pitch in the chest frame
                var chest = h.rig.chest != null ? h.rig.chest : h.rig.transform;
                var dir = chest.InverseTransformDirection((h.target - h.rig.head.position).normalized);
                float yawT = Mathf.Clamp(Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, -YawLimit, YawLimit);
                float pitchT = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(dir.y, -1, 1)) * Mathf.Rad2Deg, PitchMin, PitchMax);
                float step = TurnRate * dt;
                h.yaw = Mathf.MoveTowards(h.yaw, yawT, step);
                h.pitch = Mathf.MoveTowards(h.pitch, pitchT, step);
                // spring bob (nod / sway, deg)
                var a = -accLocal * 2.5f - w * w * h.bob - 2f * 0.45f * w * h.bobVel;
                h.bobVel += a * dt; h.bob += h.bobVel * dt;
                h.bob = Vector3.ClampMagnitude(h.bob, 8f);
                h.rig.head.localRotation = Quaternion.Euler(h.pitch + h.bob.z, h.yaw, -h.bob.x);
            }
        }

        void Pick(Head h)
        {
            h.nextPick = t + 2f + 4f * (float)rng.NextDouble();
            h.picks++;
            var head = h.rig.head.position;
            var fwd = transform.forward; var up = transform.up;
            var options = new List<(Vector3 p, string what, float weight)>();
            // the way ahead
            options.Add((head + fwd * 40f + up * 0.5f, "ahead", 1.2f));
            // nearest wall / rock: a few probes around, the closest hit
            float best = float.MaxValue; Vector3 wall = default;
            for (int k = 0; k < 6; k++)
            {
                float yaw = (float)(rng.NextDouble() * 160 - 80), pitch = (float)(rng.NextDouble() * 70 - 30);
                var d = Quaternion.AngleAxis(yaw, up) * Quaternion.AngleAxis(-pitch, transform.right) * fwd;
                if (Physics.Raycast(head + d * 0.5f, d, out var hit, 40f, ~(1 << 2), QueryTriggerInteraction.Ignore) && hit.distance < best)
                { best = hit.distance; wall = hit.point; }
            }
            if (best < 40f) options.Add((wall, "wall", 1.5f));
            var crane = craneWorld?.Invoke();
            if (crane.HasValue && Vector3.Distance(crane.Value, head) < 60f) options.Add((crane.Value + up * 3f, "crane", 1.5f));
            var earth = earthDir?.Invoke();
            if (earth.HasValue) options.Add((head + earth.Value * 1000f, "Earth", 1f));
            var scout = scoutWorld?.Invoke();
            if (scout.HasValue && Vector3.Distance(scout.Value, head) < 150f) options.Add((scout.Value, "scout rover", 2f));
            // don't pick the same thing twice in a row if there's a choice
            float sum = 0; foreach (var o in options) sum += o.what == h.what && options.Count > 1 ? 0 : o.weight;
            float r = (float)rng.NextDouble() * sum;
            foreach (var o in options)
            {
                float wgt = o.what == h.what && options.Count > 1 ? 0 : o.weight;
                if (r <= wgt) { h.target = o.p; h.what = o.what; h.hasTarget = true; return; }
                r -= wgt;
            }
            var last = options[options.Count - 1];
            h.target = last.p; h.what = last.what; h.hasTarget = true;
        }
    }
}
