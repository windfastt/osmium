# Osmium — Blueprint

> **Osmium** (the densest naturally occurring element) is a Windows desktop compressor/archiver whose one
> overriding goal is the **smallest possible lossless output**, with every speed/ratio trade-off exposed — from
> "Fast" to "Insane" and an experimental "Exhaustive" mode where hours of CPU per gigabyte are acceptable.

Companion documents: [FORMAT.md](FORMAT.md) (binary spec) · [PLAN.md](PLAN.md) (phases, multi-agent process)
· [CHECKLIST.md](CHECKLIST.md) · [IMPROVISATION.md](IMPROVISATION.md) (critic review, changes, backlog).

## 1. Goals and non-goals

**Goals**
1. Best-in-class ratio through: per-block pipelines (reversible filters + codec + tuned params), a context-mixing
   codec, dynamic per-file analysis, trial compression with successive halving, parameter search, dedup, and
   similarity-ordered solid blocks.
2. Every knob available: a large multifaceted settings surface, per-file manual overrides ("patches") and rules,
   experimental features behind an explicit switch.
3. Scale out: spread trials and heavy block compression over other PCs on the LAN, securely.
4. First-class Windows citizen: notification-area (tray) app, Explorer context menu, `.osa` association,
   watch folders, installer, CLI.
5. Never lose data: verify before commit, atomic writes, integrity hashes everywhere, deterministic codecs.

**Non-goals (v1)**
- Writing 7z/RAR/ZIP archives (Osmium reads only its own `.osa` format; ZIP export is backlog).
- Bit-exact recompression of embedded deflate (PNG/ZIP/PDF) and lossless JPEG recompression — requires
  native libraries (preflate, packJPG/lepton) that cannot be used managed-only; backlog.
- Top-level Windows 11 context menu (needs a signed sparse MSIX package) — backlog.

## 2. Architecture

```
                         ┌───────────────────────────── Windows only ─────────────────────────────┐
 Explorer verbs ──┐      │  Osmium.App.exe (WinForms, tray)                                        │
 Send To / CLI ───┼─pipe─┤   TrayApplicationContext · MainForm pages · TuneDialog · SettingsRenderer│
 Installer ───────┘      │   Win32 adapters: registry, DWM theme, icons, idle/power                │
                         └───────────────▲──────────────────────────────────────────────────────────┘
                                         │ references
 ┌──────────────── net10.0 (cross-platform, unit-tested on Linux) ────────────────────────────────────┐
 │ Osmium.Desktop   settings model + [Setting] descriptors · IPC protocol · coalescer ·               │
 │                  shell registration plan · tray text · watch-folder settle engine                  │
 │ Osmium.Core      Container (.osa) · Codecs · Filters · Analysis · Planning · Rules · Jobs ·        │
 │                  Distributed (secure channel, discovery, scheduler) · Crypto · Hashing             │
 │ Osmium.Cli       osmium.exe: a / x / l / t / analyze / explain / lab / tune / worker / rules check │
 └────────────────────────────────────────────────────────────────────────────────────────────────────┘
   Managed-only dependencies: ZstdSharp.Port · LZMA-SDK (7-Zip C# SDK) · SharpCompress (BZip2, PPMd) ·
   System.IO.Hashing · BCL (Brotli, Deflate, AES-GCM, ECDH, HKDF, PBKDF2, SHA-256)
```

Data flow for compression:
`scan → rules pre-pass → analyze (sniff + stats) → rules → dedup → group & order → plan pipeline per group
(trials / search, possibly distributed) → compress blocks in parallel (memory-budgeted) → verify each block →
write blocks + index + footer to .tmp → verify archive → atomic rename`.

## 3. Container format (summary — full spec in FORMAT.md)

```
[Header + TLV extensions + CRC32] [Frame]* [Index frame] [Footer 32 B]
Frame      = "OBLK" | flags | frameNumber | bodyLength | CRC32 | body
Block body = descriptor (stages + per-stage input sizes + named params) | rawHash XXH128 | payloadHash XXH3 | CRC32 | payload
Index      = an ordinary (compressed, optionally encrypted) frame: block table + front-coded entries + metadata
Footer     = indexOffset | indexLength | archiveId[0..8] | CRC32 | "OSAE"
```
- Each **block** is an independent unit with its own pipeline, so the planner can mix codecs inside one archive.
- **Entries** (files, dirs) reference ordered **segments** `(frame, offsetInBlock, length)`. Dedup and files
  spanning blocks fall out naturally.
