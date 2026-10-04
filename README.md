# Acceleration Control

An in-game programmable block script for **Space Engineers 1** that limits the
acceleration your ship produces when you press `W` `A` `S` `D` `Space` `C`.

In vanilla Space Engineers every movement key fires the thrusters at 100 %.
Ships built with a high thrust-to-weight ratio (so they can still lift off
when fully loaded) become twitchy when empty. This script takes over the
thrusters while a movement key is held and produces a fixed, adjustable
acceleration instead — in m/s², independent of cargo mass.

## Features

- Adjustable acceleration limit in m/s² (or in g), changed from the cockpit toolbar
- Compensates ship mass and natural gravity: pressing `C` on a planet descends at
  the chosen rate instead of dropping, `Space` climbs at the chosen rate
- Works in all six directions, relative to the cockpit/remote control you are using
- Optional limited dampeners: braking also uses the limit instead of full thrust
- Shows the limit and the maximum possible acceleration per direction on LCDs or a
  cockpit screen
- **Cruise** (drive assist): holds a constant forward speed, e.g. 0.75 m/s for drilling,
  and cancels sideways drift caused by uneven mining
- **Approach** (drive assist): aim at an asteroid, scan it with a camera, and the ship
  flies there at full speed and brakes in time to stop a set distance (default 75 m)
  before the surface
- **Ship status**: cargo fill level with ore breakdown, battery, uranium and hydrogen
  with remaining time, jump drive charge, and the remaining **delta-v** with the
  number of trips it allows
- **Ore map**: deposits marked with a camera scan or logged automatically while mining,
  shown as a 3D radar and as a list, with GPS export and toolbar-driven buttons
- **Planets**: flights on and around planets (climb, follow the curvature, descend
  above the target, ship kept level), atmosphere speed limit, wind/drag/lift
  compensation, and support for the Real Solar Systems mod (planet zones with their
  own coordinates, flights across zone changes)
- **Landing**: `land` scans the ground under the whole ship with the cameras, picks a
  flat and even spot (searching around if needed), lands level or along a gentle
  slope and locks the landing gear
- Settings survive saving/reloading the world

## Setup

1. Build a programmable block on the ship.
2. Open it, click **Edit**, paste the full content of
   [`dist/AccelerationControl.cs`](dist/AccelerationControl.cs) and click **Check code**, then **OK**.
3. Put the programmable block on your cockpit toolbar twice, using the **Run**
   action with these arguments:
   - slot 1: `up`   → increase the limit
   - slot 2: `down` → decrease the limit
4. For **Approach**, build a camera facing forward (same direction as the cockpit).
   If you have several, add `[Accel]` to the name of the one to use.
5. Optional displays, by adding a tag to an LCD panel's name:
   - `[Accel]` flight control page, `[Accel Status]` ship status page
   - `[Accel Map]` ore map with buttons (radar or list view), `[Accel List]` ore list
   - To use a cockpit screen instead, set `CockpitSurface`, `StatusCockpitSurface`,
     `MapCockpitSurface` or `ListCockpitSurface` in the Custom Data to the screen
     index (counted from 0).
6. For the map buttons, add one toolbar slot with **Run** and the argument `ui`.
   It toggles the UI mode, in which the movement keys operate the menu
   (see Buttons below).
7. Optional: more cameras pointing in different directions make the background
   survey find asteroids faster.

> **Why not the mouse wheel?** The programmable block API does not expose the
> mouse wheel (in a cockpit it only cycles toolbar slots). Scripts can read the
> movement keys, mouse look and roll, but nothing else, so toolbar buttons are
> the closest option.

## Commands

Run the programmable block with one of these arguments:

