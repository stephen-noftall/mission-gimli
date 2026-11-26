using UnityEngine;
using UnityEngine.SceneManagement;

namespace MoonBaseAlpha.UI
{
    /// <summary>
    /// Start screen: title, briefing, the 4-character mission seed (Randomize) and the landing zone. LAUNCH rebuilds
    /// the world if the seed or zone changed (scene reload, ~3 s), else starts the mission at once. The world runs
    /// paused behind it.
    /// </summary>
    public class StartScreen : MonoBehaviour
    {
        public const string Title = "ARTEMIS-MARIUS // THE GIMLI-2 EXPEDITION";
        public const string Briefing = "NASA's GIMLI-1 survey found a vast network of ancient lava tubes beneath the Marius Hills Pit. " +
            "Follow-up rover GIMLI-2 was lowered inside to map the tunnels - until it stopped at a solid rock collapse, its radar " +
            "showing a hollow chamber beyond the rubble. Drive to the pit, descend on the crane, and drill through the seal to find " +
            "out what has been trapped inside for billions of years.";
        public const string Footer = "GIMLI is real: in September 2026 NASA selected this radar, seismic and camera instrument suite to fly " +
            "to the Marius Hills Pit on a commercial lander and rover. It will find out whether the pit opens into a lava tube - a " +
            "possible shelter for a future Moon base. This exhibit imagines GIMLI has already flown and found the tubes; GIMLI-2 and " +
            "this crewed expedition are plausible 'What if?' scenarios.";

        public static bool IsOpen { get; private set; }
        /// <summary>Where LAUNCH was drawn (GUI coordinates; tests click it with a synthetic mouse event).</summary>
        public Rect LaunchRect { get; private set; }
        /// <summary>Tests: draw the screen once (inside a test component's OnGUI, with a synthetic Event.current).</summary>
        public void DrawForTest() => OnGUI();
        public GameBootstrap game;
        public string code;
        public Mission.Tier tier;
        GUIStyle title, body, small, button, big, field, tierStyle, tierOn;

        void OnEnable()
        {
            IsOpen = true;
            // the world's code if it was launched from one (Restart / New mission keep it), else MOON (the tested default)
            code = Mission.CodeFromSeed(WorldSeed.Value) ?? "MOON";
            tier = Mission.Current;
            Time.timeScale = 0f;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        void OnDisable() { IsOpen = false; }

        void Update()
        {
            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (QuitConfirm.Asking || QuitConfirm.ChangedFrame == Time.frameCount) return;
            if (Input.GetKeyDown(KeyCode.Escape) || Controls.PadDown(Controls.Btn.Back)) { QuitConfirm.Ask(); return; }   // S12: Esc here = exit (asks first)
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Controls.PadDown(Controls.Btn.Use)) { Launch(); return; }
            // the pad (P3): LB / RB = cave layout (SET1-3, RANDOM), d-pad left / right = landing zone
            int lay = Controls.PadDown(Controls.Btn.Board) ? -1 : Controls.PadDown(Controls.Btn.Map) ? 1 : 0;
            if (lay != 0)
            {
                int n = Mission.TestedCodes.Length, cur = System.Array.IndexOf(Mission.TestedCodes, Mission.Normalize(code));
                if (cur < 0) cur = n;
                cur = (cur + lay + n + 1) % (n + 1);
                code = cur < n ? Mission.TestedCodes[cur] : Mission.RandomCode();
            }
            int dz = Controls.DPadXDown;
            if (dz != 0) tier = (Mission.Tier)Mathf.Clamp((int)tier + dz, 0, 2);
        }

        /// <summary>True if launching needs a world rebuild (another seed or zone than the world was built with).</summary>
        public bool NeedsRebuild => Mission.SeedFromCode(code) != WorldSeed.Value || game == null || tier != game.BuiltTier;

        public void Launch()
        {
            var c = Mission.Normalize(code);
            if (c == null) return;
            PlayerPrefs.SetString("mba.lastCode", c);
            bool rebuild = Mission.SeedFromCode(c) != WorldSeed.Value || game == null || tier != game.BuiltTier;
            Mission.Current = tier;
            Mission.ShowStartScreen = false;
            Time.timeScale = 1f;
            if (rebuild && Application.isPlaying)
            {
                WorldSeed.Set(Mission.SeedFromCode(c), false);
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
                return;
            }
            IsOpen = false;
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
            enabled = false;
            game?.BeginMission();
        }