- Integrity: CRC-32 on every structural header, XXH3 of each payload (quick test), XXH128 of each raw block and
  each file.
- Forward compatibility: critical/ignorable header flags and extension records; unknown methods disable only the
  affected block.
- Frame headers carry magic + frame number + CRC, so a lost index can be salvaged by scanning.
- Encryption: PBKDF2-HMAC-SHA512 → HKDF → AES-256-GCM key + password verifier; the *whole* block body (descriptor,
  hashes, payload) and the index are encrypted, so no plaintext content hash or file name is exposed.
- Frames are written in completion order (parallel compression); the index maps segments to frame numbers.

## 4. Codecs

| Id | Codec | Implementation | Key parameters (range, default) | Notes |
|---|---|---|---|---|
| 0 | Store | – | – | |
| 1 | Deflate | BCL | level Optimal/SmallestSize | compatibility baseline |
| 2 | Brotli | BCL `BrotliEncoder` | quality 0–11 (11), window 10–24 (24) | strong on text, very fast decode |
| 3 | Zstd | ZstdSharp.Port | level 1–22 (19), windowLog 10–31 (27), LDM on/off, strategy, targetLength | fast decode, long range |
| 4 | LZMA | LZMA-SDK | dict 64 KiB–1.5 GiB (64 MiB), lc 0–8 (3), lp 0–4 (0), pb 0–4 (2), fb 5–273 (273), mf bt2/bt4 | lc+lp tuned per data |
| 5 | BZip2 | SharpCompress | block size 9 | rarely wins; kept for exhaustive search |
| 6 | PPMd (var. H/I) | SharpCompress | order 2–32 (8), memory 1 MiB–2 GiB (256 MiB) | excellent on text |
| 7 | **Osmium CM** | original | table bits 12–26 (22), model set flags, counter limit, mixer rates, match min length, APM rate | strongest; symmetric ~0.5 MB/s |

**Spike results** (this container, 3 MB text / 8 MB .NET DLL): Osmium CM 121 502 B / 2 294 222 B vs `xz -9e`
134 624 B / 2 571 252 B — ≈ 10 % smaller than xz on both.

### 4.1 Osmium CM (original context-mixing codec)
- **Coder**: 32-bit carry-less binary arithmetic coder, 12-bit probabilities, 4-byte flush of `x1`.
- **Models**: order-0 and order-1 direct tables; hashed orders 2, 3, 4, (5), 6, (8); word unigram and word-pair
  (case-folded letters + UTF-8 bytes); sparse contexts (bytes −2/−3, −4/−8, −2/−4); match model (min length 6,
  verified candidates, length-bucketed adaptive predictor + strength input).
- **Storage**: 64-byte buckets = checksum/priority word + 15 nibble-tree nodes; 2-way associative, lower-priority
  victim, survivor aging. Node = 22-bit probability + 10-bit count with count-based learning rate `1/(n+1.5)`
  down to `1/limit`.
- **Mixing**: 4 first-layer neurons with weight sets selected by partial byte, (context hits × bit position),
  (match state × bit position), previous byte; a final mixer per bit position. Integer fixed-point throughout.
- **SSE**: three interpolating APMs (order-0, order-1, match-state); final probability is a fixed blend.
- **Determinism**: no floating point anywhere; tables generated from integer constants. Encoder and decoder
  share one predictor class, so they are symmetric by construction.

## 5. Reversible filters