| Argument            | Effect                                                   |
|---------------------|----------------------------------------------------------|
| `up [step]`         | Increase the limit by `Step` (or by the given amount)    |
| `down [step]`       | Decrease the limit by `Step` (or by the given amount)    |
| `set <value>`       | Set the limit, e.g. `set 7.5` (m/s²) or `set 0.5g`       |
| `reset`             | Back to `DefaultAcceleration`                            |
| `on` / `off` / `toggle` | Enable or disable the script (off = vanilla thrust)  |
| `dampeners [on/off]`| Toggle limited dampeners                                 |
| `cruise`            | Toggle cruise at the current cruise speed                |
| `cruise <m/s>`      | Set the cruise speed and start cruising, e.g. `cruise 0.5` |
| `cruise up` / `cruise down` | Change the cruise speed by `CruiseStep`          |
| `cruise on` / `cruise off` | Start / stop cruise                               |
| `approach`          | Scan straight ahead with the camera and fly to the target; if nothing is in range, search along the line of sight |
| `stop`              | Cancel cruise or approach                                |
| `calibrate reset`   | Forget the measured fuel efficiency (see Ship status)    |
| `mark <ore>`        | Scan straight ahead and map the hit point as a deposit, e.g. `mark iron` |
| `mark <ore> here`   | Map the current position (drills, or the ship)           |
| `mark base here`    | Store the current position as the base (e.g. while docked) |
| `select next` / `select prev` | Select the next / previous deposit             |
| `route`             | Plan a route to the selected entry and show it on the map |
| `goto`              | Fly to the selected entry along a planned route (stops `ApproachBuffer` before it); long distances start with a jump |
| `goto GPS:name:x:y:z:` | Fly to GPS coordinates (paste a GPS from the game's GPS list); stops in front of the surface if the point is inside a rock |
| `dock`              | Fly to the nearest known dock (within 20 km) and dock; a dock is learned by docking there once by hand |
| `land` / `land GPS:name:x:y:z:` | Land below the ship / fly to the GPS and land there (see Landing) |
| `water here` / `water off` | Store the ship's height as this planet's water surface (water mod) / forget it |
| `undock`            | Disconnect and back off from the base                    |
| `delete`            | Delete the selected deposit                              |
| `filter [<ore>/all]`| Show only one ore; without argument: next ore            |
| `zoom in` / `zoom out` | Change the radar range (1 km to 200 km)               |
| `view radar` / `view list` | Switch the `[Accel Map]` screen                   |
| `ui`                | Toggle the UI mode (movement keys operate the map menu)  |
| `ui left/right/up/down/ok/back` | Operate the map buttons directly (see Ore map) |
| `map import`        | Merge GPS / MAP lines from blocks tagged `[Accel Import]` and from map screens of docked ships |
| `map send`          | Broadcast the map over antennas; other ships and stations running this script merge it |
| `map clear confirm` | Delete all deposits and known obstacles                  |
| `reload`            | Re-read Custom Data and rescan blocks                    |

## Configuration

The script writes its options into the programmable block's **Custom Data** on
first run. Edit them there and run `reload`.

| Key                  | Default   | Meaning                                                        |
|----------------------|-----------|----------------------------------------------------------------|
| `DefaultAcceleration`| `5`       | Limit used on first start and by `reset` (m/s²)                |
| `Step`               | `1`       | Change per `up` / `down` (m/s²)                                |
| `MinAcceleration`    | `0.5`     | Lowest allowed limit (m/s²)                                    |
| `MaxAcceleration`    | `50`      | Highest allowed limit (m/s²)                                   |
| `LimitDampeners`     | `false`   | Start with limited dampeners enabled                           |
| `DampenerGain`       | `1.5`     | How hard limited dampeners brake relative to speed (1/s)       |
| `LcdTag`             | `[Accel]` | LCD panels with this text in their name show the control page  |
| `StatusTag`          | `[Accel Status]` | LCD panels with this text in their name show the status page |
| `CockpitSurface`     | `-1`      | Cockpit screen index for the control page, `-1` = off          |
| `StatusCockpitSurface` | `-1`    | Cockpit screen index for the status page, `-1` = off           |
| `CruiseSpeed`        | `0.75`    | Cruise speed on first start (m/s)                              |
| `CruiseStep`         | `0.25`    | Change per `cruise up` / `cruise down` (m/s)                   |
| `VelocityGain`       | `2`       | How firmly cruise/approach correct speed errors (1/s)          |
| `CameraTag`          | `[Accel]` | Camera with this text in its name is used for scanning         |
| `ScanRange`          | `15000`   | Maximum scan distance (m)                                      |
| `ApproachBuffer`     | `75`      | Distance to stop before the scanned surface (m)                |
| `ApproachFullThrust` | `true`    | Approach uses 100 % thrust; `false` = respect the limit        |
| `MaxSpeed`           | `100`     | Top speed used by approach (the game's speed limit)            |
| `BrakeSafety`        | `0.8`     | Fraction of the braking thrust that approach plans with        |
| `HydrogenThrustPerLiter` | `1400` | Start value for hydrogen efficiency (N·s per liter), calibrated in flight |
| `UraniumMWhPerKg`    | `1`       | Start value for reactor fuel energy (MWh per kg), calibrated in flight |
| `ElectricThrustPerMW`| `120000`  | Fallback for electric thrusters if their power use cannot be read |
| `ScreenTextScale`    | `1`       | Text size on the sprite screens (map, list, status), 0.5 to 2; larger text may not fit everywhere |
| `MapTag`             | `[Accel Map]` | LCD panels with this text show the ore map with buttons    |
| `ListTag`            | `[Accel List]` | LCD panels with this text show the ore list               |
| `MapCockpitSurface`  | `-1`      | Cockpit screen index for the ore map, `-1` = off               |
| `ListCockpitSurface` | `-1`      | Cockpit screen index for the ore list, `-1` = off              |
| `AutoLogMining`      | `true`    | Log deposits automatically when new ore arrives while drilling |
| `LogStone`           | `false`   | Also log stone                                                 |
| `MergeDistance`      | `150`     | Entries of the same ore closer than this are treated as one (m) |
| `GravityWellFactor`  | `1.7`     | Gravity well size relative to a scanned planet's radius        |
| `Survey`             | `true`    | Cameras scan the surroundings in the background for asteroids (not while docked) |
| `SurveyRange`        | `6000`    | Range of the background scans (m)                              |
| `SearchRange`        | `50000`   | How far an approach searches along the line of sight (m)       |
| `AlignShip`          | `true`    | Turn the ship along its route with the gyroscopes              |
| `CollisionGuard`     | `true`    | Scan the path ahead during flights and react to obstacles      |
| `AvoidGravityWells`  | `true`    | Routes go around planet gravity wells (unless the target is inside) |
| `ImportTag`          | `[Accel Import]` | Blocks with this text in their name are read by `map import` |
| `UseJumpDrive`       | `true`    | GO and dock use the jump drive for long distances              |
| `JumpMinDistance`    | `20000`   | Jump only if the target is at least this far away (m)          |
| `JumpArrival`        | `3000`    | The jump ends this far before the target; the rest is flown (m) |
| `JumpClearance`      | `1000`    | Minimum distance of the jump destination from known obstacles (m) |
| `DockApproach`       | `30`      | Distance in front of the base connector where docking starts (m, plus ship radius) |
| `UseStrongestThrusters` | `true` | Turn the ship so its strongest thrusters push along the flight, and flip for braking if worth it |
| `FlipTime`           | `30`      | Seconds planned for turning around before braking, until the ship's turning is measured |
| `PlanetZones`        | `false`   | Real Solar Systems (experimental): each planet zone has its own coordinates. While off, zones recorded earlier are ignored (everything counts as one space; takes effect when the script restarts) |
| `PlanetCruiseHeight` | `1500`    | Height above the ground (start, target, terrain seen) for flights on a planet (m); short hops fly lower |
| `AtmosphereHeight`   | `12000`   | Assumed top of the atmosphere above sea level until the ship has measured it (m) |
| `AtmosphereSpeed`    | `100`     | Speed limit inside an atmosphere (m/s); the ship brakes to it before entering, `0` = off |
| `GravityFalloff`     | `7`       | Gravity falloff exponent until measured (vanilla planets: 7; mods may use less) |
| `CompensateWind`     | `true`    | Measure wind, drag and lift and compensate them during flights |
| `WaterLevel`         | `0`       | Water surface above sea level (water mod); planet routes cruise at least 200 m above it (m) |
| `MaxSlope`           | `15`      | Steepest ground `land` accepts (degrees); from 3 degrees on the ship lands tilted along the slope |
| `MaxBump`            | `1`       | Largest bump or dip `land` accepts under the ship, relative to the fitted ground plane (m); about the ship's belly clearance |

## Drive assists

### Cruise

Point the ship at the rock and run `cruise` (or `cruise 0.5` for a specific speed).
The script holds exactly that forward speed and keeps sideways and vertical speed at
zero, so drilling resistance and bumps no longer throw the ship off course.
The direction follows the ship: turn the ship to change the drilling direction.

- `A`/`D`/`Space`/`C` still work while cruising to correct the position.
- `W` or `S` ends cruise, so you can always back out immediately.
- Cruise only runs while someone is in the cockpit.

### Approach

Aim the ship at an asteroid (the camera looks where the cockpit looks) and run
`approach`. The camera raycasts up to `ScanRange`, the script picks a point
`ApproachBuffer` meters in front of the surface and flies there: full
acceleration, up to `MaxSpeed`, then braking so it stops at that point.

If the scan finds nothing (asteroids far away are often not detected by raycasts
until the ship gets closer), the ship flies along the line of sight and keeps
scanning ahead. Its speed is limited so that it can always stop within the part
of the line that has been scanned clear. As soon as a scan hits something, the
normal approach takes over. After `SearchRange` without a hit, the ship stops.

While approaching, the map screens and the control page show the flight phase
(ACCELERATING, CRUISING, BRAKING), the distance to the stop point and the
current stopping distance. On the map, braking starts when the orange stopping
mark reaches the end of the distance bar.

- Any movement key cancels the approach immediately.
- Cameras need time to charge long scans (about 2 km per second). The approach
  starts with the range that is charged and keeps scanning ahead while flying.
- The braking plan is made for space. In strong gravity, braking downwards can be
  weaker than planned; lower `BrakeSafety` if you use it there.

## Ship status

The status page (`[Accel Status]` or `StatusCockpitSurface`) is drawn graphically:
a cargo card and a power & fuel card, side by side on wide screens and stacked on
square ones. It shows:

- **Cargo**: fill level of cargo containers, connectors and drills, total mass and the
  ores on board, largest first.
- **Battery**: charge, remaining time at the current drain, or the charging power.
- **Uranium**: reactor fuel and remaining time at the average consumption.
- **Hydrogen**: tank fill level, amount and remaining time at the average consumption.
- **Jump**: jump drive charge.
- **Delta-v**: how much speed change the fuel on board still allows, separately for
  hydrogen and electric thrusters, and the number of trips that makes.

In space there is no drag, so a trip costs about the same delta-v no matter how far
it goes: accelerate to `MaxSpeed` and brake again, i.e. `2 × MaxSpeed`. Gravity is
extra: lifting off a planet costs much more.

Delta-v is an estimate. It assumes all fuel goes into thrust:

- Hydrogen: liters on board × thrust per liter ÷ ship mass. The thrust per liter
  starts at `HydrogenThrustPerLiter` and is measured while hydrogen thrusters fire,
  so modded thrusters are handled too.
- Electric: stored energy (batteries plus uranium × `UraniumMWhPerKg`) × thrust per MW
  ÷ ship mass. The thrust per MW is read from the thrusters' info; the uranium energy
  is measured while the reactors run.

Values marked `*` are not calibrated yet. Run `calibrate reset` after changing the
thruster or reactor setup significantly.

## Ore map

Scripts cannot read the ore detector, so deposits get onto the map in two ways:

- **Mark**: point the ship's nose at an ore marker of the ore detector and press
  **MARK** (or run `mark <ore>`). The camera scans in that direction and stores the
  point where it hits the asteroid, i.e. the surface above the ore.
- **Automatic while mining**: when the drills are running and a new ore arrives in
  the cargo (containers, drills, O2/H2 generators) or in the drills, the drill
  position is logged. Ice counts like any ore, even when the gas generators use it
  up about as fast as it is mined. Entries of the same ore within
  `MergeDistance` are merged, so one deposit is not logged over and over.

Every scan also stores the asteroid or planet it hit. In addition, all cameras of
the ship take turns scanning their field of view in the background (`Survey`), so
asteroids you fly past appear on the map by themselves. Planets are measured
directly when the ship is in their gravity. The map shows them as obstacles and
gravity wells, and **GO** refuses to fly straight through a known asteroid.

The **base** is marked like an ore: choose *Base* in the MARK list, or run
`mark base here` while docked. It is shown as a square.

**Recorded way to a deposit.** While the ship moves, the script keeps its recent
poses (position and orientation). `mark <ore> here` and the first automatic log of
a mining session store the last 300 m of the way in with the deposit. **GO** on such
a deposit flies to the start of that way and then follows it slowly (8 m/s) in the
recorded orientation, so the ship arrives on the right side of the asteroid, facing
it as when mining, and stops about 5 m before the recorded end. The cameras look
ahead along the way; if something blocks it for 10 seconds, the ship stops and asks
the pilot to take over (useful for very jagged asteroids). Deposits logged before
this feature have no way stored; they are approached as before.

### Screens

- **Radar** (`[Accel Map]`): a plane through the ship that turns with it, forward is up.
  Deposits sit on stems that show how far above or below the ship they are. Grey
  spheres are known asteroids, violet areas are gravity wells. Deposits beyond the
  range appear as small markers on the edge. On wide screens (1.5:1 or wider) the
  radar is on the left and the info panel and buttons on the right, drawn about
  twice as large as the square layout would be. Wide screens with a low resolution
  (below 200 px high, e.g. cockpit screens that only have a 256 px texture) get a
  compact layout: three large lines about the selection or the flight, and only the
  selected entry labelled on the radar. The programmable block's detail info shows
  the screen's resolution.
- **List** (`[Accel List]`, or the `[Accel Map]` screen after pressing LIST): deposits
  sorted by distance, with the direction relative to the ship's nose (degrees
  left/right and up/down, plus a small indicator).
- Both show the selected deposit, whether the direct path is clear and the delta-v
  of the trip; during a flight the remaining distance, stopping distance and an ETA
  (leg by leg with the planned waypoint speeds, `MaxSpeed` or `AtmosphereSpeed` in
  air, planned braking and the turn of a flip; climbs in gravity count the weight).
- The status screen shows the current acceleration limit (`up`/`down`) in the title
  line of the power card.
- The Custom Data of every map screen contains all deposits as GPS lines. Copy them
  and use **Paste from clipboard** in the game's GPS menu to get HUD markers.

### Buttons

The map screens have a button row. Run `ui` (one toolbar slot) to switch to
**UI mode**: the ship holds its position and the movement keys operate the menu:

| Key | Action |
|-----|--------|
| W / S | Select the previous / next entry |
| A / D | Highlight the previous / next button |
| Space | Press the button (OK) |
| C | Close a dialog; outside a dialog, leave UI mode |

Held keys repeat. `UI MODE` is shown in the screen header while it is active.
Scripts cannot read other keys (arrow keys, Enter, Ctrl), so these are the
movement keys. The actions are also available as commands (`ui left`, `ui right`,
`ui up`, `ui down`, `ui ok`, `ui back`) for toolbar slots.

| Button | Action |
|--------|--------|
| LIST / MAP | Switch between list and radar view |
| MARK   | Pick an ore (or *Base*), then scan straight ahead and map the hit point |
| ROUTE  | Plan a route to the selected entry and show it on the map |
| GO     | Fly to the selected entry along the route |
| ZOOM   | Next radar range |
| FILTER | Show only one ore, cycling through the mapped ores |
| DELETE | Delete the selected deposit (asks for confirmation) |

## Navigation

**ROUTE** plans a route to the selected entry and draws it on the radar, with its
length, delta-v and flight time. **GO** plans and flies it.

- **Leaving a hangar**: before a flight turns the ship (towards the route or to
  align for a jump), the cameras check a sphere around it (ship radius + 5 m). Hangar
  walls and other ships are not on the map, so this is the only way to know. If
  something is inside, the ship first moves straight out without turning, in the
  ship direction that the cameras see clear for three ship radii plus
  `ApproachBuffer`, preferably backwards, and checks again afterwards. If no
  camera sees a direction clear, the way the ship came in is used; otherwise the
  flight stops with "fly out by hand". Rear and side cameras make this work in
  every direction.
- **Leaving a rock**: if the ship is next to an asteroid or a deposit (e.g. after
  mining), it first moves straight out at 10 m/s without turning: backwards if that
  leads away from the rock, else directly away or along another ship axis. The way
  must be clear on the map (another rock may be right behind the ship) and seen
  clear by the cameras. If no camera looks that way, only the way the ship came in
  is used (it just passed there, e.g. backing out of a mine it drilled forward
  into); otherwise the flight stops and asks to move away by hand. Only then does
  it turn, plan and fly or jump.
- **Charged cameras**: a camera that looks the right way but has not charged enough
  range yet (the background survey uses it up) makes the ship wait a second and look
  again, up to ten times, instead of moving without seeing. The survey pauses
  meanwhile. Landed on a planet with only the ground close by, the way out is
  straight up, also without a camera looking up.
- **Camera checks for straight moves**: every camera facing the way looks straight
  along it from where it sits on the hull (cameras offset to the sides are fine), plus
  rays to the ship's centre line and four lines around it. One camera facing the way
  that sees it clear is enough.
- **Planning**: if a known asteroid (or a planet's gravity well) is in the way, the
  route gets a waypoint beside it, keeping the ship's radius plus `ApproachBuffer`
  of distance. If the ship would drift too far in the turn at that waypoint (from
  the planned speed and its actual weakest sideways thrust), the waypoint is moved
  further out instead of slowing down (up to 5 km).
- **Asteroid size**: asteroids are irregular. Their size starts as half the size of
  their voxel box and grows whenever a camera ray hits rock outside it (background
  survey, collision guard, scans), so the known size only ever gets more accurate. Several obstacles give several waypoints. If no complete route is
  found (very dense fields), GO refuses instead of flying a risky path.
- **Flying**: full thrust up to `MaxSpeed`. Waypoints are passed without stopping.
  Their speeds are planned backwards from the end of the route: each waypoint is
  passed only as fast as the ship can still slow down for the rest of the route,
  and slower for sharper turns. The ship can therefore always stop at the end,
  even if the last waypoint is just before the target. The gyroscopes turn the ship so that its strongest
  thrusters push along the route (`UseStrongestThrusters`; otherwise the nose points
  along the route).
- **Rough arrival**: waypoints only need to be hit roughly. A waypoint counts as
  passed within 50 m (more at high speed) or as soon as the ship has crossed the
  plane between its incoming and outgoing leg, so the ship never turns back for it.
  The end of a flight counts as reached within 10 m or a quarter of
  `ApproachBuffer` (whichever is larger; 10 m before docking) once the ship is
  slower than 1 m/s. A ship that overshoots the end point within that distance
  just stops where it is.
- **Flip and burn**: if the side that would brake is much weaker than the strongest
  side, the ship turns around for the final braking and brakes with its strongest
  thrusters. The turn is given the time the ship needs to turn around, computed
  from its measured turning (see below; `FlipTime`, default 30 s, until that is
  measured): braking starts earlier
  by the distance flown in that time, which also lowers the top speed on short trips
  that never reach `MaxSpeed`. The script compares the trip time with and without
  turning around and only flips when it is faster.
- **At the target**: the stop point keeps `ApproachBuffer` plus the ship's radius
  (its bounding sphere) from the surface, measured from the ship's center, so the
  ship can turn safely there. Near the stop point it holds its heading; once slow,
  it turns its nose to the target (e.g. for drilling).
- **Turning**: the script measures how fast the gyroscopes can speed up and slow
  down the ship's rotation, per axis, whenever they turn at full torque (the
  shape of the ship matters, so each axis is learned on its own; cargo is taken
  into account through the mass). Turns then slow down in time instead of
  overshooting, and the flip time above is computed from it.
- Docking, cruise, leaving a rock and all manual movement never turn the ship; all
  thrusters work together there. Turning the ship yourself
  takes over the gyroscopes; any movement key cancels the flight.
- **Collision guard**: during the flight the cameras scan the path ahead, with a
  center ray and a ring of rays at the ship's radius, as far as the stopping
  distance. If the target rock sticks out further than scanned (a protrusion
  beside the scanned point), the stop point moves closer. An unknown asteroid or a
  grid on the path makes the route go around it (planned in the next tick). Closer
  than about 1.3 stopping distances, the ship dodges sideways past it if that needs
  clearly less than its sideways thrust (often cheaper than stopping, and the only
  way when stopping in time is impossible); otherwise it stops in front of the
  obstacle, moves away from it and plans again. If stopping in time is impossible
  and the sideways thrust is not enough, the ship **evades**: it turns its strongest
  thrusters sideways and pushes aside and back at full thrust (ignoring the
  acceleration limit) until its path clears the obstacle, then plans on. The guard
  looks at most 8 km ahead: rays rarely hit rocks farther away, and longer rays
  need more camera charge, so at high speed it scanned too rarely.
- **Live replanning**: an asteroid found later (by the guard or the background
  survey) that lies on the rest of the route triggers a new plan from the current
  position.
- **Gyroscope directions**: the script checks the direction of the gyroscope
  overrides against the measured rotation and corrects it by itself. Until that is
  done (usually within the first second of the first turn), it turns slowly.
  `calibrate reset` repeats this.

## Planets

### Flying on a planet

When the start or the target of a flight lies in a planet's gravity well and the
direct line would pass lower than the cruise height, the route:

1. climbs straight up to the cruise height (`PlanetCruiseHeight` above the higher of
   the ground under the ship and the target; for short hops a quarter of the
   distance, at least 200 m),
2. follows the curvature of the planet at that height (waypoints at most 100 m below
   the cruise sphere) and heads straight for the point above the target as soon as
   that line clears the planet. Long flights go 500 m above the atmosphere instead if that is
   faster (climb and descent at `AtmosphereSpeed`, the rest at `MaxSpeed`). Coming
   from higher up (from space, or after entering a planet zone in orbit) the ship
   flies straight to the farthest point of that arc it can see past the planet (a
   tangent) instead of circling at its height,
3. descends vertically above the target and stops `ApproachBuffer` plus the ship's
   radius above it. Targets high above the planet (above the atmosphere and more
   than 10 % of the radius above sea level, e.g. asteroids in a large gravity well)
   are flown to directly once the planet is out of the way.

The ship flies level (its up side against gravity, only the nose turning towards
the flight direction, no flip-and-burn or strongest-thruster orientation) inside
an atmosphere, or where gravity is more than 30 % of what its weakest thrust side
can push. In weaker gravity (e.g. high above a planet with a wide gravity well) it
is flown like in space. Climbs and descents in level flight use the up/down
thrusters. Braking on a descent is planned with the upward thrust
minus gravity. If the collision guard sees terrain ahead, the route is planned again
higher. Leaving a mine on a planet starts with a straight climb.

All planet heights are geodetic: distances from the planet's center, which the game
reports exactly together with the sea level radius. The cruise height is a sphere
around the center, so a deep sea floor under the route does not pull it down: it is
at least `PlanetCruiseHeight` (short hops: a quarter of the distance, min. 200 m)
above the highest of

- the ground under the ship at the start and the target point (its distance from
  the center),
- terrain the collision guard has seen on this flight,
- the water surface (water mod; raycasts do not see water): per planet with
  `water here` (float on the water or hover just above it and run it once), else
  `WaterLevel` above sea level.

Final descents end above the target.

Flights in gravity only start if the ship's upward thrusters give at least 1.1 times
the local gravity. Locked landing gear is unlocked while a flight is under way, so
the ship does not pull against it (and auto-lock does not catch the ground again).

### Atmosphere

Inside the atmosphere the speed is limited to `AtmosphereSpeed`; above it, a flight
that goes down into the atmosphere slows down in time to enter at that speed. The
top of the atmosphere is measured with the ship's atmospheric thrusters (they gain
thrust in air) or ion thrusters (they lose thrust) and stored per planet. Until then `AtmosphereHeight` above sea level is assumed.

