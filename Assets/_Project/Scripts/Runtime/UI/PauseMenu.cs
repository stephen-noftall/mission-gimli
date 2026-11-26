using UnityEngine;
using UnityEngine.SceneManagement;

namespace MoonBaseAlpha.UI
{
    /// <summary>
    /// Esc pause menu: Resume / Restart (same seed and landing zone) / New mission (back to the start screen) / Quit,
    /// plus the dialogue volume and mute. The simulation stops (timeScale 0) and the mouse is free while it is open;
    /// Esc or Resume closes it and captures the mouse again.
    /// </summary>
    public class PauseMenu : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }
        /// <summary>A screen with buttons is up (Esc menu, tube map, start screen, debrief): a click must not lock the cursor.</summary>
        public static bool AnyMenuOpen => IsOpen || TunnelMap.IsOpen || StartScreen.IsOpen || MissionDirector.DebriefOpen || QuitConfirm.Asking;
        float savedScale = 1f;
        GUIStyle titleStyle, buttonStyle, labelStyle;

        void OnDisable() { if (IsOpen) Close(); }

        void Update()
        {
            if (StartScreen.IsOpen || QuitConfirm.Asking || QuitConfirm.ChangedFrame == Time.frameCount) return;
            if (Controls.Down(Controls.Btn.Menu) && !TunnelMap.IsOpen && TunnelMap.ClosedFrame != Time.frameCount) { if (IsOpen) Close(); else Open(); }
            else if (IsOpen)
            {
                // the pad: A resume, X restart, Y new mission, B resume, Back exit (asks)
                if (Controls.PadDown(Controls.Btn.Use) || Controls.PadDown(Controls.Btn.Brake)) Close();
                else if (Controls.PadDown(Controls.Btn.Lights)) Restart();
                else if (Controls.PadDown(Controls.Btn.View)) NewMission();
                else if (Controls.PadDown(Controls.Btn.Back)) QuitConfirm.Ask();
            }
            if (IsOpen && (Cursor.lockState != CursorLockMode.None || !Cursor.visible)) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            savedScale = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            Time.timeScale = savedScale;
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }

        /// <summary>Same seed, same landing zone, straight into the mission.</summary>
        public static void Restart()
        {
            IsOpen = false;
            Time.timeScale = 1f;
            Mission.ShowStartScreen = false;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        /// <summary>Back to the start screen (new seed / landing zone).</summary>
        public static void NewMission()
        {
            IsOpen = false;
            Time.timeScale = 1f;
            Mission.ShowStartScreen = true;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        void OnGUI()
        {
            if (!IsOpen) return;
            titleStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 26, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            buttonStyle ??= new GUIStyle(GUI.skin.button) { fontSize = 18 };
            labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 15 };
            GUI.depth = -100;
            GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            float w = 280, h = 46, x = (Screen.width - w) / 2, y = Screen.height / 2 - 190;
            GUI.Label(new Rect(x - 60, y, w + 120, 40), "PAUSED", titleStyle);
            GUI.Label(new Rect(x - 60, y + 36, w + 120, 24), $"Mission {Mission.CurrentCode} - {Mission.Info.zone} ({Mission.Info.rank})", new GUIStyle(labelStyle) { alignment = TextAnchor.MiddleCenter });
            bool pad = Controls.PadActive;
            if (GUI.Button(new Rect(x, y + 70, w, h), pad ? "Resume (A)" : "Resume", buttonStyle)) Close();
            if (GUI.Button(new Rect(x, y + 124, w, h), pad ? "Restart (X)" : "Restart (same mission)", buttonStyle)) Restart();
            if (GUI.Button(new Rect(x, y + 178, w, h), pad ? "New mission (Y)" : "New mission", buttonStyle)) NewMission();
            // dialogue volume + mute
            float vy = y + 240;
            GUI.Label(new Rect(x, vy, 120, 24), "Dialogue", labelStyle);
            float v = GUI.HorizontalSlider(new Rect(x + 90, vy + 7, w - 170, 20), Radio.Volume, 0f, 1f);
            if (!Mathf.Approximately(v, Radio.Volume)) Radio.Volume = v;
            bool m = GUI.Toggle(new Rect(x + w - 70, vy, 70, 24), Radio.Muted, " Mute", labelStyle);
            if (m != Radio.Muted) Radio.Muted = m;
            QuitConfirm.ExitButton(new Rect(x, y + 280, w, h), buttonStyle);
        }
    }
}