| Id | Filter | Transform | Invertibility |
|---|---|---|---|
| 1 | x86 E8/E9 | At each E8/E9 whose operand's top byte is 00/FF: rel → (rel + pos + 5) mod 2²⁵, keep the 00/FF marker as bit 24; skip 5. Otherwise skip 4 (not 1). | The decoder sees the same byte at the cursor and the same marker byte at +4, because a transform only rewrites bytes inside the window it just evaluated and the scan never revisits them. |
| 2 | ARM64 BL | Every 4-aligned word with top six bits `100101`: imm26 → (imm26 + pos/4) mod 2²⁶ | Opcode bits unchanged, so the decoder selects exactly the same words. |
| 3 | Delta(s) | `y[i] = x[i] − x[i−s]` | Sequential prefix sum. |
| 4 | Transpose(s) | Byte planes: all byte-0s of each s-byte record, then byte-1s…; tail kept as-is | Pure permutation determined by (length, s). |
| 5 | PCM16(ch) | Per channel: fixed order-2 predictor, then optional sign-sign LMS (order ≤ 32) on the residual; output zig-zag residuals, optionally split into low/high byte planes | Prediction uses only already-reconstructed samples; arithmetic is exact mod 2¹⁶ and the LMS update uses the same wrapped residual on both sides. |
| 6 | Image(w,bpp) | Optional subtract-green, then MED/LOCO-I prediction per channel; residual mod 256; row padding passes through | Two invertible passes (pointwise, then causal prediction). |
| 7 | Text | Words (maximal `[A-Za-z]` runs) classified lower / Capitalized / UPPER / mixed; case flags + lowercase form; frequent words → 1-byte or 2-byte codes; reserved bytes (escape, flags, codes) are rare non-letters, and their literal occurrences are escaped | Reserved bytes are never letters and are always escaped as literals, so parsing is unambiguous; a word token is always followed by a non-letter, so the decoder's case-run boundary equals the encoder's word boundary. |

Filters that depend on alignment (ARM64, Delta, Transpose, PCM, Image) are fed aligned data: the group builder
pads members of such groups to the stride (padding is unreferenced and compresses to nothing), and PCM/Image files
get their own block with region parameters taken from the parsed header. Any stage whose output is not smaller
than its input falls back to Store.

Filters are chosen by analysis/trials, never blindly: a filter that does not shrink the trial output is dropped.

## 6. Dedup, solid grouping, verification and safety

- **Whole-file dedup**: files with identical size + XxHash128 are confirmed by SHA-256 and stored once.
- **Content-defined chunking** (Insane, or on request): Gear rolling hash (FastCDC normalised chunking),
  min 16 KiB / avg 64 KiB / max 256 KiB; chunks identified by SHA-256; duplicate chunks become shared segments.
- **Solid blocks**: one per (class, group) split at `BlockCap`; ordering by extension, then name. (Similarity
  ordering via MinHash is backlog.)
- **Verification** (always on; advanced opt-out): every block is decoded through the full pipeline and its XXH128
  compared before it is written — remote results are additionally re-checked by the coordinator; after writing, the
  archive is re-opened and every frame is structurally verified (CRCs, payload hashes, index) before the atomic rename.
- **Safety**: `<name>.osa.partial-xxxx` output, fsync, rename; never touches sources; refuses to overwrite an existing
  archive unless asked; extraction writes `*.osmium-part-xxxx`, verifies the file hash, then renames; paths are
  validated with Windows rules on every OS (FORMAT §7); a source that changes while being read fails the job.
## 7. Analysis (`Osmium.Core/Analysis`)

### 7.1 Reading and sampling
- Positional reads (`RandomAccess.Read`) so analysis of many files runs in parallel (default 8, 2 on HDD).
- Deterministic sampling seeded by file size: identical content ⇒ identical samples ⇒ reproducible plans.
- Header (first 4 KiB) and tail (last 4 KiB) are used for signatures/trailers only, never for statistics.
- Body: whole file if ≤ 1 MiB; otherwise K stratified 64 KiB windows
  (Fast 1, Balanced 16, Max 32, Ultra 64, Insane 128).
- Windows are placed inside parsed structure regions when known (PE code sections, WAV `data`, BMP pixels).

### 7.2 Signatures (kind detection)
PE (machine → x86/x64/ARM64; IL-only .NET assemblies get **no** x86 filter), ELF, Mach-O, WAV (PCM 16/24, float),
BMP (24/32 bpp, row stride, top-down flag), text (BOMs, strict UTF-8 rate, printable ratio, UTF-16 LE/BE, subtypes
XML/JSON/CSV/log/source), already-compressed (ZIP/docx/jar, 7z, RAR4/5, gzip, xz, zstd, bzip2, lz4, CAB, JPEG, PNG,
GIF, WebP, MP3, MP4/MOV, MKV/WebM, FLAC, Ogg), PDF (share of `FlateDecode` stream bytes), databases (SQLite page
size → record hint, ESE, Jet/ACE, OLE CFB).

### 7.3 Statistics
- H0 (order-0 entropy), H1 (empirical order-1 on the sample), printable / high-bit / zero ratios, zero-run coverage,
  strict UTF-8 validity, newline density.
