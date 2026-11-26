# Science report

What the game models, how, and how sure we are. Each entry is **real** (from data or established physics),
**modelled** (built from published estimates or terrestrial analogues, with simplifications), or **SPECULATIVE**
(a game choice or an untested idea). Started in round 8 (2026-11-23); the README's *Real vs speculative* table is the
short version of this file and must stay in sync with it.

## Light

### Lamp bounce in the tubes (round 8): modelled
- **What:** light from the rover / suit lamps that hits rock is partly reflected and lights the walls around the
  beam. This is why a real lava tube is not pitch black just outside a lamp spot.
- **How:** each lamp beam is sampled with 5 rays. Each hit reflects I = albedo x I_lamp x omega_ray x cos(i) / pi
  (a Lambertian patch). Fresh basalt / regolith albedo is 0.09. All hits of one lamp group (rover or walker) are
  gathered into **one** soft source in the air between the lamps and the lit rock. It is smoothed over 0.3 s and
  evaluated in the rock shaders as E = I / (d^2 + r_s^2), with a softening radius r_s >= 3 m and diffuse light only.
- **Simplifications:** one bounce; no occlusion of the bounced light; only rock receives it (the rover and suits
  keep the visor fill). The round-7 version placed up to ~30 point lights at the ray hits. They jumped as the
  helmet lamps bobbed, which caused the "pulsing lights in the walls" seen in play.

### Pit bounce (round 8): modelled
- **What:** the sunlit upper part of the pit wall (and the floor, when the Sun is high enough) relights the
  shadowed walls.
- **How:** a one-bounce radiosity estimate for a cylindrical shaft (R ~26 m, D ~40 m), redone every 0.5 s:
  - lit depth on the far wall: d = min(D, 2R tan(elevation))
  - lit wall area: ~pi R d
  - wall irradiance: E_sun cos(elevation) x 2/pi
  - reflected flux: Phi = albedo (0.12) x (wall + lit floor)
  - irradiance on shadowed surfaces: Phi x (the share that stays inside the pit) / (the interior area)
- **Result:** shadowed walls are about **1/44** of a Sun-facing surface's brightness at 60 deg Sun elevation and
  **1/640** at 3 deg (a sliver lit). The brief asked for ~1/30 and ~1/1000.
- **Rendering:** up to 2 soft, shadowless, diffuse sources, masked to the shaft ellipse below the rim (overhang ceilings get none). No light
  reaches the tubes (tested 80 m in: 0 lux).
