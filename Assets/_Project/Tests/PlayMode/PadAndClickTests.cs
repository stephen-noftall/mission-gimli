using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using MoonBaseAlpha.UI;

namespace MoonBaseAlpha.Tests
{
    /// <summary>
    /// Round 12: item 0 (LAUNCH clicked with a synthetic mouse event while the rover's click-to-lock is live) and P3 (the
    /// Cadet mission played start to debrief with the scripted pad only: A launches, the stick drives onto the deck, A
    /// lowers the crane and opens the gates, the stick drives to the seal, holding A drills, A on the debrief plays again).
    /// </summary>
    public class PadAndClickTests
    {
        /// <summary>Clicks a GUI rect from inside an OnGUI pass (IMGUI buttons react to mouse down + up on the rect).</summary>
        class Clicker : MonoBehaviour
        {
            public StartScreen target; public bool done;
            int step;
            void OnGUI()
            {
                if (done || target == null || !StartScreen.IsOpen) return;
                if (Event.current.type != EventType.Layout) return;   // one pass per frame (batch mode may send no Repaint)
                var real = Event.current;
                var p = target.LaunchRect.center;
                // separate frames for down and up: control ids restart with each OnGUI call, so the button sees its own id
                if (step == 0) { target.DrawForTest(); step = 1; return; }   // LaunchRect is known after a pass
                var e = new Event { type = step == 1 ? EventType.MouseDown : EventType.MouseUp, mousePosition = p, button = 0, clickCount = 1 };
                Event.current = e;
                target.DrawForTest();
                Event.current = real;
                if (step == 2) done = true;
                step++;
            }
        }