- `ProbeZstd1Ratio`: a real zstd-1 pass on the sample — a cheap redundancy proxy.
- Stride detection: equality autocorrelation for s = 1..64, delta-entropy gain D(s) and transpose gain T(s) for
  s ∈ {1,2,3,4,6,8,12,16,24,32}; harmonic suppression (smallest s within 0.9× of the best); significant if gain ≥ 0.5 bit/byte.

### 7.4 Compatibility classes → seed candidates

| Class | Rule | Seed pipelines |
|---|---|---|
| Incompressible | compressed kind, or H0 ≥ 7.9 and zstd-1 ratio ≥ 0.985 | Store (Ultra+: one LZMA trial, keep only if ≥ 1% gain). Never CM. |
| x86/x64 code | PE/ELF x86 or x64 (not IL-only) | x86 filter + LZMA (lc0/lp2/pb2 and lc3/lp0/pb2), CM(binary), PPMd |
| ARM64 code | PE/ELF ARM64 | ARM64 filter + same |
| Text | printable > 0.95 | Text filter + CM(text), PPMd o6–o16, Brotli 11/w24, zstd 19 long |
| UTF-16 text | UTF-16 detection | Transpose(2) + PPMd/CM; raw PPMd |
| PCM audio | WAV 16-bit | PCM16(channels) + CM / LZMA(lc0 lp1 pb1) |
| Raster image | BMP 24/32 | BMP 2D + CM / LZMA |
| Structured, stride s | significant stride | Delta(s), Transpose(s) each + LZMA/CM; also unfiltered |
| Repetitive/sparse | zero-run coverage > 30 % | zstd 22 + long window, LZMA large dictionary |
| Generic binary | otherwise | LZMA (two lc/lp/pb variants), zstd 22, CM(default), PPMd |

## 8. Planner and profiles (`Osmium.Core/Planning`)

### 8.1 Profiles

| Profile | Analysis | Trials | Parameter search | Solid cap | Intended use |
|---|---|---|---|---|---|
| Store | none | none | – | 64 MiB | packaging only |
| Fast | sniff + H0 | none (table: zstd 3 / Store) | – | 16 MiB | quick |
| Balanced (default) | 16 windows | none; table + filters (zstd 19 long / LZMA / PPMd) | – | 64 MiB | everyday |
| Max | 32 windows | 1 rung (1 MiB), ≤ 4 candidates, no CM | – | 256 MiB | strong, still fast to decode |
| Ultra | 64 windows | rungs 1 MiB → 4 MiB, η = 3, ≤ 10 candidates, CM allowed | hill-climb ≤ 24 evals/group | 512 MiB | archival |
| Insane | 128 windows | rungs 256 KiB → 16 MiB, ≤ 40 candidates, **top-2 raced on full blocks**, chunk dedup | hill-climb ≤ 48 evals/group | 1 GiB | "any hassle" |
| Exhaustive *(experimental)* | full | every candidate in the bounded grid on full data | grid ≤ 500 evals/group | class-wide | research / tiny inputs |

### 8.2 Objective
`J = predictedSize + λe·EstEncodeSec + λd·EstDecodeSec`, with hard constraints on encode and decode memory.
Times come from a **deterministic cost model** (per-codec reference throughput), not wall-clock measurements, so
plans are reproducible and identical whether trials ran locally or on workers. λd defaults to 0; the
"decode speed weight" setting (None / Light / Strong) penalises symmetric codecs such as CM.
Ties within 0.1 % go to the faster decoder, then to the lexically smallest spec hash.

### 8.3 Group formation
1. Apply rule pre-pass (Exclude / Store / analysis depth).
2. Dedup (whole-file always; CDC chunks when enabled).
3. Partition by `(forced spec ?? compatibility class, named solid group)`; `solid: own` ⇒ singleton block.
4. Order within a class: extension, then similarity (SimHash over sampled 4-grams, greedy nearest-neighbour
   for ≤ 20k items), same base-name bonus.
5. Split into blocks ≤ `BlockCap` = min(profile cap, memory-derived cap).
6. Ultra+: merge adjacent small groups when the merged sample compresses smaller.

### 8.4 Core loop
```
plan(files):
  pre-rules → analyze (parallel) → resolve rules (with provenance) → dedup → groups
  for g in groups by descending estimated cost:
     if g.forced: spec = forced ⊕ param overrides
     else:
        cands = seeds(g.class, g.stats, profile) ⊕ overrides, filtered by rules + memory, capped
        spec  = successiveHalving(g, cands)          # rungs of growing sample size, keep top 1/η
        if profile ≥ Ultra: spec = hillClimb(g, spec) # ±1 step per parameter dimension, toggle filters
        if profile = Insane: spec = race top-2 on the full group, keep the smaller
     emit(g, spec, explain)                            # the final compression starts immediately
```
Every candidate, rung result, pruning reason and search move is recorded in an **explain** log that is
stored in the archive's metadata and shown by `osmium explain` and the app.

