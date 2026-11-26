# Moon Base Alpha

An open-source, science-first exploration game set at the **Marius Hills Pit** on the Moon, the target of
NASA's **GIMLI** project (PSI / Honeybee Robotics, selected October 2026), which will investigate whether the
pit opens into a lava tube.

It is ~2035. A small crew has landed near the pit. A robotic scout picked up something deep in the tubes; a
quick crewed mission lowers a rover into the pit with a crane and drives into an endless, layered lava-tube
network to find it. Everything is built to be physically and scientifically plausible; where the game goes
beyond what is known, it says so (see *Real vs speculative*).

Unity 6.6 (6000.6.10f1), URP 17, C# (Burst for terrain / cave generation). MIT licence.

## Quick start

1. Install Unity 6000.6.10f1 (Unity Hub) and Python 3 with `numpy scipy pillow`.
2. Bake the real lunar terrain (downloads ~0.4 GB of NASA LRO data the first time):
   `python Tools/terrain/build_terrain.py`
3. Open the project in Unity Hub, open `Assets/_Project/Scenes/Main.unity`, press Play.

## The mission: Artemis-Marius // the GIMLI-2 expedition

A 5-20 minute story for the science-centre kiosk. The **start screen** shows the briefing, a 4-character
**mission seed** (A-Z, 0-9; Randomize; share it to replay one cave layout) and the **landing zone**:
PINPOINT (Cadet, 0.5 km out, GIMLI-2 on tube level 2), STANDARD (Explorer, 1.5 km, level 3) or SAFE ZONE
(Commander, 3 km, level 5). Drive to the crane past GIMLI-1 on the rim, ride down, press **G** to open the gates,
follow GIMLI-2's relay beacons and the HUD marker through the tubes, then **hold F** at the rubble seal to drill
through it. The crew and Houston talk on the radio (light-time delayed; recordings in
`Assets/StreamingAssets/Dialog/`, script in `Docs/dialog-recording-script.md`). A debrief screen ends the run.

## Controls

| Key | Action |
|---|---|
| W / Up, S / Down | drive / brake then reverse (on foot: walk) |
| A D / Left Right | steer (on foot: strafe) |
| Space | hard brake (on foot: jump) |
| Shift (on foot) | lope (bounding gait) |
| E | get out of / into the rover |
| H | headlights + driver's helmet lamps + roll-bar lamp (crew lamps are always on) / on foot: helmet lamps |
| R | right the rover |
| L / U / K | crane: lower to the pit floor / raise to the rim (rover on the platform) / skip the ride |
| G | crane at the bottom: open the end gates (they stay shut until you do) |
| F (hold 4 s) | at GIMLI-2's seal: core-drill through the rubble |
| C | first / third person (third person: wheel = camera distance) |
| V | cameras: first person -> rover mast -> base -> crane (only while it moves) -> first person |
| Mouse wheel / d-pad | visor zoom 1x / 2x / 4x / 8x (first person) |
| \ , [ ] , Shift+\ | tuning: next setting, down / up, reset it. Play-test knobs first: rover top speed, walk speed, crane speed, crane sway, wheel grip, motor torque, rover winch speed, visor lift; then lamps, exposure EV, visor fill. The HUD shows the absolute value; report the values you like (a later round bakes them in) |
| J | winch / anchor kit at a collapse shaft: rig + lower (rear to the edge), detach (line stays), attach at a line's foot (winch up), recover the kit at the anchor |
| M / Shift+M | 3D tube map (drag: rotate, wheel: zoom; deposits, your track, fixed lines) / flat navigation map |
| F3 / F4 / F6 | performance readout (fps, ms, 3D render res) / quality preset / frame cap (vSync) |
| 1 2 3 4 | teleport: mission start (landing zone), crane, tube mouth, GIMLI-2 |
| 5 / Shift+5 | next / previous special site: collapse shafts (top and below), lava-fall steps, ramps, large chambers, GIMLI-2, GIMLI-1 |
| Backspace / F5 | restart |
| T | mission time-scale |
| F1 / F2 | help (incl. the mission seed code) / engineering data |
| Esc | pause menu: Resume / Restart (same mission) / New mission (start screen) / Quit, dialogue volume + mute (the mouse is free while it is open) |

## What's simulated

- **Terrain:** real LRO elevation data (SLDEM2015 + LROC NAC 4 m DTM) on a curved 1737 km Moon, with
  sub-resolution craters (power-law sizes) and boulders.
- **Sky:** Sun, Earth (phase, rotation, libration) and eclipses from an ephemeris (Meeus + IAU lunar
  orientation); mission start 2030-06-14 22:00 UTC (lunar morning) - the in-game sky matches the real one.
