#!/usr/bin/env python3
"""Markdown result tables for docs/BENCHMARKS.md from a tools/crosscheck.py JSON file.

Usage: python3 tools/bench/tables.py docs/benchmarks-0.1.1.json > tables.md
"""
import json
import sys
from pathlib import Path

NAMES = {
    "zip": "zip -9", "bzip2": "tar \\| bzip2 -9", "brotli": "tar \\| brotli -q 11 -w 24",
    "zstd": "tar \\| zstd --ultra -22 --long=31", "xz": "tar \\| xz -9e", "7z": "7-Zip LZMA2 -mx=9 (256 MB dictionary)",
    "7z-ppmd": "7-Zip PPMd (order 32, 1 GB)", "lrzip": "tar \\| lrzip -z (ZPAQ back end)", "zpaq": "ZPAQ -m5",
}


DESC = {
    "mixed-folder": ("Mixed folder", "20 license texts (4 of them duplicates) and a 1.3 MiB CSV; a .NET IL assembly (6.3 MiB) and two x64 ELF "
                     "binaries; 1.2 MB of 12-byte telemetry records; a 16-bit stereo WAV; a 24-bit BMP; three PNG and three gzip files; "
                     "an empty file and a file with a non-ASCII name."),
    "windows-app": ("Windows application folder", "Osmium's own self-contained win-x64 publish output: 273 DLLs (.NET ReadyToRun "
                    "assemblies, which carry IL plus precompiled x64 code, and native runtime DLLs), 3 EXEs, 4 PDBs and 4 JSON files — "
                    "the kind of payload an installer ships."),
}


def t(s):
    return "< 0.1 s" if s < 0.05 else f"{s:.1f} s" if s < 90 else f"{int(s // 60)} min {round(s % 60):02d} s"


def main():
    raw = json.loads(Path(sys.argv[1]).read_text())
    rows = [r for r in raw if r.get("ok")]
    for ds in dict.fromkeys(r["dataset"] for r in rows):
        rs = sorted((r for r in rows if r["dataset"] == ds), key=lambda r: r["bytes"])
        xz = next((r for r in rs if r["family"] == "xz"), None)  # absent when xz did not run (e.g. crosscheck --only)
        title, desc = DESC[ds]
        print(f"### {title} — {rs[0]['dataset_bytes'] / 2**20:.1f} MiB, {rs[0]['files']} files\n")
        print(desc + "\n")
        print("| Tool | Size (bytes) | Of original | vs `xz -9e` | Compress | Extract | Round trip |")
        print("|---|---:|---:|---:|---:|---:|---|")
        for r in rs:
            osm = r["family"].startswith("osmium")
            name = f"**Osmium {r['family'][7:].capitalize()}**" if osm else NAMES[r["family"]]
            vs = "—" if xz is None or r is xz else f"{(r['bytes'] / xz['bytes'] - 1) * 100:+.1f} %".replace("-", "−")
            print(f"| {name} | {r['bytes']:,} | {r['ratio'] * 100:.2f} % | {vs} | {t(r['compress_s'])} | {t(r['decompress_s'])} | ✔ identical |")
        print()
    missing = [r for r in raw if not r.get("ok")]
    for r in missing:
        print(f"<!-- not run: {r['dataset']} {r['tool']}: {r.get('note', '')} -->", file=sys.stderr)


if __name__ == "__main__":
    main()
