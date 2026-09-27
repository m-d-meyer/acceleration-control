# Notes for Claude

Context for continuing work on this project in a new session.

## Working agreements

- Chat with the user in **German**; everything in the repository (code, comments,
  README, commit messages) in **English**.
- Single player game with mods (e.g. a speed mod; `MaxSpeed` is set to 300+).
- The user tests in game and reports back with screenshots. Nothing can be run in
  game from here, so state clearly what is verified (compiler check, simulations)
  and what is not.
- Work happens on branch `claude/keen-feynman-ek97hm`; PRs target `main`, the user
  reviews and merges. PR 1 (everything up to docking) is merged; PR
  https://github.com/m-d-meyer/acceleration-control/pull/2 adds planets.

## What the project is

An in-game programmable block script for Space Engineers 1 (`AccelerationControl/`,
MDK2 project, C# 6). Features, roughly in the order they were added:

1. Acceleration limit for WASD/Space/C via thruster overrides (mass + gravity compensated)
2. Cruise (constant forward speed for drilling) and approach (camera scan, fly, stop
   `ApproachBuffer` before the surface; searches along the line of sight if nothing is in range)
3. Ship status (cargo/ores, battery, uranium, hydrogen, jump, delta-v with in-flight
   calibration), drawn as sprites (`StatusDisplay.cs`)
4. Ore map: `mark` by camera scan, auto-logging while drilling, base, named waypoints
   (imported GPS), obstacles (asteroids/planets from scans and a background camera
   survey), radar + list views with a button menu (UI mode: WASD/Space/C), GPS export,
   `map import`, `map send` over IGC
5. Navigation: route planning around obstacles (`Navigation.cs`), gyroscope alignment
   with automatic sign calibration, collision guard (scans ahead, replans, stops),
   live replanning when new obstacles appear, `goto GPS:...`
6. Jump drive (`Jumping.cs`): aligns, sets distance, tries `ApplyAction("Jump")`; the
   game apparently refuses script-triggered jumps, so it then asks the pilot to press
   Jump and continues after the jump
7. Docking (`Docking.cs`): pose recorded when docking by hand; approach point, turning
   check, swept-volume corridor check (ship box moved along the connector axis),
   final approach with continuous scanning; `undock`
8. Planets (`Planets.cs`, PR 2): planet routes (climb, arc at cruise height, vertical
   descent), level flight in gravity, gravity-aware braking, atmosphere speed limit
   (top learned from atmospheric/ion thruster effectiveness), disturbance observer
   for wind/drag/lift, Real Solar Systems zones (teleport detection, map entries per
   zone, flights across zone changes)

The README describes all commands and Custom Data options for players.

## Environment and tools

- The cloud container has no Space Engineers. Install the .NET SDK per session:
  `apt-get install -y dotnet-sdk-8.0` (nuget.org is reachable; steamcommunity.com and
  support.keenswh.com are blocked by the network policy).
- `python3 tools/gen_stubs.py` clones the MDK-SE wiki (`malware-dev/MDK-SE.wiki`) and
  generates C# stubs of the game API into `tools/SyntaxCheck/obj/Stubs.cs` (not checked
  in). Operators of VRageMath types are hand-written in the script; if an operator is
  missing there, prefer writing the code differently over guessing (e.g.
  `Vector3D + double` is not declared: use `new Vector3D(x)`).
- `python3 tools/build.py --check` merges the project into `dist/AccelerationControl.cs`
  (the file the user pastes into the game) and compiles it against the stubs.
  - Over 100,000 characters: comments/indentation are stripped; still too long: full
    minification renames the script's own symbols via Roslyn
    (`tools/SyntaxCheck/Minifier.cs`) and compile-checks the result.
  - Always rebuild `dist/` before committing source changes.
- The MDK wiki API docs (`api/*.md` in the wiki clone) are the reference for member
  names and signatures. They date from about 2022.
- For layout or logic that cannot be run in game, a Python mock-up/simulation in the
  scratchpad has worked well (map mock-up, approach braking, route planner with
  thousands of random asteroid fields).

