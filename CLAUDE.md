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
  reviews and merges. PR 1 (everything up to docking) and PR 2 (planets, recorded
  ways, gates, several docks) are merged; the Workshop material followed in PR 3.

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
9. Recorded paths (`Paths.cs`): recent poses ("crumbs") are kept; docking by hand
   stores the last 300 m of the way in relative to the base grid, mining/`mark here`
   stores the way to a deposit; `Mode.Path` follows them (dock, reverse for undock,
   deposits) and hands over to the pilot when blocked for 10 s

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
    The minifier also drops `readonly`, uses `var` where the type matches exactly and
    adds short static wrappers for frequent static API calls (`Math.Max`,
    `Vector3D.Distance`, ...). Minified size is about 98.3k of 100k: space is tight;
    config options use the `Option(key, value)` helpers in `Config.cs` to save room.
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
- Stress test: goto from inside a hangar (base grid in an asteroid, door opened by
  pistons) turned the ship (target 1000+ km away: jump alignment or route turn) into
  the wall. Grids are not on the map. `TryLeaveConfined` (StartGoal and after each
  departure) scans a sphere of ShipRadius + 5 m with the cameras; if blocked, moves
  straight out along a ship axis verified clear by 5 rays, else stops.
  Second test (before that fix): goto just outside the hangar backed the ship into
  another asteroid behind it (departure direction never checked). `NeedsDeparture`
  and `TryLeaveConfined` share `ChooseWayOut`: candidates must be `MapClear` and seen
  clear by `CheckPath` (parallel rays from every camera facing the way, since the
  user's cameras sit offset on the sides, plus 5 rays to the centre corridor);
  unverified only the way the ship came in (`TrackCameFrom`), else stop.
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
  `ScreenTextScale` option; the PB detail info shows the map texture size. The
  user's wide cockpit screen has a 256x256 texture (only a wide strip used): below
  200 px height the radar uses a compact layout (200 units high, 3 text lines,
  `TextFit` shrinks/shortens texts). Its used area is 256 x 153.6 px: the centered
  viewport starts at y = 51.2, so all sprites sat between pixels and blurred;
  `P()` and `Rect()` snap to whole pixels.

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
- Planet test (user): the script died right after a jump towards a planet: the
  radar drew every route leg as dashes of 12 units, and planet arcs thousands of km
  off the radar produced endless dashes ("Script Too Complex"). Legs are clipped to
  the layout (`ClipToLayout`), at most 60 dashes. After the jump, outside the
  gravity but in the planet's zone, the zone had switched to "" (zone was tied to
  gravity), so the planet was unknown and a jump to the surface was planned: the
  zone now changes only by teleport. Planet arcs from high above go straight to the
  farthest visible arc point (tangent). Level flight only in an atmosphere or when
  g > 30 % of the weakest thrust side (user: strongest thrusters point down;
  weak gravity far out is flown like space).
- Atmospheric miner test: 7 km hop climbed 1.5 km (cruise = 25 % of the distance,
  capped by PlanetCruiseHeight; user fine with it). Returning to dock it hit the
  blades of a wind turbine on the base: the docking turn check ignored all base
  grids (now counted in the "around" scan), and the guard's rays towards the look
  point miss thin things beside the path (every other guard scan is now a ray
  parallel to the path from a camera facing the way). ETA stood still while
  climbing (it ignored the stop at the top of the climb): now leg by leg with
  `_cornerLimits`.
- RSS space base test: (a) "jump complete" fired while the ship was still moving
  (distance from the jump start > 1 km); now a jump is the first unexplained
  position jump after a drive countdown (`_jumped`), a second one is a teleport.
  (b) Flights starting in gravity never jumped (50 h ETA): `CheckJumpOnRoute` once a
  second tries a jump per leg once out of gravity. (c) Backing into the asteroid
  behind the ship again: cameras drained by the survey were "unseen", so the blind
  way was taken; now a camera looking that way but uncharged means wait
  (`WaitForCameras`, up to 10 s, survey paused). (d) Landed on the Earth base the
  start was refused (ground in the turn sphere, no up camera): up is allowed blind
  when only the ground below confines. Open: the space base's stored pose was
  50,000 km off in another zone (base probably in a planet's orbit zone outside
  gravity, where the zone key cannot be derived); docking came from the wrong side.
- Planet obstacles: raycast hits on a known planet (center within 1 km) only store the
  entity id; `UpdatePlanet` measures radius/well (duplicates caused replanning loops).

