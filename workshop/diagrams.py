"""Explanatory diagrams for the Workshop page (not in-game screenshots).

Writes images/accel.png, route.png, docking.png, planet.png and landing.png in the colours of
the script's screens. Run: python3 workshop/diagrams.py (needs Pillow).
"""
import math
import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "images")
SANS = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
SANS_B = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"

BG = (8, 18, 24)
PANEL = (14, 32, 42)
GRID = (32, 78, 96)
FAINT = (22, 52, 64)
CYAN = (70, 205, 235)
TEXT = (215, 240, 250)
DIM = (120, 160, 175)
ROUTE = (255, 190, 60)
WARN = (255, 110, 80)
GRAV = (150, 90, 255)
ROCK = (92, 88, 84)
ROCK_EDGE = (140, 134, 126)
GOOD = (100, 220, 140)
W, H = 1600, 900


def font(size, bold=False):
    return ImageFont.truetype(SANS_B if bold else SANS, size)


def canvas(title, subtitle):
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    d.rectangle([0, 0, W, 96], fill=PANEL)
    d.line([0, 96, W, 96], fill=GRID, width=2)
    d.text((40, 18), title, font=font(40, True), fill=CYAN)
    d.text((42, 66), subtitle, font=font(22), fill=DIM)
    return img, d


def dashed(d, a, b, color, width=3, dash=14, gap=10):
    length = math.dist(a, b)
    if length == 0:
        return
    ux, uy = (b[0] - a[0]) / length, (b[1] - a[1]) / length
    t = 0
    while t < length:
        e = min(t + dash, length)
        d.line([(a[0] + ux * t, a[1] + uy * t), (a[0] + ux * e, a[1] + uy * e)], fill=color, width=width)
        t = e + gap


def arrow(d, a, b, color, width=4, head=16):
    d.line([a, b], fill=color, width=width)
    ang = math.atan2(b[1] - a[1], b[0] - a[0])
    for s in (-1, 1):
        d.line([b, (b[0] - head * math.cos(ang + s * 0.45), b[1] - head * math.sin(ang + s * 0.45))], fill=color, width=width)


def ship(d, x, y, angle, size=26, color=CYAN):
    pts = [(size, 0), (-size * 0.7, size * 0.6), (-size * 0.35, 0), (-size * 0.7, -size * 0.6)]
    c, s = math.cos(angle), math.sin(angle)
    d.polygon([(x + px * c - py * s, y + px * s + py * c) for px, py in pts], fill=color)


def rock(d, cx, cy, r, seed):
    pts = []
    for i in range(24):
        a = 2 * math.pi * i / 24
        k = 1 + 0.12 * math.sin(3 * a + seed) + 0.07 * math.sin(7 * a + 2 * seed)
        pts.append((cx + r * k * math.cos(a), cy + r * k * math.sin(a)))
    d.polygon(pts, fill=ROCK, outline=ROCK_EDGE)


def label(d, x, y, text, color=TEXT, size=22, bold=False, anchor="la"):
    d.text((x, y), text, font=font(size, bold), fill=color, anchor=anchor)


# ---------------------------------------------------------------------------

