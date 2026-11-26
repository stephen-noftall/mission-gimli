# Controls

Round 12 (P3). The exhibit target is a cheap **wired USB Xbox (XInput) controller**: a keyboard gets destroyed. The
visitor set below works on the pad and on the keyboard + mouse. Debug, tuning and operator keys stay keyboard-only.
The code is in `Assets/_Project/Scripts/Runtime/Core/Controls.cs`. Unity reads the pad through the old Input Manager:
`Horizontal` / `Vertical` (left stick), `RightStickX/Y`, `LeftTrigger`, `RightTrigger`, `DPadHorizontal`,
`DPadVertical`, and joystick buttons 0-8. Every on-screen prompt takes its key names from one table
(`Controls.Name`), so it shows `[F]` on the keyboard and `(A)` once the pad is used.

## Visitor set (pad + keyboard)

| Action | Xbox pad | Keyboard / mouse | Notes |
|---|---|---|---|
| Drive / walk | Left stick | W A S D / arrows | Double-tap forward = FAST, double-tap back = SLOW (0.35 s window) |
| Look | Right stick | Mouse (click to capture) | Driving: head look. 3rd person: orbit |
| **USE** (context) | **A** | **F** | Crane: lower / open the gates / raise. Shaft rig: ride down, clip on the rope, install a rig. Seal: hold to drill. Start screen: LAUNCH |
| Brake (on foot: jump) | B | Space | |
| Lights (rover / helmet) | X | H | |
| Camera view (1st / 3rd person) | Y | C | V cycles the extra cameras (keyboard) |
| Get out / in | LB | E | |
| 3D tube map | RB | M | Map: right stick rotates, triggers zoom (mouse: drag + wheel) |
| Recover the rover (stuck / flipped) | Left stick click | R | |
| Visor zoom | D-pad up / down | Mouse wheel | |
| Menu (pause) | Start | Esc | Pause menu on the pad: A resume, X restart, Y new mission, Back exit |
| Exit (asks first) | Back (in menus) | Esc on the start screen / EXIT buttons | Confirm: A / Enter exit, B / Esc cancel |

**Start screen on the pad:** LB / RB = cave layout (SET1, SET2, SET3, RANDOM), D-pad left / right = landing zone,
A = LAUNCH, Back = exit. **Debrief:** A = play again, Y = new mission, Back = exit.

Merged into USE (round 12): the old F (drill), G (gates), J (winch / rig), L (crane lower) and U (crane raise). The
old keys still work on the keyboard, for the operator.

## Operator / debug (keyboard only)

| Key | What |
|---|---|
| F1 | Controls help (the start-up help is replaced by the command display in normal mode) |
| F2 | Engineering page (rover physics) |
| F3 | Diagnostic mode: every panel and marker, perf numbers |
| F4 | Quality preset (Low / Medium / High) |
| F5, Backspace, 1 | Restart at the landing zone |
| 2 / 3 / 4 | Teleport: crane pad / tube mouth / GIMLI-2 |
| 5 / Shift+5 | Next / previous special site in the tubes |
| F6 | Frame cap on / off |
| K | Skip the crane ride |
| L / U / G / J | Crane lower / raise / gates, winch (also on USE) |
| T | Mission time scale (sky) |
| Y | Lunar-day preset (sky) |
| Tab | 4-wheel / 2-wheel steering |
| Shift (on foot) | Lope |
| Shift+M | 2D overview map (debug) |
| \ , [ , ] , Shift+\ | Tuning: next knob, lower, raise, reset |
| Enter | Start screen: LAUNCH; exit dialog: confirm |
