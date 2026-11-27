#!/usr/bin/env python3
"""Checks the native sources before they are built. Run: python3 -I native/check_sources.py [--src DIR]

1. Every upstream file in sources.files (roles lib and test), as extracted by fetch_sources.py, carries its licence
   statement, and every copyright line in it is one of the known lines below. A new copyright holder fails the check
   until THIRD-PARTY-NOTICES.md lists it (and this list is extended with it).
2. Osmium's interface layer (src/, include/) references no dictionary, decoder, thread-pool or multi-threaded
   Zstandard function.
3. The build files define none of the macros that would add threads, debug code or change Zstandard's output, and
   do not glob, configure or run anything from the upstream trees.
Exit status 0 when everything passed, 1 otherwise.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

LICENCE = {
    "lzma-sdk": ["Igor Pavlov : Public domain"],
    "zstd": ["This source code is licensed under both the BSD-style license (found in the LICENSE file in the root "
             "directory of this source tree) and the GPLv2 (found in the COPYING file in the root directory of this "
             "source tree). You may select, at your option, one of the above-listed licenses."],
}
COPYRIGHT = {
    "lzma-sdk": {
        # 7zVersion.h: the 7-Zip program's copyright string (macro MY_COPYRIGHT_CR, unused by the library); the SDK
        # itself is in the public domain (DOC/lzma-sdk.txt).
        "#define MY_COPYRIGHT_CR \"Copyright (c) 1999-2026 Igor Pavlov\"",
    },
    "zstd": {
        "Copyright (c) Meta Platforms, Inc. and affiliates.",
        "Copyright (c) Yann Collet - Meta Platforms, Inc",
        "Copyright (c) 2016 Tino Reichardt",
    },
}

FORBIDDEN_IN_SHIM = [
    "ZSTD_CCtx_loadDictionary", "ZSTD_CCtx_refPrefix", "ZSTD_CCtx_refCDict", "ZSTD_createCDict",
    "ZSTD_decompress", "ZSTD_createDCtx", "ZSTD_DCtx", "ZSTD_decodeFrame", "LzmaDec",
    "ZSTD_createCCtxParams", "ZSTD_CCtx_setParametersUsingCCtxParams", "ZSTD_createThreadPool",
    "ZSTD_CCtx_refThreadPool", "POOL_create", "ZSTD_c_nbWorkers", "ZSTD_estimate", "ZSTD_CCtx_reset",
    "ZSTD_compressSequences", "ZSTD_flushStream", "ZSTD_endStream", "ZSTD_initCStream",
]
FORBIDDEN_DEFINES = ["ZSTD_MULTITHREAD", "DEBUGLEVEL", "ZSTD_CLEVEL_DEFAULT", "ZSTD_EXCLUDE_", "Z7_ST",
                     "ZSTD_LEGACY_SUPPORT", "ZSTD_LIB_DEPRECATED", "ZSTD_DLL_EXPORT"]
FORBIDDEN_IN_BUILD = [r"\badd_subdirectory\s*\(", r"\bfile\s*\(\s*GLOB", r"\bexecute_process\s*\(",
                      r"\bExternalProject", r"\bFetchContent", r"\binclude\s*\(\s*\$\{(LZ|ZS)\}"]


def normalise(text: str) -> str:
    """Joins comment lines: drops leading '*' and '//' and collapses white space."""
    out = []
    for line in text.replace("\r\n", "\n").split("\n"):
        line = line.strip()
        line = re.sub(r"^(/\*+|\*+/?|//+)", "", line).strip()
        out.append(line)
    return re.sub(r"\s+", " ", " ".join(out))


def check_upstream(src: Path, problems: list[str]) -> int:
    lock = {}
    for line in (HERE / "sources.lock").read_text(encoding="utf-8").splitlines():
        f = line.split()
        if f and not f[0].startswith("#") and len(f) == 7:
            lock[f[0]] = f[1]
    count = 0
    for line in (HERE / "sources.files").read_text(encoding="utf-8").splitlines():
        f = line.split()
        if not f or f[0].startswith("#"):
            continue
        sha, name, role, path = f
        if role == "doc":
            continue
        p = src / f"{name}-{lock[name]}" / Path(*path.split("/"))
        if not p.is_file():
            problems.append(f"{name}: {path} is missing (run fetch_sources.py first)")
            continue
        text = p.read_text(encoding="utf-8", errors="replace")
        flat = normalise(text)
        for sentence in LICENCE[name]:
            if sentence not in flat:
                problems.append(f"{name}: {path} lacks its licence statement")
        for raw in text.replace("\r\n", "\n").split("\n"):
            if re.search(r"copyright\s*(\(c\)|©|\d{4})", raw, re.IGNORECASE):   # a notice, not a macro name
                cleaned = re.sub(r"^\s*(/\*+|\*+|//+)?\s*", "", raw).rstrip(" */\t")
                if cleaned not in COPYRIGHT[name]:
                    problems.append(f"{name}: {path}: unknown copyright line: {cleaned!r}")
        count += 1
    return count


def check_shim(problems: list[str]) -> int:
    files = sorted((HERE / "src").glob("*.c")) + sorted((HERE / "src").glob("*.h")) + sorted((HERE / "include").glob("*.h"))
    for p in files:
        text = p.read_text(encoding="utf-8")
        code = re.sub(r"/\*.*?\*/", " ", text, flags=re.S)
        code = re.sub(r"//[^\n]*", " ", code)
        for name in FORBIDDEN_IN_SHIM:
            if re.search(r"\b" + re.escape(name), code):
                problems.append(f"{p.relative_to(HERE)} references {name}")
        for name in FORBIDDEN_DEFINES:
            if re.search(r"#\s*define\s+" + re.escape(name), code):
                problems.append(f"{p.relative_to(HERE)} defines {name}")
    return len(files)


def check_build_files(problems: list[str]) -> None:
    for p in [HERE / "CMakeLists.txt", HERE / "build.sh", HERE / "build.ps1"]:
        if not p.exists():
            continue
        lines = [ln for ln in p.read_text(encoding="utf-8").splitlines() if not ln.lstrip().startswith("#")]
        text = "\n".join(lines)
        for name in FORBIDDEN_DEFINES:
            if re.search(r"(-D|/D|\bset\(OSN_DEFINES[^)]*)\s*" + re.escape(name), text):
                problems.append(f"{p.name} defines {name}")
        for pattern in FORBIDDEN_IN_BUILD:
            if re.search(pattern, text):
                problems.append(f"{p.name} matches forbidden build construct {pattern}")
    defines = re.search(r"set\(OSN_DEFINES([^)]*)\)", (HERE / "CMakeLists.txt").read_text(encoding="utf-8"))
    if not defines:
        problems.append("CMakeLists.txt: OSN_DEFINES not found")
    else:
        for d in defines.group(1).split():
            if any(d.startswith(f) for f in FORBIDDEN_DEFINES):
                problems.append(f"CMakeLists.txt: OSN_DEFINES contains {d}")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--src", type=Path, default=HERE.parent / "artifacts" / "native-src")
    args = ap.parse_args(argv)
    problems: list[str] = []
    upstream = check_upstream(args.src, problems)
    shim = check_shim(problems)
    check_build_files(problems)
    for p in problems:
        print(f"check_sources: {p}", file=sys.stderr)
    if problems:
        return 1
    print(f"check_sources: {upstream} upstream files carry their licence and known copyright lines; "
          f"{shim} interface files reference no forbidden function; build files define no forbidden macro")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
