using System.Collections;
using System.IO;
using UnityEngine;

namespace MoonBaseAlpha
{
    /// <summary>
    /// Exe check (round 12, P1): <c>-shots &lt;dir&gt;</c> renders a few fixed views from the built player (cockpit screens,
    /// a wheel with its hub cover and fender, the tube) into PNGs and quits, so the exe can be compared with the editor's
    /// renders (shader variants stripped from the player show up here, not in editor tests).
    /// </summary>
    public class PlayerShots : MonoBehaviour
    {
        public GameBootstrap game;

        public static string Dir
        {
            get
            {
                var a = System.Environment.GetCommandLineArgs();
                int i = System.Array.IndexOf(a, "-shots");
                return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
            }
        }
        public static bool Requested => Dir != null;

        IEnumerator Start()
        {
            Application.runInBackground = true;
            yield return new WaitForSecondsRealtime(12f);
            var cam = game.cam; var b = game.Buggy;
            Directory.CreateDirectory(Dir);
            var eye = Buggy.BuggyBuilder.DriverEye(b);
            yield return Shot("exe_cockpit", eye.position, Quaternion.LookRotation(b.transform.forward * 0.6f - b.transform.up * 0.8f + b.transform.right * 0.05f, b.transform.up));
            var wheel = b.transform.TransformPoint(new Vector3(-2.6f, 0.9f, 1.6f));
            yield return Shot("exe_wheel", wheel, Quaternion.LookRotation(b.transform.TransformPoint(new Vector3(-0.9f, 0.3f, 1.3f)) - wheel, b.transform.up));
            game.Teleport(2);
            yield return new WaitForSecondsRealtime(4f);
            var e2 = Buggy.BuggyBuilder.DriverEye(b);
            yield return Shot("exe_tube", e2.position, e2.rotation);
            Debug.Log($"[PlayerShots] done: {Dir}");
            Application.Quit();
        }

        IEnumerator Shot(string name, Vector3 pos, Quaternion rot)
        {
            yield return new WaitForEndOfFrame();
            var cam = game.cam;
            cam.transform.SetPositionAndRotation(pos, rot);
            var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var prev = cam.targetTexture;
            cam.targetTexture = rt; cam.Render(); cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); tex.Apply();
            RenderTexture.active = null; cam.targetTexture = prev;
            File.WriteAllBytes(Path.Combine(Dir, name + ".png"), tex.EncodeToPNG());
            Destroy(tex); rt.Release(); Destroy(rt);
            Debug.Log($"[PlayerShots] {name}");
        }
    }
}