## 9. Rules and per-file patches (`Osmium.Core/Rules`)

```csharp
record Rule(string Id, bool Enabled, int Priority, RuleMatch Match, RuleActions Actions, RuleOrigin? Origin);
record RuleMatch(string[]? Globs, string? Regex, string[]? Extensions, SizeRange? Size, FileKind[]? Kinds, string? Path);
record RuleActions(PipelineSpec? Pipeline, bool? Store, bool? Exclude, string[]? ForbidCodecs, string[]? ForbidFilters,
                   string? Solid /* auto | own | group:<name> */, bool? SkipDedup,
                   Dictionary<string,string>? Params /* "lzma.dict": "256MiB" */, string? Profile);
```
- Match fields are AND-ed, array items OR-ed. Globs are relative to the job root with `/` separators and `**`;
  case-insensitive on Windows. Regex uses `RegexOptions.NonBacktracking` with a timeout.
- **Precedence per action property**: highest `(Layer, Priority, Order)` wins (specificity was dropped after the
  critic review — simpler to explain). Layers: built-in < global (`%APPDATA%\Osmium\rules.json`, plus the codec/filter
  switches from Settings) < job (per-file patches from the Compress page).
  `ForbidCodecs/ForbidFilters` are unioned; `Exclude` from any rule wins; `Store` ≡ pipeline Store.
- Every effective value carries provenance (rule id + layer) for explain.
- **Patch = rule with `Origin.type = "tuned"`** created by "Tune this file" (app right-click → Tune / Deep auto-tune,
  or `osmium tune`). Matching by exact path (default), content hash, or `*.ext` in the folder.

## 10. Jobs (`Osmium.Core/Jobs`)

- Job kinds: Compress, Extract, Test, Analyze, Tune/Lab.
- States: Queued → Scanning → Analyzing → Planning → Running → Verifying → Finalizing → Completed / Failed / Canceled
  (+ Paused).
- **Progress snapshots** (immutable records) polled by the UI at 2–4 Hz: bytes in/out, files, current file, phase,
  ETA computed in cost units (not bytes — CM and Store differ ~1000×).
- **Pause/cancel**: codecs call a checkpoint at least every 64 KiB–1 MiB of input; the checkpoint blocks while
  paused and throws when cancelled.
- **Memory budget**: a weighted async semaphore in bytes (default min(user cap, 70 % of available RAM − 1 GiB));
  every task leases its estimated encode/decode memory. Planner clamps model/dictionary sizes so no single task
  exceeds the budget.
- Codecs are single-threaded per task (determinism); parallelism is across blocks and trials.
- **Crash safety**: output goes to `<name>.osa.partial-xxxx`; it is renamed only after the index is written, flushed
  and the archive structurally re-verified. Sources are never touched; "delete sources" is not offered in v1.

## 11. Distributed compression (`Osmium.Core/Distributed`)

