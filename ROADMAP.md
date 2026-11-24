# Roadmap

Where Osmium is going. **In progress** items are being built for the next release; **Planned** items are designed
but not started; **Exploring** items need a prototype before they are promised. Status of everything already built:
[docs/CHECKLIST.md](docs/CHECKLIST.md). The work plans the contributors follow: [docs/plans/](docs/plans/).

## In progress: 0.2.0

0.2.0 is a minor release ([plan](docs/plans/0.2.0.md)). Existing archives keep decoding; an archive that uses a
codec new in 0.2.0 needs 0.2.0 or later to open, and older versions say so instead of failing obscurely.

Already on `main` (see the [changelog](CHANGELOG.md)): the license texts in every download and `osmium license`; the
UDP firewall rule that LAN discovery needs; a CLI password prompt that ignores non-character keys; the website rebuilt
from the real product; the settings-reference generator and benchmark tools in the repository.

### Close the gap to ZPAQ on x86/x64 code

On a 124 MiB folder of Windows program files (273 DLLs, mostly .NET ReadyToRun plus native x64), as a share of the
original ([docs/BENCHMARKS.md](docs/BENCHMARKS.md), [raw results](docs/benchmarks-0.1.1.json)):

| Tool | Share of the original |
|---|---|
| ZPAQ -m5 | 25.61 % |
| lrzip -z | 26.49 % |
| **Osmium 0.1.1 Ultra** | **27.30 %** |
| xz -9e | 28.81 % |

Osmium Ultra stored about 112 MiB of that folder as one `x86+cm(…,models=4095)` block. On the mixed test folder
Osmium made the smallest archive (Insane 29.5 % against lrzip 31.7 %), so the gap is specific to machine code.

- **Plan** ([details](docs/plans/0.2.0-b-compressor.md)): a new codec, `cm2`, beside Osmium CM, with bit-history
  (state machine) models, instruction-aware contexts (opcode, ModRM, prefixes, position in the instruction), an
  order-2 APM and a two-layer mixer. Osmium CM (`cm`) is frozen: its output is pinned by golden tests, so every
  existing archive decodes byte for byte.
- **Constraints:** integer-only and deterministic on every platform and thread count; at most 1.5× slower than `cm`
  at the same memory.
- **Done when:** the gap to ZPAQ -m5 on program files shrinks measurably without losing ground on the mixed folder,
  measured with `tools/crosscheck.py` on the folders `tools/bench/make_corpora.py` builds, and the numbers are in
  [IMPROVISATION.md](docs/IMPROVISATION.md) §5 and BENCHMARKS.

### Cheaper parameter search for Ultra and Insane

In a separate timing run (IMPROVISATION.md §5.4), Ultra spent 292 of its 346 seconds on the 16 MiB mixed folder
planning, most of it trying parameter variations of context-mixing pipelines on the largest sample. The search is
worth keeping: it shrinks the chosen pipeline by 2–8 % per group.

- **Plan:** try each search step's variations on the previous, smaller sample first and promote only the best few to
  the full sample. Insane, whose largest sample is 16 MiB, gains the most.
- **Done when:** plans stay deterministic, archive sizes stay within about 0.2 % of today's on both benchmark
  folders, and the planning time drops measurably.

### Also in 0.2.0

- **`osmium salvage`:** recover what can be trusted from a damaged archive by scanning for intact frames; files
  whose blocks are all intact are restored, everything else is reported, and nothing unverified is written as good.
- **Smarter grouping:** similar files placed next to each other inside a solid block, and the faster decoder
  preferred when two candidates are within 0.1 %. Kept only where the benchmark folders show a gain.
- **App:** settings that no longer revert changes made elsewhere, cluster keys that can be shown, pasted with
  spaces or line breaks, and validated, keyboard and display fixes, an About screen, and an option (off by default)
  to move the originals to the Recycle Bin after a verified compression.
- **Quality:** golden tests for every `cm` model set and every filter, planner tests with simulated trials, tests on
  Windows ARM64 in CI, and website screenshots rendered by the real app during CI.

## Planned

- **Recompress already-compressed files.** Bit-exact re-deflate for PNG, ZIP, DOCX/XLSX and PDF streams, and lossless
  JPEG recompression. Today these are detected and stored as-is.
- **Repair and grow archives.** A duplicate index copy and recovery records (so `osmium salvage` can restore more),
  append/update, multi-volume archives.
- **Windows integration.** Top-level Windows 11 context menu (signed sparse package), notifications with actions,
  an out-of-process job host so the tray app never blocks.
- **Signed builds** once a code-signing certificate is available, so Windows can verify the publisher (SmartScreen
  warnings also depend on download reputation).
- **Smarter grouping, continued.** Different pipelines for different regions of one file, persistent analysis and
  trial caches.

## Exploring

- **Faster context mixing.** Integer SIMD inner loops and a pipelined verify; the target is 1.5–2× without changing
  the output.
- **Cluster.** Passphrase pairing (PAKE), IPv6 discovery, verifying symmetric-codec blocks on a second worker.
- **Search.** A genetic parameter search for Exhaustive.
