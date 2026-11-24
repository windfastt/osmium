#!/usr/bin/env python3
"""Import the app's real screenshots into docs/img for the product page.

The Windows CI job runs `Osmium.App.exe --selftest-ui <dir>` (light) and `... <dir> dark`, which render every page and
dialog of the real app from a sample folder it creates, and write PNGs plus manifest.json (file, width, height,
theme, caption). On pushes to main, CI force-pushes that folder to the orphan branch `screenshots`.

  python3 tools/site/fetch_screenshots.py                 # fetch origin/screenshots
  python3 tools/site/fetch_screenshots.py --from DIR      # use a local folder (e.g. an unzipped CI artifact)

Only the screenshots the page shows (build.py's GALLERY, light and dark) are imported. Each is checked against its
manifest entry, converted to lossless WebP and written to docs/img with a new docs/img/manifest.json that records the
commit the screenshots were rendered from. Then run `python3 tools/site/build.py`.

The branch content is treated as data: names must match [a-z0-9-]+.png, images are size-checked, and nothing is
written outside docs/img.
"""
import argparse
import io
import json
import re
import subprocess
import sys
from pathlib import Path

from PIL import Image

SITE = Path(__file__).resolve().parent
ROOT = SITE.parents[1]
OUT = ROOT / "docs" / "img"
sys.path.insert(0, str(SITE))
from build import GALLERY  # noqa: E402  (same folder; defines the ids the page uses)

NAME = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*\.png$")
MAX_SIDE, MAX_BYTES = 4096, 16 << 20


def git(*args, binary=False):
    r = subprocess.run(["git", "-C", str(ROOT), *args], capture_output=True, check=False)
    if r.returncode != 0:
        sys.exit(f"git {' '.join(args)} failed: {r.stderr.decode(errors='replace').strip()}")
    return r.stdout if binary else r.stdout.decode()


def from_branch(ref):
    remote, _, branch = ref.partition("/")
    git("fetch", "--quiet", remote, f"+refs/heads/{branch}:refs/remotes/{remote}/{branch}")
    names = git("ls-tree", "--name-only", ref).split()
    message = git("log", "-1", "--format=%s%n%b", ref).strip()
    files = {n: git("show", f"{ref}:{n}", binary=True) for n in names if n.endswith((".png", ".json"))}
    return files, message


def from_folder(folder):
    files = {p.name: p.read_bytes() for p in folder.iterdir() if p.is_file() and p.suffix in (".png", ".json")}
    return files, f"local folder {folder}"


def entries(files):
    """Merge manifest.json and any manifest-*.json (a dark run may write its own)."""
    merged = {}
    for name in sorted(n for n in files if re.fullmatch(r"manifest(-[a-z]+)?\.json", n)):
        data = json.loads(files[name])
        if isinstance(data, dict):  # tolerate {"screenshots": [...]} or {"files": [...]}
            data = data.get("screenshots") or data.get("files") or []
        for e in data:
            merged[e["file"]] = e
    return merged


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ref", default="origin/screenshots", help="remote branch to read (default: origin/screenshots)")
    ap.add_argument("--from", dest="folder", type=Path, help="read a local folder instead of the branch")
    ap.add_argument("--out", type=Path, default=OUT, help="output folder (default: docs/img)")
    a = ap.parse_args()
    out_dir = a.out.resolve()
    files, message = from_folder(a.folder) if a.folder else from_branch(a.ref)
    manifest = entries(files)
    if not manifest:
        sys.exit("no manifest.json found: the self-test writes one next to the PNGs")
    source = re.search(r"\b[0-9a-f]{40}\b", message)

    wanted = [sid for sid, _ in GALLERY]
    wanted += [sid + "-dark" for sid in wanted]
    out, problems = [], []
    out_dir.mkdir(parents=True, exist_ok=True)
    for sid in wanted:
        name = sid + ".png"
        e = manifest.get(name)
        if e is None or name not in files:
            if not sid.endswith("-dark"):
                problems.append(f"{name}: not among the screenshots; the page leaves it out")
            continue
        if not NAME.match(name) or len(files[name]) > MAX_BYTES:
            problems.append(f"{name}: unexpected name or size, skipped")
            continue
        im = Image.open(io.BytesIO(files[name]))
        im.load()
        w, h = im.size
        if w > MAX_SIDE or h > MAX_SIDE or (w, h) != (int(e["width"]), int(e["height"])):
            problems.append(f"{name}: {w}x{h} does not match the manifest ({e['width']}x{e['height']}), skipped")
            continue
        caption = " ".join(str(e.get("caption", "")).split())
        if not caption:
            problems.append(f"{name}: no caption, skipped")
            continue
        theme = "dark" if sid.endswith("-dark") else "light"
        buf = io.BytesIO()
        im.convert("RGBA" if im.mode in ("RGBA", "LA", "P") else "RGB").save(buf, "WEBP", lossless=True, method=6)
        (out_dir / f"{sid}.webp").write_bytes(buf.getvalue())
        out.append({"file": f"{sid}.webp", "width": w, "height": h, "theme": theme, "caption": caption})
        print(f"{sid}.webp  {w}x{h}  {len(files[name]) // 1024} KiB png -> {len(buf.getvalue()) // 1024} KiB webp")

    # Remove only screenshots an earlier import wrote (listed in SOURCE.json) that this import did not write again;
    # other images in the folder (the logo, the social preview) are not ours to delete.
    keep = {e["file"] for e in out}
    previous = out_dir / "SOURCE.json"
    managed = json.loads(previous.read_text()).get("screenshots", []) if previous.exists() else []
    for name in managed:
        p = out_dir / name
        if name not in keep and NAME.match(name.replace(".webp", ".png")) and p.is_file():
            p.unlink()
    meta = {"rendered_from": source.group(0) if source else None, "screenshots": out}
    (out_dir / "manifest.json").write_text(json.dumps(out, indent=2, ensure_ascii=False) + "\n")
    (out_dir / "SOURCE.json").write_text(json.dumps(meta | {"screenshots": [e["file"] for e in out]}, indent=2) + "\n")
    for p in problems:
        print("note:", p, file=sys.stderr)
    print(f"{len(out)} screenshots in {out_dir}; rendered from {meta['rendered_from'] or message}")
    print("next: python3 tools/site/build.py")


if __name__ == "__main__":
    main()
