# Workshop page

Material for the Steam Workshop items.

- `description.bbcode` - description of the Acceleration Control item (Steam
  BBCode, under the 8000 character limit).
- `dockgate.bbcode` - description of the DockGate companion item.
- `images/` - pictures for the pages. They are illustrations drawn from the script's
  colours and screen layouts, not in-game screenshots:
  - `map.png` (map screens) and `status.png` (status page) are mock-ups of the
    script's screens; the in-game look differs in details.
  - `accel.png`, `route.png`, `docking.png`, `planet.png`, `landing.png` are explanatory diagrams.
  - `cover.png` (Workshop preview, 1024 x 1024) is the radar mock-up with the title (`cover.py`).
    `route.png` is computed: a 2D copy of the script's route planner places the
    waypoints, and a simple point-mass simulation (corner speeds planned
    backwards, waypoint switching as in the script) draws the flown line.
- `map_mockup.py`, `status_mockup.py`, `diagrams.py` regenerate the images
  (`python3 workshop/<name>.py`, needs Pillow and the DejaVu fonts).

The descriptions embed the images from this repository's `main` branch
(`raw.githubusercontent.com`), which only works while the repository is public.
Otherwise upload them as preview images of the Workshop item and remove the
`[img]` lines.
