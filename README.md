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
- Settings survive saving/reloading the world

## Setup

1. Build a programmable block on the ship.
2. Open it, click **Edit**, paste the full content of
   [`AccelerationControl.cs`](AccelerationControl.cs) and click **Check code**, then **OK**.
3. Put the programmable block on your cockpit toolbar twice, using the **Run**
   action with these arguments:
   - slot 1: `up`   → increase the limit
   - slot 2: `down` → decrease the limit
4. For **Approach**, build a camera facing forward (same direction as the cockpit).
   If you have several, add `[Accel]` to the name of the one to use.
5. Optional: add `[Accel]` to the name of an LCD panel to show the status there, or
   set `CockpitSurface` in the Custom Data to show it on a cockpit screen.

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
| `approach`          | Scan straight ahead with the camera and fly to the target |
| `stop`              | Cancel cruise or approach                                |
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
| `LcdTag`             | `[Accel]` | LCD panels with this text in their name show the status        |
| `CockpitSurface`     | `-1`      | Cockpit screen index for the status display, `-1` = off        |
| `CruiseSpeed`        | `0.75`    | Cruise speed on first start (m/s)                              |
| `CruiseStep`         | `0.25`    | Change per `cruise up` / `cruise down` (m/s)                   |
| `VelocityGain`       | `2`       | How firmly cruise/approach correct speed errors (1/s)          |
| `CameraTag`          | `[Accel]` | Camera with this text in its name is used for scanning         |
| `ScanRange`          | `15000`   | Maximum scan distance (m)                                      |
| `ApproachBuffer`     | `75`      | Distance to stop before the scanned surface (m)                |
| `ApproachFullThrust` | `true`    | Approach uses 100 % thrust; `false` = respect the limit        |
| `MaxSpeed`           | `100`     | Top speed used by approach (the game's speed limit)            |
| `BrakeSafety`        | `0.8`     | Fraction of the braking thrust that approach plans with        |

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

- Any movement key cancels the approach immediately.
- Cameras need time to charge long scans (about 2 km per second). If the camera
  is not charged yet, the status shows `Scanning... camera xx%` and the scan fires
  once it is ready. The script keeps the camera charging in the background.
- Distant asteroids are sometimes not detected by raycasts until you get closer
  (a known game limitation). If the scan finds nothing, fly closer and try again.
- The braking plan is made for space. In strong gravity, braking downwards can be
  weaker than planned; lower `BrakeSafety` if you use it there.

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