def accel():
    img, d = canvas("ACCELERATION LIMIT", "WASD / Space / C accelerate at the limit you choose - mass, gravity and thrust are compensated")
    x0, y0, x1, y1 = 140, 170, 1060, 800
    d.rectangle([x0, y0, x1, y1], fill=PANEL, outline=GRID, width=2)
    for i in range(1, 6):
        yy = y1 - (y1 - y0) * i / 5
        d.line([x0, yy, x1, yy], fill=FAINT, width=1)
        label(d, x0 - 14, yy, "%d" % (i * 20), DIM, 18, anchor="rm")
    for i in range(1, 6):
        xx = x0 + (x1 - x0) * i / 6
        d.line([xx, y0, xx, y1], fill=FAINT, width=1)
        label(d, xx, y1 + 12, "%d s" % (i * 5), DIM, 18, anchor="ma")
    label(d, x0 - 14, y0 - 30, "speed m/s", DIM, 18)
    vmax = 100.0
    tmax = 30.0

    def P(t, v):
        return (x0 + (x1 - x0) * t / tmax, y1 - (y1 - y0) * v / vmax)
    # vanilla: 4 g forward, full dampener stop
    vanilla = [P(t / 10, min(4 * 9.81 * t / 10, 100)) for t in range(0, 301)]
    d.line(vanilla, fill=WARN, width=4)
    for accel_, col in ((9.81, ROUTE), (4.0, CYAN)):
        pts = [P(t / 10, min(accel_ * t / 10, 100)) for t in range(0, 301)]
        d.line(pts, fill=col, width=5)
    lx = 1110
    items = [
        (WARN, "Vanilla: full thrust (here 4 g)", "depends on load and thrusters"),
        (ROUTE, "Limit 1 g", "same feel loaded or empty"),
        (CYAN, "Limit 0.4 g", "gentle, e.g. heavy haulers"),
    ]
    for i, (col, a, b) in enumerate(items):
        yy = 200 + i * 110
        d.rectangle([lx, yy + 6, lx + 36, yy + 14], fill=col)
        label(d, lx + 52, yy - 4, a, TEXT, 24, True)
        label(d, lx + 52, yy + 30, b, DIM, 20)
    label(d, lx, 560, "Also on board:", CYAN, 24, True)
    for i, t in enumerate(["cruise: constant speed for drilling",
                           "approach: scan, fly, stop before rock",
                           "status: cargo, fuel, delta-v"]):
        label(d, lx, 600 + i * 36, "- " + t, TEXT, 20)
    img.save(os.path.join(OUT, "accel.png"))


# 2D copy of the script's planner (Navigation.cs: PlanSegment, BlockingObstacle,
# DetourPoint): a leg that cuts an obstacle's clearance is split at a point
# DetourFactor x clearance beside the obstacle, and both halves are checked again.
DETOUR_FACTOR = 1.3


def seg_distance(c, a, b):
    ax, ay = b[0] - a[0], b[1] - a[1]
    t = max(0.0, min(1.0, ((c[0] - a[0]) * ax + (c[1] - a[1]) * ay) / (ax * ax + ay * ay)))
    return math.dist(c, (a[0] + ax * t, a[1] + ay * t)), t


def blocking(obstacles, a, b, skip_a, skip_b):
    first, best = None, 1e18
    for c, clearance in obstacles:
        if skip_a and math.dist(a, c) < clearance or skip_b and math.dist(b, c) < clearance:
            continue
        if seg_distance(c, a, b)[0] >= clearance:
            continue
        if math.dist(a, c) < best:
            first, best = (c, clearance), math.dist(a, c)
    return first


def detour_point(obstacles, o, a, b):
    c, clearance = o
    _, t = seg_distance(c, a, b)
    px, py = a[0] + (b[0] - a[0]) * t - c[0], a[1] + (b[1] - a[1]) * t - c[1]
    n = math.hypot(px, py) or 1
    ox, oy = px / n, py / n
    scale = DETOUR_FACTOR
    while scale < DETOUR_FACTOR * 3:
        for sx in (1, -1):         # in 2D: the side the path passes, then the other
            q = (c[0] + sx * ox * clearance * scale, c[1] + sx * oy * clearance * scale)
            if all(math.dist(q, c2) >= r2 for c2, r2 in obstacles):
                return q
        scale *= 1.6
    return (c[0] + ox * clearance * DETOUR_FACTOR, c[1] + oy * clearance * DETOUR_FACTOR)


def plan(obstacles, a, b, route, depth=0, at_start=True, at_target=True):
    o = blocking(obstacles, a, b, at_start, at_target)
    if o is None or depth >= 6:
        return
    q = detour_point(obstacles, o, a, b)
    plan(obstacles, a, q, route, depth + 1, at_start, False)
    route.append(q)
    plan(obstacles, q, b, route, depth + 1, False, at_target)


