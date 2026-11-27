#!/usr/bin/env python3
"""Compares the native encoder sweep lists of several platforms. Run: python3 -I native/sweep/compare.py A.txt B.txt ...

Each list (written by SweepTests in tests/Osmium.Core.Tests/Native) has one line per case: id, input, input hash,
parameters, output length, output SHA-256. Every list must have the same cases, with the same inputs and parameters,
and the same output on every platform. Exit status 0 when all lists agree, 1 otherwise.
"""
from __future__ import annotations

import sys


def load(path: str) -> dict[str, list[str]]:
    rows: dict[str, list[str]] = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            fields = line.split(" ")
            if len(fields) != 6:
                raise SystemExit(f"{path}: malformed line: {line[:120]}")
            if fields[0] in rows:
                raise SystemExit(f"{path}: case {fields[0]} appears twice")
            rows[fields[0]] = fields
    return rows


def main(paths: list[str]) -> int:
    if len(paths) < 2:
        print("compare.py needs at least two lists", file=sys.stderr)
        return 2
    lists = {p: load(p) for p in paths}
    first, ref = paths[0], lists[paths[0]]
    problems = 0
    for p in paths[1:]:
        other = lists[p]
        missing = sorted(set(ref) ^ set(other))
        for case in missing[:20]:
            print(f"{case}: only in {first if case in ref else p}")
        problems += len(missing)
        for case in sorted(set(ref) & set(other)):
            a, b = ref[case], other[case]
            if a[1:4] != b[1:4]:
                print(f"{case}: input or parameters differ between {first} and {p}")
                problems += 1
            elif a[4:6] != b[4:6]:
                if problems < 50:
                    print(f"{case}: output differs: {first} {a[4]} {a[5][:16]}, {p} {b[4]} {b[5][:16]} ({a[3]})")
                problems += 1
    total = len(ref)
    by_codec = {c: sum(1 for k in ref if k.startswith(c + ".")) for c in ("lzma", "zstd")}
    if problems:
        print(f"sweep: {problems} differences across {len(paths)} lists of {total} cases")
        return 1
    print(f"sweep: {total} cases ({by_codec['lzma']} LZMA, {by_codec['zstd']} Zstandard) identical in {len(paths)} lists")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
