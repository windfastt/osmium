# Osmium — Critic Review, Improvisations and Decision Log

This file records what the adversarial review (and the cross-checks between architects) found, what was changed
because of it, what was deliberately not adopted, and the backlog of further ideas. Severity: **B** blocker,
**M** major, **m** minor, **I** idea.

## 1. Findings from cross-checking the architects' reports

| # | Finding | Resolution |
|---|---|---|
| X1 | **B** — The engine architect's x86 E8/E9 rule advanced by 1 byte after a non-qualifying E8/E9. A later transform at `i+1..i+3` could then rewrite byte `i+4`, which the decoder re-reads at `i` → different decision → corrupted output. | Lead's rule kept: **skip 4** after a non-qualifying E8/E9. The critic independently reached the same conclusion. Covered by an exhaustive test over every 7-byte string of a hostile alphabet (78 125 cases) plus a randomized adversarial corpus. |
| X2 | Both architects asked for a 1 GiB raw block limit; the first blueprint draft allowed 4 GiB "Insane" blocks, impossible with `byte[]`. | Hard limit `rawSize ≤ 2^30` in format and code; all profile caps ≤ 1 GiB. |
| X3 | The UX architect showed `Osmium.exe` (GUI) and `osmium.exe` (CLI) collide on case-insensitive file systems. | GUI is `Osmium.App.exe`. |
| X4 | Two single-file executables would each embed the ~70 MB runtime. | App + CLI are co-published self-contained into one folder; a separate single-file `osmium.exe` is still produced for headless workers / portable extraction. |
| X5 | Encoder/decoder symmetry risk in CM. | CM is integer-only with constant tables; a single predictor class serves both directions; verified on edge sizes and corpora; golden-vector tests pin the output across platforms. |

## 2. Critic review → changes adopted

| # | Sev | Finding | Change made |
|---|---|---|---|
| 1 | B | Length-changing filters (Text) had no stored length; CM/PPMd/LZMA need the exact output length. | Pipeline descriptor stores every stage's input size; decoders assert exact sizes. |
| 2 | B | Path validation missed `\`, `<>"\|?*`, trailing dot/space, device names with extensions, superscript `COM¹`, `CONIN$`, duplicates, case collisions. | `PathSafety` applies Windows rules on every OS; index rejects duplicate paths; case collisions are renamed on extraction. |
| 3 | B | Extraction could destroy an existing file if verification then failed. | Write to a randomised `*.osmium-part-xxxx`, verify XXH128, then atomically replace. |
| 4 | B | 4 GiB blocks impossible. | 1 GiB limit (X2). |
| 5 | B | Third-party decoders allocate from the payload (LZMA dictionary, zstd window, PPMd/CM memory) → decompression bomb / OOM. | LZMA properties are rebuilt from the descriptor with the dictionary clamped to the block size; zstd `windowLogMax` is clamped to the block size (and the encoder clamps its window identically); CM tables are clamped to the block size on both sides; decode memory is checked against a configurable limit before decoding; every decoder exception becomes "block corrupt". |
| 6 | B | Forward compatibility: index records not length-prefixed; params silently clamped on read. | Each block and entry record is length-prefixed (unknown trailing fields are skipped by older readers); header flags split into critical/informational; odd extension types critical; method ids frozen; out-of-range or unknown parameters are rejected on read, never clamped. |
| 7 | M | Header (KDF iterations, flags) not authenticated; iteration count could be inflated to hang the opener. | Index AAD includes the full header bytes; readers refuse more than 20 M iterations. Each frame is encrypted exactly once (frame numbers are assigned at write time and never reused). |
| 8 | M | Blocks should be self-describing in both modes. | Frame header (magic, flags, frame number, length, CRC) + body CRC + payload hash; GCM tag for encrypted bodies. |
| 9 | M | Overlapping segments allow an expansion bomb. | Index validation (bounds, sums, duplicates) and an expansion check: extraction asks/refuses when output exceeds a configurable multiple of stored raw data or available disk space. |
| 10 | M | Source may change between hashing and reading. | File hashes are computed from the bytes actually placed in blocks; size + mtime are rechecked after reading; changed files fail loudly. The scanner does not follow reparse points, skips cloud placeholders (`Offline`/`RecallOnDataAccess`) and lists locked files. |
| 11 | M | Alignment-sensitive filters inside solid blocks. | Group builder pads member starts to the group alignment; PCM/Image files get their own block with region parameters. |
| 12 | M | E8/E9 rule must be code, not prose. | Implemented exactly as specified, with exhaustive tests (X1). |
| 13 | M | Text filter is risky. | **Not adopted as proposed** (escape-free variant). The escape-based design is kept because it also works when few bytes are free (UTF-8 text), and it passes 300 rounds of hostile fuzzing plus the exhaustive suite. It is always trial-gated: it is only used when it makes the final output smaller. |
| 14 | M | Verification must be on at every level. | Every block is decoded and compared before it is written (opt-out only as an advanced setting); after writing, the archive is re-opened and every frame is structurally verified (CRCs, payload hashes, index) without a second full decode; a block that does not shrink is stored. |
| 15 | M | Memory leases could deadlock (encode lease then verify lease). | One lease per block covering max(encode, decode) + raw + scratch for the whole lifecycle. |
| 16 | M | Extraction order with dedup. | Extraction is block-by-block with positional writes; files are verified by hashing after completion; test mode reassembles in order. |
| 17 | m | CM decoder silently pads past the end. | Decoder checks that the input was consumed exactly. |
| 18 | m | CM memory estimate incomplete. | Includes APMs, mixers and history. |
| 20 | m | 256 MiB CM tables for 4 KB trials. | Table and match sizes are clamped to the block size (identically in encoder and decoder). |
| 22 | M | Mark-of-the-Web not propagated (7-Zip CVE-2025-0411 class). | On Windows, an archive's `Zone.Identifier` stream is copied to every extracted file. |
| 23 | M | Distributed: passphrase mode allows offline dictionary attack; version skew; heartbeats; chunking; memory caps. | Pairing code (random 256-bit key) only; protocol + engine fingerprint check in the handshake; independent heartbeat task; chunked transfers; workers enforce their own memory cap; remote work disabled for encrypted jobs by default; documentation states plainly that workers see file *contents* (never names). |
| 24 | m | Metadata could leak absolute paths. | Plan/explain metadata uses archive-relative paths only and can be switched off. |
| 26 | m | Docs disagreed (footer size/magic). | FORMAT.md is authoritative and was rewritten after the merge. |
| 27 | m | "Byte-identical" cluster test impossible with completion-order frames and random ids. | Frames are written in **logical order** (out-of-order completions are buffered), files are sorted by ordinal path before planning, and archive ids/salts come from an injectable RNG in tests — so archives are reproducible except for id/salt. |