### Wind, drag and lift

During flights the script measures the ship's acceleration and subtracts what the
thrusters (their actual output) and gravity explain. The rest is the external
acceleration: wind, aerodynamic drag, lift from wings. It is filtered (1 s) and
compensated like gravity, so the ship holds course and speed in wind and the
thrusters only add what the wings do not carry. Not used while cruising: drilling
pushes back, and compensating that would push the ship into the rock when the drills
break through. The control page shows it as
"Wind/drag". `CompensateWind=false` turns it off.

### Landing

`land` sets the ship down below where it is; `land GPS:...` flies to the GPS first
(like `goto`) and lands there. Needs gravity, landing gear and cameras that look
down.

1. The ship moves to a point above the ground (from the game's surface height)
   where a camera under its middle sees the edges of the footprint: half the
   footprint's width + half the ship's height + 5 m for the ship's centre. It
   holds its heading.
2. The cameras scan the ground under the ship's **whole footprint**: its bounding
   box seen from above, plus 3 m all round, on a grid of rays 1.2 m apart (a
   20 x 30 m footprint: about 500 rays; large ships get wider spacing, at most
   about 2500 rays). As many rays per tick as the cameras have
   charged (2 km of range per second each), so it takes a few seconds with several
   cameras and up to a minute or more for a large ship with one; the control page
   shows the progress. Wings, outriggers and anything else that sticks out are inside the
   box, so they are covered too (the rays must reach them: put cameras under the
   wings of wide ships).
3. A plane is fitted through the hits. The spot is taken if
   - the cameras saw at least 3/4 of the points (else the landing stops and asks for
     cameras facing down),
   - no ray went through without hitting ground (a drop or a hole),
   - no hit lies more than `MaxBump` (1 m) above or below the plane (boulders,
     trees, parked ships, ledges),
   - the slope is at most `MaxSlope`, and for a tilted landing the ship's weakest
     side can push 1.2 times the part of gravity along the slope.
   Otherwise the next spot on a spiral around the target is tried (up to 12, spread
   by about the ship's size); the reason is shown ("slope 22°, uneven 1.8 m").
4. Flatter than 3 degrees the ship stays level, else it turns its up side to the
   ground's normal. Then it descends straight down, as fast as it can brake, near the
   ground at most 0.3 m/s + 0.3 x the height, the last metre at 0.3 m/s, holding its
   place sideways.
5. Landing gear that is ready to lock is locked; then the flight ends ("Landed").

To take off, use `goto`/GO: flights unlock the landing gear and start upwards.

Simulated (scratchpad `rocksim.py`, `landscan.py`): with rays 1.2 m apart every
boulder from 3 m across is hit, from 2.5 m 97 % (with 4 m spacing only 12-59 %).
On 600 random hilly spots with one boulder of 2-6 m each, no boulder taller than
1.25 m was missed (the missed ones were 1.0-1.2 m high, at the `MaxBump` limit); a
4 m grid missed 52 of 232. On hilly ground a large ship finds about half of the
spots even enough, so the spiral search matters. The plane fit finds slopes within
0.3 degrees; the descent
touches down at about 0.3 m/s with 0.5-8 m/s² of braking and 0.5 s thruster lag
(without the cap near the ground up to 3.7 m/s). Flown in game several times,
also on a slope.

Not covered: the ship's sides above the ground (a wing next to a cliff wall or a
tree taller than the ship's belly beside the footprint margin), rocks smaller than
about 2.5 m across (between the rays), and ground that moves (water, other ships).

### Real Solar Systems (experimental, off by default)

> **Experimental.** Scripts cannot see the mod's zones; the script guesses them from
> teleports and gravity, and nested zones (a moon inside its planet's zone, orbit
> and surface zone) still confuse it: flights between zones can go the wrong way.
> The zone support is off unless `PlanetZones=true` is set in the Custom Data. With
> it off, a teleport (any position jump the jump drive does not explain, e.g. from
> a zone change, a star gate mod or a carrier ship jumping) simply stops the flight.
> The script's author asked the mod's author for a scripting interface.

With this mod the planets you see move, but the real planets are static and far
away; approaching a planet teleports the ship into that planet's zone, which has its
own coordinates. The script detects teleports (a position jump that the velocity and
the jump drive do not explain):

- Map entries, obstacles and the base remember the zone they were recorded in. The
  radar shows only the current zone; the list shows entries of other zones with
  "other zone" instead of a distance. GPS export only contains the current zone.
- A teleport ends the current flight. Within the same planet zone (e.g. between
  orbit and surface) the route is simply planned again.
- GO/dock to an entry in another zone: in a planet zone the ship climbs straight up
  until the zone changes. In space the script cannot see the moving proxy planets
  itself, so fly there yourself (the `track` command that followed a planet's moving
GPS was removed to make room for landing). As soon
  as the ship is in the target's zone, the flight continues automatically. The
  control page shows "Waiting for the zone of …".
- The mod's zone change can change the ship's velocity (a planet "running into" a
  resting ship). Keep dampeners on when entering a zone by hand. If the ship then
  falls faster than its upward thrust can stop above the ground, the control page
  shows a warning. Simulated: a ship with 1.5 g of upward thrust entering 60 km up at
  1500 m/s cannot be saved; at 500 m/s, or with 3 g, all runs stopped safely.

The control page shows any measurable gravity, also in space without a real planet
("no planet"). That shows whether the proxy planets have a gravity scripts can see.

Each planet has two zones around the same centre but with different coordinates: an
outer **orbit zone** and, inside it, a **surface zone**. A teleport between them
towards the planet leads into the surface zone; its edge is learned then and used
afterwards (e.g. after a restart). GO between the two climbs straight up or goes
straight down until the zone changes. Entries recorded by an older version in a
surface zone are labelled as orbit zone: mark them again.

The zone of a planet reaches farther out than its gravity. A teleport outside
gravity is assigned to the nearest known planet (within 1.5 times its gravity
radius plus 50 km), e.g. a base in space near the Moon belongs to the Moon's zone.
If no planet is known yet, entries recorded there are marked provisionally and
relabelled as soon as the ship reaches that planet's gravity without another
teleport. The same holds after the script starts outside gravity (a zone
change while it was off cannot be seen): the zone it remembers counts as
provisional until the gravity of a planet confirms or corrects it. If the base was recorded in the wrong zone by an earlier version, dock by
hand once more.

Set `PlanetZones=true` before recording entries in such a world, otherwise they
count as space. The gravity falloff of the mod's planets is measured in flight
(`GravityFalloff` is only the start value), so the size of the gravity wells that
routes avoid in space is estimated correctly.

## Jump drive

During a flight the script jumps as soon as it can: when the ship leaves a planet's
gravity (no jumps inside it) or the target changes, and the current leg is at least
`JumpMinDistance` long and would take more than two minutes to fly, it stops, aligns
and jumps (drive charged). A jump is recognised by the sudden position change, so it
also works when the ship was still moving when the jump was planned.

If the first leg of the planned route is longer than `JumpMinDistance` (and the
ship is outside gravity), GO first jumps along that leg, which is known to be clear: the ship stops, sets the jump distance, waits until a jump drive is ready,
turns its nose to the target (within 2 degrees; after 20 seconds within 5) and
jumps ("blind jump" along the nose). You can also press Jump yourself at any time
during this; the script notices the jump and continues. The jump ends
`JumpArrival` meters before the target, at a point at least `JumpClearance` away
from known asteroids and gravity wells (the distance is shortened if needed). After
the jump the rest is planned and flown as usual.

The game may not accept a jump started by a script. In that case the script keeps
the ship aligned with the distance set and shows **press JUMP on your toolbar**:
use the jump drive's Jump action from the cockpit toolbar (the jump goes where the
cockpit points, which the script keeps on target). After the jump it continues on
its own. The game may also refuse or shorten a jump near gravity or obstacles the
script does not know; if no jump happens within 90 seconds, the flight stops.

## Docking

1. Dock at the base by hand once. The script notices the connection and stores the
   dock pose: where the connector was, how the ship was oriented and where its
   grid was, plus all grids belonging to the base (including rotor and piston
   parts). The base entry on the
   map is set to that position. The last 300 m of the way in are stored too,
   relative to the base grid (see *Recorded way in* below).
2. From then on, **GO** on the base (or `dock`) flies there, jumping if far, and
   stops at an approach point in front of the connector (twice the ship's radius
   plus `DockApproach`), far enough out to turn without touching the base.
3. **Space to turn**: the cameras check the space around the ship (its bounding
   sphere) for other ships and players before it turns. If something is there,
   the ship waits.
4. The ship turns into the stored orientation.
5. **Way in**: the cameras check the path into the dock. Only what lies in the
   space the ship actually sweeps through counts: the ship's own shape in the
   docked pose, moved out along the connector axis, and only the part still ahead.
   Rock counts only if it is clearly inside that space (the docked pose itself was
   free), so bases built into asteroids work. Ships next to the path (e.g. at
   neighbouring connectors) do not block. If something is in the path, the ship
   waits in place and the message says what it is and how far away.
6. The ship moves in slowly along the connector axis, correcting sideways drift,
   and keeps scanning the rest of the way; if something shows up, it stops and
   waits. When the connector is ready, it connects.
7. `undock` disconnects and backs off along the connector axis.

On the flight to the base, the collision guard ignores the base and the rock it
stands on (docking has its own checks), unless the ship could no longer stop in
front of them. Waiting before turning ends after 2 minutes; on the way in the ship
waits until the way is clear or you take over. The
checks use all cameras that can see the respective points; cameras pointing
towards the connector side of the ship make them more complete. Sensors with
`[Accel]` in their name are also used during the final approach (set their range
yourself). Parts of the base never count as obstacles.

**Several bases.** Every base keeps its own dock: docking by hand at another base
adds (or updates) a base entry on the map with that dock, its recorded way in and
its gate connector, and the others are kept. **GO** on a base entry docks there;
`dock` takes the nearest base with a dock in the current zone within 20 km; if
there is none, it refuses (teach the dock by hand first, or use GO on a base entry
for a far base). `undock` uses the dock the ship is at. **GO** or `goto` while docked undocks
first (along the recorded way out, if there is one) and then starts the flight.

While docked, the ship's thrusters are switched off (the game's dampeners otherwise
kept firing in the docking direction after the lock). `undock` switches them on
again, and so does undocking by hand (within a second). `undock` also releases
locked landing gear and, with a gate, lets go of the connector only once the base
reports the gate open. One dock per base entry:
docking by hand at another connector of the same base replaces that base's dock.

