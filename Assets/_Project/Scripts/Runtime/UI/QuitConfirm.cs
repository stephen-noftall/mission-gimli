using UnityEngine;

namespace MoonBaseAlpha.UI
{
    /// <summary>
    /// Round 12 (S12): one shared quit path. Every full-screen window (start screen, Esc menu, debrief) has an EXIT button
    /// that asks first ("Exit Moon Base Alpha?"); Esc on the start screen asks too. Enter / EXIT confirms, Esc / CANCEL
    /// goes back. (Exhibit: a later attract / kiosk mode may put this behind an operator key.)
    /// </summary>
    public class QuitConfirm : MonoBehaviour
    {
        public static bool Asking { get; private set; }
        /// <summary>The frame the dialog opened or closed (Esc must not reopen it in the same frame).</summary>
        public static int ChangedFrame { get; private set; } = -1;
        /// <summary>Tests: quitting was confirmed (the app is told to quit).</summary>
        public static int QuitRequests { get; private set; }
        GUIStyle title, button;

        public static void Ask() { if (Asking) return; Asking = true; ChangedFrame = Time.frameCount; }
        public static void Cancel() { if (!Asking) return; Asking = false; ChangedFrame = Time.frameCount; }

        public static void QuitNow()
        {
            Asking = false; QuitRequests++;
            Debug.Log("[Game] exit confirmed");
#if UNITY_EDITOR
            if (Application.isPlaying) UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        /// <summary>The EXIT button for a window (asks first).</summary>
        public static void ExitButton(Rect r, GUIStyle style)
        {
            if (GUI.Button(r, "EXIT", style)) Ask();
        }

        void Update()
        {
            if (!Asking || ChangedFrame == Time.frameCount) return;
            if (Input.GetKeyDown(KeyCode.Escape) || Controls.PadDown(Controls.Btn.Brake) || Controls.PadDown(Controls.Btn.Menu)) Cancel();
            else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Controls.PadDown(Controls.Btn.Use)) QuitNow();
            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }

        void OnGUI()
        {
            if (!Asking) return;
            title ??= new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            button ??= new GUIStyle(GUI.skin.button) { fontSize = 20, fontStyle = FontStyle.Bold };
            GUI.depth = -300;
            GUI.color = new Color(0, 0, 0, 0.75f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            float w = 460, h = 170, x = (Screen.width - w) / 2, y = (Screen.height - h) / 2;
            GUI.Box(new Rect(x, y, w, h), GUIContent.none);
            GUI.Label(new Rect(x, y + 18, w, 40), "Exit Moon Base Alpha?", title);
            if (GUI.Button(new Rect(x + 30, y + 90, 180, 52), Controls.PadActive ? "EXIT (A)" : "EXIT", button)) QuitNow();
            if (GUI.Button(new Rect(x + w - 210, y + 90, 180, 52), Controls.PadActive ? "CANCEL (B)" : "CANCEL", button)) Cancel();
        }
    }
}