### Scope changes from the review
- **Cut:** passphrase-based cluster keys (pairing code only); SHA-256 per raw block for remote verification (the coordinator keeps the raw bytes and compares hashes directly); Insane merge/split refinement (backlog); the 500-evaluation Exhaustive grid ("Exhaustive" = every seed candidate on the full data, plus hill-climbing); rule *specificity* (precedence is layer → priority → order, simpler to explain).
- **Simplified:** dark theme uses WinForms' native colour mode plus custom-painted navigation and the DWM title bar instead of a fully custom renderer.
- **Added (user-expected):** keep-awake during jobs, process priority, pause on battery, estimated decode time and RAM before compressing and in archive info, overwrite policies, extract single files.

## 3. Improvisations (compression gains) — status

| Idea | Expected | Status |
|---|---|---|
| CM record contexts from stride analysis (`stride` param) | 5–25 % on tables/BMP/structured | **Adopted** |
| Measured CM tuning before freezing v1 (final-stage mixing, learning-rate warm-up, counter limits) | 1–3 % | Evaluated empirically; see §5 |
| Alignment padding for stride-sensitive groups | free | **Adopted** |
| Clamp dictionaries / windows / tables to block size | fewer OOMs, more parallel trials | **Adopted** |
| Insane "race": top-2 candidates compressed on the full block, smaller kept | 0–3 % | **Adopted** |
| LMS audio predictor | +5–8 % on WAV | **Adopted** (sign-sign LMS, order ≤ 32) |
| E8/E9 restricted to PE code sections; ARM64 ADRP | +1–3 % on binaries | Backlog |
| SIMD (integer, deterministic) CM inner loops; pipelined CM verify | 1.5–2× speed | Backlog |
| Bit-history (state machine) counters, order-2 APM, 2-layer paq-style network | +3–8 % | Backlog |

## 4. Backlog
- Bit-exact deflate recompression (PNG/ZIP/DOCX/PDF) via a zlib-exact port or preflate-style diffs; lossless JPEG recompression.
- GA parameter search; persistent analysis/plan/trial caches; crash-resume journal.
- Intra-file segmentation (different pipelines per region of one file).
- Out-of-process job host for the tray app; NativeAOT shell forwarder.
- Windows 11 top-level context menu (signed sparse package + IExplorerCommand); toasts with actions.
- PAKE for passphrase-based clusters; IPv6 discovery; cross-worker verification of symmetric-codec blocks.
- Duplicate index copy, `osmium salvage`, recovery records, multi-volume, append/update (needs random nonces).
- Recycle Bin removal of sources after verified compression.

## 5. Measured decisions

### 5.1 CM tuning before freezing the format
Samples: 1.5 MB of license/changelog text, 2 MB of a .NET DLL, 1 MB of synthetic int32+float records; CM with
256 MiB tables. Total compressed bytes, lower is better:

| Variant | Total | vs. baseline | Decision |
|---|---|---|---|
| Baseline (fixed final blend) | 1 090 710 | — | |
| Stretch-domain final blend | 1 090 875 | +0.02 % | rejected |
| Learned final mixer (per bit position) | 1 086 810 | −0.36 % | adopted |
| Learning-rate warm-up (first ~16 KiB) | 1 089 214 | −0.14 % | adopted |
| Counter limit 1023 for orders 0–1 | 1 095 463 | +0.44 % | rejected (critic suggestion that hurt) |
| Confidence input per context | 1 074 920 | −1.45 % | adopted |
| **Confidence + learned final + warm-up** | **1 070 422** | **−1.86 %** | **CM v1** (≈30 % slower) |

The finished CM v1 beats `xz -9e` by about 10 % on the same text and DLL samples, and is pinned by a golden-hash
test that must pass on both Linux and Windows.

### 5.2 Planner fixes found by measurement
- Hill-climbing lowered CM table memory on small samples (hash-collision noise made a smaller table look better),
  which would have hurt on full blocks. Memory-like parameters (CM memory, LZMA dictionary, PPMd memory) are now
  excluded from the search; they are resource limits, not model choices.
- The planner fixture tests (PLAN.md phase-3 exit criteria) caught a sniffer bug: without a byte-order mark, the
  UTF-16 heuristic accepted any data with ≥ 40 % zero odd bytes and ≥ 40 % printable even bytes — which little-endian
  integer tables satisfy, so they were treated as text and never got the delta/transpose candidates meant for them.
  The no-BOM thresholds are now 90 %/90 %, with a regression test for both directions.

### 5.3 Cross-check against other compressors (0.1.1)
`tools/crosscheck.py` ran Osmium's profiles and eight other tools (nine settings) on two folders, at the strongest
commonly used setting of each, and unpacked and compared every archive. Full tables: [BENCHMARKS.md](BENCHMARKS.md).

| Share of the original | Mixed folder (16 MiB) | Windows application folder (124 MiB) |
|---|---|---|
| `tar \| xz -9e` | 36.4 % | 28.8 % |
| 7-Zip LZMA2 `-mx=9` | 34.8 % | 28.2 % |
| `tar \| lrzip -z` | 31.7 % | 26.5 % |
| ZPAQ `-m5` | 31.8 % | **25.6 %** |
| Osmium Max / Ultra / Insane | 32.3 % / 29.6 % / **29.5 %** | 28.8 % / 27.3 % / not run |

Lessons:
- Per-class pipelines are the win on mixed folders: even Balanced and Max beat 7-Zip and xz there, and Ultra/Insane
  beat ZPAQ -m5 and lrzip by 6–7 %.
- On homogeneous machine code the LZMA profiles can only match xz (same algorithm family), and Osmium CM is 6.2 %
  behind ZPAQ -m5. Its x86 modelling is the next thing to improve: the backlog items in §3 (E8/E9 limited to PE code
  sections, bit-history counters, a two-layer mixer network) target exactly this.
- 0.1.0's notes listed Ultra at 4 983 958 and Insane at 4 927 730 bytes on the mixed folder, measured before the
  sniffer fix changed how its 12-byte telemetry records are classified. 0.1.1 measures 4 965 074 (−0.4 %) and
  4 944 191 (+0.3 %). The 0.1.0 times for Ultra (53 s) and Insane (~10 min) were wrong; see §5.4.

### 5.4 Where Ultra's time goes (0.1.1)
The cross-check measured Ultra on the mixed folder at 483 s, not the 53 s that 0.1.0's notes listed; that figure
could not be reproduced and is withdrawn. `osmium a … --profile ultra` printed
*planning 427.7 s* of the 483 s: compressing and verifying the seven blocks took 55 s.

The plan log (`osmium explain`) shows why. The two large groups (6.3 MiB .NET IL, 5.1 MiB x64 code) chose CM with
every context model, which runs at roughly 0.3–0.5 MB/s, and the parameter search then spends up to 24 trial
compressions of the 4 MiB sample on each. The search is worth it — it improved the chosen pipeline by 8.4 % (text),
2.1 % (x64 code) and 2.2 % (.NET IL, BMP, records) on the sample — but groups were planned one after another, and
each search step leaves cores idle while its last few trials finish: the run used about 2.6 of 4 cores.

| Change | Planning | Total | Archive |
|---|---|---|---|
| 0.1.1 before the fix (groups planned one after another) | 427.7 s | 483.1 s | 4 965 074 bytes |
| Groups planned concurrently, one shared pool of trial slots | 292.1 s | 345.7 s | 4 965 074 bytes, identical plan log and blocks |

Next step (not in 0.1.1): screen each search step's neighbours on the previous rung's smaller sample and promote
only the best to the full sample. Insane, whose last rung is 16 MiB, would gain the most.