**Recorded way in.** If the dock was recorded with a way in (the ship came at
least 20 m while the script ran), steps 2-7 are replaced: the ship flies to the
start of the recorded way and follows it in the recorded orientation, slowly, as you
flew it (1.5 m/s near the connector), which also works in tight hangars and from the
right side. It turns into each recorded pose before moving on, scans ahead along the
way and waits while something is in it; after 10 seconds blocked it stops and asks
you to take over. `undock` follows the same way backwards out. Docks recorded before
this feature have no way in: dock by hand once more to record it. The way is
recorded only while the script runs (the last 300 m before the connector locks, at
least 20 m); the message after docking says "Dock position and the way in saved".
Reloading the world or the script while docked keeps the recorded way. Only
connections made while this ship is piloted (or docking by script) count as docking:
a small ship docked onto this one is carried along, not taken for the base, and its
grid is ignored by the cameras. Connections that exist when the script starts count
as docked only at the known dock. Without a
way in, `dock` shows "No recorded way in" and uses the point in front of the
connector, which is wrong for hangars whose connector does not face the entrance.

On the last meters the ship moves its connector to the recorded place (slightly
into the other connector) until it locks; if it has not locked after 15 seconds,
the ship stops and says so. While the way into or out of the dock is blocked by a
part of the base (e.g. a gate that a sensor opens), the ship waits up to a minute.

