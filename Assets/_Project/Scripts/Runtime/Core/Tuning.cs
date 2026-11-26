using System.Collections.Generic;
using UnityEngine;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha
{
    /// <summary>
    /// Live-tunable settings for play-test feedback (after the Godot port's Tuning.cs).
    /// \ selects the next setting, [ / ] lower / raise it (x1.25; exposure in 1/3 EV), Shift+\ resets it.
    /// Lights are scaled through <see cref="PhysicalLight.physicalIntensity"/> (base values are remembered per lamp).
    /// </summary>
    public static class Tuning
    {
        public sealed class Setting
        {
            public string Name; public float Value, Default; public bool Additive; public string Lamp;
            public float Min = 0.01f, Max = 1000f;
            /// <summary>Absolute value for the HUD (e.g. "18.8 km/h"), from the factor.</summary>
            public System.Func<float, string> Abs;
            public override string ToString()
            {
                string a = Abs != null ? $" ({Abs(Value)})" : "";
                return Additive ? $"{Name} {Value:+0.00;-0.00;0} EV{a}" : $"{Name} x{Value:0.##}{a}";
            }
        }

        // ---- round-8 play-test knobs (first; the user tunes them, a later session bakes the values in)
        public static readonly Setting RoverSpeed = new() { Name = "rover top speed", Value = 1.25f, Default = 1.25f, Max = 2.5f,
            Abs = v => $"{15f * v:0.#} km/h" };
        public static readonly Setting WalkSpeed = new() { Name = "walk speed", Value = 1.5f, Default = 1.5f, Max = 3f,
            Abs = v => $"walk {1.1f * v:0.0} / lope {2.2f * v:0.0} m/s" };
        /// <summary>Whole crane ride (winch speed/accel/jerk, slew rate, gate swing). Max = the stability-test limit.</summary>
        public static readonly Setting CraneSpeed = new() { Name = "crane speed", Value = 1.5f, Default = 1.5f, Min = 0.25f, Max = CraneSpeedLimit,
            Abs = v => $"winch {2.4f * v:0.0} m/s, slew {2f * Mathf.Min(v, 1.15f):0.0} deg/s" };
        public const float CraneSpeedLimit = 2.0f;
        /// <summary>Anti-sway damping factor (1 = round-7 damping; lower = a lazy pendulum swing).</summary>
        public static readonly Setting CraneSway = new() { Name = "crane sway", Value = 0.4f, Default = 0.4f, Min = 0.25f, Max = 1f,
            Abs = v => $"pendulum damping {Mathf.Lerp(0.6f, 1f, v) * 100:0}%" };
        public static readonly Setting WheelGrip = new() { Name = "wheel grip", Value = 2f, Default = 2f, Max = 4f,   // round 12: x2 (game tuning, above real regolith)
            Abs = v => $"basalt mu {0.75f * v:0.00}" };
        public static readonly Setting MotorTorque = new() { Name = "motor torque", Value = 1f, Default = 1f, Max = 3f,
            Abs = v => $"{220f * v:0} N m/wheel" };
        public static readonly Setting WinchSpeed = new() { Name = "rover winch speed", Value = 1f, Default = 1f, Min = 0.25f, Max = 3f,
            Abs = v => $"{0.75f * v:0.00} m/s" };
        public static readonly Setting VisorLift = new() { Name = "visor lift", Value = 1f, Default = 1f, Min = 0f, Max = 2f,
            Abs = v => $"max +{5f * v:0.0} EV" };

        public static readonly Setting Headlights = new() { Name = "headlights", Value = 1f, Default = 1f, Lamp = "HeadlightSpot" };
        public static readonly Setting FloodLamps = new() { Name = "flood lamps", Value = 1f, Default = 1f, Lamp = "FloodLight" };
        public static readonly Setting HelmetLamps = new() { Name = "helmet lamps", Value = 1f, Default = 1f, Lamp = "HelmetLamp" };
        public static readonly Setting ExposureEv = new() { Name = "exposure", Value = 0f, Default = 0f, Additive = true };
        public static readonly Setting VisorFill = new() { Name = "visor fill", Value = 1f, Default = 1f };
        public static readonly Setting[] All =
        {
            RoverSpeed, WalkSpeed, CraneSpeed, CraneSway, WheelGrip, MotorTorque, WinchSpeed, VisorLift,
            Headlights, FloodLamps, HelmetLamps, ExposureEv, VisorFill,
        };
        public static int Selected;
        public static Setting Current => All[Selected];
        public static float LastChange = -100f;

        public static void Select(Setting s) { int i = System.Array.IndexOf(All, s); if (i >= 0) Selected = i; }
        /// <summary>Tests: every knob at its neutral value (x1 / 0 EV) so physics checks see the base model.</summary>
        public static void Neutral() { foreach (var t in All) t.Value = t.Additive ? 0f : 1f; }
        public static void ResetAll() { foreach (var t in All) t.Value = t.Default; }

        public static void Cycle() { Selected = (Selected + 1) % All.Length; Changed(); }
        public static void Step(int dir)
        {
            var c = Current;
            c.Value = c.Additive ? Mathf.Clamp(c.Value + dir / 3f, -6f, 6f)
                    : c.Value <= 0f && dir > 0 ? Mathf.Max(c.Min, 0.1f)
                    : Mathf.Clamp(c.Value * Mathf.Pow(1.25f, dir), c.Min, c.Max);
            if (!c.Additive && c.Min <= 0f && c.Value < 0.05f && dir < 0) c.Value = 0f;
            Changed();
        }
        public static void Reset() { Current.Value = Current.Default; Changed(); }
        static void Changed()
        {
            LastChange = Time.unscaledTime;
            Debug.Log($"[Tuning] {Current}");
        }

        public static string Hud()
        {
            var sb = new System.Text.StringBuilder("TUNING  [ - ] +   \\ next   ");
            int n = All.Length;
            sb.Append($"{All[(Selected + n - 1) % n].Name}   <b>> {Current}</b>   {All[(Selected + 1) % n].Name}  ...  ({Selected + 1}/{n})");
            return sb.ToString();
        }

        static readonly Dictionary<PhysicalLight, float> baseIntensity = new();

        /// <summary>Push the settings into lamps + exposure (call every frame; cheap: a handful of lights).</summary>
        public static void Apply(ExposureController exposure)
        {
            foreach (var pl in PhysicalLight.All)
            {
                if (pl == null) continue;
                Setting s = null;
                foreach (var t in All) if (t.Lamp != null && pl.name.StartsWith(t.Lamp)) { s = t; break; }
                if (s == null) continue;
                if (!baseIntensity.TryGetValue(pl, out float b)) { b = pl.physicalIntensity; baseIntensity[pl] = b; }
                pl.physicalIntensity = b * s.Value;
            }
            if (exposure != null) { exposure.evCompensation = ExposureEv.Value; exposure.fillScale = VisorFill.Value; }
        }
    }

    /// <summary>Keys + fading HUD line for <see cref="Tuning"/>.</summary>
    [DefaultExecutionOrder(250)]   // before ExposureController (300) pushes light intensities
    public class TuningController : MonoBehaviour
    {
        public ExposureController exposure;
        GUIStyle style;

        void Update()
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetKeyDown(KeyCode.Backslash)) { if (shift) Tuning.Reset(); else Tuning.Cycle(); }
            if (Input.GetKeyDown(KeyCode.LeftBracket)) Tuning.Step(-1);
            if (Input.GetKeyDown(KeyCode.RightBracket)) Tuning.Step(+1);
        }

        void LateUpdate() => Tuning.Apply(exposure);

        void OnGUI()
        {
            float age = Time.unscaledTime - Tuning.LastChange;
            if (age > 4f || !MoonBaseAlpha.UI.DebugHud.Diagnostic) return;   // round 12 (P6): F3 only
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter, richText = true };
            float a = Mathf.Clamp01((4f - age) / 1f);
            float W = Screen.width;
            var r = new Rect(W * 0.5f - 480, 92, 960, 24);
            style.normal.textColor = new Color(0, 0, 0, 0.8f * a);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), Tuning.Hud(), style);
            style.normal.textColor = new Color(0.55f, 1f, 0.6f, a);
            GUI.Label(r, Tuning.Hud(), style);
        }
    }
}
