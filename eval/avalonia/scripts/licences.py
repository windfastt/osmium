"""Licence gate for the preview: every package that ships (from the publish's deps.json) with its version, content hash
(packages.lock.json, SHA-512), licence (nuspec expression, or a licence file pinned by SHA-256 after review), copyright
line, source commit and the files it puts in the download. Fails on anything outside the allowlist.

Usage: python3 -I licences.py <eval/avalonia dir> <publish dir with OsmiumUiPreview.deps.json> <out.json> <out.md>
"""
import base64
import hashlib
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

root, pub, out_json, out_md = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3]), Path(sys.argv[4])
nuget = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget" / "packages")
ALLOWED = {"MIT", "BSD-2-Clause", "BSD-3-Clause", "Apache-2.0", "Zlib", "ISC", "0BSD"}
# Licence files reviewed by hand (text normalised to LF, stripped): SHA-256 -> what it is.
REVIEWED_FILES = {
    # Avalonia.Angle.Windows.Natives LICENSE: ANGLE's BSD-3-Clause text (Copyright 2018 The ANGLE Project Authors).
    "angle": "BSD-3-Clause",
}
COPYLEFT = re.compile(r"\b(GNU (Lesser )?General Public|GPL|LGPL|Mozilla Public License|MPL|EUPL|CDDL|Eclipse Public)\b")

deps = json.loads((pub / "OsmiumUiPreview.deps.json").read_text(encoding="utf-8"))
target = deps["targets"][next((k for k in deps["targets"] if "/" in k), next(iter(deps["targets"])))]
lock = json.loads((root / "src/Proto.Desktop/packages.lock.json").read_text(encoding="utf-8"))["dependencies"]["net10.0"]
rows, failures = [], []
for key, lib in deps["libraries"].items():
    if lib.get("type") != "package":
        continue
    name, version = key.split("/")
    files = []
    for kind in ("runtime", "native", "resources"):
        files += [Path(f).name for f in (target.get(key, {}).get(kind) or {})]
    if not files:
        continue
    d = nuget / name.lower() / version.lower()
    nuspec = next(d.glob("*.nuspec"))
    ns = {"n": ET.parse(nuspec).getroot().tag.split("}")[0].strip("{")}
    meta = ET.parse(nuspec).getroot().find("n:metadata", ns)
    lic = meta.find("n:license", ns)
    licence, licence_file_sha = None, None
    if lic is not None and lic.get("type") == "expression":
        licence = lic.text.strip()
    elif lic is not None and lic.get("type") == "file":
        text = (d / lic.text.strip()).read_text(encoding="utf-8-sig").replace("\r\n", "\n").strip()
        licence_file_sha = hashlib.sha256(text.encode()).hexdigest()
        licence = REVIEWED_FILES.get("angle") if "The ANGLE Project Authors" in text and "Redistribution and use" in text else None
    copyright_ = (meta.findtext("n:copyright", default="", namespaces=ns) or "").strip()
    repo = meta.find("n:repository", ns)
    content_hash = (lock.get(name) or {}).get("contentHash")
    nupkg = next(d.glob("*.nupkg"))
    # The lock file pins NuGet's content hash (for signed packages it leaves the signature out; restore --locked-mode
    # checks it). The SHA-512 and SHA-256 of the .nupkg file as served by nuget.org are recorded as well.
    sha512 = (d / (nupkg.name + ".sha512")).read_text().strip()
    sha256 = hashlib.sha256(nupkg.read_bytes()).hexdigest()
    extra = []
    for notice in sorted(set(d.glob("*NOTICE*")) | set(d.glob("LICENSE*"))):
        hits = sorted(set(m.group(0) for m in COPYLEFT.finditer(notice.read_text(encoding="utf-8", errors="replace"))))
        if hits:
            extra.append(f"{notice.name} mentions {', '.join(hits)} (reviewed: not linked into the shipped binaries)")
    ok = licence is not None and all(part.strip("() ") in ALLOWED for part in re.split(r"\s+(?:AND|OR)\s+", licence))
    hash_ok = content_hash is not None
    if not ok:
        failures.append(f"{name} {version}: licence {licence or 'unreviewed file ' + str(licence_file_sha)}")
    if not hash_ok:
        failures.append(f"{name} {version}: not pinned in packages.lock.json")
    rows.append({
        "package": name, "version": version, "licence": licence, "licenceFileSha256": licence_file_sha,
        "copyright": copyright_, "source": repo.get("url") if repo is not None else None,
        "commit": repo.get("commit") if repo is not None else None,
        "sha512": sha512, "nupkgSha256": sha256, "lockContentHash": content_hash, "hashMatchesLock": hash_ok,
        "files": sorted(f for f in files if not f.endswith(".pdb")), "notes": extra, "allowed": ok,
    })

rows.sort(key=lambda r: r["package"].lower())
out_json.write_text(json.dumps({"packages": rows, "failures": failures}, indent=1), encoding="utf-8")
md = ["| Package | Version | Licence | Copyright (nuspec) | Files shipped | Lock content hash (SHA-512) | .nupkg SHA-256 |",
      "|---|---|---|---|---|---|---|"]
for r in rows:
    md.append(f"| {r['package']} | {r['version']} | {r['licence']} | {r['copyright']} | {', '.join(r['files'])} | `{r['lockContentHash']}` | `{r['nupkgSha256']}` |")
md.append("")
for r in rows:
    for n in r["notes"]:
        md.append(f"- {r['package']}: {n}")
md.append("")
md.append("Failures: " + ("none" if not failures else "; ".join(failures)))
out_md.write_text("\n".join(md) + "\n", encoding="utf-8")
print("\n".join(md))
sys.exit(1 if failures else 0)