### Gates

A base can open a gate, hangar door, pistons, rotors or lights for the ship. Put the
companion script `dist/DockGate.cs` into a programmable block on the base; ship and
base need antennas in range of each other (relays work).

1. Build a timer block named **Dock Open** whose toolbar opens the way in (any
   actions: hangar doors, pistons, rotors, lights), and one named **Dock Close**
   that closes it again.
2. Optional: name the moving parts **Dock Gate** (or put them in a group of that
   name). The base answers "ready" once all of them have stopped moving (doors open,
   pistons and rotors at rest); without them it answers right away.
3. Several docks on one base: add a part of the connector's name, e.g. timers
   `Dock Open Hangar A` / `Dock Close Hangar A` and group `Dock Gate Hangar A`
   belong to the connector `Connector Hangar A`. Names without such a part belong
   to every connector.

`dock` asks the base to open when the flight starts and again before following
the way in; the ship waits at the start of the way until the base reports the gate
open (at most a minute; without an answer within two seconds it just goes on).
When the ship is docked it asks the base to close. `undock` opens the gate before
moving out and closes it at the end of the way out. Running the base PB with `open`
or `close` triggers the timers by hand. The gate stays open if a dock flight is
cancelled. Dock by hand once with this version, so the ship knows the base
connector to ask for.