def fly(points, vmax=8.0, accel=0.35):
    """Rough point-mass flight. Corner speeds are planned backwards from the end
    (like PlanCornerSpeeds, turn factor cos^2); a waypoint counts as reached
    within 25 px or after crossing the bisector plane (like WaypointReached)."""
    n = len(points)
    corner = [0.0] * n
    for k in range(n - 2, 0, -1):
        a, w, b = points[k - 1], points[k], points[k + 1]
        u = (w[0] - a[0], w[1] - a[1])
        v = (b[0] - w[0], b[1] - w[1])
        cos = (u[0] * v[0] + u[1] * v[1]) / (math.hypot(*u) * math.hypot(*v))
        corner[k] = min(vmax * max(0.1, cos) ** 2, math.sqrt(corner[k + 1] ** 2 + 2 * accel * math.dist(w, b)))
    pos, vel, i, track = list(points[0]), [0.0, 0.0], 1, []
    for _ in range(40000):
        track.append(tuple(pos))
        target = points[i]
        if i < n - 1:
            a, b = points[i - 1], points[i + 1]
            ux, uy = target[0] - a[0], target[1] - a[1]
            vx, vy = b[0] - target[0], b[1] - target[1]
            nu, nv = math.hypot(ux, uy), math.hypot(vx, vy)
            bis = (ux / nu + vx / nv, uy / nu + vy / nv)
            if math.dist(pos, target) < 25 or (pos[0] - target[0]) * bis[0] + (pos[1] - target[1]) * bis[1] > 0:
                i += 1
                continue
        dist = math.dist(pos, target)
        if i == n - 1 and dist < 1 and math.hypot(*vel) < 0.2:
            break
        speed = min(vmax, math.sqrt(corner[i] ** 2 + 2 * accel * 0.8 * dist))
        dn = dist or 1
        want = ((target[0] - pos[0]) / dn * speed, (target[1] - pos[1]) / dn * speed)
        ex, ey = want[0] - vel[0], want[1] - vel[1]
        en = math.hypot(ex, ey)
        if en > accel:
            ex, ey = ex / en * accel, ey / en * accel
        vel = [vel[0] + ex, vel[1] + ey]
        pos = [pos[0] + vel[0], pos[1] + vel[1]]
    return track


def route():
    img, d = canvas("NAVIGATION", "Routes around known asteroids, gyros turn the ship, cameras guard the way ahead")
    rocks = [((620, 420), 120, 1), ((1000, 650), 90, 2), ((1120, 300), 70, 3), ((400, 690), 60, 4)]
    obstacles = []
    for (cx, cy), r, s in rocks:
        clearance = r + 60
        obstacles.append(((cx, cy), clearance))
        d.ellipse([cx - clearance, cy - clearance, cx + clearance, cy + clearance], outline=FAINT, width=2)
        rock(d, cx, cy, r, s)
    # target asteroid with deposit
    tx, ty, tr = 1420, 640, 95
    rock(d, tx, ty, tr, 5)
    d.polygon([(tx - 40, ty - 106), (tx - 26, ty - 92), (tx - 40, ty - 78), (tx - 54, ty - 92)], fill=(235, 235, 245))
    label(d, tx - 20, ty - 150, "Platinum #1", ROUTE, 24, True)
    start = (170, 330)
    stop = (1318, 575)
    obstacles.append(((tx, ty), tr + 60))
    waypoints = []
    plan(obstacles, start, stop, waypoints)
    pts = [start] + waypoints + [stop]
    for a, b in zip(pts, pts[1:]):
        dashed(d, a, b, ROUTE, 5, 18, 10)
    track = fly(pts)
    d.line(track, fill=CYAN, width=3)
    for i, (wx, wy) in enumerate(waypoints):
        d.polygon([(wx, wy - 13), (wx + 13, wy), (wx, wy + 13), (wx - 13, wy)], outline=ROUTE, width=3)
        label(d, wx - 20, wy - 34, "W%d" % (i + 1), ROUTE, 22, True, "ra")
    d.ellipse([stop[0] - 9, stop[1] - 9, stop[0] + 9, stop[1] + 9], fill=ROUTE)
    ang = math.atan2(pts[1][1] - start[1], pts[1][0] - start[0])
    # guard rays
    for k in (-0.12, -0.05, 0.02, 0.09):
        a2 = ang + k
        arrow(d, start, (start[0] + 320 * math.cos(a2), start[1] + 320 * math.sin(a2)), (40, 110, 130), 2, 10)
    ship(d, start[0], start[1], ang, 30)
    lx, ly = 1120, 470
    for i, (col, dash, text) in enumerate(((ROUTE, True, "planned route"), (CYAN, False, "flown (simulated, approx.)"))):
        yy = 150 + i * 32
        if dash:
            dashed(d, (60, yy), (110, yy), col, 5, 14, 8)
        else:
            d.line([(60, yy), (110, yy)], fill=col, width=3)
        label(d, 124, yy - 12, text, TEXT, 20)
    d.ellipse([60, 204, 110, 254], outline=FAINT, width=2)
    label(d, 124, 218, "clearance: rock + ship + buffer", TEXT, 20)
    label(d, 60, 500, "collision guard: camera rays", DIM, 20)
    label(d, 60, 528, "ahead, replans or stops", DIM, 20)
    box = [60, 760, 1540, 860]
    d.rectangle(box, fill=PANEL, outline=GRID, width=2)
    label(d, 84, 776, "Corner speeds are planned backwards from the end; waypoints move outwards if a turn would drift too far.", TEXT, 20)
    label(d, 84, 812, "Long legs start with a jump (the pilot may have to press Jump). ETA and delta-v on the screens.", DIM, 20)
    img.save(os.path.join(OUT, "route.png"))