- Space base near the Moon, outside gravity, is in the Moon's RSS zone; the Earth
  zone reaches far beyond gravity (30-60 s at 300 m/s). Teleports outside gravity
  take the zone of the nearest known planet (`NearbyPlanetZone`, 1.5 x gravity
  radius + 50 km); unknown -> provisional "" zone, relabelled
  (`RelabelProvisional`) when that planet's gravity is reached without teleport.
- Dock data is base-relative (`_baseMatrix`, `_dockLocal`, `_dockPathLocal`);
  `NoteHit` refreshes the base pose from every raycast hit on the dock grid
  (`hit.Orientation`, `hit.Position` = AABB centre, hence `_baseCenterLocal`). All
  raycasts should go through `Cast()`. Storage keys DockBase/DockLocal/DockPath;
  older saves need one manual dock. Path following is only compile-checked, not
  simulated or flown.
- Hangar test: `dock` flew through the rock above the hangar opening. Found: every
  script start while docked re-recorded the dock with no crumbs and dropped the way
  in (now kept for the same dock); the path index advanced by projection alone, so
  a ship off to the side skipped points (now only near the segment). `NoteHit`
  checks that the reported orientation reproduces the hit's box size (`_baseHalf`).
  Real cause: a small ship docked on the user's ship made the script "docked" from
  the start (small ship stored as base, no crumbs ever). `CheckDocking` now tracks
  connections per connector: a new one is a dock only while this ship is piloted
  (or in Mode.Dock/Path); at script start only at the known dock position; others
  are `_carried` (ignored by `IsOwnHit`, `DockConnector`, `_wasConnected`).

- Path docking stopped ~20 cm short (end tolerance 0.5 m, connector not in lock
  range): the last 3 m now steer the connector to `_dockPosition` - 0.3 m along
  the axis until Connectable (15 s limit). Base hits on the dock/undock way are
  ignored only inside the docked ship's box + 1.5 m (`InDockedBox`), so a closed
  gate blocks; blocked waits last 60 s there (sensor gates).
- Gates: `DockGate/` is a second project, a companion PB script for the base
  (build.py builds both into `dist/`). Protocol on IGC tag `AccelDock`: ship
  broadcasts `open|<base connector id>` / `close|...`; base triggers timers
  "Dock Open"/"Dock Close" (name part matching the connector name selects a dock),
  replies by unicast "busy", then "ready" when "Dock Gate" blocks stopped. Ship:
  `Gate()`, `GateWait()` (2 s for an answer, 60 s for opening), connector id saved
  as DockGate. Not tested in game.
- Ship script at ~99.0k of 100k after this; the Custom Data help comment was cut to
  one line to make room.

- Several docks: the active dock lives in the fields as before (state section);
  each base entry (`Deposit.Dock`, a MyIni, section "D") keeps its own copy,
  saved as map sections `Dock<i>`. `WriteDock`/`ReadDock` (Config.cs) serialise,
  `StoreDock`/`ActivateDock`/`ChooseDock` (Docking.cs) switch; `_dockEntry` is the
  active dock's base entry. `dock` refuses without a known dock in this zone
  within `DockRange` (20 km; user: never fall back to a far dock). At script start a connection to another known base
  activates that base's dock.
- Size: `OwnBlocks<T>()` replaces the repeated construct-filtered
  `GetBlocksOfType`; the minifier (step 5b) caches frequent static API values
  (enum members, constants, static fields, InvariantCulture) in short fields.
  Ship script ~98.0k after the multi-dock feature.