- **Light:** physical units (128,000 lux sunlight, candela lamps), lunar photometry (lunar-Lambert + opposition
  surge), earthshine, ground bounce, one-bounce lamp light in tubes; one "smart visor" auto-exposure with
  visor glare around the Sun and a local dimming patch.
- **Rover (MR-4):** LRV-derived 4-seat rover; unsprung wheel masses, tuned dampers, Bekker / Janosi wheel-soil
  model with Apollo soil parameters, hub motors, battery; regolith / talus / basalt ground types.
- **Astronaut:** 205 kg suited, friction-limited 1/6 g walking, lope, jump, helmet lamps.
- **Crane:** LSMS-type slewing crane; platform on a rope joint (real pendulum dynamics), jerk-limited winch,
  input-shaped slew (no residual swing), anti-sway damping, hinged end gates, wheel tie-downs, safety interlock;
  contact forces act on moving ground (Newton's third law). Radio with the real Earth-Moon delay.
- **Tunnels:** endless, seeded, layered network streamed in chunks (signed distance fields + surface nets), plus a
  coarse render-only ring to twice the stream radius so the tube recedes into darkness. No light = black.
- **Shock sensor:** peak deceleration over 0.1 s; > 3 g is a hard landing (HUD warning, Mission Control asks
  about the suspension after the radio round trip). A fall into a shaft leaves the rover "DAMAGED" (top speed capped).
- **Round 8:** soft one-bounce lamp light and sunlit pit-wall bounce, smart-visor low-light assist, drivable tube
  junctions (talus ramps at crossings, gentle lava falls), rover winch + 10 anchor kits with fixed lines, live cockpit
  screens, 3D tube map with rock deposits, 1080p render + FSR upscaling, uncapped frame rate.
- **Round 9 (the mission):** start screen, seeds and landing zones; GIMLI-1 lander + rover on the rim; the GIMLI-2
  cargo lander's tracks; the player's own tyre tracks (regolith and talus, not basalt); a guaranteed drivable route
  to GIMLI-2 with relay beacons; the rubble seal, drill, vapour burst, spectrometer and the sealed chamber; dialogue
  by ID with radio band-pass and Quindar tones.
  Details, numbers and sources: **`Docs/science-report.md`**.

## Real vs speculative

| Real / from data | Modelled from published estimates | Speculative (game) |
|---|---|---|
| | Tube layout: one tube system per lava flow (main tube, braids, branches, spurs), stacked flows joined by collapses (Kazumura, Undara; round 11) | The tube network itself (GIMLI's job to find out); the 100 m giant tube |
| Terrain around the pit (LRO) | Pit interior (LROC Pits Atlas: funnel 79-92 m, shaft 49-55 m, 40 m deep) | What GIMLI-2 found (the sealed chamber) |
| Sun / Earth / eclipse geometry | Tube sizes: main tubes 40-120 m (Kaku 2017, Carrer 2024, Blair 2017) | Levels below ~150-200 m depth |
| Lunar gravity, vacuum, soil mechanics (Apollo) | Tube grade ~1% (measured from the rille above in LRO data) | Zn/S volatile crusts in deep levels |
| GIMLI target and instruments (NASA selected GIMLI in Sep 2026 to probe the pit for a lava tube) | Arched roofs, flat solidified floors, ledges, breakdown, levels (terrestrial analogues) | The endless extent |
| | Materials: lava glaze, ilmenite / iron glints, orange & green volcanic glass | |
| Gold-coated sun visor (Apollo, xEMU / AxEMU) | Earth-Moon radio round trip ~2.6 s (light time) | Smart visor: auto-dimming exposure, local dimming patch over the Sun, 1-8x zoom |
| Cameras: rover mast camera (Apollo GCTA on the LRV; Artemis rover concepts), crane hook camera and site camera mast (standard on crane jobs) | | Hard-landing threshold (3 g) |
| Lamp + sunlit-wall bounce (one-bounce radiosity, albedo 0.09-0.12) | Tube junctions with talus ramps, worn lava falls (Kazumura, Undara, Corona tube) | Smart visor low-light assist |
| Rover traction limits (measured: 27.5 / 30 / 32.5 deg on regolith / talus / basalt) | Shaft rigs: bolted span cable + trolley, rope ascender (highlines, rope access; round 12) | Rigs, ropes and kits on a rover; the trolley's messenger line (round 13); surviving a fall into a shaft (game mercy) |
| LRV: hub motors, no gearbox, ~13 km/h (record ~18) | SLOW / FAST drive ranges = a speed limit + torque curve; lidar edge-stop (Mars AutoNav-like) | FAST (~37 km/h); wheel grip x2 (game tuning); the Cadet crane time-lapse |
| Breakdown: roofs fail in slabs; angular rubble rests at ~35 deg | The rubble seal as a breakdown pile (round 12) | |
| xEMU: ~8 h EVA, 29.6 kPa suit; O2 ~0.84 kg/day at rest | Suit O2 / battery / CO2 / heart rate from the metabolic rate (helmet HUD, round 12) | |
| | Lava glaze, ilmenite glints, talus near openings | Glass beads inside tubes (pyroclastic: fall-in only, fading with depth) |
| GIMLI's payload: radar, seismometers, gravimeter, cameras on a CLPS lander + rover | GIMLI-1 lander and rover look (no design published) | GIMLI having flown and found the tubes; GIMLI-2 |
| Engine blast sandblasts nearby hardware (Apollo 12 / Surveyor 3) - why the cargo lander is ~370 m out | Tyre tracks ~20% darker, chevron tread (LRO: disturbed regolith is darker) | Relay-beacon blink, the golden route |
| Tracks last millions of years (no wind or rain) | Radar seeing a hollow behind rubble (GPR finds voids) | A sealed, gas-tight chamber for billions of years |
| Apollo 14 sample 14321: a ~4-billion-year-old fragment of Earth rock on the Moon | Ice survives at -20 C in a sealed space (vapour balance) | A whole granite boulder from Earth inside it |
| The young Earth's air had no free oxygen; gas venting into vacuum freezes to ice crystals | | The gas mix H2O / CO2 / N2 / Ar (lunar volcanic gas was mostly CO, H2O, S) |

Temperatures: the shaded floor of a pit stays near +17 C, warmed by its sunlit walls (Horvath et al. 2022). Deep in
the tubes the rock settles to the Moon's mean subsurface temperature, about -20 C (Apollo 15 / 17 heat-flow probes).
No lasting water ice in open tubes: even at -20 C, ice sublimates into vacuum over geologic time. It needs cold
traps below ~-160 C, or a sealed space (the speculative chamber of the mission).
Full list with numbers, simplifications and sources: **`Docs/science-report.md`** (kept in sync with this table).

## Project layout

```
Assets/_Project/Scripts/Runtime   Core, Terrain, Cave (pit + tunnel network), Buggy, Crew, Base (landers, crane),
                                  Sky (ephemeris), Rendering (exposure, lights, procedural meshes), UI
Assets/_Project/Scripts/Editor    SceneBuilder, Verification (headless test + screenshot suite)
Assets/_Project/Shaders           LunarRegolith, CaveRock, SkyEarth/Sun/SunGlare/Stars, Dust, UnderRock
Tools/terrain, Tools/sky          data bake script, ephemeris reference implementation
Docs/                             science report, controls, dialog script (test output goes to Docs/verification, not in the repo)
```

## Verification

`Tools > Moon Base Alpha > Run Verification`, or headless:
`Unity -batchmode -projectPath . -executeMethod MoonBaseAlpha.Editor.Verification.Run`
Deterministic physics tests (free fall, suspension, braking, slopes, astronaut jump/walk, crane cycle, tunnel
drive), ephemeris checks, tunnel-network science checks (seed reproducibility, widths, grade, slopes, junction
ramps, reachability), round-8 checks (pit bounce, visor assist, winch + anchor kits, cockpit screens, tube map) and
screenshots into `Docs/verification/` (`-sections a,b` runs only matching sections).

Performance: `MoonBaseAlpha.exe -perfbench -logFile perf.log` measures the start site and a tube with costs switched
on/off one at a time (render resolution, old point-light bounce, far tube ring, crew lamps), logs a `[PerfBench]`
table and quits.

## World seed

The tunnel network and small-scale surface detail come from one seed (default 20300614). The start screen and the
F1 panel use a 4-character mission code (A-Z, 0-9; ~1.7 million layouts) that maps to the seed one-to-one; F1 also
takes a raw number, as do `-seed N` on the command line and `settings.json` in the app's persistent data folder.
The LRO terrain itself never changes.

## Credits & licences

- Code: MIT (see `LICENSE`).
- Lunar data: NASA LRO LOLA / SLDEM2015, LROC NAC DTM (public domain; downloaded at build time).
- Earth textures: Solar System Scope, CC BY 4.0 (`Assets/_Project/Textures/Earth/CREDITS.txt`).
- Science references: LROC Pits Atlas; Kaku et al. 2017 (GRL); Chappaz et al. 2017; Carrer et al. 2024
  (Nature Astronomy); Blair et al. 2017 (Icarus); Oberbeck et al. 1969; Horvath et al. 2022; Apollo LRV and
  soil-mechanics reports; NASA LSMS (Langley).
