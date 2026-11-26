# itch.io page - Moon Base Alpha

Live (public since 2026-11-26): https://freegamesforfun.itch.io/mission-gimli

Paste-ready text and settings for the itch.io project page. Upload: `Builds/Release/MoonBaseAlpha-windows-x64-r13.zip`.

## Settings (Edit game)
- **Title:** Moon Base Alpha
- **Project URL:** moon-base-alpha
- **Short description / tagline:** Drive a lunar rover into the real Marius Hills Pit and search the lava tubes below for a lost robot.
- **Classification:** Games. **Kind of project:** Downloadable.
- **Release status:** In development (early release).
- **Pricing:** No payments / or "$0 or donate" (your call).
- **Uploads:** the zip, tick **Windows**. (Or push with butler, below.)
- **Genre:** Simulation. **Tags:** space, moon, exploration, driving, rover, science, educational, sci-fi, first-person, procedural-generation
- **App store links / source:** link the GitHub repo once it's public (MIT).
- **Community:** Comments on (useful for early feedback).
- **Visibility:** Draft first, check the page, then Public.

## Description

**It is ~2035. A small crew has landed beside the Marius Hills Pit on the Moon.**

GIMLI-2, a robotic scout, went down into the lava tubes beneath the pit, and its signal has gone quiet deep in
the network. Your job is to drive the pressurised rover to the crane on the rim, ride it 40 m down into the pit,
and follow GIMLI-2's relay beacons through a layered maze of lava tubes until you find it and see what it found.

### Real science, honestly labelled
- **Real terrain:** the surface is built from NASA Lunar Reconnaissance Orbiter elevation data (SLDEM2015 + LROC
  4 m DTM) on a correctly curved Moon.
- **Real pit:** sized from the LROC Pits Atlas. The Marius Hills Pit is the target of NASA's GIMLI project.
- **Real physics:** 1/6 g, vacuum, Apollo-era soil mechanics, rover grip and wheelspin on steep regolith, and
  light-time delay on the radio to Houston.
- **Speculative, and it says so:** the tube network beyond the pit, and what's inside. The game keeps a
  "real vs speculative" list.

### Features
- Three difficulty tiers: **Cadet** (~10 min), **Explorer** and **Commander**, with longer routes and shaft
  descents on a winch rig.
- Hand-picked cave layouts (SET1-SET3), plus RANDOM.
- Helmet HUD with crew life support (O2, suit pressure, CO2, heart rate), a 3D tube map and a step-by-step guide.
- Plays on a wired Xbox controller or keyboard + mouse.

### Download and play
1. Click **Download** and save `MoonBaseAlpha-windows-x64-r13.zip`.
2. Right-click the zip, choose **Extract All**, and extract it anywhere. Don't run it from inside the zip.
3. Open the `MoonBaseAlpha` folder and double-click **MoonBaseAlpha.exe**.
4. If Windows SmartScreen says "Windows protected your PC", click **More info**, then **Run anyway**. The game
   isn't code-signed yet.
5. On the start screen, pick **SET1** and **PINPOINT (Cadet)**, then press **LAUNCH** (Enter, or A on a pad).
6. Follow the **CURRENT STEP** line on the HUD. F1 shows the controls; Esc pauses.

Using the itch.io app? Click **Install**, then **Launch**.

### System requirements
Windows 10 / 11, 64-bit, DirectX 11 GPU. Tuned for Radeon 780M-class integrated graphics at 1080p (Medium).

### Early release notes
RANDOM layouts are untested and might strand you. There's no voice audio yet (radio lines show as text).
Feedback is very welcome in the comments.

*Independent fan work, not affiliated with NASA, PSI or Honeybee Robotics. Lunar data: NASA LRO (public domain).
Earth textures: Solar System Scope (CC BY 4.0). Code: MIT.*

## Images (made 2026-11-26, in `Builds/Release/itch/`)
Cover `cover_630x500.png`; screenshots 1-7 (1920 x 1080, normal HUD, SET1 Cadet): start screen, landing site,
crane at the pit rim, lava tube, shaft edge, tube map, GIMLI-2's rubble seal.

## Assets to make (originally listed; now in `Builds/Release/itch/`)
- **Cover image:** 630 x 500 (shown at 315 x 250). A rover-at-the-pit-rim shot works.
- **Screenshots:** 3-5 at 1920 x 1080: the surface + Earth, the crane over the pit, a tube with headlights,
  the HUD, the 3D tube map. Capture in the exe, or with `MoonBaseAlpha.exe -shots <dir>` (`Core/PlayerShots.cs`).

## Upload with butler (optional, for updates)
1. Install butler: https://itch.io/docs/butler/installing.html (or `scoop install butler` if it's in a bucket).
2. `butler login` (opens the browser once).
3. `butler push Builds/Release/MoonBaseAlpha <itch-user>/moon-base-alpha:windows --userversion 0.13`
   butler pushes only changed files, so later rounds upload fast. Push the folder, not the zip.
