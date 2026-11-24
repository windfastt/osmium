#!/usr/bin/env python3
"""Build the two benchmark folders used by tools/crosscheck.py.

  python3 tools/bench/make_corpora.py mixed  <dest>               # ~16 MiB of mixed data
  python3 tools/bench/make_corpora.py winapp <dest> --from DIR    # copy of an Osmium win-x64 publish folder

"mixed" copies real files from a Debian/Ubuntu system with the .NET 10 SDK (license texts, an x64 ELF library and
program, a .NET IL assembly, gzip changelogs, PNG icons) and generates the rest deterministically (CSV, 12-byte
telemetry records, a 16-bit stereo WAV, a 24-bit BMP, duplicates, an empty file and a non-ASCII file name).
Missing system files are skipped with a warning. Generated files are new in this script, so a rebuilt folder is
equivalent to, not byte-identical with, the one measured for 0.1.1: docs/benchmarks-0.1.1-corpus.sha256 lists
the exact files of that run.

"winapp" is Osmium's own self-contained Windows build (build/publish.ps1 -Arch x64 -SkipInstaller writes it to
artifacts/publish/win-x64), the kind of payload an installer ships.

Each folder gets a MANIFEST.sha256 next to it (outside the folder, so it is not benchmarked).
"""
import argparse
import glob
import hashlib
import math
import random
import shutil
import struct
import sys
import zlib
from pathlib import Path

LICENSES = ["Apache-2.0", "Artistic", "BSD", "CC0-1.0", "GFDL", "GFDL-1.2", "GFDL-1.3", "GPL", "GPL-1", "GPL-2", "GPL-3",
            "LGPL", "LGPL-2", "LGPL-2.1", "LGPL-3", "MPL-1.1", "MPL-2.0"]
SYSTEM_FILES = {
    "bin/libcrypto.so.3": ["/usr/lib/x86_64-linux-gnu/libcrypto.so.3"],
    "bin/xz": ["/usr/bin/xz"],
    "bin/Microsoft.Build.dll": sorted(glob.glob("/usr/lib/dotnet/sdk/10.*/Microsoft.Build.dll")) + sorted(glob.glob("/usr/share/dotnet/sdk/10.*/Microsoft.Build.dll")),
    "media/changelog.gz": sorted(glob.glob("/usr/share/doc/gcc-1*-base/changelog.gz")),
    "media/changelog.Debian.gz": sorted(glob.glob("/usr/share/doc/gcc-1*-base/changelog.Debian.gz")),
    "media/changelog.libstdc++.gz": sorted(glob.glob("/usr/share/doc/gcc-1*-base/C++/changelog.libstdc++.gz")),
    "media/display-im6.q16.png": ["/usr/share/icons/hicolor/512x512/apps/display-im6.q16.png"],
    "media/gnupg-module-overview.png": ["/usr/share/info/gnupg-module-overview.png"],
}


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def csv_rows(rng, target):
    cities = ["Lisbon", "Oslo", "Quito", "Nairobi", "Osaka", "Perth", "Tucson", "Ghent", "Lyon", "Cusco"]
    lines, size, day = ["id,date,station,city,temperature_c,humidity_pct,pressure_hpa,status"], 0, 0
    while size < target:
        day += 1
        for st in range(8):
            line = (f"{len(lines)},2025-{1 + day // 28 % 12:02d}-{1 + day % 28:02d},ST{st:03d},{cities[st % len(cities)]},"
                    f"{rng.gauss(14, 8):.1f},{rng.randint(20, 99)},{rng.gauss(1013, 9):.1f},{'ok' if rng.random() > 0.03 else 'check'}")
            lines.append(line)
            size += len(line) + 1
    return ("\n".join(lines) + "\n").encode()


def telemetry(rng, records):
    out, t, a, b = bytearray(), 1_700_000_000, 0, 0.0
    for _ in range(records):
        t += 1000 + rng.randint(-3, 3)
        a = max(-32768, min(32767, a + rng.randint(-40, 40)))
        b += rng.gauss(0, 0.01)
        out += struct.pack("<IhHf", t & 0xFFFFFFFF, a, rng.randint(0, 3) << 14 | 512, b)
    return bytes(out)


