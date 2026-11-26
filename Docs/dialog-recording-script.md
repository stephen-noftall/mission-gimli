# Artemis-Marius - dialogue recording script

Generated from `Assets/StreamingAssets/Dialog/dialog.csv` (the source of truth: edit the CSV, then regenerate).

**Files:** one per line, mono, 44.1 kHz, MP3 (or 16-bit WAV), named `<ID>.mp3`, placed in
`<game>_Data/StreamingAssets/Dialog/` (or `Assets/StreamingAssets/Dialog/` in the project). Lines without a
file stay subtitles only. The game adds the radio band-pass and the Quindar tones on HOUSTON lines: record dry.

**Speakers:** CMDR = commander (the driver); SPEC = mission specialist / geologist; HOUSTON = CAPCOM in
Mission Control (arrives after the 2.6 s light delay); CREW = any crew member (generic rover lines).

**Counts:** CMDR 27, HOUSTON 17, SPEC 24, CREW 13; 81 lines.

| ID | Speaker | Line | Context |
|---|---|---|---|
| `b1_touchdown` | CMDR | Houston, Artemis-Marius. Engine shutdown complete, dust settling. Rover off-load check is green. | Beat 1. Mission start at the crew lander. |
| `b1_distance_cadet` | HOUSTON | Copy, Artemis-Marius. Telemetry shows you half a kilometre out from the pit on bearing zero-four-five. Try not to scratch the fenders - that rover still has its new-car smell. | Beat 1. Reply to b1_touchdown. PINPOINT (Cadet) tier. |
| `b1_distance_explorer` | HOUSTON | Copy, Artemis-Marius. Telemetry shows you one and a half kilometres out from the pit on bearing zero-four-five. Try not to scratch the fenders - that rover still has its new-car smell. | Beat 1. Reply to b1_touchdown. STANDARD (Explorer) tier. |
| `b1_distance_commander` | HOUSTON | Copy, Artemis-Marius. Telemetry shows you three kilometres out from the pit on bearing zero-four-five. Try not to scratch the fenders - that rover still has its new-car smell. | Beat 1. Reply to b1_touchdown. SAFE ZONE (Commander) tier. |
| `b1_smell` | SPEC | No promises, Houston. Though after an EVA the cabin mostly smells like burnt gunpowder - that's moondust for you. | Beat 1. After b1_distance. |
| `b1_gimli1_spec` | SPEC | There it is - GIMLI-1. The little lander that started all this. See its rover's tracks along the rim? That radar is how we know the tubes are down there. | Beat 1. GIMLI-1 site comes into view on the drive. |
| `b1_gimli1_houston` | HOUSTON | Copy. Give our first rover a wave for us - it's been listening to that pit for a long time. | Beat 1. Reply to b1_gimli1_spec. |
| `b1_gimli1_name` | SPEC | GIMLI - like the dwarf. Good name for a mission that goes digging under the hills. | Beat 1. After b1_gimli1_houston (a light joke on the name). |
| `b1_cargo_lander` | SPEC | That's the GIMLI-2 cargo lander. They set it down half a kilometre out so the engine blast wouldn't sandblast the pit - robots hauled the crane over in pieces. You can still see the tracks. | Beat 1. Passing the 7 m cargo lander. |
| `b1_tracks` | CMDR | Look at our tracks. No wind, no rain - they'll still be here in a million years. | Beat 1. Optional: first look back at the rover's own tracks. |
| `b2_platform` | CMDR | Visual on the crane. Pulling onto the platform - lining up with the side guides. | Beat 2. Rover drives onto the crane platform. |
| `b2_gates` | SPEC | Front and aft gates locked. Ready for lift. | Beat 2. L pressed: the gates close. |
| `b2_below_rim` | CMDR | Dropping below the sunlit lip... visors are washed out, give it a second. | Beat 2. Platform goes below the sunlit rim; the exposure shifts. |
| `b2_bounce` | SPEC | There we go - eyes adjusting to the bounced light. Look at the layering on that wall... clean basalt flows all the way down. If there's a gift shop down here, I'm buying. | Beat 2. The pit walls light up in the bounced sunlight. |
| `b2_pit_temp` | SPEC | Pit floor reads plus seventeen Celsius. The sunlit walls keep it warm down here - warmer than anywhere on the surface at night. | Beat 2. Nearing the pit floor. |
| `b2_touchdown_cadet` | HOUSTON | Copy, no gift shop on the orbiter maps. Touchdown confirmed - open the gates when you're ready to roll. GIMLI-2's transponder is on your HUD, level two. | Beat 2. Platform on the pit floor. Cadet tier. |
| `b2_touchdown_explorer` | HOUSTON | Copy, no gift shop on the orbiter maps. Touchdown confirmed - open the gates when you're ready to roll. GIMLI-2's transponder is on your HUD, level three. | Beat 2. Platform on the pit floor. Explorer tier. |
| `b2_touchdown_commander` | HOUSTON | Copy, no gift shop on the orbiter maps. Touchdown confirmed - open the gates when you're ready to roll. GIMLI-2's transponder is on your HUD, level five. | Beat 2. Platform on the pit floor. Commander tier. |
| `b3_offplatform` | CMDR | Off the platform. High-beams on - no light past the skylight bounce. | Beat 3 trigger A. Rover leaves the platform into the dark. |
| `b3_tube_temp` | SPEC | Temperature's dropping... minus twenty and dead steady now. Up top it swings from plus one-twenty to minus one-seventy. Down here it's a freezer that never cycles. | Beat 3 trigger A. About 40 m into the tube. |
| `b3_basalt` | SPEC | Laser scanner's painting smooth basalt floor ahead. Traction's way better than that dusty regolith. | Beat 3 trigger B. About 15 s driving on smooth basalt. |
| `b3_highway` | CMDR | Copy. Feels like a fresh highway - just mind the breakdown piles. | Beat 3 trigger B. Reply to b3_basalt. |
| `b3_ramp` | CMDR | Dropping down a level over this collapse ramp. Easy on the throttle. | Beat 3 trigger C. First ramp down a level. |
| `b3_breakdown` | SPEC | Classic breakdown - roof slabs shaken loose by moonquakes and impacts over billions of years. GIMLI-2's relay beacon is still solid ahead. | Beat 3 trigger C. Reply to b3_ramp. |
| `b3_lowbatt` | CMDR | Heads up, battery's below thirty percent. No more sightseeing - let's keep moving. | Beat 3 trigger E. Battery below 30%. |
| `b3_final_level` | HOUSTON | Telemetry shows you're on GIMLI-2's level now. Signal strength is maxed out just ahead. | Beat 3 trigger F. Rover reaches the target level. |
| `b3_final_cmdr` | CMDR | Copy, Houston. Rounding the next bend - let's see what stopped our little scout. | Beat 3 trigger F. Reply to b3_final_level. |
| `d_glaze` | SPEC | Look at the walls - that glassy black lining is lava glaze. The tube's skin, quenched when the flow drained away. | Trigger D. First lava glaze lit by the headlights. |
| `d_glints` | SPEC | See the sparkle in the high-beams? Ilmenite crystals and specks of native iron, right in the basalt. That's titanium ore. | Trigger D. First ilmenite / iron glints. |
| `d_orange` | SPEC | Orange glass beads on the floor! Like Apollo 17's orange soil - droplets from a volcanic fire fountain. They must have rained in through a skylight. | Trigger D. First orange glass beads. |
| `d_green` | SPEC | Green glass - like Apollo 15 found. Volcanic spray from deep in the mantle. | Trigger D. First green glass beads. |
| `d_crust` | SPEC | Pale yellow crust along those cracks... sulfur and zinc, maybe, frozen out of the volcanic gas. Nobody's ever seen this in place. | Trigger D. First Zn/S crust (speculative). |
| `d_tag_1` | CMDR | Tagging it on the map for the return trip, Doc. Not enough battery to stop now - GIMLI-2 first. | Trigger D. Reply to a sighting (variant 1). |
| `d_tag_2` | CMDR | Marked. We'll come back for it if the amp-hours allow. | Trigger D. Reply to a sighting (variant 2). |
| `d_houston` | HOUSTON | Commander's right, Artemis-Marius. Primary objective first - you can play rockhound on the way back. | Trigger D. First sighting only. |
| `t_junction` | CMDR | Houston, the tunnel splits here - just like GIMLI-2 mapped it. Following its beacon. | Tube feature: junction (first). |
| `t_junction_2` | SPEC | Another branch - this network goes off in every direction. Beacon says straight on. | Tube feature: junction (later). |
| `t_junction_houston` | HOUSTON | Copy, we have you at that junction on GIMLI-2's map. | Tube feature: reply to t_junction. |
| `t_breakdown` | SPEC | Rubble pile ahead - a slab of roof came down here. Steer around it. | Tube feature: breakdown pile ahead. |
| `t_breakdown_cmdr` | CMDR | Going around. Slow and easy. | Tube feature: reply to t_breakdown. |
| `t_collapse_ramp` | CMDR | Houston, the roof's fallen in here and broken through to the tube below. There's a rubble ramp down - GIMLI-2 drove it, so can we. | Tube feature: collapse ramp between two tubes. |
| `t_collapse_spec` | SPEC | Talus slope, about twelve degrees. Plenty of grip on that rubble. | Tube feature: reply to t_collapse_ramp. |
| `t_lavafall` | SPEC | See the step in the floor? An old lava fall - the flow poured over it like a waterfall, then wore down into this slope. | Tube feature: lava-fall step. |
| `t_chamber` | SPEC | Whoa - the tube opens into a huge chamber. The high-beams don't even reach the far wall. | Tube feature: large chamber. |
| `t_deadend` | CMDR | Dead end - this side tube just pinches out. Backing out to the main tunnel. | Tube feature: dead-end spur. |
| `t_shaft` | CMDR | Collapse shaft ahead - big drop to the level below. Staying back from the edge. | Tube feature: collapse shaft ahead (with EDGE AHEAD). |
| `t_shaft_houston` | HOUSTON | Copy the shaft. GIMLI-2's route goes around it - you've got anchor kits if you need them. | Tube feature: reply to t_shaft. |
| `s_batt_90` | CMDR | Houston, status check: battery at about ninety percent, all systems nominal. | Status report (every 3-4 min). Battery band 90%. |
| `s_batt_80` | CMDR | Houston, status check: battery at about eighty percent, all systems nominal. | Status report. Battery band 80%. |
| `s_batt_70` | CMDR | Houston, status check: battery at about seventy percent, all systems nominal. | Status report. Battery band 70%. |
| `s_batt_60` | CMDR | Houston, status check: battery at about sixty percent, all systems nominal. | Status report. Battery band 60%. |
| `s_batt_50` | CMDR | Houston, status check: battery at about fifty percent, all systems nominal. | Status report. Battery band 50%. |
| `s_batt_40` | CMDR | Houston, status check: battery at about forty percent, all systems nominal. | Status report. Battery band 40%. |
| `s_batt_30` | CMDR | Houston, status check: battery at about thirty percent, all systems nominal. | Status report. Battery band 30%. |
| `s_batt_20` | CMDR | Houston, status check: battery at about twenty percent, all systems nominal. | Status report. Battery band 20%. |
| `s_batt_10` | CMDR | Houston, status check: battery at about ten percent, all systems nominal. | Status report. Battery band 10%. |
| `s_ack_1` | HOUSTON | Copy, Artemis-Marius, we see the same. | Reply to a status report (variant 1). |
| `s_ack_2` | HOUSTON | Copy all. Power trend looks good from here. | Reply to a status report (variant 2). |
| `b4_visual` | CMDR | Visual on GIMLI-2! Parked right up against a solid roof collapse. Looks like it ran out of road. | Beat 4. Within about 10 m of GIMLI-2. |
| `b4_radar` | SPEC | GIMLI-2's radar is lighting up behind that rubble. That's no basalt density - it's hollow. Deploying the core drill. | Beat 4. After b4_visual; then the HUD shows Hold [F] to drill the seal. |
| `b5_gas` | SPEC | Pressure spike - gas venting out of the bore hole! Water vapour, CO2, nitrogen... and no oxygen. None at all. | Beat 5. The drill breaks through. |
| `b5_probe` | CMDR | Feeding the probe camera through now... | Beat 5. Camera moves through the breach. |
| `b5_granite` | SPEC | Houston, tell the geology team to put down their coffee. That boulder isn't moon rock - it's granite. Granite from Earth. We're looking at a piece of the early Earth! | Beat 5. Level 3 and 5 runs: the Earth boulder. |
| `b5_houston` | HOUSTON | Copy all, Artemis-Marius! A four-billion-year-old time capsule - and no oxygen in that air, just like the young Earth before life changed it. Take that core sample; the labs will be arguing about this for a decade. | Beat 5. Reply to b5_granite or b5_frost_only. |
| `b5_frost_only` | SPEC | Frost veins all over the chamber walls - this pocket has been sealed since the lava cooled. Houston, we've got volcanic gas more than ten times older than the dinosaurs. | Beat 5. Level 2 runs instead of b5_granite. |
| `crane_shudder_crew` | CREW | Whoa - felt a shudder through the platform. Holding on. | Crane: shudder during the descent. |
| `crane_shudder_capcom` | HOUSTON | Copy, saw that. Winch is running a bit hot but you're within limits, continue. | Crane: reply to crane_shudder_crew. |
| `hard_landing_crew` | CREW | Hard landing! Everyone OK? | Rover: shock sensor above 3 g. |
| `hard_landing_capcom` | HOUSTON | We saw that spike - check your suspension. | Rover: reply to hard_landing_crew. |
| `bump_remark_1` | CREW | Oof - felt that one. | Rover: a jolt between 1.5 and 3 g (variant 1). |
| `bump_remark_2` | CREW | Easy on the bumps, we've got a long way to go. | Rover: a jolt (variant 2). |
| `bump_remark_3` | CREW | Bit of a jolt there. Everything's holding. | Rover: a jolt (variant 3). |
| `winch_lowering` | CREW | Anchor bolted, davit out. Lowering over the edge to the talus. | Rover winch: start lowering down a shaft. |
| `winch_unhooked` | CREW | Unhooked. Line stays rigged - that's our way back up. | Rover winch: unhooked at the bottom. |
| `winch_hooked` | CREW | Hooked on. Taking up the slack - winching up. | Rover winch: start winching up. |
| `winch_recovered` | CREW | Anchor recovered and stowed. | Rover winch: anchor kit back on board. |
| `winch_on_rim` | CREW | On the rim. Line's still rigged. | Rover winch: back up on the rim. |
| `winch_on_talus` | CREW | Wheels on the talus. Slack line. | Rover winch: at the bottom. |
| `fall_crew_1` | CREW | We fell in! ... Everyone OK? Harnesses held. Roll cage took it. | Rover fell down a shaft without a kit. |
| `fall_crew_2` | CREW | Rover's hurt - steering's pulling and she won't go above a crawl. | Rover fell: damage report. |
| `fall_capcom` | HOUSTON | We saw it. Glad you're all OK. Telemetry shows suspension damage: keep it slow. Find a ramp back up. | Rover fell: reply. |