## Known facts and pitfalls from testing

- Camera raycasts often miss asteroids farther than about 6 km (physics not loaded).
- Thrust direction of a thruster: `WorldMatrix.Backward` (confirmed in game).
- Asteroid obstacle radius: starts at half the voxel box size and grows with every
  observed surface point (hits outside the sphere). An underestimated, irregular rock
  caused a collision on a detour; simulations showed that collisions with rocks
  larger than estimated happen on straight legs, not in turns.
- Detour waypoints are moved outwards (`WidenDetours`, user's preference over slowing
  down) when the estimated turn drift (corner speed, weakest sideways thrust, corner
  cutting) exceeds the margin to the rock.
- The collision guard treats a rock hit on the last leg as the target rock only if
  the final stop point lies on that rock; other rocks are obstacles.
- The game only calls `Save()` on world save, so the script writes `Storage` whenever
  the map changes.
- Docking data format changed once (grid pose added); after such changes the user has
  to dock by hand again.
- Jump: the gyros settle at about 0.6 degrees of heading error; alignment
  tolerances must not be tighter than that (now 2 degrees).
- `ApplyAction(name)` throws (NullReferenceException) if the block does not offer
  that action to scripts; use `GetActionWithName` and check for null. The jump
  drive's "Jump" action apparently is not available to scripts.
- `Main` wraps everything in try/catch and releases all overrides on errors, so a
  crash never leaves thrusters or gyroscopes overridden.
- Route planning ignores obstacles whose clearance contains the start point, so a
  flight next to a rock must begin with the departure leg (straight out, no
  turning); turning a long ship in place next to a rock caused a collision.
- Replanning is deferred to the start of the next tick (`_replanPending`): planning
  twice in one tick with many known asteroids likely hit the instruction limit
  ("Script Too Complex" cannot be caught by try/catch).