def wav(seconds=6.0, rate=44100):
    n = int(seconds * rate)
    frames = bytearray()
    for i in range(n):
        tt = i / rate
        left = 0.45 * math.sin(2 * math.pi * 440 * tt) + 0.1 * math.sin(2 * math.pi * 880 * tt)
        right = 0.40 * math.sin(2 * math.pi * 660 * tt + 0.3)
        frames += struct.pack("<hh", int(left * 32767), int(right * 32767))
    hdr = b"RIFF" + struct.pack("<I", 36 + len(frames)) + b"WAVE" + b"fmt " + struct.pack("<IHHIIHH", 16, 1, 2, rate, rate * 4, 4, 16)
    return hdr + b"data" + struct.pack("<I", len(frames)) + bytes(frames)


def bmp(w=400, h=300):
    row = (w * 3 + 3) & ~3
    pixels = bytearray()
    for y in range(h):
        line = bytearray()
        for x in range(w):
            line += bytes(((x * 255) // w, (y * 255) // h, ((x + y) * 255) // (w + h)))
        pixels += line + b"\0" * (row - len(line))
    hdr = b"BM" + struct.pack("<IHHI", 54 + len(pixels), 0, 0, 54) + struct.pack("<IiiHHIIiiII", 40, w, h, 1, 24, 0, len(pixels), 2835, 2835, 0, 0)
    return hdr + bytes(pixels)


def png(rng, w=160, h=120):
    raw = b"".join(b"\0" + bytes(rng.randrange(256) for _ in range(w * 3)) for _ in range(h))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def mixed(dest):
    rng = random.Random(20261124)
    for name in LICENSES:
        src = Path("/usr/share/common-licenses") / name
        if src.exists():
            write(dest / "docs" / name, src.read_bytes())
        else:
            print(f"warning: {src} not found, skipped", file=sys.stderr)
    for rel, candidates in SYSTEM_FILES.items():
        src = next((Path(c) for c in candidates if Path(c).exists()), None)
        if src:
            write(dest / rel, src.read_bytes())
        else:
            print(f"warning: no source for {rel}, skipped", file=sys.stderr)
    write(dest / "docs" / "data.csv", csv_rows(rng, 1_390_000))
    write(dest / "bin" / "telemetry.dat", telemetry(rng, 100_000))
    write(dest / "media" / "tone.wav", wav())
    write(dest / "media" / "gradient.bmp", bmp())
    write(dest / "media" / "preview.png", png(rng))
    gpl3 = dest / "docs" / "GPL-3"
    if gpl3.exists():
        write(dest / "sub" / "deep" / "GPL-3-copy", gpl3.read_bytes())
    write(dest / "sub" / "empty.txt", b"")
    write(dest / "ünïcode dir" / "naïve файл.txt", "naïve файл ✓\n".encode())


def manifest(dest):
    lines = []
    for p in sorted(x for x in dest.rglob("*") if x.is_file()):
        lines.append(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(dest.parent).as_posix()}")
    out = dest.parent / f"{dest.name}.MANIFEST.sha256"
    out.write_text("\n".join(lines) + "\n")
    size = sum(x.stat().st_size for x in dest.rglob("*") if x.is_file())
    print(f"{dest}: {len(lines)} files, {size / 2**20:.1f} MiB; manifest {out}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("kind", choices=["mixed", "winapp"])
    ap.add_argument("dest", type=Path)
    ap.add_argument("--from", dest="src", type=Path, help="winapp: the publish folder to copy")
    a = ap.parse_args()
    if a.dest.exists():
        sys.exit(f"{a.dest} exists; choose a new folder")
    if a.kind == "mixed":
        mixed(a.dest)
    else:
        if not a.src or not a.src.is_dir():
            sys.exit("winapp needs --from <publish folder>, e.g. artifacts/publish/win-x64")
        shutil.copytree(a.src, a.dest)
    manifest(a.dest)


if __name__ == "__main__":
    main()