- The PB whitelist is not checked by the stub compile: the minifier's cached
  `IFormatProvider` field compiled here but the game refused it ("type or member
  'IFormatProvider' is prohibited"). Generated wrappers/fields now only use types
  the script already names (`Allowed` in Minifier.cs). New API types in source
  code carry the same risk; the in-game "Check code" is the only whitelist test.
- GO/goto while docked: `UndockFirst` runs `Undock()` and keeps the flight in
  `_afterUndock`, started at the end of the way out (or of the undock route);
  cleared on cancel/stop/give-up. User report: after script docking, the thrusters
  that pushed into the dock stayed at 100 % until a key was pressed; cause not
  found by reading the code (every tick releases unused axes). Workaround: a new
  dock connection forces `ReleaseAll(true)`. Follow-up: overrides were 0, the
  game's dampeners fired in the docking direction (likely a game quirk). Now all
  own thrusters are disabled on docking (`Thrusters(false)`, `_thrustersOff` saved
  as ThrustersOff) and enabled by `Undock()` or when CheckDocking sees no dock.
  `Undock()` also unlocks landing gear (and in reverse path mode each tick), and
  with a recorded way keeps the connector locked until `GateWait` reports the gate
  open (`_undockPending`): unlocked and waiting, the connector pulled the ship back.
- Loaded ship overshot the connector at 2-3 m/s on a recorded way: path speed
  was min(8, 0.4 x distance) with a hard 1.5 m/s cap 20 m before the dock, i.e. a
  step from 8 to 1.5 m/s that needs ~3 m/s² of braking. Now v = sqrt(brake x
  distance) (half of `BrakeAccel` in that direction, max 3) and the dock cap is
  reached by braking. Simulated (scratchpad `pathbrake.py`, thruster lag 0.5 s):
  old profile overshoots below ~3 m/s² braking, new one arrives at 1.5 m/s down
  to 0.5 m/s².
- Review for fixed cutoffs (user's request after the overshoot): recorded ways
  had no speed plan for bends or pose turns (target pose jumped per point,
  0.35 rad align error = full stop). Now `_pathLimits` are planned backwards in
  `StartPathFollow` (bend cos^2, `TurnSpeed` = segment length x 0.3 rad/s / pose
  change, braking to the next point with `BrakeAlong` = min(BrakeAccel, 3)), the
  target pose is interpolated along each segment and speed scales down smoothly
  with the align error. Simulated (scratchpad `pathbend.py`, 90 degree bend,
  2D, lag): old 11-23 m off the way, new 0.5-1.9 m. Docking final approach, hold
  and lateral correction are also capped by `BrakeAlong`. Open (reported, not
  changed): the guard's look distance grows with speed but cameras see ~6 km, so
  fast flights outrun what the guard can check; undock without a recorded way
  backs out blind; FlipTime is a setting, not measured. Ship script ~99.6k.
- Follow-up (user): gyro P control (gain 2, max 1.5 rad/s) overshot twice on 180
  degree turns of the large miner. Now per ship axis rate = min(gain x error,
  sqrt(1.4 x alpha x error)); alpha = `_gyroTorque` (angular accel x mass, saved as
  GyroTorque) learned every 30 ticks while an axis is commanded > 0.15 rad/s away
  from its rotation. `FlipTime` property uses it (180 degrees, 20 % + 2 s), the
  setting only until measured. Simulated (`gyrosim.py`, torque-limited gyros):
  old 84-156 degree overshoot, new ~1 degree, learned within the first turn.
  Dodge (user: accelerating sideways is often cheaper than stopping): guard hit
  closer than 1.3 stopping distances -> if 2 x needed lateral offset / t^2 <
  min(SideAccel, limit) x BrakeSafety (x 0.5 if stopping is still possible), a
  waypoint beside the obstacle (clearance x DetourFactor) is inserted and the rest
  is replanned from it (`_dodge`, `_dodging`; planning from the ship would ignore
  the obstacle, since the ship is inside its clearance). Simulated
  (`dodgesim.py`): rocks seen 3-6 km ahead at 100 m/s with 1 m/s² braking are
  now passed 165-233 m clear instead of a collision after a failed stop.
- Emergency evasion (user flies 300 m/s): when stopping in time is impossible
  and the plain dodge is not enough (not for planets), `_evading`: gyros turn the
  strongest side (`BestThrust`) towards `_evadeDir`, target velocity = v + (aside -
  motion) x 1000 at unlimited accel, until the obstacle is passed or the path
  clears it, then replanned from a point outside its clearance (`_dodge`). Guard
  paused meanwhile. Guard look distance capped at `GuardRange` 8 km (before: up to
  1.5 stopping distances, tens of km at 300 m/s = 1 s camera charge per 2 km).
  Simulated (`evadesim.py`, per-axis saturation, turning 0.03-0.5 rad/s², 6 km):
  390-840 m clear where stopping hit the rock. An estimate of the reach (turn time
  + thrust) was too pessimistic, so the evasion is always used when stopping fails.
- Minifier step 7 (`MergeFields`) merges same-type instance field and const
  declarations (-2.7k characters). Ship script ~98.3k.
- Workshop: `workshop/` holds the Steam descriptions (BBCode, 8000 character
  limit), images (mock-ups/diagrams, not screenshots) and their generators. The
  user wants the disclaimer "code written 100% by Claude Opus 5.5, tested in game
  by me" at the top of the Workshop text.

## Open ideas / next steps

- Verify in game: gyroscope sign calibration, jump flow, docking checks.
- Possibly: moving bases for docking, route display of the jump leg on the radar,
  a font size option.
