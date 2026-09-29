"""Workshop preview image: the radar mock-up with the script's name on it.

Writes images/cover.png (1024 x 1024, Steam shows the preview square and
allows at most 1 MB). Run: python3 workshop/cover.py (needs Pillow).
"""
import os
from PIL import Image, ImageDraw, ImageFont

from map_mockup import radar, OUT, SANS_B, BG, PANEL, GRID, CYAN, TEXT, DIM, ROUTE

SANS = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"


def cover():
    img = radar().convert("RGBA")
    S = img.width
    overlay = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(overlay)
    # title band over the radar's own header
    d.rectangle([0, 0, S, 150], fill=PANEL + (255,))
    d.line([0, 150, S, 150], fill=GRID + (255,), width=3)
    d.text((S // 2, 20), "ACCELERATION CONTROL", font=ImageFont.truetype(SANS_B, 66), fill=CYAN + (255,), anchor="ma")
    d.text((S // 2, 102), "flight assistant for Space Engineers", font=ImageFont.truetype(SANS, 30), fill=DIM + (255,), anchor="ma")
    # feature band at the bottom (covers the radar's button row)
    d.rectangle([0, S - 118, S, S], fill=BG + (255,))
    d.line([0, S - 118, S, S - 118], fill=GRID + (255,), width=3)
    f = ImageFont.truetype(SANS_B, 30)
    for row, words in enumerate([["acceleration limit", "ore map", "routes"], ["docking", "planets", "landing"]]):
        text = "   \u00b7   ".join(words)
        y = S - 100 + row * 46
        x = (S - d.textlength(text, font=f)) / 2
        for i, w in enumerate(words):
            d.text((x, y), w, font=f, fill=(ROUTE if (i + row) % 2 else TEXT) + (255,))
            x += d.textlength(w, font=f)
            if i < len(words) - 1:
                d.text((x, y), "   \u00b7   ", font=f, fill=DIM + (255,))
                x += d.textlength("   \u00b7   ", font=f)
    out = Image.alpha_composite(img, overlay).convert("RGB")
    path = OUT + "cover.png"
    out.save(path, optimize=True)
    print("saved", path, out.size, os.path.getsize(path) // 1024, "KB")


if __name__ == "__main__":
    cover()