- **Simplification (the user's call):** up-facing floors keep their round-7 look. The existing "ground from below"
  ambient light and the visor fill stand in for the floor's bounce.

### Smart visor "look into the dark" (round 8)
- **Real:** the eye adapts locally. Looking into a dark doorway from bright sunlight, you start to see detail after
  a second or two.
- **SPECULATIVE:** a visor doing this electronically.
- **How:** when the centre of the view (12.5 deg cone) is more than ~4 stops darker than the rest of the frame,
  a soft-edged gain ramps in over ~1.4 s. It aims for 2.5 stops under the surround, up to +5 EV x the
  "visor lift" knob. It multiplies the HDR image before tone mapping, so it only amplifies light that is there:
  black stays black. A highlight knee keeps bright pixels inside the patch from blowing out.
- **Test:** from the rim, looking at the shadowed foot of the far wall: 4.8 stops darker than the surround,
  lift +2.3 EV.

## GIMLI (mission background): real
- **Selection:** NASA selected GIMLI (Geophysical Instruments for Marius Lunar pit Investigation) on 30 September
  2026 under PRISM. PSI leads it (PI Nathaniel Putzig), with Honeybee Robotics and the Norwegian Space Agency.
- **Payload:** radar, seismic sensors, a gravimeter and cameras, delivered by a CLPS lander and rover.
- **Goal:** whether the Marius Hills Pit leads into a large lava tube, a possible natural shelter from temperature
  extremes and radiation for future astronauts.
- **Caution:** the PSI team stresses that the pit is not confirmed as a tube opening.
- **In the game:** the mission story imagines it has flown (SPECULATIVE).
- **Sources:**
  - NASA release 26-080, "NASA adds new science investigations for Moon Base":
    https://nasa.gov/news-release/nasa-adds-new-science-investigations-for-moon-base
  - Universe Today: https://universetoday.com/articles/nasa-selects-gimli-to-search-the-marius-lunar-pit-for-a-potential-lunar-base-site

## Temperatures (round 8 review): real
- **Pit floor in shade:** about +17 C. Diviner-based modelling of the Mare Tranquillitatis pit (Horvath et al.
  2022): the overhang blocks the cold sky and the sunlit walls and floor keep the shaded part warm.
- **Deep tubes:** the mean subsurface temperature. Apollo 15 / 17 heat-flow probes measured ~250-256 K
  (-23 .. -17 C) at ~1 m, steady day and night. Lunar heat flow (~20 mW/m^2) adds only ~1-2 C per 100 m of rock.
  So the tubes are about -20 C, grading towards the pit's +17 C near the mouth. Used in the mission dialogue;
  there is no thermal model yet (Queued).

## Rover

### Traction and climbing (round 8 measurement): modelled
- **Model:** Bekker pressure-sinkage plus Janosi-Hanamoto shear. Apollo soil parameters for regolith, a firmer
  "talus", and bare basalt (wire mesh with titanium chevrons on rock, mu 0.75).
- **Measured max grade from rest** (full throttle, 20 s): regolith **27.5 deg**, talus **30 deg**, basalt
  **32.5 deg**. In every case wheelspin stops it (slip ~50%), not motor torque. The Apollo LRV was rated for
  ~25 deg.
- **The round-7 "can't climb the side-tube bank":** the bank was steeper than any soil allows. That is a geometry
  problem, fixed by the tube junctions below, not a grip problem. "Wheel grip" and "motor torque" are on the
  \ tuning list for play-testing.

### Wheel grip x2 (round 12): game tuning
- **Measured (grip x1, real soil values):** max grade from rest 27.5 / 30 / 32.5 deg on regolith / talus / basalt.
  At the game default (grip x2) 35 / 35 / 40 deg. In every case the limit is wheelspin (grip), not motor torque, so
  grip is the right knob.
- **Game tuning, not physics:** x2 makes the wire-mesh-on-basalt friction mu 1.5. Real values are lower (rubber on
  dry rock reaches ~0.8-1.0; Apollo-era wheel tests on lunar simulant give ~0.6-0.8 effective). It lets a visitor
  climb out of the bowl past GIMLI-1 (tested: out with a run-up at x2) instead of needing a recovery.

### Drive ranges SLOW / FAST (round 12)
- **Real:** a lunar rover has no gearbox. The LRV had a hub motor and harmonic drive in each wheel; speed was set by
  the hand controller. Its rated top speed was ~13 km/h; the record was ~18 km/h (Apollo 17, downhill).
- **Modelled:** the two ranges are a speed limit plus a torque curve. SLOW is the round-11 top speed (~19 km/h) with
  full torque; FAST is 2x the speed (~37 km/h) with half the torque, and a smaller lateral-acceleration budget
  (steering shrinks with speed: rollover margin at 1/6 g).
- **Physics:** braking distance v^2 / (2 mu g). From 37 km/h on basalt with the game grip it is ~5-10 m with a hard
  stop; the edge assist plans with a gentle 0.6 m/s^2 (~100 m of look-ahead at 37 km/h).
- **SPECULATIVE:** FAST, beyond any lunar rover so far.

### Edge assist: auto-slow + auto-stop (round 12)
- **Real basis:** Mars rovers' AutoNav stops at slopes and drop-offs seen by the hazard cameras. The Apollo LRV had
  nothing like it; the crew drove by eye.
- **Modelled, plausible:** a lidar edge-stop on a crewed rover. Within the braking distance of a pit rim or collapse
  shaft ahead, the rover drops to SLOW and slows on a braking curve; at 15 m it stops, facing the edge. The driver
  can still creep on at walking pace (to rig a winch, or on purpose). Talus ramps are not edges. A lane beside a
  hole (>= 12 m) does not trigger it.

### Shaft rigs: span cable, trolley, rope ascender (round 12)
- **Real:** rock bolts in basalt (mining, rope access); tyrolean traverses and highlines (a tensioned cable between two
  anchors with a running trolley); powered rope ascenders (e.g. Atlas, ActSafe) that climb a fixed rope with a load.
- **Modelled:** two bolted wall anchors high across the tube, a span cable with a motorised trolley over the hole, and
  a lift cage on the rover (4 poles meeting above the centre = one hook point) with its own winch. The rover lifts
  clear of the lip (>= 1 m), the trolley carries it over the hole, the winch lowers it, and the rope is left hanging as
  the way back. Winch 1.125 m/s (jerk-limited, 0.18 m/s^2); trolley moves at <= 0.45 m/s^2.
- **Tested on every rig of SET1-3:** down and up with no hull contact on the lip, walls or roof; 2-3 min per ride for
  67-128 m drops.
- **Game:** installing a rig kit is skipped (fade, "+40 min").
- **Round 13:** the advance team's rigs carry no rope: the rope stays on the rover until it is used, then it is left
  hanging as the way back. Only where GIMLI-2 went down on its way to the seal does its rope still hang from the
  trolley; the crew go out, pull the trolley in and clip it on (fade, "+10 min"). Explorer's route goes down 1 shaft,
  Commander's 2.
- **Messenger line (round 13):** stuck at the bottom of a rig with no rope (after a fall), the trolley lowers a light
  line and hauls one of the rover's ropes up (fade, "+15 min"). Real practice: cavers and riggers pull a heavy rope
  up a pitch with a light "messenger" or pull cord. A motorised trolley that does this remotely is a game assumption.
- **SPECULATIVE:** an advance team's rigs, ropes and kits carried by a rover; the trolley's remote messenger line.

### Anchor kits and rover winch (round 8, replaced by the shaft rigs in round 12)
- **Modelled:** rock bolts plus a cable, as in caving and rope access; a 4-point bridle keeps a lifted load level
  (standard lifting practice); jerk-limited winching (accel 0.12 m/s^2, jerk 0.25 m/s^3, 0.75 m/s).
- **The davit:** an edge-roller arm in each kit swings the rover out over the drop. Its swing is limited to
  ~0.45 m/s^2.
- **Test at the 75 m shaft:** the rover stays within 8 deg of upright and never touches the walls. Lift-off,
  lowering and the haul up stay under 0.3 g vertical.
- **SPECULATIVE:** a lunar rover carrying 10 of these kits.
- **SPECULATIVE (game mercy):** falling into a shaft without a line is survivable. The roll cage and 4-point
  harnesses (the LRV had seat belts) hold, and the rover is guided upright onto a glancing slide down the talus.
  It ends up "ROVER DAMAGED" with its top speed capped at 45%.
  - **Reality:** a 75 m fall in 1/6 g ends at ~15.6 m/s (56 km/h). That is very likely not survivable for the crew.

## Suit life support on the helmet HUD (round 12): modelled
- **Real numbers:** xEMU / AxEMU suits are designed for ~8 h of EVA. Nominal suit pressure is 29.6 kPa (4.3 psi).
  A person at rest uses ~0.84 kg of O2 a day (~35 g/h, NASA BVAD); EVA work burns ~200-300 kcal/h (230-350 W). The
  swing-bed CO2 scrubber keeps the partial pressure low; NASA's short-term limit is ~1 kPa (7.6 mmHg).
- **Model:** each suit has 0.65 kg of usable O2. Use follows the metabolic rate: ~0.25 g O2 per kJ (20.2 kJ per
  litre of O2, 1.43 g per litre). Seated in the rover (180 W) that is ~45 g/h; walking (330 W) ~83 g/h, ~8 h of
  endurance. The suit battery lasts ~8 h (helmet lamps +12%). Pressure is regulated at 29.6 kPa. CO2 runs at
  0.1-0.35 kPa, and suit temperature at ~22 C (cooling garment), both rising with work. Heart rate is ~75 seated and
  ~105 walking.
- **Simplifications:** no refill (the MR-4 is an open rover) and no fail state; the mission is far shorter than the
  endurance, so O2 is a gauge. Warnings go to the log at 25% and 10%. Thermal loads and sensors are queued ("Suit
  sensors + thermal model").

## Tubes

### The lava-flow tube layout (round 11): modelled from Earth, the network itself SPECULATIVE
Replaces the round-8 "levels" (each level was a copy of the same lattice, 75 m apart: not how tubes form).
- **One tube system per lava flow (modelled, Earth analogues):** a flow makes one main tube that follows it downhill,
  with braided loops (parallel channels that split and rejoin), distributary branches and short dead-end spurs, more
  of them towards the flow front. Kazumura (Hawaii, ~65 km, one tube with braided and multi-level sections, average
  ~2% grade) and Undara (Australia, one long tube) are the models. The top flow's main tube runs along the rille and
  through the pit (the pit is a skylight in its roof).
- **Floors (modelled):** a tube floor sits at a near-constant depth below its own flow's surface and follows the
  regional slope (~1%, measured along the rille in LRO data); hot lava cut it down a few metres in places (thermal
  erosion). Lava falls are steps where it cut deeper; in the game they are capped at 4 m and worn into rubble slopes.
- **Stacked flows (modelled):** younger flows bury older ones (the layered flows in the Hadley Rille wall, Apollo 15).
  Each lower flow is its own system, 20-40 deg off the top flow's direction, 50-70 m deeper; a tube is never taller
  than its flow allows. Flows connect only where the rock between two crossing tubes collapsed: a talus ramp where
  less than 40 m of rock fell in (drivable), else a shaft (winch). No collapse within 300 m of the pit.
- **Sizes (modelled):** main tubes ~50-70 m wide by default (published Marius estimates range from tens of metres to
  over 100 m; Kaguya radar, Kaku et al. 2017); in 1/6 g tubes up to a kilometre or more could stand (Blair et al.
  2017; GRAIL hints at large voids, Chappaz et al. 2017). The deepest flow's main tube is a 100 m "giant tube":
  **SPECULATIVE**.
- **SPECULATIVE:** the network itself. Nobody knows how many tubes are under Marius or how they branch; finding out is
  GIMLI's job. The map shows that honestly: GIMLI-1's radar gives a coarse estimate within ~700 m, GIMLI-2's lidar
  maps its route precisely, your rover maps what it passes.
- **Game safety (not science):** fixed lines left by an "advance team" at shafts with no other way back up.
- **Sources:** Haruyama et al. 2009 (Kaguya, Marius Hills hole); Kaku et al. 2017, GRL (Kaguya radar void along the
  rille); Chappaz et al. 2017, GRL (GRAIL); Blair et al. 2017, Icarus (lunar tube stability); Carrer et al. 2024,
  Nature Astronomy (radar: a conduit at the Mare Tranquillitatis pit); Kazumura / Undara cave surveys.

### Junctions, lava falls, ramps (round 8): modelled (lattice layout, parked)
- **Terrestrial analogues:** Kazumura (Hawaii), Undara (Australia) and the Corona tube (Lanzarote) have:
  - branches and tributaries
  - multi-level sections
  - lava falls a few metres high
  - floors of a side tube sitting higher than the main tube's
- **Tubes that would have merged:** two tubes at about the same level whose footprints overlap (or that cross
  with less than 6 m of roof) would have merged while active, or later collapsed into each other. They become a
  junction with two rubble ramps of at most 12 deg, cut into the higher floor (talus from the collapsed roof).
- **Over/under crossings:** crossings with >= 6 m of rock in between are left as they are (minimum found: 9 m).
- **Lava falls:** each step is spread over ~7x its height, at most ~12 deg plus floor rubble (as if worn into a
  rubble slope). Breakdown piles are kept under ~15 deg.
- **Level ramps:** at most 18 deg. If the next row's node is too close, the ramp runs to a node further on.
- **Level ramps, round 9 fix (game geometry):** the levels sit almost exactly above each other, so a ramp running
  junction to junction lay in the same corridor as the tubes above and below. It became a holed trench in its own
  level's floor and ended in a ~10 m cliff into the lower junction: none of the 46 ramps near the pit was drivable
  end to end. Each ramp now runs in its own corridor beside the junction (away from the main tube, branches and
  collapse shafts), with flat connectors at both ends and 30 m of flat run-out before the slope. 45 of 46 are now
  drivable on the real floor. A tube crossing a ramp half-way down can still leave a step; the mission route
  checks every link it uses on the real floor and avoids those.
- **Measured on the real SDF floor (8 m steps):**
  - junction ramps: at most ~25 deg
  - lava falls: at most ~16 deg
  - plain tube floors (rubble only): ~11 deg
  - The rover climbs 30-32 deg.
- **No dead ends:** in a test block (levels 0-3, 10 rows x 11 columns), every tube and chamber can be reached by
  driving from the pit mouth, so each has a drivable way back up to level 0.
- **SPECULATIVE:** the endless extent and the levels below ~150-200 m (unchanged). Surface exits (jameo-style
  collapse trenches) are not built yet (Queued).

### Rock deposits: science review (round 8)
| Deposit | Status | Notes |
|---|---|---|
| Fresh mare basalt, slightly brown-green | real | Unweathered rock under the roof: no space weathering, so lighter than the regolith. |
| Lava glaze (glassy wall lining) | modelled | Common in terrestrial tubes (quenched / remelted lining); plausible on the Moon. |
| Ilmenite / native-iron glints | real | Mare basalts carry ilmenite (FeTiO3) and tiny Fe metal grains (reducing lunar magmas). Drawn as rare tiny facets near the viewer. |
| Talus and dust near openings | real | Regolith and breakdown that fell or slid in at the pit and collapses. |
| Orange / green glass beads on floors | **SPECULATIVE** inside tubes | Apollo 17 orange soil (74220) and Apollo 15 green glass are pyroclastic: fire-fountain beads that settle on the *surface*. In a tube they would be mostly fall-in through the pit, skylights and collapses. **Fixed in round 8:** cover now fades with depth to 30% by ~160 m below the level-0 floor. Their round-7 colour edges followed a 180 m grid ("orange square" bug); now the colour drifts smoothly and the patch edges are broken into a bead spray. |
| Pale Zn/S volatile crusts along cracks, deeper levels | **SPECULATIVE** | The orange beads carry Zn, S, Pb, Cl and F surface coatings condensed from volcanic gas. Sublimate crusts in a cooling tube are an extrapolation. |
| Water ice | not modelled | No lasting ice in open tubes: at the tubes' ~-20 C it sublimates into vacuum over geologic time (stable only below ~-160 C, or in a sealed space). Ice in deep cold traps is Queued; the mission's sealed, frost-lined chamber is **SPECULATIVE**. |

The 3D tube map (key M) colours tubes with the same noise (ported to C# in `Cave/RockDeposits.cs`). The
speculative deposits say "(speculative)" in its legend.

### The Cadet crane time-lapse (round 12): game timing
For the short Cadet run the crane ride plays as a x3 time-lapse (the same physics, three times faster). It is
deliberately unrealistic, so the run stays near 5 minutes. Explorer and Commander keep the real-time ride.

## Crane (round 8): modelled
- **Type and size:** NASA Langley's Lunar Surface Manipulation System (LSMS) is a real lightweight truss crane
  concept. Our 46 m boom (needed to reach over the pit's funnel to the floor) is a scaled-up, SPECULATIVE version:
  a few tonnes in 1/6 g.
- **Delivery:** the uncrewed cargo lander that brought it (in the story, the GIMLI-2 mission) sits ~370 m from the
  pit. That is deliberate: descent engines sandblast nearby hardware (Apollo 12 pitted Surveyor 3 from ~180 m).
- **Pendulum:** in 1/6 g a 4.5 m sling swings with a ~10 s period, and the 45 m cable with ~33 s. The cable
  pendulum's anti-sway damping scales with the "crane sway" knob from 100% down to 60% of round 7's. Lower failed
  the 1 m stability test. The deck's rocking (sling geometry + tag lines) is now damped twice as hard as in round 7:
  with a lazier pendulum the two modes couple, and the deck rocked up to ~10 deg in the slew.
- **Settle holds:** as an operator would, the crane waits for the load to settle (< 0.3 m off the vertical,
  at most 10 s) before it slews and before it lowers.
- **Slew:** still input-shaped, now with accel and decel ramps of *two* pendulum periods (any whole number cancels
  the residual swing; two halve the tangential acceleration that tipped the loaded deck). The load's offset from the vertical during the slew is centripetal (~ omega^2 r L / g), so the
  slew rate follows "crane speed" only up to x1.15. That keeps the load under the brief's 1 m.
- **Speed:** "crane speed" (default x1.5, at most x2.0) scales winch speed, accel and jerk, and the gate time.
  The winch accel is capped at 0.27 m/s^2 and the jerk at 0.6 m/s^3 (the rover's suspension amplifies them ~1.7x) and the last 15 cm of slack
  are taken up at a creep, so the rover stays
  under 0.3 g vertical.
- **Tests:** the shipped ride, and a ride at the speed limit with the least damping (both must stay under 1 m of
  swing and 0.3 g).
- **SPECULATIVE / game:** the crane starts moving the moment you press L (the gates close while the slack is
  taken up; lift-off waits for them).

## The GIMLI-2 expedition (round 9)

The 5-20 minute story mission. What in it is real, modelled or SPECULATIVE:

### GIMLI-1 on the rim: real payload, generic look, SPECULATIVE flight
- **Real:** NASA's GIMLI (see *GIMLI* above) is a radar, seismic, gravimeter and camera suite that will fly to the
  Marius Hills Pit on a commercial (CLPS) lander with a small rover.
- **Modelled:** a ~3.5 m four-legged lander with a solar panel and a dish, a car-sized rover with a
  ground-penetrating radar antenna, stereo cameras and a gravimeter, and a line of seismometer stakes. No design
  has been published, so the look is generic.
- **SPECULATIVE:** that it has already flown and found the tubes.

### The cargo lander and the tracks: real
- **Stand-off:** the 7 m cargo lander (in the story it brought the crane and GIMLI-2) sits ~370 m from the pit.
  Descent engines sandblast nearby hardware: Apollo 12 landed ~180 m from Surveyor 3 and its exhaust pitted the
  probe's surfaces. Landing at the rim would also risk the pit edge.
- **Size class:** real; Blue Origin's Blue Moon Mk1 carries ~3 t of cargo.
- **Tracks:** pre-made tracks run from the cargo lander to the crane pad (robots hauling crane sections) and
  along the rim (GIMLI-1's radar traverses); the player's rover leaves its own. With no wind or rain they last
  for millions of years; only micrometeorite "gardening" slowly erases them. LRO images still show the Apollo 15-17
  rover tracks after 50 years.
- **Modelled:** the imprint is drawn ~20% darker with a chevron tread. In LRO images, disturbed regolith (rover
  tracks, astronaut paths) is darker than the undisturbed surface: the bright, porous top layer is pressed flat.
  No tracks on bare tube basalt (nothing to imprint).

### The route, relay beacons, radar: modelled / game
- **Relay beacons:** radio does not pass through rock, so a robot exploring a cave must leave a chain of relays
  behind it. Real practice: the DARPA Subterranean Challenge robots dropped relay nodes. The beacons' slow blink is a
  game cue (emissive only, no light).
- **Golden route:** a game guarantee. The shortest drivable way through the generated network (level ramps,
  junction ramps; links past a collapse shaft avoided), ending in a dead-end spur.
- **Radar sees a hollow:** ground-penetrating radar detects voids (lunar rovers' radars have imaged layers tens
  to hundreds of metres down; terrestrial GPR finds caves and voids). Its radio waves are invisible: the sweeping
  light on the rubble is a stand-in.

### The rubble seal: a breakdown pile (round 12): modelled
- **Real:** lava-tube roofs fail in slabs along cooling layers (breakdown). Breakdown piles are angular basalt slabs and
  blocks, from metres down to gravel, at the angle of repose of dry angular rubble (~35 deg). Fines fill the gaps.
- **Modelled:** the pile climbs from its toe to the roof at 35 deg. Block sizes follow a power law (many small, few
  large), and the big blocks lie low. It uses the same rock material as the tube walls.
- **Cadet (round 12):** where no dead-end tube is in range, GIMLI-2 stopped at a cave-in part-way along a tube. If
  that tube runs on, a second cave-in ~60 m further closes the hollow part from the far side.

### The seal and the chamber: SPECULATIVE
- **Sealed for billions of years:** SPECULATIVE. A roof collapse could fuse into a tight plug if hot lava welded
  the rubble (welded breccia is real), but staying gas-tight through moonquakes and impacts is the "What if?".
- **Ice at -20 C (real physics, if sealed):** in a closed space, ice sits in balance with its own vapour
  (~100 Pa at -20 C) and does not sublimate away. In an open tube it would be lost to vacuum over geologic time.
- **Vapour burst (real):** gas venting into vacuum expands and cools; the water in it freezes into ice crystals.
  Spacecraft water dumps do this.
- **The gas (SPECULATIVE mix, real no-oxygen):** the spectrometer shows H2O 58%, CO2 24%, N2 15%, a trace of Ar and
  **no O2**. The mix is chosen to echo the young Earth's anoxic air. Lunar volcanic gas, as read from the Apollo 15
  and 17 glass beads, was mostly CO, H2O and sulfur (Saal et al. 2008). The "no free oxygen" part is solid: neither
  volcanic gas nor the Earth's air before the Great Oxidation Event (~2.4 billion years ago) had any.
- **Earth rock on the Moon (real):** Apollo 14 sample 14321 contains a ~4-billion-year-old granitic (felsite)
  fragment whose minerals formed at Earth-like pressures and oxidation. It was probably blasted off the young Earth
  by an impact (Bellucci et al. 2019). Big impacts on the early Earth threw out material that the Moon swept up
  (Armstrong, Wells & Gonzalez 2002).
- **A whole granite boulder (SPECULATIVE):** shown on the 3- and 5-level runs; 2-level runs show frost veins only.
- **Temperatures:** pit +17 C, tubes -20 C (see *Temperatures*).
- **Sources:** Bellucci et al. 2019, EPSL 510, "Terrestrial-like zircon in a clast from an Apollo 14 breccia";
  Armstrong, Wells & Gonzalez 2002, Icarus 160, "Rummaging through Earth's attic for remains of ancient life";
  Saal et al. 2008, Nature 454, "Volatile content of lunar volcanic glasses and the presence of water in the Moon's interior"; LROC images of the Apollo landing sites.

### Story devices (game)
- The landing zones (rover start 0.5 / 1.5 / 3 km from the pit on bearing 045), the 4-character mission seed,
  the crew's paced call-outs, the 4 s core drill, the debrief.

## Rendering (not science, for the record)
- **Round-8 profile:** exe `-perfbench`, GTX 1070, 2560x1440, High preset. Frame time is CPU-wait-dominated, GPU
  time is about the same.

| case | fps | ms |
|---|---|---|
| start, 1080p + FSR | 154 | 6.5 |
| start, native | 110 | 9.1 |
| tube, 1080p + FSR | 118 | 8.5 |
| tube, native | 80 | 12.6 |
| tube, round-7 point-light bounce | 61 | 16.5 |
| tube, far ring hidden | 121 | 8.3 |
| tube, crew lamps off | 126 | 7.9 |
| tube, all lamps off | 148 | 6.8 |

- **Top costs in the tube:**
  1. the round-7 point-light bounce: ~8 ms, now gone
  2. native vs 1080p rendering: ~4 ms
  3. all lamps (spots incl. 3 crew + 2 driver helmet lamps): ~1.7 ms
  4. the far tube ring: ~0.2 ms (~60 draw calls)
- **Resolution:** the 3D view renders at 1080p (900p on Low) and AMD FSR 1 upscales it to the screen. The HUD
  draws at native resolution.
- **Frame rate:** uncapped by default; F6 turns vSync on.