**Base position.** All dock data is stored relative to the base grid. Whenever a
camera ray hits the base grid (background survey, collision guard, docking scans),
its current position and orientation are taken from the hit, so the dock is found
even when the base appears in other coordinates (e.g. another Real Solar Systems
zone frame) or has moved. Movement keys cancel docking at any time.

## GPS coordinates and waypoints

- `goto GPS:name:x:y:z:` flies to a GPS copied from the game (the text after `goto`
  can be pasted as it is, e.g. as a toolbar argument).
- To keep many coordinates on the map, paste them into the Custom Data of a block
  tagged `[Accel Import]` and run `map import`. GPS whose name is an ore or `Base`
  become deposits; any other name (e.g. `Asteroid 12`) becomes a **waypoint** that
  keeps its name. All of them can be selected and flown to with GO.
- If a point lies inside an asteroid (e.g. its center), the collision guard stops
  the ship `ApproachBuffer` in front of the surface.
- **Deleting an entry** (DELETE) only removes that deposit or waypoint. Known
  asteroids are stored separately and stay on the map as obstacles. Automatic
  logging adds an entry again only if new ore arrives while drilling there.

## Sharing the map

- The Custom Data of every map screen contains the map: `GPS:` lines for deposits
  and the base (paste them into the game's GPS list with "Paste from clipboard")
  and `MAP:` lines for known asteroids and planets.
- **Between ships by copy & paste**: put the text into the Custom Data of any block
  tagged `[Accel Import]` on the other ship and run `map import`.
- **Docked**: `map import` also reads the map screens of ships connected via
  connector.
- **Over antennas**: `map send` broadcasts the map. Every ship or station running
  this script within antenna range merges it automatically.

Entries that are already known (same ore within `MergeDistance`, same asteroid) are
not added twice.

With planet zones in use (Real Solar Systems) the export starts with a `ZONE:` line,
so the receiving ship files the entries under the right zone even if it is somewhere
else. GPS lines without it count as the current zone.

## How it works

Every tick the script reads the movement input of the controlled cockpit. For
each axis with input it computes the force needed for the target acceleration,

```
F = mass * (targetAcceleration - gravity along axis)
```

and sets the thrust override of the thrusters on that axis to deliver exactly
that force (capped at 100 %). The opposing thrusters get a near-zero override so
the game's dampeners do not fight it. When you release the key, the overrides
on that axis are cleared and the normal game logic (dampeners, drifting) takes
over again — unless limited dampeners are on, in which case the script brakes
at the limit until the ship is almost stopped.

## Notes

- Flights (approach, GO/goto, docking, cruise) go on when you leave the seat: the
  script then uses any cockpit or remote control of the ship as reference. The
  acceleration limit itself needs a pilot. A jump still waits for someone to press
  Jump if the game refuses the script's jump (cancelled after 90 s).
- If the limit is higher than what your thrusters can deliver, the thrusters
  simply run at 100 %, as in vanilla.
- Turning the programmable block **off** while flying can leave thruster
  overrides active. Use the `off` command instead, which clears them.
- The script overrides the thrusters on its own construct (including thrusters
  on rotor/piston subgrids that are aligned with the cockpit). Do not combine it
  with other scripts that also set thruster overrides.

## Limitations

Good to know before relying on the script. "Tested" means flown by the author in a
single player game (with a speed mod, Real Solar Systems, Aerodynamic Physics and a
water mod); everything else was checked with the compiler and simulations only.

**Tested in game**
- Acceleration limit, cruise, approach, ore map and screens, routes around
  asteroids, the collision guard (once also the emergency evasion: a ship parked at
  a base was seen at about 100 m/s and 100 m, the ship swerved past it), jumps (the
  pilot presses Jump when asked).
- Docking: recording by hand, `dock`/GO along the recorded way at a planet base and
  a space base, into a hangar, undocking and GO while docked, gates opened by the
  `DockGate` companion script.
- Planets: several flights from orbit down to the surface and hops in atmosphere.
- Landing: several landings, also tilted on a slope after the scan refused the
  first spots.

**Not or only briefly tested in game**
- Wind, drag and lift compensation runs during flights, its accuracy was never
  measured; water levels (`water here`) were hardly used.
- Real Solar Systems zones were tried in one save and were not reliable, hence
  experimental and off by default.
- Multiplayer and dedicated servers were never tried.

**Bases and docking**
- Bases must stand still while the ship docks. A base that was moved is found again
  once a camera ray hits it, but the ship does not follow a moving base.
- One dock per base entry on the map. The way in is recorded only while the script
  runs (last 300 m, at least 20 m flown by hand) and is replayed slowly (8 m/s);
  the ship does not steer around something new on it, it waits and then hands over.
- Gates need the `DockGate` script on the base and antennas in range.
- Close to a base among several asteroids, routes may keep less than
  `ApproachBuffer` from a rock (at least 50 m beyond the rock and the ship's radius)
  when no route with the full buffer exists.

**Sensing**
- The script only knows what its cameras have hit. Camera rays often miss asteroids
  farther than about 6 km, asteroids are approximated by spheres, and ships and
  stations are not on the map: the collision guard sees them only ahead of the ship.
  Few or badly placed cameras mean less protection.
- Landing needs cameras facing down that reach the whole footprint; rocks smaller
  than about 2.5 m across can lie between the rays, and nothing beside the
  footprint (a cliff wall next to a wing) is checked.
- Scripts cannot see water (water mod) or read wind; wind, drag and lift are
  estimated from how the ship reacts.

**Game limits**
- The game may refuse a jump started by a script: the pilot then has to press Jump.
- The script runs every tick and does a lot of work; many known asteroids cost
  instructions. It sets thruster and gyroscope overrides, so do not combine it with
  other scripts that do the same.
- The paste-ready script is minified to fit the 100,000 character limit (almost
  all of it used); the readable source is in this repository.

## Development

The script is an [MDK2](https://github.com/malforge/mdk2) project in
[`AccelerationControl/`](AccelerationControl), split into several files:

| File               | Content                                         |
|--------------------|-------------------------------------------------|
| `Program.cs`       | Entry point, block discovery, commands          |
| `ThrustControl.cs` | Acceleration limit via thruster overrides       |
| `DriveAssists.cs`  | Cruise and approach                             |
| `ShipStatus.cs`    | Cargo, fuel and delta-v monitoring              |
| `Displays.cs`      | LCD and cockpit screen output                   |
| `OreMap.cs`        | Deposits, obstacles, gravity wells, GPS export  |
| `MapDisplay.cs`    | Sprite rendering of radar and list              |
| `Menu.cs`          | Toolbar-driven buttons and dialogs              |
| `Navigation.cs`    | Route planning, gyroscopes, collision guard     |
| `Jumping.cs`       | Jump drive                                      |
| `Docking.cs`       | Automatic docking at the base                   |
| `Paths.cs`         | Recorded ways (docking, deposits), base pose, gates |
| `Planets.cs`       | Planet flights, atmosphere, Real Solar Systems  |
| `StatusDisplay.cs` | Graphical ship status page                      |
| `Config.cs`        | Custom Data configuration and saved state       |

[`DockGate/`](DockGate) is the small companion script for bases (gates, see
*Docking*), built into [`dist/DockGate.cs`](dist/DockGate.cs).

With Space Engineers installed, the project can be opened in Visual Studio or Rider
with MDK2 for full compiler checks and IntelliSense.

Without the game, `python3 tools/build.py` merges the files into the paste-ready
[`dist/AccelerationControl.cs`](dist/AccelerationControl.cs). If the result exceeds the
programmable block's limit of 100,000 characters, comments and indentation are
stripped automatically. If that is still too long, the script's own names are
shortened with the compiler (`tools/SyntaxCheck/Minifier.cs`, like MDK's full
minifier). The readable source is always in `AccelerationControl/`. `python3 tools/build.py --check` also checks the result
with the C# compiler (needs the .NET SDK):

- Always a C# 6 syntax check, the language version of the programmable block.
- After `python3 tools/gen_stubs.py`, a full compile against stubs of the game API.
  The stubs are generated from the API documentation in the
  [MDK-SE wiki](https://github.com/malware-dev/MDK-SE/wiki), so unknown members and
  type errors are found without the game. That documentation is from around 2022;
  API added later would be reported as an error.

**Check code** in the game remains the final check.

Always rebuild `dist/` after changing the source files.

## Credits

- Written with [Claude](https://claude.ai) (Anthropic's AI assistant, via Claude
  Code) following the author's design decisions and in-game tests. No code was
  taken from other scripts.
- Recording the way into a dock and replaying it is a well-known idea from
  dedicated docking scripts such as *Automatic Docking 2.0*; the implementation
  here is independent.
- Project structure: [MDK2](https://github.com/malforge/mdk2) by Malforge.
- The API checks in `tools/` use the API documentation of the
  [MDK-SE wiki](https://github.com/malware-dev/MDK-SE/wiki) (not part of the
  script) and the [Roslyn](https://github.com/dotnet/roslyn) C# compiler.

## License

[MIT](LICENSE)
