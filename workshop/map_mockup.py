"""Mockup of the planned ore map screens (radar + list), rendered like SE LCDs."""
import math
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "images") + "/"
MONO = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"
MONO_B = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono-Bold.ttf"
SANS_B = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"

def font(size, bold=False):
    return ImageFont.truetype(MONO_B if bold else MONO, size)

BG = (8, 18, 24)
PANEL = (14, 32, 42)
GRID = (32, 78, 96)
GRID_FAINT = (22, 52, 64)
CYAN = (70, 205, 235)
TEXT = (215, 240, 250)
DIM = (120, 160, 175)
ROUTE = (255, 190, 60)
WARN = (255, 110, 80)
GRAV = (150, 90, 255)

ORES = {
    "Iron": (225, 140, 95), "Nickel": (130, 215, 130), "Cobalt": (90, 140, 255),
    "Platinum": (235, 235, 245), "Ice": (150, 230, 255), "Gold": (255, 210, 75),
}
SHORT = {"Iron": "Fe", "Nickel": "Ni", "Cobalt": "Co", "Platinum": "Pt", "Ice": "Ice", "Gold": "Au"}

# Ship frame, km: x = right, y = up, z = forward
DEPOSITS = [
    ("Platinum", 1, (12.0, -5.0, 14.0)),
    ("Iron", 2, (3.0, 1.5, 6.0)),
    ("Iron", 1, (-4.5, -1.0, 2.5)),
    ("Cobalt", 1, (5.6, -2.2, 8.6)),
    ("Nickel", 1, (-9.0, 2.0, -6.0)),
    ("Ice", 1, (-6.0, 4.0, 13.0)),
]
ASTEROIDS = [((6.4, -2.8, 7.6), 1.5), ((-4.9, -1.1, 2.9), 0.9), ((3.4, 1.7, 6.5), 0.8),
             ((12.4, -5.2, 14.6), 1.2), ((-6.4, 4.3, 13.6), 0.9), ((-9.5, 2.1, -6.5), 1.1)]
WAYPOINT = (2.5, -1.0, 10.5)
TARGET = (11.7, -4.9, 13.7)          # stop point in front of the Pt asteroid

def route_stats():
    legs = [length(sub(WAYPOINT, (0, 0, 0))), length(sub(TARGET, WAYPOINT))]
    d1 = [c / legs[0] for c in WAYPOINT]
    d2 = [c / legs[1] for c in sub(TARGET, WAYPOINT)]
    cos = sum(a * b for a, b in zip(d1, d2))
    turn = 2 * MAX_SPEED * math.sin(math.acos(cos) / 2)
    dv = 2 * MAX_SPEED + turn
    eta = sum(legs) * 1000 / MAX_SPEED + 2 * MAX_SPEED / 10.0
    return legs, round(dv / 5) * 5, eta

PLANET = ((-60.0, -10.0, -30.0), 55.0)   # center, gravity radius (km)
RANGE_KM = 20.0
MAX_SPEED = 100.0
DV_TOTAL = 4240


def length(v):
    return math.sqrt(sum(c * c for c in v))


def sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


