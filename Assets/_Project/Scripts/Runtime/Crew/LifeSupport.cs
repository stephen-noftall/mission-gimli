using UnityEngine;

namespace MoonBaseAlpha.Crew
{
    /// <summary>
    /// Round 12 (P6): a simple suit life-support model for the 2 crew on this sortie, for the helmet HUD's status block
    /// (Docs/science-report.md "Suit life support"). Consumables drain at real-ish rates from the metabolic rate; there is
    /// no refill (the MR-4 is an open rover) and no fail state: the mission is well under the ~8 h suit endurance.
    /// <list type="bullet">
    /// <item>O2: 0.65 kg usable per suit; use from the metabolic rate (~0.25 g O2 per kJ): seated ~0.045 kg/h, walking
    /// ~0.08 kg/h (xEMU / NASA BVAD: ~8 h of EVA work, 0.84 kg O2 per person per day at rest).</item>
    /// <item>Suit pressure 29.6 kPa (4.3 psi, xEMU / AxEMU nominal), regulated: shown with tiny sensor noise.</item>
    /// <item>CO2 partial pressure: the swing-bed scrubber keeps it ~0.1-0.4 kPa (rising with work); limit ~1 kPa.</item>
    /// <item>Suit battery: ~8 h at a base load, helmet lamps add to it.</item>
    /// <item>Suit temperature (cooling garment loop) ~22 C, warmer with work; heart rate ~75 seated, ~105 walking.</item>
    /// </list>
    /// </summary>
    public class LifeSupport
    {
        public const float O2Kg = 0.65f;                 // usable O2 per suit (kg)
        public const float O2PerKJ = 1.43e-3f / 20.2f;    // kg O2 per kJ of metabolic energy (20.2 kJ per litre O2, 1.43 g/l)
        public const float SeatedW = 180f, WalkW = 330f; // metabolic rate (W): seated in the rover / walking in the suit
        public const float SuitKPa = 29.6f;              // 4.3 psi
        public const float BatteryHours = 8f;            // base load
        public const float LampBatteryShare = 0.12f;     // helmet lamps: +12% of the base drain

        public class Crew
        {
            public string tag;
            public float o2 = 1f, battery = 1f, hr = 75f, tempC = 22f, co2KPa = 0.12f, pressureKPa = SuitKPa;
            public float metabolicW = SeatedW;
            public float O2RateKgH => metabolicW * 3.6f * O2PerKJ;               // W -> kJ/h -> kg/h
            public float O2HoursLeft => o2 * O2Kg / Mathf.Max(1e-4f, O2RateKgH);
            internal bool warned25, warned10;
        }

        public readonly Crew[] crew = { new Crew { tag = "CDR" }, new Crew { tag = "MS" } };
        float noiseT;

        /// <summary>Advance by dt (s). walking: which crew member is out on foot (-1 none); lamps: helmet lamps on.</summary>
        public void Step(float dt, int walking, bool lamps)
        {
            noiseT += dt;
            for (int i = 0; i < crew.Length; i++)
            {
                var c = crew[i];
                float targetW = i == walking ? WalkW : SeatedW;
                c.metabolicW += (targetW - c.metabolicW) * (1f - Mathf.Exp(-dt / 20f));   // ~20 s to settle
                float work = Mathf.InverseLerp(SeatedW, WalkW, c.metabolicW);
                c.o2 = Mathf.Max(0f, c.o2 - c.O2RateKgH * dt / 3600f / O2Kg);
                c.battery = Mathf.Max(0f, c.battery - dt / 3600f / BatteryHours * (1f + (lamps ? LampBatteryShare : 0f)));
                c.hr = Mathf.Lerp(74f, 106f, work) + 3f * Mathf.Sin(noiseT * 0.31f + i * 1.7f);
                c.tempC = 21.8f + 1.6f * work + 0.2f * Mathf.Sin(noiseT * 0.05f + i);
                c.co2KPa = 0.12f + 0.22f * work + 0.02f * Mathf.Sin(noiseT * 0.13f + i * 2.1f);
                c.pressureKPa = SuitKPa + 0.05f * Mathf.Sin(noiseT * 0.7f + i);
                if (c.o2 < 0.25f && !c.warned25) { c.warned25 = true; MessageLog.Add($"{c.tag} O2 below 25% ({c.O2HoursLeft:0.0} h left)", MessageLog.Kind.Warning); }
                if (c.o2 < 0.10f && !c.warned10) { c.warned10 = true; MessageLog.Add($"{c.tag} O2 below 10% - return to the lander", MessageLog.Kind.Warning); }
            }
        }

        public static Color Level(float frac) => frac < 0.10f ? new Color(1f, 0.35f, 0.3f) : frac < 0.25f ? new Color(1f, 0.75f, 0.3f) : new Color(0.72f, 1f, 0.82f);
    }
}
