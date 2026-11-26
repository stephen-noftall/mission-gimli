using UnityEngine;

namespace MoonBaseAlpha
{
    /// <summary>
    /// The GIMLI-2 expedition settings: landing zone (difficulty tier) and the 4-character mission seed. Static so
    /// they survive the scene reload that rebuilds the world for a new seed / tier.
    /// </summary>
    public static class Mission
    {
        public enum Tier { Cadet = 0, Explorer = 1, Commander = 2 }

        public struct TierInfo
        {
            public string zone, rank, suffix, time;
            public float startDistance;   // m from the pit centre
            public int targetLevel;       // GIMLI-2's tube level (level 0 = first from the pit mouth)
            public string Spoken => targetLevel switch { 2 => "two", 3 => "three", 5 => "five", _ => targetLevel.ToString() };
        }

        public static readonly TierInfo[] Tiers =
        {
            new TierInfo { zone = "PINPOINT", rank = "Cadet", suffix = "cadet", time = "~5-8 min", startDistance = 500f, targetLevel = 2 },
            new TierInfo { zone = "STANDARD", rank = "Explorer", suffix = "explorer", time = "~10-15 min", startDistance = 1500f, targetLevel = 3 },
            new TierInfo { zone = "SAFE ZONE", rank = "Commander", suffix = "commander", time = "~20+ min", startDistance = 3000f, targetLevel = 5 },
        };

        /// <summary>Bearing (deg from north) of the rover start as seen from the pit: "on bearing zero-four-five".</summary>
        public const float StartBearing = 45f;

        public static Tier Current = Tier.Cadet;
        public static TierInfo Info => Tiers[(int)Current];
        public static int TargetLevel => Info.targetLevel;
        /// <summary>"b1_distance" -> "b1_distance_cadet".</summary>
        public static string Tiered(string baseId) => baseId + "_" + Info.suffix;

        /// <summary>Show the start screen when the world comes up (first launch, "New mission").</summary>
        public static bool ShowStartScreen = true;

        // ------------------------------------------------------------------ seed codes

        public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        public const int CodeCount = 36 * 36 * 36 * 36;   // 1,679,616 layouts
        const uint Mul = 2654435761u;                     // odd: (index + 1) * Mul is a bijection on uint

        public static string Normalize(string code)
        {
            if (code == null) return null;
            code = code.Trim().ToUpperInvariant();
            if (code.Length != 4) return null;
            foreach (char c in code) if (Alphabet.IndexOf(c) < 0) return null;
            return code;
        }

        public static uint SeedFromCode(string code)
        {
            code = Normalize(code);
            if (code == null) return WorldSeed.Default;
            uint idx = 0;
            foreach (char c in code) idx = idx * 36 + (uint)Alphabet.IndexOf(c);
            return (idx + 1u) * Mul;
        }

        /// <summary>The code of a world seed, or null if the seed did not come from a code.</summary>
        public static string CodeFromSeed(uint seed)
        {
            uint inv = Mul;                                   // Newton: inverse of an odd number mod 2^32
            for (int i = 0; i < 5; i++) inv *= 2u - Mul * inv;
            uint idx = seed * inv - 1u;
            if (idx >= CodeCount) return null;
            var c = new char[4];
            for (int i = 3; i >= 0; i--) { c[i] = Alphabet[(int)(idx % 36)]; idx /= 36; }
            return new string(c);
        }

        public static string RandomCode()
        {
            var c = new char[4];
            for (int i = 0; i < 4; i++) c[i] = Alphabet[Random.Range(0, Alphabet.Length)];
            return new string(c);
        }

        /// <summary>
        /// Tube layouts that are fully tested (Verification "Flow tubes"): every tier's route checked on the real floor.
        /// MOON is the default; any other code is generated the same way but untested (a kid could still get stuck).
        /// </summary>
        public static readonly string[] TestedCodes = { "MOON", "GIML", "MARE" };
        /// <summary>Start-screen names of the tested layouts (SET1 = MOON, the default; SET2 = GIML; SET3 = MARE).</summary>
        public static readonly string[] TestedNames = { "SET1", "SET2", "SET3" };
        public static bool IsTested(string code) => System.Array.IndexOf(TestedCodes, Normalize(code)) >= 0;

        /// <summary>The code the tube layout is generated from: the world's code, else MOON.</summary>
        public static string TubeCode => CodeFromSeed(WorldSeed.Value) ?? "MOON";

        /// <summary>Code shown for the current world (F1, debrief): the 4 characters, or the raw number.</summary>
        public static string CurrentCode => CodeFromSeed(WorldSeed.Value) ?? WorldSeed.Value.ToString();
    }
}
