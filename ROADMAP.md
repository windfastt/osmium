# Roadmap

What comes after 0.1.1. **Next** items have measurements behind them and a definition of done; **Planned** items are
designed but not started; **Exploring** items need a prototype before they are promised. Status of everything that is
already built: [docs/CHECKLIST.md](docs/CHECKLIST.md).

## Next

### Close Osmium CM's gap to ZPAQ on x86/x64 code

On a 124 MiB folder of Windows program files (273 DLLs, mostly .NET ReadyToRun plus native x64), as a share of the
original ([docs/BENCHMARKS.md](docs/BENCHMARKS.md), [raw results](docs/benchmarks-0.1.1.json)):

| Tool | Share of the original |
|---|---|
| ZPAQ -m5 | 25.61 % |
| lrzip -z | 26.49 % |
| **Osmium Ultra** | **27.30 %** |
| xz -9e | 28.81 % |

Osmium Ultra stored about 112 MiB of that folder as one `x86+cm(…,models=4095)` block. On the mixed test folder
Osmium made the smallest archive (Insane 29.5 % against lrzip 31.7 %), so the gap is specific to machine code.

- **Where:** `src/Osmium.Core/Codecs/Cm/CmCodec.cs` (models, mixer, APMs) and
  `src/Osmium.Core/Filters/BinaryFilters.cs` (x86 E8/E9 filter).
- **Ideas:** E8/E9 limited to PE code sections; bit-history (state machine) counters; an order-2 APM; a two-layer
  paq-style mixer network; x86-aware contexts such as opcode, ModRM and the previous instruction.
- **Constraints:** CM stays integer-only and deterministic. The golden-hash test pins its output, so a
  format-visible change gets a new codec version or parameter and existing archives keep decoding.
- **Done when:** tests pass (with an updated golden hash if the output changes on purpose), the gap to ZPAQ -m5 on
  program files shrinks measurably without losing ground on the mixed folder, and
  [IMPROVISATION.md](docs/IMPROVISATION.md) §5 records the numbers, measured with `tools/crosscheck.py`.

### Faster parameter search for Ultra and Insane

In a separate timing run (IMPROVISATION.md §5.4), Ultra spent 292 of its 346 seconds on the 16 MiB mixed folder planning, most of it trying parameter variations of
context-mixing pipelines on the largest sample. The search is worth keeping: it shrinks the chosen pipeline by
2–8 % per group ([IMPROVISATION.md](docs/IMPROVISATION.md) §5.4).

- **Idea:** try each search step's variations on the previous, smaller sample first and promote only the best few to
  the full sample. Insane, whose largest sample is 16 MiB, gains the most.
- **Done when:** plans stay deterministic, archive sizes stay within about 0.2 % of today's on both benchmark
  folders, and the planning time drops measurably.

## Planned

- **Recompress already-compressed files.** Bit-exact re-deflate for PNG, ZIP, DOCX/XLSX and PDF streams, and lossless
  JPEG recompression. Today these are detected and stored as-is.
- **Repair and grow archives.** `osmium salvage` (recover files by scanning frame headers), a duplicate index copy,
  recovery records, append/update, multi-volume archives.
- **Windows integration.** Top-level Windows 11 context menu (signed sparse package), notifications with actions,
  an out-of-process job host so the tray app never blocks.
- **Signed builds** once a code-signing certificate is available, so Windows can verify the publisher (SmartScreen
  warnings also depend on download reputation).
- **Smarter grouping.** Similarity ordering of files inside a block, different pipelines for different regions of
  one file, persistent analysis and trial caches.

## Exploring

- **Faster context mixing.** Integer SIMD inner loops and a pipelined verify; the target is 1.5–2× without changing
  the output.
- **Cluster.** Passphrase pairing (PAKE), IPv6 discovery, verifying symmetric-codec blocks on a second worker.
- **Search.** A genetic parameter search for Exhaustive.
- **Housekeeping.** Optionally move sources to the Recycle Bin after a verified compression.
