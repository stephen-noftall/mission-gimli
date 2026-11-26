using System.Collections.Generic;
using UnityEngine;

namespace MoonBaseAlpha.Buggy
{
    /// <summary>
    /// Hard-landing detector (the rover's IMU): proper acceleration (dv/dt minus gravity) over a 0.1 s window,
    /// in Earth g. An impact event runs while it stays above <see cref="RemarkG"/>; at its end the peak decides:
    /// above <see cref="HardG"/> -> camera shake, HUD warning, Mission Control asks about the suspension (after
    /// the Earth-Moon round trip); between -> a crew remark only. No damage yet.
    /// Real: rover IMUs log shocks. The 3 g threshold is a game choice.
    /// </summary>
    public class ImpactMonitor
    {
        public const float EarthG = 9.81f, HardG = 3f, RemarkG = 1.5f, Window = 0.1f, HudSeconds = 4f, Cooldown = 2f;

        readonly Queue<(float t, Vector3 v)> hist = new();
        float t, eventPeak, lastEventEnd = -99f;
        bool inEvent;
        int remarkIdx;

        public float CurrentG { get; private set; }
        public float LastPeakG { get; private set; }
        public float LastHardG { get; private set; }
        public float LastHardAt { get; private set; } = -99f;
        public int HardCount { get; private set; }
        public int RemarkCount { get; private set; }
        public bool ShowHud => t - LastHardAt < HudSeconds;
        public string HudText => $"HARD LANDING  {LastHardG:0.0} g";

        static readonly string[] Remarks =
        {
            "bump_remark_1",
            "bump_remark_2",
            "bump_remark_3",
        };

        public void Reset() { hist.Clear(); inEvent = false; CurrentG = 0; }

        public void Sample(Vector3 velocity, Vector3 gravity, float dt)
        {
            t += dt;
            hist.Enqueue((t, velocity));
            while (hist.Count > 2 && t - Peek2().t >= Window) hist.Dequeue();
            var (t0, v0) = hist.Peek();
            float span = t - t0;
            if (span < Window * 0.9f) { CurrentG = 0; return; }
            var a = (velocity - v0) / span - gravity;
            CurrentG = a.magnitude / EarthG;
            if (CurrentG > RemarkG)
            {
                if (!inEvent && t - lastEventEnd < Cooldown) return;
                inEvent = true;
                eventPeak = Mathf.Max(eventPeak, CurrentG);
            }
            else if (inEvent)
            {
                inEvent = false; lastEventEnd = t;
                LastPeakG = eventPeak;
                if (eventPeak > HardG)
                {
                    HardCount++;
                    LastHardG = eventPeak; LastHardAt = t;
                    CameraShake.Add(Mathf.Clamp(0.012f * eventPeak, 0.03f, 0.15f), 0.9f);
                    Radio.Say("hard_landing_crew", 0.4f, true);
                    Radio.FromEarth("hard_landing_capcom", 1.2f, 0f, true);
                }
                else
                {
                    RemarkCount++;
                    Radio.Say(Remarks[remarkIdx++ % Remarks.Length], 0.6f);
                }
                eventPeak = 0;
            }
        }

        (float t, Vector3 v) Peek2()
        {
            // the second-oldest sample (the oldest may go once the second is a full window old)
            using var e = hist.GetEnumerator();
            e.MoveNext(); e.MoveNext();
            return e.Current;
        }
    }
}