### 11.1 Roles and work units
- **Worker** = `osmium worker` (or the app's "Lend this PC" toggle). **Coordinator** = the machine running the job.
- The local machine is just another execution node; the job always finishes even if every worker vanishes.
- Work units: `Trial` (compress a sample, return only the size) and `CompressBlock` (return packed bytes).
- **Workers never see file names** — only anonymous byte blobs and a pipeline spec.
- Policy: trials are shipped freely. Final blocks are shipped only for **asymmetric** codecs (LZMA, Brotli, zstd ≥ 19),
  because the coordinator must verify every remote block by decoding it — for symmetric codecs (CM, PPMd) that costs as
  much as compressing locally.

### 11.2 Secure channel
- TCP port **46510**, UDP discovery **46511**.
- Handshake: `Hello(nonceC, ephemeral P-256 pub, clusterTag)` → `Challenge(nonceS, ephemeral pub)` →
  `Auth = HMAC(K_auth, "c-auth" ‖ transcriptHash)` → `AuthOk = HMAC(K_auth, "s-auth" ‖ transcriptHash)`.
  The client proves knowledge first; comparisons use fixed-time equality.
- `K_master` = 32 random bytes shown as a pairing code (`OSM1-…`, base32 with a checksum). Passphrase-derived keys
  were removed after the critic review (an eavesdropper could brute-force them offline from the handshake).
  `K_auth`, `K_disc`, `clusterTag` are HKDF/HMAC derivations of it.
- Session keys: `HKDF(K_master ‖ ECDH secret, salt = transcriptHash)` → separate keys and IVs per direction
  (forward secrecy). Frames: `u32 len | AES-256-GCM(type ‖ body) | tag`, nonce = IV ⊕ sequence number,
  AAD = length; strict sequence check rejects replay/reorder. Max frame 1 MiB + overhead.

### 11.3 Discovery and scheduling
- Discovery probes are HMAC-authenticated with `K_disc`, timestamped (±5 min) and nonce-checked; workers answer only
  members of their cluster. Manual `host:port` entries cover routed networks/VPNs.
- Scheduler: central priority queue (largest estimated cost first). Remote nodes get `slots + 1` outstanding units.
  Node silent for 10 s ⇒ dead; its units are re-queued; after 3 remote failures a unit becomes local-only.
- **Integrity**: every remote block is decoded locally and checked against the SHA-256 of the raw block before it is
  accepted; a random 5 % of remote trials are recomputed locally and must match exactly (100 % in paranoid mode).
  A mismatch quarantines the worker for the job.

## 12. Desktop application

### 12.1 Projects
- `Osmium.Desktop` (net10.0, no WinForms): settings model + `[Setting]` descriptors, IPC protocol and invocation
  coalescer, shell registration plan, tray text/menu model, watch-folder settle engine. Unit-tested on Linux.
- `Osmium.App` (net10.0-windows, WinForms, code-built UI, no designer/resx) → `Osmium.App.exe`.
  The CLI is `osmium.exe`; distinct names avoid a case-insensitive collision.

### 12.2 Tray
- `Application.Run(new TrayApplicationContext())`; the main window is created lazily. `--tray` starts hidden.
- Menu: header (status) · Open Osmium · Compress files… · Compress folder… · Extract archive… · Profile ▸ ·
  Recent jobs ▸ · Pause/Resume all · Pause for ▸ · ☑ Watch folders · ☑ Lend this PC (worker) · Settings… · Exit.
- Left click opens the window. A ring progress icon is rendered from 26 pre-cached frames (0–100 % in 4 % steps) and
  colours follow the *taskbar* theme (`SystemUsesLightTheme`). HICONs are cloned then `DestroyIcon`-ed (no handle leaks).
- Tooltip clamped to 127 chars. Balloon notifications on completion with savings ("photos.osa — 1.20 GB → 708 MB,
  41 % smaller"), aggregated within 5 s; All / Failures only / None.
- Close button hides to tray (configurable); Windows shutdown / installer close requests are always honoured.
  The icon is hidden and disposed on every exit path (no ghost icons).

### 12.3 Single instance and shell invocations
- Mutex `Local\Osmium.App.{UserSid}`; named pipe `Osmium.App.{UserSid}.{SessionId}` with `CurrentUserOnly`.
- Secondary instances forward their command line *before* WinForms initialises and exit.
- Explorer starts one process per selected file; the **coalescer** batches invocations with the same verb within a
  350 ms sliding window (hard cap 4 s / 10 000 paths) into one job.
- Messages: length-prefixed UTF-8 JSON (`invoke`, `show`, `quit`, `ping`).

### 12.4 Main window
Nav rail + pages: **Compress** (file list with detected type / plan / patch indicator; right-click Tune, Deep auto-tune,
Store, Exclude, Save as rule), **Jobs**, **Archives** (browse/extract/test/info), **Lab** (benchmark matrix),
**Rules & patches**, **Cluster**, **Settings**, **Log**. TuneDialog edits a pipeline (filters + codec + params) and
runs quick tests or a budgeted deep auto-tune.

### 12.5 Settings
- Stored as JSON in `%APPDATA%\Osmium\settings.json` (schemaVersion, unknown properties preserved, atomic writes,
  corrupt file backed up and replaced with defaults).
- `[Setting(Category, Label, Description, Min, Max, Unit, Experimental, RequiresRestart, Advanced, Dangerous,
  VisibleWhen, Editor)]` attributes drive a generic renderer: bool → checkbox, enum → dropdown, numeric → spinner/slider,
  byte size → spinner + unit, path → textbox + browse. The same renderer shows codec parameters in TuneDialog.
- Categories: General, Compression, Codecs, Filters, Analysis, Dedup & Solid, Performance, Security, Integration,
  Watch folders, Cluster, **Experimental** (master switch with explicit acknowledgement; archives made with experimental
  features are flagged).

### 12.6 Theming and DPI
- Dark/light palette, owner-drawn nav rail, `DarkMode_Explorer` window theme for lists, DWM immersive dark title bar.
- PerMonitorV2: every form sets `AutoScaleMode.Dpi` with `AutoScaleDimensions = 96×96`; layouts use
  TableLayoutPanel/FlowLayoutPanel; fonts/pens/brushes cached, never created in Paint.
- Defensive runtime guards: UI thread dispatcher, global exception handlers, hidden owner for dialogs.

## 13. Windows integration, installer, CI

- `Osmium.App.exe --register --scope user|machine --features contextmenu,assoc,startup` writes a data-driven
  registration plan: `.osa` → `Osmium.Archive` (Open / Extract here / Extract to folder / Test),
  `*\shell\Osmium` and `Directory\shell\Osmium` cascading menus (Add to archive… / Compress to .osa (profile)),
  `Directory\Background` "Compress this folder", `MultiSelectModel=Player` on every verb, Run key for `--tray`.
  `--unregister --all` reverses exactly what was written. (Windows 11 shows these under "Show more options";
  a top-level `IExplorerCommand` needs a signed sparse package — backlog.)
- **Inno Setup**: per-user by default with an all-users option, tasks for desktop icon / startup / context menu /
  association / Send To / PATH, graceful `--quit` before upgrade, `--unregister` on uninstall with a registry fallback,
  optional removal of user data.
- **CI** (`windows-latest` + `ubuntu-latest`): restore, build, test, publish App + CLI self-contained into one folder
  (shared runtime), `--selftest-ui` screenshots, Inno Setup compile, portable ZIP, release on `v*` tags.

## 14. Security model

| Threat | Mitigation |
|---|---|
| Malicious archive: path traversal / device names / ADS | Strict path validation (FORMAT §7); extraction root containment check after full-path resolution |
| Malicious archive: decompression bomb / memory exhaustion | Declared `rawLen` checked against output; per-codec decode-memory estimate checked against a configurable limit **before** decoding (CM table bits, LZMA dictionary, zstd window, PPMd memory); index size and entry counts capped |
| Corrupted archive | Hash checks at header, footer, index, block and file level; corrupted blocks reported, other files still extract |
| Password attacks on encrypted archives | AES-256-GCM, PBKDF2-SHA256 600k iterations (configurable), unique salt per archive, file names inside encrypted index |
| Known-plaintext confirmation | No plaintext hashes or sizes in inline headers of encrypted archives |
| LAN attacker vs cluster | Mutual HMAC authentication bound to the ECDH transcript, AES-GCM frames with sequence numbers, discovery HMAC + timestamp + nonce, handshake rate limiting, firewall rule on the Private profile only |
| Malicious or buggy worker | Every remote block decoded and hash-checked locally; trial spot checks; quarantine; workers never receive file names |
| Local privilege issues | Tray app never elevated; per-user named pipe with `CurrentUserOnly`; registry writes scoped to HKCU unless installed for all users |

## 15. Testing strategy
See [PLAN.md §3](PLAN.md#3-verification-strategy). Key rule: every codec, filter and archive path is tested with
random data, structured data, empty/1-byte/odd-length inputs, and corruption; distributed code is tested with an
in-process loopback cluster including tampering and lying-worker cases; the WinForms layer is exercised on Windows CI
by `--selftest-ui`.

## 16. Scope

**v1 (this repository)** — everything in §3–§13 except the items below.

**Backlog (tracked in IMPROVISATION.md)**
- Bit-exact deflate recompression (PNG/ZIP/PDF/DOCX) via preflate-style reconstruction; lossless JPEG recompression.
- GA parameter search; persistent analysis/plan caches; crash-resume journal.
- Intra-file segmentation (different pipelines for regions of one file).
- Out-of-process job host for the tray app; NativeAOT shell forwarder.
- Windows 11 top-level context menu (IExplorerCommand + signed sparse package); real toasts with actions.
- Cross-worker verification of symmetric-codec blocks; IPv6 discovery.
- Recovery records (Reed–Solomon parity) and multi-volume archives.
