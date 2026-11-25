#!/usr/bin/env python3
"""Builds the app's brand fonts: static, Latin-only instances of the product page's typefaces.

Downloads the upstream variable fonts and their licences from the google/fonts repository at the commit recorded in
fonts.json, checks them against the recorded SHA-256, makes one static instance per role at the recorded axis
coordinates, subsets it to the characters the app's own words use, renames it to a family of its own, and writes it
with its licence to src/Osmium.App/Fonts. Run it from anywhere; it needs Python 3 and the packages in
requirements.txt.

    python3 tools/fonts/make_app_fonts.py            # build; fails when a download or a result differs
    python3 tools/fonts/make_app_fonts.py --record   # build and record the hashes in fonts.json
"""

import argparse
import hashlib
import io
import json
import sys
import urllib.parse
import urllib.request
from pathlib import Path

from fontTools.subset import Options, Subsetter
from fontTools.ttLib import TTFont, newTable
from fontTools.varLib import instancer

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
OUT = ROOT / "src" / "Osmium.App" / "Fonts"
SPEC = HERE / "fonts.json"

# Name records kept from the upstream font (copyright, version, manufacturer, designer, URLs, licence); the family and
# style names are written new.
KEEP_NAMES = {0, 5, 7, 8, 9, 11, 12, 13, 14}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def download(url: str) -> bytes:
    with urllib.request.urlopen(url, timeout=120) as r:
        return r.read()


def unicodes(spec: str) -> list[int]:
    out = []
    for part in spec.split(","):
        part = part.strip().upper().removeprefix("U+")
        lo, _, hi = part.partition("-")
        out.extend(range(int(lo, 16), int(hi or lo, 16) + 1))
    return out


def rename(font: TTFont, family: str) -> None:
    name = font["name"]
    ps = family.replace(" ", "") + "-Regular"
    name.names = [n for n in name.names if n.nameID in KEEP_NAMES]
    for platform, encoding, language in ((3, 1, 0x409), (1, 0, 0)):
        name.setName(family, 1, platform, encoding, language)
        name.setName("Regular", 2, platform, encoding, language)
        name.setName(f"{ps};Osmium", 3, platform, encoding, language)
        name.setName(family, 4, platform, encoding, language)
        name.setName(ps, 6, platform, encoding, language)
    # One face per family, matched as its regular style: the weight is in the outlines, so Windows never adds bold.
    os2 = font["OS/2"]
    os2.usWeightClass = 400
    os2.usWidthClass = 5
    os2.fsSelection = (os2.fsSelection & ~0b1100001) | 0b1000000  # not italic, not bold, regular
    font["head"].macStyle = 0
    if "post" in font:
        font["post"].formatType = 3.0  # no glyph names
    for tag in ("STAT", "DSIG"):
        if tag in font:
            del font[tag]


def build(source: bytes, inst: dict, chars: list[int]) -> bytes:
    font = TTFont(io.BytesIO(source), recalcTimestamp=False)
    modified = font["head"].modified
    font = instancer.instantiateVariableFont(font, {k: float(v) for k, v in inst["axes"].items()})
    options = Options()
    options.layout_features = ["*"]
    options.name_IDs = ["*"]
    options.name_languages = ["*"]
    options.notdef_outline = True
    options.glyph_names = False
    options.hinting = True
    subsetter = Subsetter(options)
    subsetter.populate(unicodes=chars)
    subsetter.subset(font)
    rename(font, inst["family"])
    if "gasp" not in font:
        gasp = newTable("gasp")
        gasp.version = 1
        gasp.gaspRange = {0xFFFF: 0x000F}  # smoothing and grid fitting at every size
        font["gasp"] = gasp
    font["head"].modified = modified
    out = io.BytesIO()
    font.save(out)
    return out.getvalue()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--record", action="store_true", help="record the hashes of the downloads and results in fonts.json")
    args = parser.parse_args()
    spec = json.loads(SPEC.read_text(encoding="utf-8"))
    base = f"https://raw.githubusercontent.com/google/fonts/{spec['commit']}/"
    chars = unicodes(spec["unicodes"])
    OUT.mkdir(parents=True, exist_ok=True)
    problems = []
    sources = {}
    for fam in spec["families"]:
        for key in ("font", "license"):
            data = download(base + urllib.parse.quote(fam[key]))
            digest = sha256(data)
            if args.record:
                fam[key + "Sha256"] = digest
            elif fam.get(key + "Sha256") != digest:
                problems.append(f"{fam[key]}: SHA-256 {digest}, recorded {fam.get(key + 'Sha256')}")
            sources[(fam["name"], key)] = data
        # The licence file starts with the font's own copyright notice (name ID 0). Where the upstream licence states
        # another year, both lines are kept.
        license_text = sources[(fam["name"], "license")].decode("utf-8").replace("\r\n", "\n")
        notice = (TTFont(io.BytesIO(sources[(fam["name"], "font")]))["name"].getDebugName(0) or "").strip()
        if not notice.startswith("Copyright"):
            problems.append(f"{fam['font']}: no copyright notice in name ID 0")
        elif license_text.splitlines()[0].strip() != notice:
            license_text = notice + "\n" + license_text
        (OUT / fam["licenseFile"]).write_text(license_text, encoding="utf-8", newline="\n")
    for inst in spec["instances"]:
        fam = next(f for f in spec["families"] if f["name"] == inst["source"])
        data = build(sources[(fam["name"], "font")], inst, chars)
        first_line = (OUT / fam["licenseFile"]).read_text(encoding="utf-8").splitlines()[0].strip()
        notice = TTFont(io.BytesIO(data))["name"].getDebugName(0) or ""
        if first_line != notice.strip():
            problems.append(f"{inst['file']}: the copyright notice \"{notice}\" is not the licence's first line \"{first_line}\"")
        digest = sha256(data)
        if args.record:
            inst["sha256"] = digest
        elif inst.get("sha256") != digest:
            problems.append(f"{inst['file']}: SHA-256 {digest}, recorded {inst.get('sha256')}")
        (OUT / inst["file"]).write_bytes(data)
        print(f"{inst['file']}: {inst['family']}, {len(data):,} bytes")
    if args.record:
        SPEC.write_text(json.dumps(spec, indent=2) + "\n", encoding="utf-8")
    total = sum((OUT / i["file"]).stat().st_size for i in spec["instances"])
    print(f"total {total:,} bytes")
    for p in problems:
        print("problem: " + p, file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
