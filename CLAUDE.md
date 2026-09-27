# Notes for Claude

Context for continuing work on this project in a new session.

## Working agreements

- Chat with the user in **German**; everything in the repository (code, comments,
  README, commit messages) in **English**.
- Single player game with mods (e.g. a speed mod; `MaxSpeed` is set to 300+).
- The user tests in game and reports back with screenshots. Nothing can be run in
  game from here, so state clearly what is verified (compiler check, simulations)
  and what is not.
- Work happens on a branch; PR https://github.com/m-d-meyer/acceleration-control/pull/1
  targets `main`. The user reviews and merges.

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
- Asteroid obstacle radius is estimated as 0.75 of half the voxel box size.
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
- Screens: LCD textures are 512 px; the user found small fonts unreadable, so keep
  text scales around 0.55 or larger.

## Open ideas / next steps

- Verify in game: gyroscope sign calibration, jump flow, docking checks.
- Possibly: moving bases for docking, route display of the jump leg on the radar,
  a font size option.
