namespace MoonBaseAlpha
{
    /// <summary>
    /// Round 13 (T1): the "Steep ground ahead" hint, rate-limited. It was shown at any slope over 20 deg, but since
    /// round 12 the rover climbs loose regolith to ~35 deg (grip x2), so it fired on ground that is fine to drive.
    /// Now: only over <see cref="Threshold"/> (near the real limit), only once the slope ahead has stayed that steep for
    /// <see cref="Hold"/> s, and then not again for <see cref="Cooldown"/> s unless the ground gets
    /// <see cref="Steeper"/> deg steeper. Idempotent per frame (IMGUI calls it several times a frame).
    /// </summary>
    public class SteepWarning
    {
        public const float Threshold = 30f, Release = 27f, Hold = 1f, Cooldown = 30f, Steeper = 5f;
        public const string Prefix = "Steep ground ahead";
        float overSince = -1f, lastShown = -1e9f, lastDeg;
        bool showing;
        /// <summary>Times the hint came up (tests).</summary>
        public int Shown { get; private set; }

        public static string Text(float deg) => $"{Prefix} ({deg:0} deg) - loose regolith, go around";

        /// <summary>The slope ahead now (deg) at time <paramref name="now"/> (s): true while the hint shows.</summary>
        public bool Update(float deg, float now)
        {
            if (deg < (showing ? Release : Threshold)) { overSince = -1f; showing = false; return false; }
            if (showing) return true;
            if (overSince < 0f) overSince = now;
            if (now - overSince < Hold) return false;
            if (now - lastShown < Cooldown && deg < lastDeg + Steeper) return false;
            showing = true; lastShown = now; lastDeg = deg; Shown++;
            return true;
        }

        public void Reset() { overSince = -1f; lastShown = -1e9f; showing = false; Shown = 0; }
    }
}
