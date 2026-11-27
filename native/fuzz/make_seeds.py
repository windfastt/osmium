#!/usr/bin/env python3
"""Writes the seed inputs of the fuzz harnesses (no binary corpus is committed). Run: python3 -I native/fuzz/make_seeds.py OUT

OUT/<harness>/ gets inputs made of the harness's settings bytes followed by synthetic content (text, 32-byte
records, pseudo-random bytes, a repeated piece, zeros) of sizes from 0 to 64 KiB. Deterministic: the same seeds every
time.
"""
from __future__ import annotations

import os
import sys

WORDS = [b"the ", b"archive ", b"block ", b"window ", b"match ", b"literal ", b"Osmium ", b"data ", b"encoder "]


class Rng:
    def __init__(self, seed: int):
        self.s = (seed * 0x9E3779B97F4A7C15 + 1) & 0xFFFFFFFFFFFFFFFF

    def next(self) -> int:
        s = self.s
        s ^= s >> 12
        s ^= (s << 25) & 0xFFFFFFFFFFFFFFFF
        s ^= s >> 27
        self.s = s
        return (s * 2685821657736338717) & 0xFFFFFFFFFFFFFFFF


def content(kind: int, n: int, seed: int) -> bytes:
    r = Rng(seed)
    if kind == 0:
        out = bytearray()
        while len(out) < n:
            out += WORDS[r.next() % len(WORDS)]
        return bytes(out[:n])
    if kind == 1:
        return bytes(((i // 32) >> (8 * (i % 32)) & 0xFF) if i % 32 < 4 else (0x42 if i % 32 == 16 else 0) for i in range(n))
    if kind == 2:
        return bytes(r.next() & 0xFF for _ in range(n))
    if kind == 3:
        piece = content(0, 256, seed + 1)
        return (piece * (n // 256 + 1))[:n]
    return bytes(n)


SETTINGS = {
    "fuzz_lzma_encode": 11,   # dict, lc, lp, pb, fb (2), mf, threads, capacity, memory cap, failing allocation
    "fuzz_zstd_encode": 11,   # level, window, ldm, ldm minimum (3), strategy, chunk, capacity, cap, failing allocation
    "fuzz_shim_args": 70,     # which, flags, the parameter struct (64), cap, stats size, ...
}


def main(argv: list[str]) -> int:
    if len(argv) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    out = argv[0]
    count = 0
    for harness, settings in SETTINGS.items():
        folder = os.path.join(out, harness)
        os.makedirs(folder, exist_ok=True)
        r = Rng(len(harness))
        for i, size in enumerate([0, 1, 7, 100, 1000, 4096, 20000, 65536]):
            for kind in range(5):
                head = bytearray(r.next() & 0xFF for _ in range(settings))
                if harness != "fuzz_shim_args":
                    head[-3] = 200   # a capacity of the full bound
                    head[-2] = 23    # no practical memory cap
                    head[-1] = 0     # no failing allocation
                data = bytes(head) + content(kind, size, i * 10 + kind)
                with open(os.path.join(folder, f"seed-{size}-{kind}"), "wb") as f:
                    f.write(data)
                count += 1
    print(f"wrote {count} seeds to {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
