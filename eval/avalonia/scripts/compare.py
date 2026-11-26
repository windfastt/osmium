"""Side by side: product page crops (Chromium), the prototype rendered headless on Linux (Skia), and optionally the
same prototype captured on Windows; per theme. Prints the dominant colours of each crop and a token verdict.

Usage: python3 -I compare.py <site reference dir> <linux shots dir> [<windows shots dir> <label>]...
Writes <linux shots>/compare-{light,dark}.png (or <windows shots>/compare-{light,dark}.png when Windows shots are given)
and exits 1 if a crop's dominant colour differs from the site's token (more than 2 levels per channel).
"""
import json
import sys
from collections import Counter
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ref, linux = Path(sys.argv[1]), Path(sys.argv[2])
extra = [(Path(sys.argv[i]), sys.argv[i + 1]) for i in range(3, len(sys.argv) - 1, 2)]
try:
    font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 13)
except OSError:
    font = ImageFont.load_default()
TOKENS = {
    "light": {"Page": 0xEEF0F4, "Card": 0xFFFFFF, "Mark": 0x5B4BD5, "Accent": 0x3A2A96},
    "dark": {"Page": 0x0E0F13, "Card": 0x17181E, "Mark": 0x9085E9, "Accent": 0xB3A9FF},
}


def top(img, n=3):
    rgb = img.convert("RGB")
    c = Counter(rgb.get_flattened_data() if hasattr(rgb, "get_flattened_data") else rgb.getdata())
    total = sum(c.values())
    return [((r << 16) | (g << 8) | b, k / total) for (r, g, b), k in c.most_common(n)]


def fmt(t):
    return ", ".join(f"#{rgb:06X} {100 * share:.0f}%" for rgb, share in t)


def near(a, b, tol=2):
    return all(abs(((a >> s) & 255) - ((b >> s) & 255)) <= tol for s in (16, 8, 0))


failures = 0
for theme in ("light", "dark"):
    boxes = json.loads((linux / f"{theme}-boxes.json").read_text())
    shots = [("Linux headless (Skia)", Image.open(linux / f"{theme}.png").convert("RGB"))]
    for d, label in extra:
        for name in (f"win-{theme}-screen.png", f"win-{theme}-printwindow.png"):
            if (d / name).exists():
                shots.append((label, Image.open(d / name).convert("RGB")))
                break

    def crop(img, name, pad=0):
        x, y, w, h = boxes[name]
        return img.crop((int(x) - pad, int(y) - pad, int(x + w) + pad, int(y + h) + pad))

    spec = [
        ("section head (.spec + h2 + .lede)", "sec-head-app", "Header", 0, "Page"),
        ("result tiles (.tiles .st)", "tiles", "Tiles", 1, "Card"),
        ("card (.card)", "card", "Card", 1, "Card"),
        ("primary button (.btn-sm .btn-primary)", "btn-primary", "Primary", 0, "Accent"),
        ("ghost button (.btn-ghost)", "btn-ghost", "Ghost", 0, "Card"),
        ("meter (.meter)", "meter", "Meter", 0, "Mark"),
    ]
    rows = []
    print(f"== {theme}")
    for label, site_name, box, pad, token in spec:
        site = Image.open(ref / f"{theme}-{site_name}.png").convert("RGB")
        crops = [crop(img, box, pad) for _, img in shots]
        rows.append((label, site, crops))
        expected = TOKENS[theme][token]
        print(f"{label:40s} token {token} #{expected:06X}")
        print(f"{'':40s} site {site.size}: {fmt(top(site))}")
        for (src, _), c in zip(shots, crops):
            t = top(c)
            ok = bool(t) and near(t[0][0], expected)
            failures += 0 if ok else 1
            print(f"{'':40s} {src} {c.size}: {fmt(t)} -> {'ok' if ok else 'DIFFERS'}")
    bg = (255, 255, 255) if theme == "light" else (30, 31, 38)
    fg = (20, 21, 27) if theme == "light" else (236, 238, 244)
    colw = max(max(s.width for _, s, _ in rows), max(c.width for _, _, cs in rows for c in cs))
    cols = 1 + len(shots)
    W = cols * (colw + 20) + 20
    H = sum(max([s.height] + [c.height for c in cs]) + 40 for _, s, cs in rows) + 40
    out = Image.new("RGB", (W, H), bg)
    d = ImageDraw.Draw(out)
    for i, title in enumerate(["product page (docs/index.html, Chromium)"] + [s for s, _ in shots]):
        d.text((20 + i * (colw + 20), 10), title, fill=fg, font=font)
    y = 34
    for label, site, crops in rows:
        d.text((20, y), label, fill=fg, font=font)
        y += 20
        for i, img in enumerate([site] + crops):
            out.paste(img, (20 + i * (colw + 20), y))
        y += max([site.height] + [c.height for c in crops]) + 20
    target = (extra[0][0] if extra else linux) / f"compare-{theme}.png"
    out.save(target)
    print("wrote", target, out.size)
sys.exit(1 if failures else 0)
