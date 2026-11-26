using System.Collections.Generic;
using System.Text;
using UnityEngine;
using MoonBaseAlpha.Crew;
using MoonBaseAlpha.Rendering;
using MoonBaseAlpha.Cave;

namespace MoonBaseAlpha.UI
{
    /// <summary>
    /// The helmet HUD (round 12, P5 + P6). Normal mode = what a smart suit visor would show on this mission: heading,
    /// location markers, the Sun filter / low-light visor, EDGE AHEAD, a status block (rover + both crew's life support),
    /// a telemetry strip (F2 expands it to the engineering page), the message log with the CURRENT STEP line, and a small
    /// command display. F3 = diagnostic mode: every panel as before (telemetry, clock, modes, perf, site / view labels,
    /// seed, help, tuning). F1 help, F2 engineering and Shift+M work in both.
    /// </summary>
    public class DebugHud : MonoBehaviour
    {
        public GameBootstrap game;
        bool showHelp, showEngineering, showMap;
        /// <summary>F3: diagnostic mode (every panel, perf numbers, debug text).</summary>
        public static bool Diagnostic { get; private set; }
        public static void SetDiagnostic(bool on) => Diagnostic = on;
        /// <summary>Panels drawn in the last GUI pass (tests: normal vs diagnostic mode).</summary>
        public readonly HashSet<string> Drawn = new();
        /// <summary>OnGUI calls so far (tests: batch mode sends none).</summary>
        public static int GuiPasses;
        string seedText;
        GUIStyle label, small, title, marker, prompt, warn, hud, hudSmall, hudTitle, step;
        Texture2D bg, hudBg, hudEdge;
        float styleScale = -1f;
        readonly StringBuilder sb = new();

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1)) showHelp = !showHelp;
            if (Input.GetKeyDown(KeyCode.F2)) showEngineering = !showEngineering;
            if (Input.GetKeyDown(KeyCode.M) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) showMap = !showMap;   // M alone: 3D tube map
            if (Input.GetKeyDown(KeyCode.F3)) Diagnostic = !Diagnostic;
            PerfStats.Sample(Time.unscaledDeltaTime);
        }

        void Styles()
        {
            if (label != null) return;
            bg = new Texture2D(1, 1); bg.SetPixel(0, 0, new Color(0.02f, 0.03f, 0.04f, 0.62f)); bg.Apply();
            var green = new Color(0.72f, 1f, 0.82f);
            label = new GUIStyle(GUI.skin.label) { fontSize = 15, normal = { textColor = green }, richText = true };
            small = new GUIStyle(label) { fontSize = 13 };
            title = new GUIStyle(label) { fontSize = 16, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.85f, 0.45f) } };
            marker = new GUIStyle(label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.65f, 0.85f, 1f) } };
            prompt = new GUIStyle(label) { fontSize = 18, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            warn = new GUIStyle(prompt) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.45f, 0.3f) } };
        }

        void Panel(Rect r) => GUI.DrawTexture(r, bg);

        /// <summary>Helmet-style panel: opaque dark fill (readable on the bright surface and in the dark tubes) + a thin border.</summary>
        void HudPanel(Rect r)
        {
            GUI.DrawTexture(r, hudBg);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1), hudEdge); GUI.DrawTexture(new Rect(r.x, r.yMax - 1, r.width, 1), hudEdge);
            GUI.DrawTexture(new Rect(r.x, r.y, 1, r.height), hudEdge); GUI.DrawTexture(new Rect(r.xMax - 1, r.y, 1, r.height), hudEdge);
        }

        void HudStyles(float s)
        {
            if (hudBg == null)
            {
                hudBg = new Texture2D(1, 1); hudBg.SetPixel(0, 0, new Color(0.015f, 0.025f, 0.03f, 0.86f)); hudBg.Apply();
                hudEdge = new Texture2D(1, 1); hudEdge.SetPixel(0, 0, new Color(0.45f, 0.85f, 0.7f, 0.75f)); hudEdge.Apply();
            }
            if (Mathf.Approximately(s, styleScale)) return;
            styleScale = s;
            var green = new Color(0.72f, 1f, 0.82f);
            hud = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(15 * s), richText = true, wordWrap = true, normal = { textColor = green } };
            hudSmall = new GUIStyle(hud) { fontSize = Mathf.RoundToInt(12.5f * s), wordWrap = false };
            hudTitle = new GUIStyle(hud) { fontSize = Mathf.RoundToInt(12 * s), fontStyle = FontStyle.Bold, wordWrap = false, normal = { textColor = new Color(0.55f, 0.9f, 0.75f, 0.85f) } };
            step = new GUIStyle(hud) { fontSize = Mathf.RoundToInt(17 * s), fontStyle = FontStyle.Bold, wordWrap = false, normal = { textColor = new Color(1f, 0.85f, 0.45f) } };
        }

        static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        static Texture2D softDisc;
        static Texture2D SoftDisc
        {
            get
            {
                if (softDisc != null) return softDisc;
                const int N = 64;
                softDisc = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float r = Mathf.Sqrt((x - N / 2 + 0.5f) * (x - N / 2 + 0.5f) + (y - N / 2 + 0.5f) * (y - N / 2 + 0.5f)) / (N / 2);
                    softDisc.SetPixel(x, y, new Color(1, 1, 1, Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(0.8f, 1f, r))));
                }
                softDisc.Apply();
                return softDisc;
            }
        }

        void OnGUI()
        {
            GuiPasses++;
            if (game == null || !game.Initialised) return;
            if (MissionDirector.HideHud) return;   // probe camera / debrief
            Styles();
            var b = game.Buggy; var sky = game.Sky; var ex = game.Exposure; var pc = game.Player;
            float W = Screen.width, H = Screen.height;
            bool firstPerson = pc == null || pc.FirstPersonView;
            const bool enhanced = true;   // smart visor: markers always on
            if (Event.current.type == EventType.Layout) Drawn.Clear();
            float S = Mathf.Clamp(H / 1080f, 0.75f, 1.6f);
            HudStyles(S);
            // helmet frame around the first-person view
            if (firstPerson) GUI.DrawTexture(new Rect(0, 0, W, H), HelmetOverlay.Texture, ScaleMode.StretchToFill, true);
            if (showMap) DrawMap(W, H);
            var cam = game.cam;
            if (!Diagnostic)
            {
                DrawNormal(W, H, S, b, pc);
                DrawCommon(W, H, S, b, pc, firstPerson, enhanced, false);
                if (showHelp) DrawHelp(W, H);
                return;
            }
            Drawn.Add("perf"); Drawn.Add("diag-telemetry"); Drawn.Add("diag-clock"); Drawn.Add("diag-modes");
            {
                PerfStats.Stats(out float avg, out float worst);
                Panel(new Rect(W / 2 - 220, 62, 440, 68));
                string gpu = PerfStats.GpuMs > 0 ? $"GPU {PerfStats.GpuMs:0.0} ms" : "GPU n/a";
                var rr = QualityPresets.RenderResolution;
                GUI.Label(new Rect(W / 2 - 212, 64, 430, 66),
                    $"<b>{(avg > 0 ? 1000f / avg : 0):0} fps</b>  frame {avg:0.0} ms  1% {worst:0.0} ms  {gpu}\n" +
                    $"Quality {QualityPresets.Describe(game.Quality ? game.Quality.level : QualityPresets.Level.Medium)} (F4)   " +
                    $"{(QualityPresets.FrameCap ? "vSync ON" : "uncapped")} (F6)\n" +
                    $"3D {rr.x}x{rr.y}{(QualityPresets.Upscaling ? " FSR" : "")} -> screen {Screen.width}x{Screen.height}", small);
            }

            // ---- top-left: telemetry
            sb.Clear();
            if (pc == null || pc.State == PlayerControl.Mode.Driving)
            {
                sb.AppendLine($"<b>MR-4 ROVER</b>   {b.Speed * 3.6f:0.0} km/h");
                sb.AppendLine($"Battery {b.stateOfCharge * 100:0.0}%   {b.PowerDraw / 1000f:0.00} kW");
                sb.AppendLine($"Trip {b.DistanceM / 1000.0:0.00} km   {(b.DistanceM > 10 ? b.EnergyUsedWh / (b.DistanceM / 1000.0) : 0):0} Wh/km");
                float tilt = Vector3.Angle(b.transform.up, -TerrainFrame.DownAt(b.transform.position));
                sb.AppendLine($"Tilt {tilt:0} deg   Steer {(b.fourWheelSteer ? "4WS" : "2WS")}" + (b.Damaged ? "   <color=#ff7050><b>ROVER DAMAGED</b></color>" : ""));
            }
            else
            {
                var w = pc.Walker;
                sb.AppendLine($"<b>ON FOOT</b>   {w.Speed:0.0} m/s {(w.Grounded ? "" : "  (airborne)")}");
                sb.AppendLine($"Rover {Vector3.Distance(w.transform.position, b.transform.position):0} m away");
                sb.AppendLine($"Helmet lamps {(w.LampsOn ? "ON" : "off")}");
            }
            var f = WorldOrigin.WorldToFrame(pc != null ? pc.Focus.position : b.transform.position);
            float surf = game.Height.DataHeight(f.x, f.z);
            float below = surf - (float)f.y;
            var net = game.Cave.Network;
            if (below > 15f)
            {
                float along = net.ToUV(new Unity.Mathematics.float2((float)f.x, (float)f.z)).x;
                sb.Append($"<b>UNDERGROUND</b>   {below:0} m below the surface\n{along / 1000f:0.00} km down-tube   seed {Mission.CurrentCode}");
            }
            else sb.Append($"Pos E{f.x:0} N{f.z:0}  alt {f.y - game.Cave.Field.rim:0.0} m   seed {Mission.CurrentCode}");
            Panel(new Rect(10, 10, 320, 112));
            GUI.Label(new Rect(18, 12, 320, 112), sb.ToString(), small);

            if (enhanced) {
            // ---- top-centre: clock + compass
            var up = -TerrainFrame.DownAt(cam.transform.position);
            var fwd = Vector3.ProjectOnPlane(cam.transform.forward, up);
            float heading = (Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg + 360f) % 360f;
            string card = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[Mathf.RoundToInt(heading / 45f) % 8];
            Panel(new Rect(W / 2 - 170, 10, 340, 46));
            GUI.Label(new Rect(W / 2 - 162, 12, 330, 22), $"<b>{heading:000}° {card}</b>     UTC {SimClock.NowUtc:yyyy-MM-dd HH:mm}  ({SimClock.Instance?.ScaleLabel ?? "1x"})", small);
            var s = sky.State;
            string ecl = s.sunVisibleFraction < 0.999 ? $"  ECLIPSE {100 - s.sunVisibleFraction * 100:0}%" : "";
            GUI.Label(new Rect(W / 2 - 162, 32, 330, 22), $"Sun el {sky.SunElevationDeg:0.0}°   Earth {s.earthLitFraction * 100:0}% lit{ecl}", small);
            }

            // ---- top-right: modes
            string visor = "AUTO" + (pc != null && pc.Zoom > 1 ? $"   <b>ZOOM {pc.Zoom}x</b>" : "") + (sky.SunFilterOn ? "   <b>SUN FILTER</b>" : "")
                         + (game.Visor != null && game.Visor.Showing ? $"\n<b>VISOR: LOW-LIGHT ASSIST +{game.Visor.Ev:0.0} EV</b>" : "");
            string lamps = pc != null && pc.State == PlayerControl.Mode.OnFoot ? (pc.Walker.LampsOn ? "helmet lamps ON" : "lamps off")
                         : (b.GetComponent<Buggy.BuggyDriver>().headlights[0].enabled ? "headlights ON" : "lights off");
            bool assist = game.Visor != null && game.Visor.Showing;
            Panel(new Rect(W - 290, 10, 280, assist ? 88 : 70));
            GUI.Label(new Rect(W - 282, 12, 270, assist ? 88 : 70),
                $"Visor: <b>{visor}</b>\nView: <b>{(pc != null ? pc.ViewLabel : "FIRST PERSON")}</b> (V)   {lamps}\nF1 help   F2 engineering", small);
            if (pc != null && !pc.FirstPersonView && !pc.ThirdPerson)
            {
                Panel(new Rect(W / 2 - 110, H - 112, 220, 30));
                GUI.Label(new Rect(W / 2 - 110, H - 112, 220, 30), "<b>" + pc.ViewLabel + "</b>", prompt);
            }
            if (Time.unscaledTime - game.SiteLabelTime < 5f && !string.IsNullOrEmpty(game.SiteLabel))
            {
                Panel(new Rect(W / 2 - 300, 118, 600, 32));
                GUI.Label(new Rect(W / 2 - 300, 118, 600, 32), game.SiteLabel, prompt);
            }

            DrawCommon(W, H, S, b, pc, firstPerson, enhanced, true);
            if (showHelp) DrawHelp(W, H);
            if (showEngineering) DrawEngineering(new Rect(W - 740, H - 150, 730, 140), b, ex);   // bottom right (the log is bottom left)
        }

        /// <summary>Everything both modes show: Sun filter, low-light label, markers, EDGE AHEAD / impact, log + step.</summary>
        void DrawCommon(float W, float H, float S, Buggy.BuggyController b, PlayerControl pc, bool firstPerson, bool enhanced, bool diag)
        {
            var sky = game.Sky; var cam = game.cam;
            var f = WorldOrigin.WorldToFrame(pc != null ? pc.Focus.position : b.transform.position);
            float below = game.Height.DataHeight(f.x, f.z) - (float)f.y;
            // ---- visor local dimming patch over the Sun (speculative auto-dimming visor)
            if (sky.SunFilter > 0.02f && firstPerson)
            {
                var sp = cam.WorldToScreenPoint(cam.transform.position + sky.SunDir * 1000f);
                if (sp.z > 0)
                {
                    float rpx = Mathf.Tan(Sky.SkyController.FilterRadiusDeg * Mathf.Deg2Rad) / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * H * 0.5f;
                    var prev = GUI.color;
                    GUI.color = new Color(0.05f, 0.04f, 0.02f, 0.55f * sky.SunFilter);
                    GUI.DrawTexture(new Rect(sp.x - rpx, H - sp.y - rpx, 2 * rpx, 2 * rpx), SoftDisc, ScaleMode.StretchToFill, true);
                    GUI.color = prev;
                    GUI.Label(new Rect(sp.x - 60, H - sp.y + rpx + 2, 120, 20), "SUN FILTER", small);
                    Drawn.Add("sunfilter");
                }
            }
            // ---- low-light assist: a faint label under the lifted patch
            if (game.Visor != null && game.Visor.Showing && firstPerson)
            {
                float rpx = Mathf.Tan(Rendering.VisorAssist.ConeDeg * Mathf.Deg2Rad) / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * H * 0.5f;
                var prev = GUI.color; GUI.color = new Color(1, 1, 1, Mathf.Clamp01(game.Visor.Ev / 2f) * 0.8f);
                GUI.Label(new Rect(W / 2 - 120, H / 2 + rpx + 4, 240, 20), "VISOR: LOW-LIGHT ASSIST", marker);
                GUI.color = prev;
                Drawn.Add("visor");
            }
            // ---- markers: surface ones on the surface, underground ones underground; the pit / crane in both
            if (enhanced)
            {
                foreach (var m in MarkersFor(cam.transform.position, below >= 15f))
                    Marker(cam, (m.world - cam.transform.position).normalized, m.label, m.colour);
                Drawn.Add("markers");
            }
            // ---- context: crane / rig prompts, get in / out, recovery hints (the CURRENT STEP line shows them; F3: a box)
            string ctx = Context(b, pc);
            if (ctx != null)
            {
                // log a prompt once per appearance (numbers in it - distances, angles - change every frame)
                // (round 13, T1: steep-ground hints stay on the step line only, and the same prompt is not logged again
                // within PromptRelog s when it flickers off and on)
                string key = System.Text.RegularExpressions.Regex.Replace(ctx, "[0-9.]+", "#");
                if (key != lastCtx && !ctx.StartsWith(SteepWarning.Prefix)
                    && (!promptLogged.TryGetValue(key, out float lt) || Time.unscaledTime - lt > PromptRelog))
                { MessageLog.Add(ctx, MessageLog.Kind.Prompt); promptLogged[key] = Time.unscaledTime; }
                lastCtx = key; ctxTime = Time.unscaledTime;
            }
            else lastCtx = null;
            var drv = b.GetComponent<Buggy.BuggyDriver>();
            string warning = b.Impact.ShowHud ? b.Impact.HudText : !string.IsNullOrEmpty(game.EdgeWarning) ? game.EdgeWarning : drv != null && drv.AssistLabel != null && drv.Edge != Buggy.BuggyDriver.EdgeState.None ? drv.AssistLabel : null;
            if (warning != null)
            {
                var r = new Rect(W / 2 - 190 * S, H * 0.3f, 380 * S, 42 * S);
                HudPanel(r); GUI.Label(r, warning, warn);
                Drawn.Add("warning");
            }
            if (b.Impact.ShowHud) MessageLog.AddOnce("impact", b.Impact.HudText, MessageLog.Kind.Warning); else MessageLog.Clear("impact");
            if (diag && ctx != null)
            {
                Panel(new Rect(W / 2 - 170, H - 70, 340, 34));
                GUI.Label(new Rect(W / 2 - 170, H - 70, 340, 34), ctx, prompt);
            }
            DrawLog(W, H, S, ctx);
        }

        string lastCtx; float ctxTime = -100f;
        const float PromptRelog = 20f;
        readonly System.Collections.Generic.Dictionary<string, float> promptLogged = new();
        /// <summary>Round 13 (T1): the steep-ground hint, rate-limited.</summary>
        public readonly SteepWarning Steep = new();

        /// <summary>The context prompt now (crane, shaft rig, get in / out, stuck / flipped, steep ground).</summary>
        string Context(Buggy.BuggyController b, PlayerControl pc)
        {
            string ctx = null;
            if (pc != null)
            {
                if (pc.State == PlayerControl.Mode.Driving) ctx = Mathf.Abs(b.Speed) > 1f ? null : $"{Controls.Name(Controls.Btn.Board)} get out";
                else if (pc.NearRoverDistance < 2.8f) ctx = $"{Controls.Name(Controls.Btn.Board)} get in the rover";
            }
            if (game.Crane != null && !string.IsNullOrEmpty(game.Crane.Prompt)) ctx = game.Crane.Prompt;
            if (game.Winch != null && !string.IsNullOrEmpty(game.Winch.Prompt)) ctx = game.Winch.Prompt;
            float tiltNow = Vector3.Angle(b.transform.up, -TerrainFrame.DownAt(b.transform.position));
            string rec = Controls.Name(Controls.Btn.Recover);
            if (tiltNow > 40f) ctx = $"Rover is on its side - {rec} to right it";
            else if (pc != null && pc.State == PlayerControl.Mode.Driving && b.HighCentred && Mathf.Abs(b.throttle) > 0.05f)
                ctx = $"High-centred on a rim - try reverse, or {rec} to recover";
            else if (pc != null && pc.State == PlayerControl.Mode.Driving && ctx == null)
            {
                float steep = SlopeAhead(b);
                if (Steep.Update(steep, Time.unscaledTime)) ctx = SteepWarning.Text(steep);
            }
            // "get out" is only a hint, not a step
            if (ctx != null && ctx.EndsWith("get out")) return null;
            return ctx;
        }

        /// <summary>
        /// The message log (bottom left): radio lines, prompts and warnings, newest at the bottom, older ones dim, colour by
        /// speaker / type; under it the CURRENT STEP line (a context prompt overrides the beat step while it lasts).
        /// </summary>
        void DrawLog(float W, float H, float S, string ctx)
        {
            float w = Mathf.Min(640f * S, W * 0.42f), pad = 8f * S, rows = 7f, rowH = 20f * S;
            float stepH = 30f * S, logH = rows * rowH + 22f * S + pad;
            var r = new Rect(16f * S, H - 16f * S - stepH - 6f * S - logH, w, logH);
            HudPanel(r);
            GUI.Label(new Rect(r.x + pad, r.y + 3f * S, w, 18f * S), "LOG", hudTitle);
            var e = MessageLog.Entries;
            float y = r.yMax - pad; int shown = 0;
            for (int i = e.Count - 1; i >= 0 && shown < 12; i--)
            {
                var en = e[i];
                Color c = en.kind switch
                {
                    MessageLog.Kind.Houston => new Color(0.65f, 0.85f, 1f),
                    MessageLog.Kind.Crew => new Color(1f, 0.95f, 0.82f),
                    MessageLog.Kind.Warning => new Color(1f, 0.5f, 0.35f),
                    MessageLog.Kind.Prompt => new Color(1f, 0.85f, 0.45f),
                    _ => new Color(0.72f, 1f, 0.82f),
                };
                if (shown > 0) c.a = Mathf.Lerp(0.8f, 0.45f, shown / 6f);
                string txt = string.IsNullOrEmpty(en.who) ? en.text : $"<b>{en.who}:</b> {en.text}";
                var content = new GUIContent($"<color=#{Hex(c)}{(int)(c.a * 255):X2}>{txt}</color>");
                float h = hud.CalcHeight(content, w - 2 * pad);
                if (y - h < r.y + 22f * S) break;
                y -= h;
                GUI.Label(new Rect(r.x + pad, y, w - 2 * pad, h), content, hud);
                shown++;
            }
            Drawn.Add("log");
            string stepText = ctx ?? game.Director?.CurrentStep;
            if (!string.IsNullOrEmpty(stepText))
            {
                var sr = new Rect(r.x, r.yMax + 6f * S, w, stepH);
                HudPanel(sr);
                GUI.Label(new Rect(sr.x + pad, sr.y + 3f * S, w - 2 * pad, stepH), $"<size={Mathf.RoundToInt(11 * S)}>CURRENT STEP</size>   {stepText}", step);
                Drawn.Add("step");
                CurrentStepShown = stepText;
            }
        }

        /// <summary>The step line shown last (tests).</summary>
        public string CurrentStepShown { get; private set; }

        /// <summary>Normal mode: heading, status block, telemetry strip, command display.</summary>
        void DrawNormal(float W, float H, float S, Buggy.BuggyController b, PlayerControl pc)
        {
            var cam = game.cam; var sky = game.Sky;
            // heading (top centre)
            var up = -TerrainFrame.DownAt(cam.transform.position);
            var fwd = Vector3.ProjectOnPlane(cam.transform.forward, up);
            float heading = (Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg + 360f) % 360f;
            string card = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[Mathf.RoundToInt(heading / 45f) % 8];
            var hr = new Rect(W / 2 - 70 * S, 10 * S, 140 * S, 28 * S);
            HudPanel(hr); GUI.Label(hr, $"<b>{heading:000}° {card}</b>", new GUIStyle(hud) { alignment = TextAnchor.MiddleCenter });
            Drawn.Add("heading");

            // status block (top left): rover + both crew's life support
            {
                var drv = b.GetComponent<Buggy.BuggyDriver>();
                bool fast = b.RangeMode == Buggy.BuggyController.DriveRange.Fast;
                sb.Clear();
                sb.Append($"<b>MR-4</b>  {Mathf.Abs(b.Speed) * 3.6f:0.0} km/h  <b><color=#{(fast ? "ffbf4d" : "b8ffd1")}>{(fast ? "FAST" : "SLOW")}</color></b>");
                if (drv != null && drv.AssistLabel != null) sb.Append($"  <color=#ffbf4d>{drv.AssistLabel}</color>");
                sb.Append('\n');
                float soc = b.stateOfCharge;
                sb.Append($"BATTERY <color=#{Hex(Crew.LifeSupport.Level(soc))}>{soc * 100:0}%</color>");
                if (game.Winch != null) sb.Append($"   ROPES {game.Winch.Ropes}   RIGS {game.Winch.RigKits}");
                if (b.Damaged) sb.Append("   <color=#ff7050><b>DAMAGED</b></color>");
                sb.Append("\n<color=#8cd9b8>CREW  O2         SUIT    CO2    BATT  TEMP  HR</color>");
                foreach (var c in game.Life.crew)
                    sb.Append($"\n{c.tag,-4}<color=#{Hex(Crew.LifeSupport.Level(c.o2))}>{c.o2 * 100,3:0}% {c.O2HoursLeft,4:0.0}h</color>  {c.pressureKPa:0.0}kPa  {c.co2KPa:0.00}  <color=#{Hex(Crew.LifeSupport.Level(c.battery))}>{c.battery * 100,3:0}%</color>  {c.tempC:0}°C  {c.hr:0}");
                var r = new Rect(10 * S, 10 * S, 390 * S, 112 * S);
                HudPanel(r);
                GUI.Label(new Rect(r.x + 8 * S, r.y + 4 * S, r.width - 12 * S, r.height), sb.ToString(), new GUIStyle(hudSmall) { font = MonoFont });
                Drawn.Add("status");
            }

            // telemetry strip (top right); F2 expands it to the engineering page
            {
                var dir = game.Director;
                float met = dir != null ? dir.MissionTime : 0f;
                var s = sky.State;
                float motorW = 0f, slip = 0f; int n = 0;
                foreach (var w in b.wheels) { motorW += Mathf.Max(0f, w.motorTorque * w.omega); slip += Mathf.Abs(w.slip); n++; }
                sb.Clear();
                sb.Append($"UTC {SimClock.NowUtc:yyyy-MM-dd HH:mm}   MET {(int)(met / 3600):0}:{(int)(met / 60) % 60:00}:{(int)met % 60:00}\n");
                sb.Append($"SUN EL {sky.SunElevationDeg:0.0}°   EARTH {s.earthLitFraction * 100:0}% LIT\n");
                sb.Append($"DRIVE {motorW / 1000f:0.00} kW   SLIP {(n > 0 ? slip / n * 100 : 0):0}%   BUS {b.PowerDraw / 1000f:0.00} kW\n");
                sb.Append("SUSP");
                foreach (var w in b.wheels) sb.Append($" {w.x * 100:0}");
                sb.Append($" cm   TRIP {b.DistanceM / 1000.0:0.00} km");
                var r = new Rect(W - 10 * S - 330 * S, 10 * S, 330 * S, 80 * S);
                HudPanel(r);
                GUI.Label(new Rect(r.x + 8 * S, r.y + 4 * S, r.width - 12 * S, r.height), sb.ToString(), hudSmall);
                Drawn.Add("telemetry");
                if (showEngineering) { DrawEngineering(new Rect(W - 10 * S - 740, r.yMax + 6 * S, 740, 140), b, game.Exposure); Drawn.Add("engineering"); }
            }

            // command display (bottom right): the 3-5 controls that matter now
            {
                bool padOn = Controls.PadActive;
                string U = Controls.Name(Controls.Btn.Use);
                var lines = new List<string>();
                bool onFoot = pc != null && pc.State == PlayerControl.Mode.OnFoot;
                if (!onFoot)
                {
                    lines.Add($"{(padOn ? "Left stick" : "WASD")}  drive   (2x tap: FAST / SLOW)");
                    lines.Add($"{Controls.Name(Controls.Btn.Brake)}  brake    {Controls.Name(Controls.Btn.Lights)}  lights    {Controls.Name(Controls.Btn.View)}  camera");
                    if (game.Crane != null && !string.IsNullOrEmpty(game.Crane.Prompt)) lines.Add($"{U}  crane");
                    else if (game.Winch != null && !string.IsNullOrEmpty(game.Winch.Prompt)) lines.Add($"{U}  shaft rig");
                    else if (game.Director != null && game.Director.DrillReady) lines.Add($"hold {U}  drill the seal");
                    if (game.Director != null && game.Director.InTubes) lines.Add($"{Controls.Name(Controls.Btn.Map)}  3D tube map");
                    lines.Add($"{Controls.Name(Controls.Btn.Board)}  get out (stopped)");
                }
                else
                {
                    lines.Add($"{(padOn ? "Left stick" : "WASD")}  walk    {Controls.Name(Controls.Btn.Brake)}  jump");
                    lines.Add($"{Controls.Name(Controls.Btn.Lights)}  helmet lamps    {Controls.Name(Controls.Btn.View)}  camera");
                    lines.Add($"{Controls.Name(Controls.Btn.Board)}  get in (at the driver seat)");
                }
                float lh = 19f * S, h = lines.Count * lh + 12f * S, w = 330f * S;
                var r = new Rect(W - 16f * S - w, H - 16f * S - h, w, h);
                HudPanel(r);
                for (int i = 0; i < lines.Count; i++) GUI.Label(new Rect(r.x + 8 * S, r.y + 6 * S + i * lh, w - 12 * S, lh), lines[i], hudSmall);
                Drawn.Add("commands");
            }
        }

        static Font monoFont;
        static Font MonoFont
        {
            get
            {
                if (monoFont == null) { try { monoFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New", "Lucida Console" }, 13); } catch { } }
                return monoFont;
            }
        }

        void DrawEngineering(Rect r, Buggy.BuggyController b, ExposureController ex)
        {
            sb.Clear();
            foreach (var w in b.wheels)
                sb.AppendLine($"{w.name} {(w.grounded ? "   " : "AIR")} {w.load,5:0} N  sink {w.sinkage * 100,4:0.0} cm  slip {w.slip * 100,4:0}%  " +
                              $"a {w.slipAngle * Mathf.Rad2Deg,5:0.0}  susp {w.x * 100,3:0} cm  {(w.soil.rigid ? "basalt" : w.soil.kphi > 1e6 ? "talus" : "regolith")}  {(string.IsNullOrEmpty(w.limit) ? "" : "lim: " + w.limit)}");
            {
                var upv = -TerrainFrame.DownAt(b.transform.position);
                float grade = 90f - Vector3.Angle(b.transform.forward, upv);
                sb.AppendLine($"Grade {grade:+0;-0} deg   top speed {b.MaxSpeedNow * 3.6f:0.0} km/h ({b.RangeMode})   torque {b.PeakTorqueNow:0} N m/wheel   grip x{Tuning.WheelGrip.Value:0.##}");
            }
            if (game.Winch != null) sb.AppendLine($"Ropes {game.Winch.Ropes}/{Buggy.RoverWinch.RopesPerMission}, rig kits {game.Winch.RigKits}/{Buggy.RoverWinch.RigKitsPerMission} ({game.Winch.RiggedCount} rigs)   winch {game.Winch.state}" + (b.Damaged ? "   ROVER DAMAGED: top speed capped" : ""));
            sb.Append($"EV {Mathf.Log(1f / Mathf.Max(ex.Exposure, 1e-9f), 2):0.0}  metered {ex.MeteredLuminance:0.###}  dark {ex.Darkness:0.00}  fill {ex.GameFillLux:0} lux");
            Panel(r);
            GUI.Label(new Rect(r.x + 8, r.y + 4, r.width, r.height), sb.ToString(), small);
        }

        void DrawHelp(float W, float H)
        {
            Drawn.Add("help");
            var r = new Rect(W - 380, 92, 370, 650);
            Panel(r);
            GUI.Label(new Rect(r.x + 10, r.y + 6, r.width, 24), "CONTROLS (Docs/controls.md)", title);
            GUI.Label(new Rect(r.x + 10, r.y + 32, r.width - 20, r.height),
                "<b>Visitor (pad / keyboard)</b>\n" +
                "  Left stick / WASD   drive (2x tap fwd FAST, back SLOW)\n" +
                "  Right stick / mouse look\n" +
                "  A / F      USE: crane, shaft rig, hold = drill\n" +
                "  B / Space  brake (on foot: jump)\n" +
                "  X / H      lights      Y / C  camera view\n" +
                "  LB / E     get out / in\n" +
                "  RB / M     3D tube map (stick rotates, triggers zoom)\n" +
                "  LS / R     recover the rover\n" +
                "  D-pad / wheel  visor zoom 1/2/4/8x\n" +
                "  Start / Esc    menu\n" +
                "<b>Operator (keyboard)</b>\n" +
                "  F1 help   F2 engineering   F3 diagnostic mode\n" +
                "  F4 quality   F6 frame cap   Tab 4WS/2WS   V cameras\n" +
                "  L / U / G / J / K  crane lower / raise / gates, winch, skip\n" +
                "  \\  next setting   [ ]  down / up   Shift+\\  reset it\n" +
                "  1 start   2 crane   3 tube   4 GIMLI-2   Backspace restart\n" +
                "  5 / Shift+5  next / previous special site\n" +
                "  Shift+M flat map   T time-scale    Y time of lunar day", small);
            // the mission seed code (4 characters, as on the start screen); a raw number still works
            seedText ??= Mission.CurrentCode;
            GUI.Label(new Rect(r.x + 10, r.y + r.height - 32, 60, 24), "Seed", small);
            seedText = GUI.TextField(new Rect(r.x + 60, r.y + r.height - 32, 120, 22), seedText);
            if (GUI.Button(new Rect(r.x + 190, r.y + r.height - 32, 80, 22), "Apply"))
            {
                var code = Mission.Normalize(seedText);
                if (code != null) game.Reseed(Mission.SeedFromCode(code));
                else if (uint.TryParse(seedText, out var ns)) game.Reseed(ns);
            }
            if (GUI.Button(new Rect(r.x + 280, r.y + r.height - 32, 80, 22), "Random")) game.Reseed(Mission.SeedFromCode(Mission.RandomCode()));
        }

        /// <summary>A HUD marker: what it points at, its label and colour (Earth / Sun are directions far away).</summary>
        public struct HudMarker { public string id, label; public Vector3 world; public Color colour; }

        /// <summary>
        /// The markers for a viewpoint. On the surface: EARTH, SUN, GIMLI-1, CRANE (the crane platform). Underground:
        /// the GIMLI-2 guide, fixed lines, and PIT / CRANE (the crane's landing spot on the pit floor: the way out).
        /// No surface markers underground and no underground markers on the surface.
        /// </summary>
        public List<HudMarker> MarkersFor(Vector3 eye, bool underground)
        {
            var list = new List<HudMarker>();
            var sky = game.Sky;
            void Add(string id, Vector3 w, string label, Color c) => list.Add(new HudMarker { id = id, world = w, label = label, colour = c });
            var dir = game.Director;
            bool story = dir != null && dir.phase != MissionDirector.Phase.Idle;
            if (!underground)
            {
                Add("earth", eye + sky.EarthDir * 1000f, "EARTH", new Color(0.6f, 0.85f, 1f));
                Add("sun", eye + sky.SunDir * 1000f, "SUN", new Color(1f, 0.9f, 0.5f));
                if (story && game.GimliWorld.HasValue && (game.GimliWorld.Value - eye).sqrMagnitude < 2500f * 2500f)
                    Add("gimli1", game.GimliWorld.Value, $"GIMLI-1  {Vector3.Distance(game.GimliWorld.Value, eye):0} m", new Color(0.75f, 0.9f, 1f));
                if (game.CraneWorld.HasValue) Add("crane", game.CraneWorld.Value, "CRANE", new Color(0.8f, 1f, 0.6f));
            }
            else
            {
                if (story)
                {
                    // GIMLI-2's transponder: points along the route, distance along it
                    if (dir.TransponderOn && dir.GuideTarget(eye, out var gw, out var glabel)) Add("gimli2", gw, glabel, new Color(1f, 0.6f, 0.3f));
                }
                else if (game.ScoutWorld.HasValue) Add("gimli2", game.ScoutWorld.Value, "GIMLI-2", new Color(1f, 0.6f, 0.3f));
                var field = game.Cave.Field;
                var landing = WorldOrigin.FrameToWorld(new Unity.Mathematics.double3(field.landingSpot.x, field.pitFloor + 2f, field.landingSpot.y));
                Add("pit", landing, $"PIT / CRANE  {Vector3.Distance(landing, eye):0} m", new Color(0.8f, 1f, 0.6f));
                if (game.Winch != null)
                    foreach (var l in game.Winch.Rigs)
                    {
                        var lw = WorldOrigin.FrameToWorld(l.roller);
                        if ((lw - eye).sqrMagnitude < 250f * 250f) Add("line", lw, l.RopeHanging ? "SHAFT RIG - ROPE" : "SHAFT RIG", new Color(1f, 0.55f, 0.15f));
                    }
            }
            return list;
        }

        /// <summary>Steepest terrain slope 4-14 m ahead of the rover (surface only).</summary>
        float SlopeAhead(Buggy.BuggyController b) => SlopeAhead(game, b);

        public static float SlopeAhead(GameBootstrap game, Buggy.BuggyController b)
        {
            var f = WorldOrigin.WorldToFrame(b.transform.position);
            if (game.Height.DataHeight(f.x, f.z) - (float)f.y > 10f) return 0f;   // underground: n/a
            var up = -TerrainFrame.DownAt(b.transform.position);
            var fwd = Vector3.ProjectOnPlane(b.transform.forward, up).normalized;
            float worst = 0;
            for (float d = 4f; d <= 14f; d += 2f)
            {
                var p = f + new Unity.Mathematics.double3(fwd.x, 0, fwd.z) * d;
                float h0 = game.Height.Height(p.x, p.z, 1f), h1 = game.Height.Height(p.x + fwd.x * 2, p.z + fwd.z * 2, 1f);
                float hl = game.Height.Height(p.x - fwd.z * 1.5, p.z + fwd.x * 1.5, 1f), hr = game.Height.Height(p.x + fwd.z * 1.5, p.z - fwd.x * 1.5, 1f);
                worst = Mathf.Max(worst, Mathf.Atan(Mathf.Abs(h1 - h0) / 2f) * Mathf.Rad2Deg, Mathf.Atan(Mathf.Abs(hl - hr) / 3f) * Mathf.Rad2Deg);
            }
            return worst;
        }

        static Material lineMat;

        /// <summary>Inertial-navigation map: your track, explored tunnels (by level), pit, lander, scout.</summary>
        void DrawMap(float W, float H)
        {
            var rect = new Rect(W / 2 - 300, H / 2 - 260, 600, 520);
            Panel(rect);
            GUI.Label(new Rect(rect.x + 10, rect.y + 6, 400, 22), "NAV MAP (explored)   M to close", title);
            var nav = game.Nav;
            if (nav == null) return;
            var focus = WorldOrigin.WorldToFrame(game.Player.Focus.position);
            float scale = 600f / nav.MapRange;
            Vector2 P(double x, double z) => new Vector2(rect.center.x + (float)(x - focus.x) * scale, rect.center.y - (float)(z - focus.z) * scale);
            if (Event.current.type == EventType.Repaint)
            {
                if (lineMat == null) { lineMat = new Material(Shader.Find("Hidden/Internal-Colored")); lineMat.SetInt("_ZTest", 0); }
                lineMat.SetPass(0);
                GL.PushMatrix(); GL.LoadPixelMatrix();
                GL.Begin(GL.LINES);
                Color[] levelCol = { new(0.5f, 0.9f, 1f), new(0.5f, 1f, 0.6f), new(1f, 0.9f, 0.4f), new(1f, 0.6f, 0.3f), new(1f, 0.4f, 0.6f), new(0.8f, 0.5f, 1f) };
                foreach (var e in nav.KnownEdges)
                {
                    GL.Color(levelCol[Mathf.Clamp(e.level, 0, levelCol.Length - 1)]);
                    var a = P(e.a.x, e.a.z); var bb = P(e.b.x, e.b.z);
                    if (!rect.Contains(a) && !rect.Contains(bb)) continue;
                    GL.Vertex3(a.x, H - a.y, 0); GL.Vertex3(bb.x, H - bb.y, 0);
                }
                GL.Color(new Color(1, 1, 1, 0.9f));
                for (int i = 1; i < nav.Track.Count; i++)
                {
                    var a = P(nav.Track[i - 1].x, nav.Track[i - 1].z); var bb = P(nav.Track[i].x, nav.Track[i].z);
                    GL.Vertex3(a.x, H - a.y, 0); GL.Vertex3(bb.x, H - bb.y, 0);
                }
                GL.End();
                GL.PopMatrix();
            }
            void Dot(double x, double z, Color c, string label)
            {
                var p = P(x, z);
                if (!rect.Contains(p)) return;
                marker.normal.textColor = c;
                GUI.Label(new Rect(p.x - 50, p.y - 10, 100, 20), label, marker);
            }
            Dot(focus.x, focus.z, Color.white, "+ YOU");
            Dot(0, 0, new Color(1f, 0.8f, 0.6f), "o PIT");
            if (game.LanderFrame.HasValue) Dot(game.LanderFrame.Value.x, game.LanderFrame.Value.z, new Color(0.7f, 0.9f, 1f), "LANDER");
            if (game.ScoutFrame.HasValue) Dot(game.ScoutFrame.Value.x, game.ScoutFrame.Value.z, new Color(1f, 0.6f, 0.3f), "SCOUT");
        }

        void Marker(Camera cam, Vector3 dir, string name, Color col)
        {
            var p = cam.WorldToScreenPoint(cam.transform.position + dir * 1000f);
            float W = Screen.width, H = Screen.height;
            marker.normal.textColor = col;
            if (p.z > 0 && p.x > 0 && p.x < W && p.y > 0 && p.y < H)
            {
                float y = H - p.y;
                GUI.Label(new Rect(p.x - 140, y - 34, 280, 20), name, marker);
                GUI.Label(new Rect(p.x - 40, y - 22, 80, 14), "v", marker);
            }
            else
            {
                // arrow at the screen edge towards the object
                var local = cam.transform.InverseTransformDirection(dir);
                var d2 = new Vector2(local.x, local.y);
                if (d2.sqrMagnitude < 1e-6f) d2 = Vector2.up;
                d2.Normalize();
                float hw = name.Length > 10 ? 120f : 45f;
                float ex = W / 2 + d2.x * (W / 2 - 25 - hw), ey = H / 2 - d2.y * (H / 2 - 50);
                string arrow = Mathf.Abs(d2.x) > Mathf.Abs(d2.y) ? (d2.x > 0 ? ">" : "<") : (d2.y > 0 ? "^" : "v");
                Panel(new Rect(ex - hw, ey - 12, 2 * hw, 24));
                GUI.Label(new Rect(ex - hw, ey - 12, 2 * hw, 24), d2.x < 0 && Mathf.Abs(d2.x) > Mathf.Abs(d2.y) ? $"{arrow} {name}" : $"{name} {arrow}", marker);
            }
        }
    }
}