def docking():
    img, d = canvas("DOCKING", "Dock once by hand - the way in is recorded and replayed, gates open over the antenna")
    # asteroid with hangar, side cut
    d.rectangle([900, 150, 1600, 780], fill=ROCK)
    for i in range(12):
        x = 900 + (i * 97) % 700
        y = 170 + (i * 53) % 590
        d.ellipse([x, y, x + 30, y + 18], fill=(80, 76, 72))
    hangar = [900, 430, 1450, 640]
    d.rectangle(hangar, fill=(30, 40, 46))
    d.rectangle([960, 630, 1450, 650], fill=(70, 90, 100))            # floor (base grid)
    d.rectangle([1260, 600, 1300, 630], fill=ROUTE)                    # connector
    label(d, 1280, 668, "connector", ROUTE, 20, anchor="ma")
    # gate (open, retracted up)
    d.rectangle([952, 360, 968, 432], fill=GOOD)
    label(d, 984, 380, "gate: 'Dock Open' timer", GOOD, 20)
    label(d, 984, 404, "(DockGate script on the base)", DIM, 18)
    label(d, 1180, 250, "base in an asteroid", DIM, 22, anchor="ma")
    # recorded way: from outside, down and in
    way = []
    for i in range(0, 41):
        t = i / 40
        if t < 0.55:
            s = t / 0.55
            x = 200 + 620 * s
            y = 260 + 260 * (s * s)
        else:
            s = (t - 0.55) / 0.45
            x = 820 + 460 * s
            y = 520 + 70 * math.sin(s * math.pi / 2)
        way.append((x, y))
    for (x, y) in way[::2]:
        d.ellipse([x - 5, y - 5, x + 5, y + 5], fill=CYAN)
    for i in (4, 12, 20, 28, 36):
        (xa, ya), (xb, yb) = way[i], way[i + 1]
        a = math.atan2(yb - ya, xb - xa)
        arrow(d, (xa, ya), (xa + 50 * math.cos(a), ya + 50 * math.sin(a)), CYAN, 3, 12)
    ship(d, 200, 260, math.atan2(way[1][1] - 260, way[1][0] - 200), 34)
    label(d, 120, 190, "start of the recorded way", TEXT, 22, True)
    label(d, 330, 470, "last 300 m of your own approach,", CYAN, 22, True)
    label(d, 330, 500, "position and orientation, stored", CYAN, 22, True)
    label(d, 330, 530, "relative to the base grid", CYAN, 22, True)
    box = [60, 700, 860, 860]
    d.rectangle(box, fill=PANEL, outline=GRID, width=2)
    lines = ["dock: nearest known dock (20 km), GO: any base on the map",
             "slow and in the recorded pose; waits while the way is blocked",
             "connector steered into lock; undock = same way out",
             "one dock per base; the base is found again when it moved"]
    for i, t in enumerate(lines):
        label(d, 84, 716 + i * 34, "- " + t, TEXT if i == 0 else DIM, 20)
    img.save(os.path.join(OUT, "docking.png"))