# ---------------------------------------------------------------- radar ---
def radar():
    S = 1024
    img = Image.new("RGB", (S, S), BG)
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img, "RGBA")

    cx, cy = 512, 395
    R = 440               # px for RANGE_KM horizontally
    k = 0.52              # plane tilt (vertical squash)
    s = R / RANGE_KM
    h = s * 0.85          # px per km height

    def plane(x, z):
        return cx + x * s, cy - z * s * k

    def proj(p):
        x, y, z = p
        px, py = plane(x, z)
        return px, py - y * h

    # header
    d.rectangle([0, 0, S, 64], fill=PANEL)
    d.text((24, 14), "ORE MAP", font=font(34, True), fill=CYAN)
    d.text((S - 24, 20), "Range 20 km   Filter: all", font=font(24), fill=DIM, anchor="ra")

    # gravity well (intersection of the gravity sphere with the ship plane)
    (gx, gy, gz), gr = PLANET
    pr = math.sqrt(max(gr * gr - gy * gy, 0))
    gpx, gpy = plane(gx, gz)
    well = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    wd = ImageDraw.Draw(well)
    wd.ellipse([gpx - pr * s, gpy - pr * s * k, gpx + pr * s, gpy + pr * s * k], fill=GRAV + (38,))
    # clip to the radar area
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).ellipse([cx - R - 20, cy - R * k - 20, cx + R + 20, cy + R * k + 20], fill=255)
    img.paste(well, (0, 0), Image.composite(well, Image.new("RGBA", (S, S)), mask))
    d = ImageDraw.Draw(img, "RGBA")
    # dashed boundary arc
    for a in range(0, 360, 3):
        if a % 6:
            continue
        t0, t1 = math.radians(a), math.radians(a + 3)
        p0 = (gpx + pr * s * math.cos(t0), gpy + pr * s * k * math.sin(t0))
        p1 = (gpx + pr * s * math.cos(t1), gpy + pr * s * k * math.sin(t1))
        if ((p0[0] - cx) / (R + 20)) ** 2 + ((p0[1] - cy) / (R * k + 20)) ** 2 <= 1:
            d.line([p0, p1], fill=GRAV + (220,), width=3)
    d.text((cx - R * 0.93, cy + R * k * 0.55), "GRAVITY WELL", font=font(20, True), fill=GRAV)
    d.text((cx - R * 0.93, cy + R * k * 0.55 + 24), "Planet 67 km  <", font=font(18), fill=GRAV)

    # range rings + cross
    for rk, label in ((5, "5"), (10, "10"), (20, "20 km")):
        r = rk * s
        d.ellipse([cx - r, cy - r * k, cx + r, cy + r * k], outline=GRID if rk == 20 else GRID_FAINT, width=2)
        d.text((cx + r * 0.72, cy - r * k * 0.72 - 20), label, font=font(17), fill=DIM)
    d.line([cx - R, cy, cx + R, cy], fill=GRID_FAINT, width=1)
    d.line([cx, cy - R * k, cx, cy + R * k], fill=GRID_FAINT, width=1)
    d.text((cx, cy - R * k - 30), "FORWARD", font=font(17), fill=DIM, anchor="ma")

    # collect drawables sorted by depth (far = high z first)
    items = []
    for c, r in ASTEROIDS:
        items.append((c[2], "ast", c, r))
    for ore, n, p in DEPOSITS:
        items.append((p[2], "ore", (ore, n), p))
    items.sort(key=lambda i: -i[0])

    # route (under markers)
    route = [(0, 0, 0), WAYPOINT, TARGET]
    pts = [proj(p) for p in route]
    for a, b in zip(pts, pts[1:]):
        steps = 40
        for i in range(steps):
            if i % 2:
                continue
            t0, t1 = i / steps, (i + 1) / steps
            d.line([(a[0] + (b[0] - a[0]) * t0, a[1] + (b[1] - a[1]) * t0),
                    (a[0] + (b[0] - a[0]) * t1, a[1] + (b[1] - a[1]) * t1)], fill=ROUTE, width=4)
    # waypoint diamond
    wx, wy = pts[1]
    d.polygon([(wx, wy - 11), (wx + 11, wy), (wx, wy + 11), (wx - 11, wy)], outline=ROUTE, width=3)
    d.text((wx - 14, wy + 16), "W1", font=font(18, True), fill=ROUTE, anchor="rm")

    for _, kind, a, b in items:
        if kind == "ast":
            c, r = a, b
            px, py = proj(c)
            bx, by = plane(c[0], c[2])
            d.line([bx, by, px, py], fill=(90, 110, 120, 140), width=2)
            d.ellipse([bx - r * s * 0.6, by - r * s * k * 0.6, bx + r * s * 0.6, by + r * s * k * 0.6],
                      fill=(80, 100, 110, 50))
            rr = r * s * 0.8
            d.ellipse([px - rr, py - rr, px + rr, py + rr], fill=(70, 82, 90, 170), outline=(120, 140, 150, 200), width=2)
            d.ellipse([px - rr * 0.55, py - rr * 0.7, px + rr * 0.1, py - rr * 0.1], fill=(110, 125, 135, 90))
        else:
            (ore, n), p = a, b
            col = ORES[ore]
            px, py = proj(p)
            bx, by = plane(p[0], p[2])
            d.line([bx, by, px, py], fill=col + (220,), width=3)
            d.ellipse([bx - 5, by - 2.5, bx + 5, by + 2.5], fill=col + (200,))
            sz = 10
            d.polygon([(px, py - sz), (px + sz, py), (px, py + sz), (px - sz, py)], fill=col)
            dist = length(p)
            sel = ore == "Platinum"
            name, sub_ = "%s%d" % (SHORT[ore], n), "%.1f km" % dist
            tx, ty = px + 16, py - 22
            if name == "Fe2":
                tx, ty = px - 16 - d.textlength(sub_, font=font(18)), py - 50
            elif name == "Co1":
                tx, ty = px + 44, py + 4
            if sel:
                tw = d.textlength(sub_, font=font(18))
                d.rectangle([tx - 6, ty - 4, tx + tw + 6, ty + 46], outline=ROUTE, width=2, fill=(40, 30, 10, 220))
            d.text((tx, ty), name, font=font(20, True), fill=ROUTE if sel else col)
            d.text((tx, ty + 22), sub_, font=font(18), fill=ROUTE if sel else DIM)

    # ship
    d.polygon([(cx, cy - 16), (cx + 11, cy + 10), (cx, cy + 4), (cx - 11, cy + 10)], fill=CYAN)

    # off-screen arrows
    d.text((cx - R - 6, cy - 12), "<", font=font(26, True), fill=GRAV)

    # footer: route panel
    y0 = 720
    legs, dv_route, eta = route_stats()
    d.rectangle([16, y0, S - 16, y0 + 180], fill=PANEL, outline=GRID, width=2)
    d.text((34, y0 + 14), "ROUTE  Platinum #1", font=font(26, True), fill=ROUTE)
    d.text((S - 34, y0 + 18), "2 legs  %.1f km" % sum(legs), font=font(22), fill=TEXT, anchor="ra")
    d.text((34, y0 + 56), "Leg 1  ship > W1   %4.1f km  around asteroid" % legs[0], font=font(20), fill=DIM)
    d.text((34, y0 + 84), "Leg 2  W1 > target %4.1f km  stop 75 m before" % legs[1], font=font(20), fill=DIM)
    # dv bar
    d.text((34, y0 + 124), "dv", font=font(22, True), fill=TEXT)
    bx0, bx1, by0 = 80, 700, y0 + 128
    d.rectangle([bx0, by0, bx1, by0 + 22], outline=GRID, width=2)
    d.rectangle([bx0 + 2, by0 + 2, bx0 + (bx1 - bx0) * dv_route / DV_TOTAL, by0 + 20], fill=ROUTE)
    d.text((bx1 + 16, by0 - 2), "%d / %d m/s" % (dv_route, DV_TOTAL), font=font(20), fill=TEXT)
    d.text((S - 34, y0 + 152), "ETA %dm %02ds" % (eta // 60, eta % 60), font=font(20), fill=TEXT, anchor="ra")

    # menu buttons
    buttons = ["LIST", "ROUTE", "GO", "ZOOM", "FILTER"]
    bw = (S - 32 - 4 * 10) / 5
    for i, b in enumerate(buttons):
        x0 = 16 + i * (bw + 10)
        active = b == "GO"
        d.rectangle([x0, 924, x0 + bw, 1004], fill=CYAN if active else PANEL, outline=CYAN, width=2)
        d.text((x0 + bw / 2, 964), b, font=font(26, True), fill=BG if active else CYAN, anchor="mm")

    return img


# ----------------------------------------------------------------- list ---
def angles(p):
    x, y, z = p
    yaw = math.degrees(math.atan2(x, z))
    pitch = math.degrees(math.atan2(y, math.hypot(x, z)))
    return yaw, pitch


def list_screen():
    W, H = 1024, 560
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img, "RGBA")
    d.rectangle([0, 0, W, 58], fill=PANEL)
    d.text((22, 12), "ORE MAP", font=font(32, True), fill=CYAN)
    d.text((W - 22, 18), "6 deposits   sorted by distance", font=font(22), fill=DIM, anchor="ra")

    cols = [30, 70, 270, 330, 490]
    y = 74
    for x, t in zip(cols, ["", "ORE", "#", "DIST", "DIRECTION"]):
        d.text((x, y), t, font=font(20, True), fill=DIM)
    y += 34

    rows = sorted(DEPOSITS, key=lambda r: length(r[2]))
    sel = ("Platinum", 1)
    for ore, n, p in rows:
        yaw, pitch = angles(p)
        is_sel = (ore, n) == sel
        if is_sel:
            d.rectangle([14, y - 4, W - 14, y + 34], fill=(60, 45, 12), outline=ROUTE, width=2)
            d.text((30, y), ">", font=font(24, True), fill=ROUTE)
        col = ORES[ore]
        d.polygon([(76, y + 15 - 9), (85, y + 15), (76, y + 15 + 9), (67, y + 15)], fill=col)
        d.text((95, y), ore, font=font(24, True), fill=ROUTE if is_sel else TEXT)
        d.text((cols[2], y), str(n), font=font(24), fill=TEXT)
        d.text((cols[3] + 110, y), "%.1f km" % length(p), font=font(24), fill=TEXT, anchor="ra")
        # direction indicator: small crosshair with dot
        ix, iy = cols[4] + 20, y + 15
        d.ellipse([ix - 14, iy - 14, ix + 14, iy + 14], outline=GRID, width=2)
        d.line([ix - 14, iy, ix + 14, iy], fill=GRID_FAINT)
        d.line([ix, iy - 14, ix, iy + 14], fill=GRID_FAINT)
        behind = abs(yaw) > 90
        r = min(abs(yaw), 90) / 90 * 12
        a = math.atan2(-pitch, yaw if not behind else math.copysign(90, yaw))
        dx = math.copysign(r, yaw) if abs(yaw) > 1 else 0
        dy = -pitch / 90 * 12
        d.ellipse([ix + dx - 4, iy + dy - 4, ix + dx + 4, iy + dy + 4], fill=WARN if behind else col)
        txt = "%3.0f° %s  %2.0f° %s%s" % (abs(yaw), "R" if yaw >= 0 else "L", abs(pitch), "U" if pitch >= 0 else "D",
                                          "  behind" if behind else "")
        d.text((ix + 30, y), txt, font=font(24), fill=TEXT)
        y += 44

    y = H - 150
    d.line([14, y, W - 14, y], fill=GRID, width=2)
    legs, dv_route, eta = route_stats()
    d.text((22, y + 12), "Platinum #1  %.1f km" % length(DEPOSITS[0][2]), font=font(24, True), fill=ROUTE)
    d.text((W - 22, y + 14), "route dv %d / %d m/s   ETA %dm %02ds" % (dv_route, DV_TOTAL, eta // 60, eta % 60),
           font=font(22), fill=TEXT, anchor="ra")

    buttons = ["MAP", "MARK", "ROUTE", "GO", "DELETE"]
    bw = (W - 28 - 4 * 10) / 5
    for i, b in enumerate(buttons):
        x0 = 14 + i * (bw + 10)
        active = b == "ROUTE"
        d.rectangle([x0, H - 86, x0 + bw, H - 18], fill=CYAN if active else PANEL, outline=CYAN, width=2)
        d.text((x0 + bw / 2, H - 52), b, font=font(24, True), fill=BG if active else CYAN, anchor="mm")
    return img


def frame(img, title):
    pad = 26
    W, H = img.size
    out = Image.new("RGB", (W + 2 * pad, H + 2 * pad + 44), (40, 44, 48))
    d = ImageDraw.Draw(out)
    d.rectangle([pad - 6, pad - 6, pad + W + 5, pad + H + 5], fill=(20, 22, 24))
    out.paste(img, (pad, pad))
    d.text((pad, pad + H + 14), title, font=ImageFont.truetype(SANS_B, 22), fill=(190, 195, 200))
    return out


if __name__ == "__main__":
    r = frame(radar(), "Large LCD - radar view (rotates with the ship, forward = up)")
    l = frame(list_screen(), "Cockpit screen - deposit list (direction relative to the ship)")
    W = r.width + l.width + 30
    H = max(r.height, l.height)
    sheet = Image.new("RGB", (W, H), (40, 44, 48))
    sheet.paste(r, (0, 0))
    sheet.paste(l, (r.width + 30, 0))
    sheet.save(OUT + "map.png")
    print("saved", sheet.size)