        static IEnumerator Load(bool startScreen)
        {
            Mission.ShowStartScreen = false;
            Mission.Current = Mission.Tier.Cadet;
            Time.timeScale = 1f;
            // the world the start screen would pick (SET1 = code MOON), so LAUNCH doesn't rebuild it
            WorldSeed.Set(Mission.SeedFromCode("MOON"), false);
            SceneManager.LoadScene("Main");
            yield return null; yield return null;
            if (startScreen)
            {
                var game = Object.FindAnyObjectByType<GameBootstrap>();
                var ss = game.GetComponent<StartScreen>() ?? game.gameObject.AddComponent<StartScreen>();
                ss.game = game;
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator LaunchClickWithLockArmed()
        {
            yield return Load(true);
            var game = Object.FindAnyObjectByType<GameBootstrap>();
            var ss = game.GetComponent<StartScreen>();
            Assert.IsTrue(StartScreen.IsOpen, "start screen not open");
            Assert.IsTrue(PauseMenu.AnyMenuOpen, "the start screen must count as a menu (click-to-lock stays off)");
            // the rover's click-to-lock scripts are live behind the screen
            Assert.IsTrue(game.cam.GetComponent<Buggy.DriverCamera>().enabled && game.Player.enabled, "click-to-lock scripts not active");
            var clicker = new GameObject("Clicker").AddComponent<Clicker>();
            clicker.target = ss;
            float t0 = Time.realtimeSinceStartup;
            while (!clicker.done && Time.realtimeSinceStartup - t0 < 5f) yield return null;
            yield return null; yield return null;
            if (!clicker.done)
            {
                ss.Launch(); Time.timeScale = 1f;   // don't leave the world paused for the next test
                WorldSeed.Set(WorldSeed.Default, false);
                if (DebugHud.GuiPasses == 0) Assert.Ignore("IMGUI does not run in batch mode (no OnGUI pass): the LAUNCH click is checked in the exe");
            }
            Assert.IsTrue(clicker.done, "no GUI pass to click in");
            Assert.IsFalse(StartScreen.IsOpen, "LAUNCH click did not close the start screen");
            Assert.AreEqual(MissionDirector.Phase.Drive, game.Director.phase, "LAUNCH click did not start the mission");
            Assert.AreEqual(1f, Time.timeScale, "time not running after the LAUNCH click");
            Object.Destroy(clicker.gameObject);
            Debug.Log("[PadAndClick] LAUNCH click ok");
            WorldSeed.Set(WorldSeed.Default, false);
        }

        /// <summary>P6: normal mode draws only the helmet set (no perf / debug panels); F3 brings every panel back.</summary>
        [UnityTest]
        public IEnumerator HelmetHudModes()
        {
            yield return Load(false);
            var game = Object.FindAnyObjectByType<GameBootstrap>();
            var hud = Object.FindAnyObjectByType<DebugHud>();
            DebugHud.SetDiagnostic(false);
            for (int i = 0; i < 5; i++) yield return null;
            if (DebugHud.GuiPasses == 0) Assert.Ignore("IMGUI does not run in batch mode (no OnGUI pass): check the HUD modes in the exe");
            var normal = new System.Collections.Generic.HashSet<string>(hud.Drawn);
            foreach (var want in new[] { "heading", "status", "telemetry", "commands", "log" })
                Assert.IsTrue(normal.Contains(want), $"normal mode lacks the {want} panel (drawn: {string.Join(",", normal)})");
            foreach (var not in new[] { "perf", "diag-telemetry", "diag-clock", "diag-modes", "help" })
                Assert.IsFalse(normal.Contains(not), $"normal mode shows the debug panel {not}");
            DebugHud.SetDiagnostic(true);
            for (int i = 0; i < 5; i++) yield return null;
            var diag = new System.Collections.Generic.HashSet<string>(hud.Drawn);
            foreach (var want in new[] { "perf", "diag-telemetry", "diag-clock", "diag-modes", "log" })
                Assert.IsTrue(diag.Contains(want), $"F3 lacks {want} (drawn: {string.Join(",", diag)})");
            DebugHud.SetDiagnostic(false);
            Debug.Log($"[PadAndClick] HUD modes ok: normal [{string.Join(",", normal)}], F3 [{string.Join(",", diag)}]");
        }

        static IEnumerator Press(Controls.SimPad pad, Controls.Btn b)
        {
            pad.held[(int)b] = true; yield return null; pad.EndFrame();
            pad.held[(int)b] = false; yield return null; pad.EndFrame();
        }

        [UnityTest]
        public IEnumerator GamepadPlaysCadet()
        {
            yield return Load(true);
            var game = Object.FindAnyObjectByType<GameBootstrap>();
            var pad = Controls.Sim = new Controls.SimPad();
            try
            {
                // A on the start screen: LAUNCH
                yield return Press(pad, Controls.Btn.Use);
                yield return null; yield return null; yield return null;
                game = Object.FindAnyObjectByType<GameBootstrap>();   // (a rebuild would have replaced it)
                Assert.IsFalse(StartScreen.IsOpen, "pad A did not launch");
                Assert.AreEqual(MissionDirector.Phase.Drive, game.Director.phase, "mission did not begin");

                // the crane: from 18 m short of the deck, drive on with the stick, stop with B
                game.Teleport(1);
                yield return new WaitForSecondsRealtime(1f);
                var b = game.Buggy; var crane = game.Crane; var plat = crane.Platform.transform;
                float t0 = Time.realtimeSinceStartup; bool parked = false;
                while (Time.realtimeSinceStartup - t0 < 60f)
                {
                    var local = plat.InverseTransformPoint(b.transform.position);
                    var aim = plat.TransformPoint(new Vector3(0, 0, Mathf.Clamp(local.z * 0.6f, -2f, 2f) * (Mathf.Abs(local.z) > 3f ? 1f : 0f)));
                    var up = b.transform.up;
                    float ang = Vector3.SignedAngle(Vector3.ProjectOnPlane(b.transform.forward, up), Vector3.ProjectOnPlane(aim - b.transform.position, up), up);
                    bool there = Mathf.Abs(local.z) < 0.7f && Mathf.Abs(local.x) < 1.1f;
                    float maxV = Mathf.Abs(local.z) > 8f ? 1.4f : 0.5f;
                    pad.move = there ? Vector2.zero : new Vector2(Mathf.Clamp(ang / 20f, -1f, 1f), b.Speed < maxV ? 0.45f : 0f);
                    pad.held[(int)Controls.Btn.Brake] = there;
                    yield return null; pad.EndFrame();
                    if (there && Mathf.Abs(b.Speed) < 0.05f && crane.RoverOnPlatform) { parked = true; break; }
                }
                pad.move = Vector2.zero; pad.held[(int)Controls.Btn.Brake] = false;
                yield return null; pad.EndFrame();
                Assert.IsTrue(parked, "could not park on the crane deck with the stick");

                // A: lower (Cadet: a x3 time-lapse), A at the bottom: open the gates
                yield return Press(pad, Controls.Btn.Use);
                yield return null;
                Assert.IsTrue(crane.Moving, "pad A did not start the crane");
                bool lapse = false; t0 = Time.realtimeSinceStartup;
                while (crane.state != Base.CraneController.State.AtBottom && Time.realtimeSinceStartup - t0 < 150f) { lapse |= game.CraneTimeLapse && Time.timeScale > 2.5f; yield return null; pad.EndFrame(); }
                float ride = Time.realtimeSinceStartup - t0;
                Assert.AreEqual(Base.CraneController.State.AtBottom, crane.state, "crane did not reach the pit floor");
                Assert.IsTrue(lapse, "Cadet crane ride did not run as a time-lapse");
                Debug.Log($"[PadAndClick] Cadet crane ride {ride:0} s real time");
                yield return Press(pad, Controls.Btn.Use);
                yield return null;
                Assert.IsFalse(crane.GatesHeld, "pad A did not open the gates");

                // GIMLI-2: from 22 m short of it, drive at the seal with the stick until the drill is offered
                game.Teleport(3);
                yield return new WaitForSecondsRealtime(1.5f);
                var seal = WorldOrigin.FrameToWorld(game.SealFrame.Value);
                t0 = Time.realtimeSinceStartup;
                while (!game.Director.DrillReady && Time.realtimeSinceStartup - t0 < 40f)
                {
                    var up = b.transform.up;
                    float ang = Vector3.SignedAngle(Vector3.ProjectOnPlane(b.transform.forward, up), Vector3.ProjectOnPlane(seal - b.transform.position, up), up);
                    pad.move = new Vector2(Mathf.Clamp(ang / 25f, -1f, 1f), b.Speed < 1.2f ? 0.4f : 0f);
                    yield return null; pad.EndFrame();
                }
                pad.move = Vector2.zero; pad.held[(int)Controls.Btn.Brake] = true;
                yield return new WaitForSecondsRealtime(1f); pad.EndFrame();
                pad.held[(int)Controls.Btn.Brake] = false;
                Assert.IsTrue(game.Director.DrillReady, "drill not offered after driving to the seal");
                // hold A to drill
                pad.held[(int)Controls.Btn.Use] = true;
                t0 = Time.realtimeSinceStartup;
                while (game.Director.phase == MissionDirector.Phase.Drive && Time.realtimeSinceStartup - t0 < 12f) { yield return null; pad.EndFrame(); }
                pad.held[(int)Controls.Btn.Use] = false; yield return null; pad.EndFrame();
                Assert.AreNotEqual(MissionDirector.Phase.Drive, game.Director.phase, "holding A did not drill the seal");
                t0 = Time.realtimeSinceStartup;
                while (game.Director.phase != MissionDirector.Phase.Debrief && Time.realtimeSinceStartup - t0 < 80f) { yield return null; pad.EndFrame(); }
                Assert.AreEqual(MissionDirector.Phase.Debrief, game.Director.phase, "no debrief");
                yield return new WaitForSecondsRealtime(2f); pad.EndFrame();
                // A on the debrief: play again
                yield return Press(pad, Controls.Btn.Use);
                yield return null; yield return null; yield return null;
                game = Object.FindAnyObjectByType<GameBootstrap>();
                Assert.IsTrue(game.Initialised && game.Director.phase == MissionDirector.Phase.Drive, "pad A on the debrief did not start a new run");
                Debug.Log("[PadAndClick] gamepad Cadet flow ok");
            }
            finally { Controls.Sim = null; Time.timeScale = 1f; WorldSeed.Set(WorldSeed.Default, false); }
        }
    }
}
