# ShipOS - handoff from Acceleration Control

Context for starting the follow-up project in a new repository and a new session.
Copy this file into the new repository as `CLAUDE.md` (and trim it as the project
grows). It was written at the end of the Acceleration Control project
(github.com/m-d-meyer/acceleration-control), whose `CLAUDE.md` holds the full
history of findings from in-game tests.

## Working agreements (carried over)

- Chat with the user in **German**; everything in the repository (code, comments,
  README, commit messages) in **English**.
- The user plays Space Engineers 1 single player with mods (speed mod, MaxSpeed
  300+; Real Solar Systems; Aerodynamic Physics; a water mod) and tests in game,
  reporting back with screenshots of the programmable block's text. Nothing can be
  run in game from the container: always say what is verified (compiler check,
  simulation) and what is not.
- Simulate logic that cannot be run (Python in the scratchpad) before shipping it;
  it caught many bugs in Acceleration Control (braking, gyros, landing, routing).
- Do not remove existing features to make room without asking the user.
- The user reviews and merges PRs; work on the branch the session names.
- Workshop texts start with the disclaimer "code written 100% by Claude Opus 5.5,
  tested in game by me" (no name). License MIT (owner "m-d-meyer").
- Acceleration Control is published on the Workshop (100+ subscribers, featured in
  Keen's Community Spotlight). Changes to it reach other players: no storage format
  changes that force a manual re-dock, keep README/Workshop text in step.

## Vision

An "intelligent ship" for Space Engineers: a **co-pilot, not an autopilot**. What
made Acceleration Control feel intelligent was not the number of features but four
traits, and ShipOS should strengthen exactly these:

1. **Perceives** its surroundings and visibly reacts ("Obstacle ahead, dodging").
2. **Knows itself**: learns its own handling (gyro torque, braking, consumption),
   notices damage and adapts.
3. **Thinks ahead**: plans braking, corners, landing spots, fuel and time.
4. **Explains itself**: says what it does and why ("Spot 1: 22 deg, uneven 1.8 m").

Plus a fifth one: **it talks with the pilot** - a command console in controlled
natural language, a log, questions before acting, selectable autonomy.

The user explicitly prefers this "the ship has a mind" feeling over the purely
functional style of MotherOS (a network/toolkit script). The in-game AI blocks
(autopilot, combat blocks) disappointed the user; ShipOS should use them as
sensors/actuators but make the decisions itself.

## Platform decision

- Programmable block scripts (vanilla, Workshop, servers with scripts enabled).
  Not a ModAPI mod (would need server installs, "sees everything"), no external
  LLM (PBs have no network; client plugins are not an option for most players).
- **Several PBs on one grid**, one module each, talking over IGC. Each PB has its
  own 100,000 character limit and its own instruction budget per tick (the budget
  matters as much as the size: "Script Too Complex" cannot be caught).
- Shared code between the PB projects (MDK2 supports shared projects / mixins), so
  thrust control, gyros, parsing helpers etc. exist once.

## Architecture (proposal, to be refined)

- **Core / Co-pilot PB**: console (command parser), dialogue (questions, answers,
  log), shared world state ("blackboard": position, fuel, cargo, damage, known
  threats, current mission, autonomy level) and the decision layer. Decisions as
  utility scores per goal ("continue mission", "return to refuel", "retreat",
  "evade threat", "wait for pickup time") instead of fixed if-then chains; the goal
  with the highest score wins, with hysteresis so it does not flip-flop.
- **Navigation PB**: Acceleration Control's flight code (routes, guard, docking,
  planets, landing, jump). It needs an IGC command interface (today commands come
  as PB arguments). In ShipOS the screens/map could move to another PB, which frees
  room in this one (Acceleration Control itself is at ~99.96k of 100k).
- **Logistics PB** (stage 2): fuel/energy/cargo prediction, mission feasibility,
  refuel stops, "mine until full, return, unload, repeat".
- **Threat PB** (stage 3/4): detection via AI blocks, turrets, cameras; evasion,
  retreat, calling other ShipOS ships; later supervised combat.
- Message protocol on IGC: small text messages with a tag per module (as DockGate
  does with `AccelDock`); keep them versioned so modules can be updated separately.

## Command console (controlled natural language, no LLM)

- Input: an LCD named e.g. `[Ship Console]` that the player edits in the terminal;
  the script reads `GetText()`, clears it and answers on a second screen like a chat
  history. Also: PB argument field, toolbar slots with preset commands. Scripts
  cannot read the game chat (vanilla).
- Parser: intents + slots. Synonyms ("fly/go/head to"), typo tolerance (edit
  distance), numbers and times ("in 20 min", "at 14:30"), references ("here", "me",
  "base", "home", map names, "that asteroid" = what the camera aims at), chaining
  ("..., then return to base when full"). English first (Workshop audience), German
  synonyms maybe later. Unknown input gets a helpful answer listing what it can do.