- Navigation distances use the grid's bounding sphere center (`ReferencePosition`);
  stop points keep `ApproachBuffer + ShipRadius` from surfaces, so turning in place at
  the stop point is safe. Flip-and-burn: `FlipTime` 30 s (user's choice); the flip is
  used only if the estimated trip time (TripTime) is shorter than without. Simulated:
  stops correctly with actual turn times up to 30 s.
- Camera raycasts DO hit the own ship when the ray passes through the hull (seen
  after flights started using sideways/backward thrust orientation). Every raycast
  result must be filtered with `IsOwnHit` (all grids of the own construct).
- Waypoint speeds are planned backwards from the end (`PlanCornerSpeeds`): before,
  the corner speed ignored the remaining distance, and a ship accelerated for 16 km
  towards a waypoint just before the base and crashed into it. Simulated setting
  that works: turn factor cos^2 and `BrakeShare` 0.7 of the planned braking on
  routes with turns (1600 random routes, worst 29 m past the end point).
- The collision guard ignores the base and the rock the dock position lies on
  (`IsBaseHit`) only while the ship can still stop in front of them; otherwise it
  makes an emergency stop. Without this, a base built into an asteroid made the
  guard stop early ("surface closer") and docking failed.
- Dock flights: base/base-rock hits behind the stop point are ignored; in front of
  it the stop point moves before the hit; emergency stop only if the braking
  distance does not suffice (the earlier rule "within ShipRadius + buffer" fired on
  every approach and caused stop/leave/return loops). Near the dock (`NearDock`)
  GO/dock go straight into the slow docking manoeuvre; gyros hold the heading until
  the turning-space check is done and do not face the target before docking.
- Docking path check: voxel hits count only 1 m inside the swept ship box (grids
  with 1.5 m margin), and only the part of the path still ahead.
- Waypoints and flight end points are hit roughly (user's request, the guard
  prevents collisions): intermediate waypoints switch within 50 m or after crossing
  the bisector plane (`WaypointReached`); the end counts within
  max(10 m, ApproachBuffer/4) below 1 m/s, and an overshoot within that just stops
  (`_settling`). Before this the ship overshot and made several attempts.
- Ice was not auto-logged while mining, though it showed in the containers. Auto-log
  now also watches the drill inventories and counts gas generator inventories
  (not verified in game which of these was the cause).
- Screens: LCD textures are 512 px; the user found small fonts unreadable, so keep
  text scales around 0.55 or larger. A wide cockpit screen looked small and blurry
  with the square layout: radar has a wide layout (>= 1.5:1, 300 units high), the
  palette is high contrast (game glare washes out mid tones), lines >= 1.6 px,
  `ScreenTextScale` option; the PB detail info shows the map texture size.

- Real Solar Systems (user's save): proxy planets move, real planets are static and
  far away; near a proxy the ship is teleported into the planet's zone with its own
  coordinates (and possibly a changed velocity). Zone key = planet center rounded to
  km ("" = space). Only this zone's obstacles are in `_obstacles` (`SwitchZone`),
  others in `_otherObstacles`. The mod's gravity is larger with a gentler falloff:
  the falloff exponent is learned in `UpdatePlanet`. Unknown and not verified: how
  the orbit zone moves ships, whether proxies have gravity for scripts. Planet
  features were only compile-checked and simulated (descent braking with thruster
  lag and wind, observer with lag, arc geometry), not flown in game.
- Planet simulations (scratchpad `planetflight*.py`, `guardsim.py`): 3D point mass,
  level ship with per-axis thrust, thruster lag, gusts, drag, ported route/waypoint/
  braking/atmosphere logic. Findings that changed the code: the pre-entry speed
  limit must only apply on the leg entering the atmosphere (it throttled routes
  cruising just above it); aim at 85 % of `AtmosphereSpeed` (lag overshoot ~18 %);
  long flights above the atmosphere halve the time; a guard-driven replan handles
  hills 3 km above the cruise height. Physical limit: a ship with 1.5 g upward
  thrust entering a zone 60 km up at 1500 m/s cannot stop (warning shown).
- RSS mod facts (from the mod description the user pasted): velocity is converted at
  zone changes (a planet "running into" a resting ship gives it the orbital speed);
  ORBIT zone follows the planet's orbit, SURFACE zone follows the surface; every GPS
  placed on a planet gets a moving proxy copy ("PROXY_DO_NOT_EDIT"). No PB API is
  known, so `track GPS:...` takes samples of such a moving GPS (Lagrange fit through
  2-3 samples), matches the planet's velocity and brakes to `ZoneEntrySpeed` before
  the zone edge (`ZoneRadiusGuess`, learned per zone at the first entry, stored in
  `ZoneRadii`). Simulated: ~100 m/s entry if the guess >= real zone, 340-450 m/s if
  the zone is larger than guessed (hence default 200 km).
- RSS config (user): real planets spawn 900,000-9,000,000 km from the origin;
  `EnablePlanetGPSAll`/`EnablePlanetGPSUnlocking` create planet GPS (moving copies
  usable for `track`); `EnableGridRotationOnZoneTransition` rotates grids at the
  surface zone edge. Open question: do proxies have script-visible gravity? The
  status page shows any gravity > 0.001 m/s² ("no planet" in proxy space) to find
  out; if yes, a gravity-based homing without GPS pasting could be added.
- Aerodynamic Physics mod (DraygoKorvan, mod id 571920453) offers wind/drag only via
  mod-to-mod messages (`RemoteDragSettings.cs`), not to PB scripts: the disturbance
  observer is the only way. Water mod: raycasts do not see water; the sea floor can
  be 500 m+ below the surface (user). Planet heights are geodetic (distance from the
  center, which `TryGetPlanetPosition`/`TryGetPlanetElevation(Sealevel)` give
  exactly); the cruise sphere is at least above `water here` (per planet) /
  `WaterLevel`, start/target ground and seen terrain.
- Planet obstacles: raycast hits on a known planet (center within 1 km) only store the
  entity id; `UpdatePlanet` measures radius/well (duplicates caused replanning loops).

## Open ideas / next steps

- Verify in game: gyroscope sign calibration, jump flow, docking checks.
- Possibly: moving bases for docking, route display of the jump leg on the radar,
  a font size option.
