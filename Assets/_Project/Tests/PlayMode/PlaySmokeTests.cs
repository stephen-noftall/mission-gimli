using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using MoonBaseAlpha.UI;

namespace MoonBaseAlpha.Tests
{
    /// <summary>
    /// Real Play-mode smoke test: loads Main the way pressing Play does, runs frames with the normal
    /// Update/FixedUpdate/LateUpdate loop and fails on any logged error or exception.
    /// Run: Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testResults Docs/verification/playmode.xml
    /// </summary>
    public class PlaySmokeTests
    {
        [UnityTest]
        public IEnumerator MainSceneStartsAndRuns()
        {
            Mission.ShowStartScreen = false;
            SceneManager.LoadScene("Main");
            yield return null;
            yield return null;
            var game = Object.FindAnyObjectByType<GameBootstrap>();
            Assert.IsNotNull(game, "GameBootstrap missing");
            Assert.IsTrue(game.Initialised, "GameBootstrap did not finish initialising (HUD would stay hidden)");
            var hud = Object.FindAnyObjectByType<DebugHud>();
            Assert.IsNotNull(hud, "HUD component missing");
            Assert.IsTrue(hud.enabled && hud.gameObject.activeInHierarchy, "HUD disabled");
            Assert.AreSame(game, hud.game, "HUD not wired to the game");

            // run ~5 s of real frames with the rover driving (input bypassed: drive the controller directly)
            var driver = game.Buggy.GetComponent<Buggy.BuggyDriver>();
            driver.enabled = false;
            float t = 0;
            while (t < 5f)
            {
                game.Buggy.throttle = 0.6f; game.Buggy.brake = 0f; game.Buggy.parkingBrake = false;
                t += Time.deltaTime;
                yield return null;
            }
            Assert.Greater(game.Buggy.DistanceM, 1.0, "rover did not move in play mode");
            Assert.IsTrue(game.Exposure.Exposure > 0 && !float.IsNaN(game.Exposure.Exposure), "bad exposure");

            // get out and walk a little, then teleports
            game.Buggy.throttle = 0; game.Buggy.brake = 1;
            float w0 = Time.time;
            while (Mathf.Abs(game.Buggy.Speed) > 0.3f && Time.time - w0 < 15f) yield return null;
            game.Player.ExitRover();
            yield return new WaitForSeconds(1f);
            Assert.AreEqual(Crew.PlayerControl.Mode.OnFoot, game.Player.State, $"could not get out (rover speed {game.Buggy.Speed:0.00} m/s)");
            game.Teleport(2);
            yield return new WaitForSeconds(2f);
            Assert.IsTrue(game.Tunnels.ChunkCount > 0, "no tunnel chunks streamed in play mode");
            game.Teleport(3);
            yield return new WaitForSeconds(2f);
            game.Teleport(1);
            yield return new WaitForSeconds(1f);
            // Esc pause menu: simulation stops, mouse free; Resume restores both
            var pause = game.GetComponent<PauseMenu>();
            Assert.IsNotNull(pause, "pause menu missing");
            var p0 = game.Buggy.transform.position;
            pause.Open();
            Assert.IsTrue(PauseMenu.IsOpen && Time.timeScale == 0f, "pause did not stop time");
            Assert.AreEqual(CursorLockMode.None, Cursor.lockState, "mouse not freed while paused");
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.Less((game.Buggy.transform.position - p0).magnitude, 0.01f, "rover moved while paused");
            pause.Close();
            Assert.IsTrue(!PauseMenu.IsOpen && Time.timeScale > 0f, "resume did not restore time");
            yield return null;
            Debug.Log($"[PlaySmoke] ok: drove {game.Buggy.DistanceM:0.0} m, exposure EV {Mathf.Log(1f / game.Exposure.Exposure, 2):0.0}, tunnel chunks {game.Tunnels.ChunkCount}");
        }

        /// <summary>Start screen: a new seed code + landing zone rebuilds the world; the mission starts; Esc Restart keeps both.</summary>
        [UnityTest]
        public IEnumerator StartScreenLaunchesAndRestarts()
        {
            Mission.ShowStartScreen = false;
            Mission.Current = Mission.Tier.Cadet;
            SceneManager.LoadScene("Main");
            yield return null; yield return null;
            var game = Object.FindAnyObjectByType<GameBootstrap>();
            // batch mode skips the screen on start; open it the way "New mission" does
            var ss = game.GetComponent<StartScreen>() ?? game.gameObject.AddComponent<StartScreen>();
            ss.game = game;
            yield return null;
            Assert.IsTrue(StartScreen.IsOpen && Time.timeScale == 0f, "start screen not open / world not paused behind it");
            ss.code = "GIML"; ss.tier = Mission.Tier.Explorer;
            Assert.IsTrue(ss.NeedsRebuild, "a new seed + zone must rebuild the world");
            ss.Launch();
            yield return null; yield return null; yield return null;
            game = Object.FindAnyObjectByType<GameBootstrap>();
            Assert.IsTrue(game.Initialised, "world not rebuilt");
            Assert.AreEqual("GIML", Mission.CurrentCode, "seed code not applied");
            Assert.AreEqual(Mission.Tier.Explorer, game.BuiltTier, "landing zone not applied");
            Assert.AreEqual(1f, Time.timeScale, "time not running after LAUNCH");
            Assert.AreEqual(MissionDirector.Phase.Drive, game.Director.phase, "mission did not begin");
            yield return new WaitForSeconds(3f);
            Assert.IsTrue(Radio.Spoken.Contains("b1_touchdown"), "opening radio line not heard");
            PauseMenu.Restart();
            yield return null; yield return null; yield return null;
            game = Object.FindAnyObjectByType<GameBootstrap>();
            Assert.AreEqual("GIML", Mission.CurrentCode, "Restart changed the seed");
            Assert.AreEqual(Mission.Tier.Explorer, game.BuiltTier, "Restart changed the landing zone");
            Debug.Log($"[PlaySmoke] start screen ok: mission {Mission.CurrentCode} {Mission.Info.zone}, route {game.Route?.Length:0} m");
            // back to the defaults for any later test
            Mission.Current = Mission.Tier.Cadet;
            WorldSeed.Set(WorldSeed.Default, false);
        }
    }
}