- Ambiguity is a feature: ask back instead of guessing ("I know 2 platinum
  deposits: Pt1 4 km, Pt2 12 km. Taking Pt1 - mine until full, then return?").
- Examples from the user:
  - "Fly to the asteroid and mine Platinum" -> mine(ore=Platinum, target=?) ->
    clarify target from the map, confirm the mission.
  - "Attack any nearby targets" -> attack(any, range=sensors). Parsing is easy, the
    combat itself is the hard part (delegate aiming/patterns to the Offensive
    Combat block, supervise and abort on damage/odds).
  - "I want to be picked up at GPS:... in 20 minutes" (on a planet) ->
    pickup(place, time): plan backwards (flight + undock + landing search), wait,
    report departure, land at a safe distance from the GPS (Acceleration Control's
    `land GPS:`).
- Estimated size of the parser: 10-15k characters in its own PB.

## Co-pilot voice

- Log screen: short sentences, what and why.
- Questions with answers via the existing button UI (OK / ignore).
- Autonomy levels: assistant (hints only), co-pilot (asks first), autonomous (acts,
  then reports).
- Sound blocks and light colours for alerts, sparingly.
- Idea to verify: set the ship antenna's `HudText` to short messages ("Condor:
  arriving in 3 min") so a player on foot sees them as a HUD signal within antenna
  range.

## Stages

1. Co-pilot core: console + parser, log, questions, autonomy levels; executes
   Acceleration Control's existing functions over IGC.
2. Foresight: fuel/energy/cargo prediction, mission feasibility and refuel stops,
   repeated mining missions, timed pickup.
3. Threats: detection, report, keep distance, break line of sight behind rocks,
   retreat to base, call for help.
4. Supervised combat.

## PB API facts found so far (check the MDK wiki API docs, they date from ~2022)

- `IMyRadioAntenna` has no list of received signals: the HUD signals of other
  grids (the user sees NPC enemies at ~14 km) are NOT visible to scripts. Only own
  broadcasting (Radius, HudText, EnableBroadcasting) and IGC messages from scripts.
- AI blocks: `IMyOffensiveCombatBlock` / `IMyDefensiveCombatBlock` have
  `SearchEnemyComponent.FoundEnemyId` (whether/which enemy, no position);
  the defensive block has `Flee()`, `IsFleeing`, flee coordinates; offensive has
  attack patterns (circle orbit, hit and run, intercept, stay at range) and target
  priority. Turrets and `IMyTurretControlBlock` give `GetTargetedEntity()` with
  position/velocity. Camera hits (`MyDetectedEntityInfo.Relationship`) say whether
  a grid is hostile. Detection ranges of the AI blocks are unknown: test in game.
- Basic task block: UI strings for "Follow Player" / "Follow Home" exist; whether a
  script can switch it on and how far "follow player" reaches is unknown. Idea for
  pickup: fly to the player's GPS, then let the block follow for the last stretch.
- Scripts cannot read the player's position when the player is not in a seat of
  the grid: the reliable way is a GPS ("Add from current position") pasted into
  the console.
- `ApplyAction(name)` throws if the block does not offer that action to scripts:
  use `GetActionWithName` and check for null. The jump drive's "Jump" action is not
  available to scripts (the pilot has to press it).
- The stub compile does not check the PB whitelist (e.g. `IFormatProvider` is
  prohibited in game); the in-game "Check code" is the only whitelist test.

## Tooling to reuse from Acceleration Control

- `tools/gen_stubs.py` (generates C# API stubs from the MDK-SE wiki clone),
  `tools/build.py --check` (merges an MDK2 project into one paste-ready file in
  `dist/`, minifies over 100k, compile-checks against the stubs; builds several
  projects), `tools/SyntaxCheck/Minifier.cs` (Roslyn: renames own symbols, drops
  `readonly`, `var`, static wrappers, cached static values, merged field
  declarations; only uses types the script already names).
- Container setup: `apt-get install -y dotnet-sdk-8.0`; nuget.org is reachable,
  steamcommunity.com is not.
- Workshop material generators (`workshop/diagrams.py`, `map_mockup.py`,
  `cover.py`, Pillow) and BBCode descriptions (8000 character limit).

## Lessons from in-game testing (condensed)

- Camera raycasts: charge 2 km of range per second per camera; often miss
  asteroids beyond ~6 km; they DO hit the own ship through the hull - filter every
  hit (all grids of the own construct, and ships docked onto this one).
- Thrust direction of a thruster is `WorldMatrix.Backward`.
- Plan heavy work for the start of a tick and spread long loops over ticks
  (planning twice in one tick, drawing thousands of route dashes and fitting
  thousands of scan points in one tick hit the instruction limit).
- Wrap `Main` in try/catch and release all thruster/gyro overrides on errors.
- The game calls `Save()` only on world save: write `Storage` when data changes.
- Physics-based speed planning instead of fixed cutoffs (sqrt(2 a d) braking,
  backwards-planned corner speeds, thruster lag ~0.5 s): fixed steps overshot.
- Learn what can be measured (gyro torque, falloff of gravity, consumption)
  instead of settings; settings only until measured.
- Things checked once a second can miss short states (a script dock ended before
  the docking check ran and was taken for a foreign connection).
- Give every automatic action a reason the player can read; silent decisions were
  the ones the user found confusing.
- RSS planet zones (teleports with own coordinates) could not be detected reliably
  from a PB; zones are experimental and off. The user asked the RSS author for a
  PB API (zone id, frame conversion, bodies).

## The user's ships (for examples and tests)

- A plane-like ship with inverted-V wings reaching far behind the hull (landing
  checks must cover the wings; cameras under the wings).
- A large miner (gyro overshoot on 180 degree turns before torque learning).
- "Condor" / "Caldev": ships the screenshots came from; earth base with a recorded
  dock way, NPC outposts on planets.

## First tasks in the new repository

1. Set up the MDK2 solution with shared code and the build/stub tooling.
2. Define the IGC protocol and the blackboard.
3. Add an IGC command interface to the navigation module (from Acceleration
   Control).
4. Build the console + parser with a test harness that runs outside the game
   (compile the parser as plain C# and feed it sentences).
5. Log screen and question/answer flow; first missions: "go to", "mine X until
   full and return", "pick me up at GPS in N minutes".