        void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            title.normal.textColor = new Color(1f, 0.93f, 0.8f);
            body = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true, alignment = TextAnchor.UpperCenter };
            body.normal.textColor = new Color(0.92f, 0.94f, 1f);
            small = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, alignment = TextAnchor.UpperCenter, fontStyle = FontStyle.Italic };
            small.normal.textColor = new Color(0.75f, 0.8f, 0.88f);
            button = new GUIStyle(GUI.skin.button) { fontSize = 17 };
            big = new GUIStyle(GUI.skin.button) { fontSize = 26, fontStyle = FontStyle.Bold };
            field = new GUIStyle(GUI.skin.textField) { fontSize = 26, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            tierStyle = new GUIStyle(GUI.skin.button) { fontSize = 16, wordWrap = true, richText = true };
            tierOn = new GUIStyle(tierStyle);
            tierOn.normal = tierOn.active = tierOn.hover = tierStyle.onNormal;
            tierOn.normal.textColor = tierOn.hover.textColor = new Color(1f, 0.85f, 0.4f);
        }

        void OnGUI()
        {
            if (!IsOpen) return;
            Styles();
            GUI.depth = -200;
            GUI.color = new Color(0, 0, 0, 0.72f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            float W = Mathf.Min(980f, Screen.width - 40f), x = (Screen.width - W) / 2f, y = Mathf.Max(20f, Screen.height / 2f - 380f);
            GUI.Label(new Rect(x, y, W, 50), Title, title); y += 64;
            GUI.Label(new Rect(x + 30, y, W - 60, 110), Briefing, body); y += 120;

            GUI.Label(new Rect(x, y, W, 26), "<b>CAVE LAYOUT</b>  (the mission seed: share it to replay this layout)", new GUIStyle(body) { richText = true, fontSize = 16 }); y += 30;
            // the tested layouts as buttons, the code field, and Random (generated the same way but untested)
            int nT = Mission.TestedCodes.Length; float bw0 = 110f, gap = 10f;
            float rowW = nT * (bw0 + gap) + 140 + gap + 200;
            float fx = x + (W - rowW) / 2f;
            for (int k = 0; k < nT; k++)
            {
                var tc = Mission.TestedCodes[k];
                if (GUI.Button(new Rect(fx, y, bw0, 44), Mission.TestedNames[k], Mission.Normalize(code) == tc ? tierOn : button)) code = tc;
                fx += bw0 + gap;
            }
            var typed = GUI.TextField(new Rect(fx, y, 140, 44), code ?? "", 4, field);
            if (typed != code) code = typed.ToUpperInvariant();
            fx += 140 + gap;
            if (GUI.Button(new Rect(fx, y, 200, 44), "RANDOM", button)) code = Mission.RandomCode();
            bool valid = Mission.Normalize(code) != null;
            string note = !valid ? "4 characters: A-Z and 0-9"
                        : Mission.IsTested(code) ? ""
                        : "Untested layout: generated by the same rules, but you might get stuck.";
            GUI.Label(new Rect(x, y + 48, W, 22), note, small);
            y += 78;

            GUI.Label(new Rect(x, y, W, 26), "<b>LANDING ZONE</b>", new GUIStyle(body) { richText = true, fontSize = 16 }); y += 30;
            float bw = (W - 40f) / 3f;
            for (int k = 0; k < 3; k++)
            {
                var t = Mission.Tiers[k];
                var rng = MoonBaseAlpha.Cave.FlowNetwork.TierRange(t.targetLevel);
                var label = $"<b>{t.zone}</b>  ({t.rank})\n{t.startDistance / 1000f:0.#} km to the pit, GIMLI-2 {rng.lo / 1000:0.#}-{rng.hi / 1000:0.#} km in\n{t.time}";
                if (GUI.Button(new Rect(x + k * (bw + 20f), y, bw, 84), label, (int)tier == k ? tierOn : tierStyle)) tier = (Mission.Tier)k;
            }
            y += 104;
            GUI.enabled = valid;
            LaunchRect = new Rect(x + W / 2f - 160f, y, 320, 60);
            if (GUI.Button(LaunchRect, Controls.PadActive ? "LAUNCH (A)" : "LAUNCH", big)) Launch();
            if (Controls.PadActive) GUI.Label(new Rect(x, y + 62, W, 20), "LB / RB: cave layout    D-pad left / right: landing zone    Back: exit", small);
            GUI.enabled = true;
            y += 76;
            GUI.Label(new Rect(x + 40, y, W - 80, 90), Footer, small);
            QuitConfirm.ExitButton(new Rect(Screen.width - 150, 16, 130, 44), button);
        }
    }
}