def planet():
    img, d = canvas("PLANETS", "Climb, arc at cruise height, vertical descent - wind and drag are compensated")
    cx, cy, R = 800, 2300, 1900
    # atmosphere and zone
    d.ellipse([cx - R - 260, cy - R - 260, cx + R + 260, cy + R + 260], fill=(14, 30, 48))
    d.ellipse([cx - R, cy - R, cx + R, cy + R], fill=(40, 60, 44))
    label(d, 330, 400, "atmosphere: speed limit", DIM, 20)

    def at(angle, h):
        return (cx + (R + h) * math.sin(angle), cy - (R + h) * math.cos(angle))
    a0, a1, hc = -0.27, 0.25, 200
    base, target = at(a0, 0), at(a1, 0)
    d.rectangle([base[0] - 26, base[1] - 22, base[0] + 26, base[1]], fill=CYAN)
    label(d, base[0], base[1] + 20, "base", CYAN, 22, True, "ma")
    d.polygon([(target[0], target[1] - 30), (target[0] + 16, target[1] - 14), (target[0], target[1]), (target[0] - 16, target[1] - 14)], fill=ROUTE)
    label(d, target[0], target[1] + 20, "deposit / GPS", ROUTE, 22, True, "ma")
    top0, top1 = at(a0, hc), at(a1, hc)
    arrow(d, base, top0, ROUTE, 5)
    arc = [at(a0 + (a1 - a0) * i / 60, hc) for i in range(61)]
    for p, q in zip(arc[::2], arc[1::2]):
        d.line([p, q], fill=ROUTE, width=5)
    arrow(d, top1, (target[0], target[1] - 34), ROUTE, 5)
    ship(d, arc[20][0], arc[20][1], math.atan2(arc[21][1] - arc[20][1], arc[21][0] - arc[20][0]), 28)
    label(d, top0[0] + 24, top0[1] + 40, "vertical climb", TEXT, 22, True)
    label(d, 800, 240, "arc at cruise height (above terrain and water)", TEXT, 22, True, "ma")
    label(d, top1[0] + 24, top1[1] + 40, "vertical descent,", TEXT, 22, True)
    label(d, top1[0] + 24, top1[1] + 70, "braking in gravity", TEXT, 22, True)
    box = [60, 760, 1540, 860]
    d.rectangle(box, fill=PANEL, outline=GRID, width=2)
    label(d, 84, 776, "Level flight in atmosphere; top speed learned from thruster effectiveness.", TEXT, 22)
    label(d, 84, 812, "Real Solar Systems: experimental zone support (off by default).", DIM, 20)
    img.save(os.path.join(OUT, "planet.png"))


