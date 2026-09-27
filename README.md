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
| `dock`              | Fly to the base and dock (after docking there once by hand) |
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
| `MapTag`             | `[Accel Map]` | LCD panels with this text show the ore map with buttons    |
| `ListTag`            | `[Accel List]` | LCD panels with this text show the ore list               |
| `MapCockpitSurface`  | `-1`      | Cockpit screen index for the ore map, `-1` = off               |
| `ListCockpitSurface` | `-1`      | Cockpit screen index for the ore list, `-1` = off              |
| `AutoLogMining`      | `true`    | Log deposits automatically when new ore arrives while drilling |
| `LogStone`           | `false`   | Also log stone                                                 |
| `MergeDistance`      | `150`     | Entries of the same ore closer than this are treated as one (m) |
| `GravityWellFactor`  | `1.7`     | Gravity well size relative to a scanned planet's radius        |
| `Survey`             | `true`    | Cameras scan the surroundings in the background for asteroids   |
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
  the cargo, the drill position is logged. Entries of the same ore within
  `MergeDistance` are merged, so one deposit is not logged over and over.

Every scan also stores the asteroid or planet it hit. In addition, all cameras of
the ship take turns scanning their field of view in the background (`Survey`), so
asteroids you fly past appear on the map by themselves. Planets are measured
directly when the ship is in their gravity. The map shows them as obstacles and
gravity wells, and **GO** refuses to fly straight through a known asteroid.

The **base** is marked like an ore: choose *Base* in the MARK list, or run
`mark base here` while docked. It is shown as a square.

### Screens

- **Radar** (`[Accel Map]`): a plane through the ship that turns with it, forward is up.
  Deposits sit on stems that show how far above or below the ship they are. Grey
  spheres are known asteroids, violet areas are gravity wells. Deposits beyond the
  range appear as small markers on the edge.
- **List** (`[Accel List]`, or the `[Accel Map]` screen after pressing LIST): deposits
  sorted by distance, with the direction relative to the ship's nose (degrees
  left/right and up/down, plus a small indicator).
- Both show the selected deposit, whether the direct path is clear and the delta-v
  of the trip.
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

- **Planning**: if a known asteroid (or a planet's gravity well) is in the way, the
  route gets a waypoint beside it, keeping the ship's radius plus `ApproachBuffer`
  of distance. Several obstacles give several waypoints. If no complete route is
  found (very dense fields), GO refuses instead of flying a risky path.
- **Flying**: full thrust up to `MaxSpeed`. Waypoints are passed without stopping,
  slower for sharper turns. The gyroscopes turn the ship's nose along the route,
  so the forward camera looks where the ship goes. Turning the ship yourself
  takes over the gyroscopes; any movement key cancels the flight.
- **Collision guard**: during the flight the cameras scan the path ahead, with a
  center ray and a ring of rays at the ship's radius, as far as the stopping
  distance. If the target rock sticks out further than scanned (a protrusion
  beside the scanned point), the stop point moves closer. An unknown asteroid or a
  grid on the path makes the route go around it; if the ship is already too close
  for that, it stops before the obstacle.
- **Live replanning**: an asteroid found later (by the guard or the background
  survey) that lies on the rest of the route triggers a new plan from the current
  position.
- **Gyroscope directions**: the script checks the direction of the gyroscope
  overrides against the measured rotation and corrects it by itself. Until that is
  done (usually within the first second of the first turn), it turns slowly.
  `calibrate reset` repeats this.

## Jump drive

For targets further away than `JumpMinDistance` (and outside gravity), GO first
jumps: the ship stops, sets the jump distance, waits until a jump drive is ready,
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
   map is set to that position.
2. From then on, **GO** on the base (or `dock`) flies there, jumping if far, and
   stops at an approach point in front of the connector (twice the ship's radius
   plus `DockApproach`), far enough out to turn without touching the base.
3. **Space to turn**: the cameras check the space around the ship (its bounding
   sphere) for other ships and players before it turns. If something is there,
   the ship waits.
4. The ship turns into the stored orientation.
5. **Way in**: the cameras check the path into the dock. Only what lies in the
   space the ship actually sweeps through counts: the ship's own shape in the
   docked pose, moved out along the connector axis. Rock or ships next to that
   path (e.g. at neighbouring connectors) do not block. If something is in the
   path, the ship waits and the message says what it is.
6. The ship moves in slowly along the connector axis, correcting sideways drift,
   and keeps scanning the rest of the way; if something shows up, it stops and
   waits. When the connector is ready, it connects.
7. `undock` disconnects and backs off along the connector axis.

Waiting ends when the way is clear; after 2 minutes docking is cancelled. The
checks use all cameras that can see the respective points; cameras pointing
towards the connector side of the ship make them more complete. Sensors with
`[Accel]` in their name are also used during the final approach (set their range
yourself). Parts of the base never count as obstacles.

This works for bases that do not move. Movement keys cancel docking at any time.

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

- If the limit is higher than what your thrusters can deliver, the thrusters
  simply run at 100 %, as in vanilla.
- Turning the programmable block **off** while flying can leave thruster
  overrides active. Use the `off` command instead, which clears them.
- The script overrides the thrusters on its own construct (including thrusters
  on rotor/piston subgrids that are aligned with the cockpit). Do not combine it
  with other scripts that also set thruster overrides.

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
| `StatusDisplay.cs` | Graphical ship status page                      |
| `Config.cs`        | Custom Data configuration and saved state       |

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
