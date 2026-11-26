using System;
using UnityEngine;

namespace MoonBaseAlpha
{
    /// <summary>
    /// Mission clock (UTC). Starts in the lunar morning at the Marius Hills Pit:
    /// 2030-06-14 22:00 UTC, Sun ~20 deg above the eastern horizon, "new" Earth.
    /// (The Earth partially eclipses the Sun ~20 h later - the 15 June 2030 lunar eclipse seen from the Moon.)
    /// </summary>
    [DefaultExecutionOrder(-800)]
    public class SimClock : MonoBehaviour
    {
        public static readonly DateTime MissionStartUtc = new DateTime(2030, 6, 14, 22, 0, 0, DateTimeKind.Utc);
        static readonly float[] Scales = { 1f, 60f, 3600f, 36000f };

        public static SimClock Instance { get; private set; }
        public static DateTime NowUtc => Instance != null ? Instance.utc : MissionStartUtc;

        /// <summary>Lunar-day presets (days after mission start), from Tools/sky/ephemeris_check.py.</summary>
        public static readonly (string name, double days)[] Presets =
        {
            ("Morning - new Earth, Sun 20 deg east", 0.0), ("Noon - Sun 75 deg up, half Earth", 5.7),
            ("Afternoon - Sun 25 deg west, Earth 78%", 10.9), ("Evening - Sun 10 deg west, Earth 87%", 12.2),
            ("Night - full Earth, earthshine only", 16.0),
        };
        public int preset = 3;   // start in the lunar evening: mission well under way
        public string PresetName => Presets[preset].name;

        public double elapsedSeconds = 12.2 * 86400.0;
        public float timeScale = 1f;
        DateTime utc = MissionStartUtc;

        public DateTime Utc => utc;
        public string ScaleLabel => timeScale <= 1f ? "1x" : $"{timeScale:0}x";

        /// <summary>Edit-mode tests: Awake doesn't run there. Returns the clock, created/registered if needed.</summary>
        public static SimClock Ensure()
        {
            if (Instance != null) return Instance;
            var c = FindAnyObjectByType<SimClock>() ?? new GameObject("SimClock").AddComponent<SimClock>();
            c.Awake();
            return c;
        }

        void Awake()
        {
            Instance = this;
            utc = MissionStartUtc.AddSeconds(elapsedSeconds);
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        public void CycleScale()
        {
            int i = Array.IndexOf(Scales, timeScale);
            timeScale = Scales[(i + 1) % Scales.Length];
        }

        public void SetElapsed(double seconds)
        {
            elapsedSeconds = seconds;
            utc = MissionStartUtc.AddSeconds(seconds);
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Y))
            {
                preset = (preset + 1) % Presets.Length;
                elapsedSeconds = Presets[preset].days * 86400.0;
            }
            elapsedSeconds += Time.deltaTime * timeScale;
            utc = MissionStartUtc.AddSeconds(elapsedSeconds);
        }
    }
}
