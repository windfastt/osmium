"""Make static Instrument Sans body instances (400, 600) from the product page's variable woff2.

Usage: python -I make_body_fonts.py <docs/fonts dir> <out dir>
Also writes the decompressed variable TTFs (for the variable-font experiment).
"""
import sys
from pathlib import Path

from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

src, out = Path(sys.argv[1]), Path(sys.argv[2])
out.mkdir(parents=True, exist_ok=True)


def rename(font, family, sub, weight):
    name = font["name"]
    full = f"{family} {sub}" if sub != "Regular" else family
    ps = (family + "-" + sub).replace(" ", "")
    for rec in list(name.names):
        if rec.nameID in (1, 2, 3, 4, 6, 16, 17, 21, 22, 25):
            name.removeNames(nameID=rec.nameID)
    for pid, eid, lid in ((3, 1, 0x409), (1, 0, 0)):
        name.setName(family, 1, pid, eid, lid)
        name.setName(sub, 2, pid, eid, lid)
        name.setName(f"{ps};osmium-proto", 3, pid, eid, lid)
        name.setName(full, 4, pid, eid, lid)
        name.setName(ps, 6, pid, eid, lid)
    font["OS/2"].usWeightClass = weight
    font["OS/2"].usWidthClass = 5
    # fsSelection: bit 0 italic, bit 5 bold, bit 6 regular
    sel = font["OS/2"].fsSelection & ~0b1100001
    font["OS/2"].fsSelection = sel | (1 << 6 if weight == 400 else 0)
    font["head"].macStyle = 0


for stem, family in (("instrument-sans-latin", "Instrument Sans"), ("anybody-latin", "Anybody"), ("martian-mono-latin", "Martian Mono")):
    var = TTFont(src / f"{stem}.woff2")
    var.flavor = None
    var.save(out / f"{stem}-variable.ttf")
    print(stem, "axes:", [(a.axisTag, a.minValue, a.defaultValue, a.maxValue) for a in var["fvar"].axes])

for weight, sub in ((400, "Regular"), (600, "SemiBold")):
    var = TTFont(src / "instrument-sans-latin.woff2")
    var.flavor = None
    inst = instancer.instantiateVariableFont(var, {"wght": weight, "wdth": 100})
    rename(inst, "Instrument Sans Body", sub, weight)
    p = out / f"InstrumentSansBody-{sub}.ttf"
    inst.save(p)
    print("wrote", p, p.stat().st_size)
