"""Generates assets/osmium.ico (app) and assets/archive.ico (.osa documents).

Design: a hexagonal "dense crystal" (osmium crystallises hexagonally) drawn as an
isometric cube with three shaded faces, on an indigo->cyan rounded square.
Run: python3 tools/make_icons.py
"""
import math
from PIL import Image, ImageDraw

SS = 4  # supersampling factor
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def gradient(size, c1, c2):
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * size - 2)
            px[x, y] = lerp(c1, c2, t) + (255,)
    return img


def cube(draw, cx, cy, r, shades):
    # hexagon vertices (pointy top)
    pts = [(cx + r * math.cos(math.radians(a)), cy + r * math.sin(math.radians(a)))
           for a in (-90, -30, 30, 90, 150, 210)]
    c = (cx, cy)
    top = [pts[0], pts[1], c, pts[5]]
    right = [pts[1], pts[2], pts[3], c]
    left = [pts[5], c, pts[3], pts[4]]
    draw.polygon(top, fill=shades[0])
    draw.polygon(right, fill=shades[1])
    draw.polygon(left, fill=shades[2])


def app_icon(size):
    S = size * SS
    base = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bg = gradient(S, (58, 42, 150), (20, 160, 210))
    mask = Image.new("L", (S, S), 0)
    m = ImageDraw.Draw(mask)
    pad = int(S * 0.04)
    m.rounded_rectangle([pad, pad, S - pad, S - pad], radius=int(S * 0.22), fill=255)
    base.paste(bg, (0, 0), mask)
    d = ImageDraw.Draw(base)
    cube(d, S / 2, S / 2 + S * 0.01, S * 0.33,
         [(240, 244, 255, 255), (170, 182, 215, 255), (120, 132, 175, 255)])
    # inner dense core: smaller cube, inverted shading
    if size >= 32:
        cube(d, S / 2, S / 2 + S * 0.01, S * 0.13,
             [(60, 50, 140, 255), (40, 110, 170, 255), (30, 70, 130, 255)])
    return base.resize((size, size), Image.LANCZOS)


def archive_icon(size):
    S = size * SS
    base = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(base)
    l, t, r, b = S * 0.16, S * 0.04, S * 0.84, S * 0.96
    fold = S * 0.2
    page = [(l, t), (r - fold, t), (r, t + fold), (r, b), (l, b)]
    d.polygon(page, fill=(246, 248, 252, 255), outline=(120, 130, 160, 255), width=max(1, SS))
    d.polygon([(r - fold, t), (r - fold, t + fold), (r, t + fold)], fill=(205, 212, 230, 255))
    cube(d, S / 2, S * 0.6, S * 0.24,
         [(58, 42, 150, 255), (20, 160, 210, 255), (32, 96, 180, 255)])
    return base.resize((size, size), Image.LANCZOS)


for name, fn in (("osmium", app_icon), ("archive", archive_icon)):
    imgs = [fn(s) for s in SIZES]
    imgs[-1].save(f"assets/{name}.ico", sizes=[(s, s) for s in SIZES], append_images=imgs[:-1])
    imgs[-1].save(f"assets/{name}-256.png")
    print("wrote", name)
