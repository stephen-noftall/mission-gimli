using UnityEngine;

namespace MoonBaseAlpha
{
    /// <summary>
    /// Round 12 (P3): the visitor's controls in one place, keyboard + mouse or a wired USB Xbox (XInput) pad through the
    /// old Input Manager (Docs/controls.md). Debug / tuning keys stay keyboard-only in their own scripts.
    /// <list type="bullet">
    /// <item>Left stick / WASD: drive or walk (double-tap forward = FAST, back = SLOW). Right stick / mouse: look.</item>
    /// <item>A / F: USE - the one context action (crane lower / open gates / raise, shaft rig ride / rope / install,
    /// hold to drill the seal; start screen: LAUNCH).</item>
    /// <item>B / Space: brake (on foot: jump). X / H: lights. Y / C: camera view. LB / E: get out / in.
    /// RB / M: tube map (right stick rotates, triggers zoom). LS click / R: recover the rover. Start / Esc: menu.
    /// D-pad up / down: visor zoom.</item>
    /// </list>
    /// Tests drive everything through <see cref="Sim"/> (a scripted pad), since Input can't be faked.
    /// </summary>
    public static class Controls
    {
        public enum Btn { Use, Brake, Lights, View, Board, Map, Menu, Recover, Back, Prev }

        /// <summary>Scripted pad for tests: held buttons and stick values (null = off).</summary>
        public class SimPad
        {
            public readonly bool[] held = new bool[16];
            public readonly bool[] prev = new bool[16];
            public Vector2 move, look; public float lt, rt, dpadY, dpadX;
            /// <summary>Call once per frame after the game read the inputs (edge detection).</summary>
            public void EndFrame() { for (int i = 0; i < held.Length; i++) prev[i] = held[i]; }
        }
        public static SimPad Sim;

        // Xbox (XInput) buttons in the old Input Manager on Windows
        static readonly KeyCode[] PadKey =
        {
            KeyCode.JoystickButton0,   // Use      A
            KeyCode.JoystickButton1,   // Brake    B
            KeyCode.JoystickButton2,   // Lights   X
            KeyCode.JoystickButton3,   // View     Y
            KeyCode.JoystickButton4,   // Board    LB
            KeyCode.JoystickButton5,   // Map      RB
            KeyCode.JoystickButton7,   // Menu     Start
            KeyCode.JoystickButton8,   // Recover  LS click
            KeyCode.JoystickButton6,   // Back     View / Back
            KeyCode.None,
        };
        static readonly KeyCode[] Keys = { KeyCode.F, KeyCode.Space, KeyCode.H, KeyCode.C, KeyCode.E, KeyCode.M, KeyCode.Escape, KeyCode.R, KeyCode.None, KeyCode.None };
        public static readonly string[] PadName = { "A", "B", "X", "Y", "LB", "RB", "Start", "LS", "Back", "" };
        public static readonly string[] KeyName = { "F", "Space", "H", "C", "E", "M", "Esc", "R", "", "" };

        /// <summary>The last input came from the pad (prompts then show pad buttons).</summary>
        public static bool PadActive { get; private set; }
        public static bool Down(Btn b)
        {
            int i = (int)b;
            if (Sim != null && Sim.held[i] && !Sim.prev[i]) { PadActive = true; return true; }
            bool pad = PadKey[i] != KeyCode.None && Input.GetKeyDown(PadKey[i]);
            bool key = Keys[i] != KeyCode.None && Input.GetKeyDown(Keys[i]);
            if (pad) PadActive = true; else if (key) PadActive = false;
            return pad || key;
        }

        /// <summary>The pad button only (menus: its keyboard twin may be a letter typed or pressed for something else).</summary>
        public static bool PadDown(Btn b)
        {
            int i = (int)b;
            if (Sim != null && Sim.held[i] && !Sim.prev[i]) { PadActive = true; return true; }
            bool pad = PadKey[i] != KeyCode.None && Input.GetKeyDown(PadKey[i]);
            if (pad) PadActive = true;
            return pad;
        }

        public static bool Held(Btn b)
        {
            int i = (int)b;
            if (Sim != null && Sim.held[i]) return true;
            return (PadKey[i] != KeyCode.None && Input.GetKey(PadKey[i])) || (Keys[i] != KeyCode.None && Input.GetKey(Keys[i]));
        }

        /// <summary>"[F]" on the keyboard, "(A)" with the pad: the one key-name table for every prompt.</summary>
        public static string Name(Btn b) => PadActive ? $"({PadName[(int)b]})" : $"[{KeyName[(int)b]}]";

        static bool axesOk = true;
        static float Axis(string name)
        {
            if (!axesOk) return 0f;
            try { return Input.GetAxisRaw(name); } catch (System.ArgumentException) { axesOk = false; return 0f; }
        }

        /// <summary>Drive / walk: x = steer, y = throttle (WASD, arrows, left stick: the "Horizontal" / "Vertical" axes).</summary>
        public static Vector2 Move
        {
            get
            {
                if (Sim != null && Sim.move.sqrMagnitude > 0f) return Sim.move;
                return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            }
        }

        /// <summary>Look delta this frame (deg-ish, mouse units): the mouse, plus the right stick at ~120 deg/s.</summary>
        public static Vector2 LookDelta(float dt)
        {
            var s = Sim != null ? Sim.look : new Vector2(Axis("RightStickX"), Axis("RightStickY"));
            if (s.sqrMagnitude > 0.04f) PadActive = true;
            float k = 120f * dt / 2f;   // the callers multiply mouse deltas by ~2 deg
            var mouse = Cursor.lockState == CursorLockMode.Locked ? new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) : Vector2.zero;
            return mouse + new Vector2(s.x, s.y) * k * (s.sqrMagnitude > 0.04f ? 1f : 0f);
        }

        /// <summary>Right stick alone (map rotation), -1..1.</summary>
        public static Vector2 RightStick => Sim != null ? Sim.look : new Vector2(Axis("RightStickX"), Axis("RightStickY"));

        /// <summary>Zoom: right trigger in, left trigger out (-1..1).</summary>
        public static float TriggerZoom => Sim != null ? Sim.rt - Sim.lt : Axis("RightTrigger") - Axis("LeftTrigger");

        /// <summary>D-pad vertical (visor zoom), -1..1.</summary>
        public static float DPadY => Sim != null ? Sim.dpadY : Axis("DPadVertical");
        public static float DPadX => Sim != null ? Sim.dpadX : Axis("DPadHorizontal");

        static float lastX, lastY;
        /// <summary>D-pad presses as edges this frame (-1, 0, +1), for menus.</summary>
        public static int DPadXDown { get { float x = DPadX; int r = x > 0.5f && lastX <= 0.5f ? 1 : x < -0.5f && lastX >= -0.5f ? -1 : 0; lastX = x; return r; } }
    }
}