def landing():
    img, d = canvas("LANDING", "The cameras scan the ground under the whole ship, then it sets down on the first flat spot")
    # side view: ground with a boulder, a steep bank and a gentle slope
    def ground(x):
        h = 640
        if x > 560:
            h -= (x - 560) * 0.62          # steep bank
        if x > 780:
            h = 640 - 220 * 0.62 - (x - 780) * 0.18   # gentle slope
        return h
    xs = list(range(40, 1101, 4))
    poly = [(x, ground(x)) for x in xs] + [(1100, 760), (40, 760)]
    d.polygon(poly, fill=(46, 62, 48))
    d.line([(x, ground(x)) for x in xs], fill=(110, 140, 110), width=3)
    rock(d, 300, 628, 34, 1.3)

    def spot_ship(x, y, angle, color):
        c, s = math.cos(angle), math.sin(angle)
        pts = [(-110, -18), (110, -18), (120, 0), (-120, 0)]
        d.polygon([(x + px * c - py * s, y + px * s + py * c) for px, py in pts], fill=color)
        for gx in (-80, 80):
            a = (x + gx * c, y + gx * s)
            d.line([a, (a[0] - 14 * s, a[1] + 14 * c)], fill=color, width=5)

    # spot 1: rays over the footprint find the boulder
    sx, sy = 260, 330
    spot_ship(sx, sy, 0, CYAN)
    for i in range(13):
        gx = 120 + i * 24
        gy = ground(gx) if abs(gx - 300) > 34 else 596
        d.line([(sx, sy + 4), (gx, gy)], fill=(40, 120, 140), width=1)
        d.ellipse([gx - 4, gy - 4, gx + 4, gy + 4], fill=WARN if abs(gx - 300) <= 34 else CYAN)
    label(d, sx, 240, "spot 1: scanning", CYAN, 22, True, "ma")
    label(d, sx, 690, "boulder: uneven 1.8 m", WARN, 20, True, "ma")

    # spot 2: bank too steep
    x2 = 670
    label(d, x2, 690, "spot 2: 32° - too steep", WARN, 20, True, "ma")
    d.line([(x2 - 14, ground(x2) - 60), (x2 + 14, ground(x2) - 32)], fill=WARN, width=5)
    d.line([(x2 + 14, ground(x2) - 60), (x2 - 14, ground(x2) - 32)], fill=WARN, width=5)

    # spot 3: gentle slope, ship tilted to the ground and descending
    x3 = 960
    ang = math.atan(0.18) * -1
    top = ground(x3) - 230
    dashed(d, (x3, top + 60), (x3, ground(x3) - 40), ROUTE, 4)
    arrow(d, (x3, ground(x3) - 90), (x3, ground(x3) - 40), ROUTE, 4)
    spot_ship(x3, ground(x3) - 16, ang, GOOD)
    label(d, x3, top - 10, "spot 3: 10°, even", GOOD, 22, True, "ma")
    label(d, x3, top + 20, "tilted to the slope, down at 0.3 m/s", TEXT, 20, False, "ma")
    label(d, x3, ground(x3) + 40, "gear locked", GOOD, 20, True, "ma")

    # top view: footprint grid and the spiral of spots
    ox, oy = 1330, 400
    d.rectangle([1150, 130, 1560, 740], fill=PANEL, outline=GRID, width=2)
    label(d, 1355, 146, "seen from above", DIM, 20, False, "ma")
    fw, fh = 150, 64
    for i in range(16):
        for j in range(8):
            px, py = ox - fw / 2 + i * fw / 15, oy - fh / 2 + j * fh / 7
            d.ellipse([px - 2, py - 2, px + 2, py + 2], fill=CYAN)
    d.rectangle([ox - fw / 2 + 12, oy - fh / 2 + 12, ox + fw / 2 - 12, oy + fh / 2 - 12], outline=TEXT, width=2)
    label(d, ox, oy + fh / 2 + 10, "ship box + 3 m, rays 1.2 m apart", TEXT, 18, False, "ma")
    prev = None
    for k in range(6):
        a, r = k * 2.4, 42 * math.sqrt(k)
        p = (ox + r * math.cos(a), oy + 160 + r * math.sin(a) * 0.9)
        if prev:
            dashed(d, prev, p, ROUTE, 2, 8, 6)
        d.ellipse([p[0] - 7, p[1] - 7, p[0] + 7, p[1] + 7], outline=ROUTE, width=3)
        label(d, p[0] + 10, p[1] - 10, str(k + 1), ROUTE, 18, True)
        prev = p
    label(d, ox, 700, "next spots on a spiral", ROUTE, 18, True, "ma")

    box = [60, 780, 1540, 870]
    d.rectangle(box, fill=PANEL, outline=GRID, width=2)
    label(d, 84, 792, "land / land GPS:...  -  holes, bumps over MaxBump and slopes over MaxSlope are refused.", TEXT, 22)
    label(d, 84, 828, "Needs landing gear and cameras facing down (under the wings of wide ships too).", DIM, 20)
    img.save(os.path.join(OUT, "landing.png"))


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    accel()
    route()
    docking()
    planet()
    landing()
    print("written to", OUT)
