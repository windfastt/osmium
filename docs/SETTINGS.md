# Osmium — Settings reference

Generated from the `[Setting]` metadata in `src/Osmium.Desktop/Settings/OsmiumSettings.cs` and the engine's parameter
definitions — the Settings page and the Tune dialog render exactly these. Stored in `%APPDATA%\Osmium\settings.json`
(portable mode: `.\data\settings.json`).

**63 settings in 11 categories**, plus 46 codec/filter parameters usable in pipelines, rules and the Tune dialog.

## General

Appearance, startup and notifications.

| Setting | Default | Range / choices | Notes |
|---|---|---|---|
| **Theme**<br>Follow Windows, or force light/dark. | System | System · Light · Dark | restart |
| **Start with Windows**<br>Start Osmium in the notification area when you sign in. | on |  |  |
| **Close button**<br>What the window's close button does. | MinimizeToTray | MinimizeToTray · Exit |  |
| **Notifications**<br>Balloon notifications when jobs finish. | All | All · FailuresOnly · None |  |
| **Confirm exit while jobs run**<br> | on |  |  |
| **Default output**<br>Where new archives go. | NextToSource | NextToSource · Folder · Ask |  |
| **Output folder**<br> | (empty) |  | shown when `Output=Folder` |
| **Open folder when finished**<br> | off |  |  |

## Compression

Defaults for new archives.

| Setting | Default | Range / choices | Notes |
|---|---|---|---|
| **Default profile**<br>Store → Insane: higher profiles try more algorithms and take longer. | Balanced | Store · Fast · Balanced · Max · Ultra · Insane |  |
| **Decode-speed weight**<br>Penalise pipelines that are slow to extract (context mixing decompresses at about 0.3 MB/s). | None | None · Light · Strong |  |
| **Deduplication**<br>Auto = profile default. Chunks finds repeats inside and across files (slower to plan). | Auto | Auto · Off · Files · Chunks |  |
| **Solid block size**<br>0 = profile default (Balanced: 32 MiB). Larger blocks compress better, most of all near-identical files or versions that would otherwise land in different blocks, but need more memory and limit parallelism. | 0 | 0 – 1,024 MiB |  |
| **Verify every block**<br>Decode each block and compare before writing it. Strongly recommended. | on |  | **caution** |
| **Store planner log in archive**<br>Lets 'Explain' show why each pipeline was chosen (archive-relative paths only). | on |  |  |
| **Exclude patterns**<br>One glob per line, e.g. *.tmp or node_modules/** | `Thumbs.db`, `desktop.ini`, `*.osmium-part-*`, `*.osa.partial-*` |  |  |
| **Include hidden files**<br> | on |  |  |
| **After a verified compression, move the originals to the Recycle Bin**<br>Needs Verify every block. Only files and folders the archive holds completely and unchanged move, and only on drives with a Recycle Bin; the job's log says why anything stays. When the Recycle Bin is full, Windows makes room by removing its oldest items, as it does for every delete. | off |  | **caution** |

## Codecs

Which algorithms the planner may use, and their resource limits.

| Setting | Default | Range / choices | Notes |
|---|---|---|---|
| **Allow context mixing (CM)**<br>Osmium CM (cm2; cm in older archives and explicit pipelines): smallest output, but compresses AND extracts slowly. | on |  |  |
| **Allow PPMd**<br> | on |  |  |
| **Allow LZMA**<br> | on |  |  |
| **Allow Zstandard**<br> | on |  |  |
| **Allow Brotli**<br> | on |  |  |
| **Allow BZip2**<br> | on |  |  |
| **CM model memory**<br>log2 of 64-byte table lines: 20 = 64 MiB, 22 = 256 MiB, 24 = 1 GiB. 0 = profile default. Extraction needs the same memory. | 0 | 0 – 24 | advanced |
| **LZMA dictionary**<br>0 = profile default. | 0 | 0 – 512 MiB | advanced |
| **Zstandard level**<br>0 = profile default. | 0 | 0 – 22 | advanced |

## Filters

Reversible pre-processing that makes data more compressible.

| Setting | Default | Range / choices | Notes |
|---|---|---|---|
| **x86/x64 branch converter**<br>For .exe/.dll/.so: CALL/JMP targets (x86), and for x64 code RIP-relative addresses too (x86v2). | on |  |  |
| **ARM64 branch converter**<br> | on |  |  |
| **Delta (records, tables)**<br> | on |  |  |
| **Byte-plane transpose**<br> | on |  |  |
| **PCM audio predictor**<br>Uncompressed .wav. | on |  |  |
| **Image predictor**<br>Uncompressed .bmp. | on |  |  |
| **Text dictionary + capitalization**<br> | on |  |  |

## Analysis

How hard the planner searches for the best pipeline.

| Setting | Default | Range / choices | Notes |
|---|---|---|---|
| **Sample windows per file**<br>0 = profile default. More windows = better detection, slower start. | 0 | 0 – 512 |  |
| **Candidates per group**<br>0 = profile default. Upper bound of pipelines raced by successive halving. | 0 | 0 – 200 |  |
| **Parameter-search evaluations**<br>-1 = profile default, 0 = off. Hill-climbing steps per group. | -1 | -1 – 1,000 |  |
| **Race top-2 on full blocks**<br>Compress every block twice with the two best pipelines and keep the smaller (Insane does this). | off |  |  |

## Performance

CPU, memory and power.

| Setting | Default | Range / choices | Notes |
|---|---|---|---|
| **Threads**<br>0 = all logical processors. | 0 | 0 – 512 |  |
| **Memory limit**<br>0 = automatic (70 % of free RAM). | 0 | 0 – 1,048,576 MiB |  |
| **Process priority**<br>Lower priority keeps your PC responsive during long jobs. | BelowNormal | Idle · BelowNormal · Normal |  |
| **Keep the PC awake while working**<br> | on |  |  |
| **Pause jobs on battery power**<br> | on |  |  |

## Security

Encryption defaults.

| Setting | Default | Range / choices | Notes |
|---|---|---|---|
| **Encrypt new archives by default**<br>AES-256-GCM; file names are encrypted too. | off |  |  |
| **Key-derivation iterations**<br>PBKDF2-SHA512 cost. Higher = slower brute force (and slower to open). | 600000 | 100,000 – 20,000,000 | advanced |
| **Remember passwords until Osmium exits**<br> | off |  |  |

## Integration

Explorer and Windows integration.

| Setting | Default | Range / choices | Notes |
|---|---|---|---|
| **Explorer context menu**<br>"Osmium" submenu on files and folders (under "Show more options" on Windows 11). | on |  |  |
| **Open .osa archives with Osmium**<br> | on |  |  |
| **Quick-compress profiles in menu**<br>Profiles offered directly in the context menu (one per line). | `balanced`, `ultra` |  |  |
| **Invocation batching window**<br>Explorer starts one process per selected file; invocations within this window become one job. | 350 | 100 – 5,000 ms | advanced, restart |

## Watch folders

Automatically compress files that appear in folders.

| Setting | Default | Range / choices | Notes |
|---|---|---|---|
| **Enable watch folders**<br> | off |  |  |

## Cluster

Share compression work between PCs on your network.

| Setting | Default | Range / choices | Notes |
|---|---|---|---|
| **Use cluster workers for my jobs**<br> | off |  |  |
| **Find workers automatically (LAN)**<br> | on |  | shown when `UseWorkers` |
| **Worker addresses**<br>host:port, one per line (for routed networks/VPNs). | (empty) |  | shown when `UseWorkers` |
| **Lend this PC as a worker**<br>Other Osmium PCs with the same cluster key can send work here. They see file contents (never names). | off |  |  |
| **Worker port**<br> | 46510 | 1,024 – 65,535 | advanced |
| **Worker slots**<br>0 = all logical processors. | 0 | 0 – 512 |  |
| **Worker memory limit**<br>0 = automatic. | 0 | 0 – 1,048,576 MiB |  |
| **Cluster key**<br>Shared secret (OSM1-…). Generate one on the Cluster page. | (empty) |  |  |
| **Use workers for encrypted archives**<br>Workers would see the plaintext of encrypted jobs. | off |  | **caution** |

## Experimental

Features that may be very slow, use huge memory, or change in future versions. Archives made with them are flagged.

| Setting | Default | Range / choices | Notes |
|---|---|---|---|
| **Enable experimental features**<br>Master switch for everything on this page. | off |  |  |
| **Offer the Exhaustive profile**<br>Every candidate compressed on the full data, then hill-climbed. | off |  | **experimental**, enabled when `Enabled` |
| **CM: all context models**<br>Adds order-5/8, sparse and x86 models to every CM candidate (slower, sometimes smaller). | off |  | **experimental**, enabled when `Enabled` |
| **Distribute symmetric codecs**<br>Send CM/PPMd blocks to workers too (verification then costs as much as compressing). | off |  | **experimental**, enabled when `Enabled` |

## Watch-folder entries

Each watched folder (Settings → Watch folders) has: path, include subfolders, include/exclude patterns
(default exclude `*.osa;*.tmp;~$*;*.part;*.crdownload;*.partial-*`), settle time (10 s), minimum size, profile and output subfolder (`_osmium`). The originals are kept unless
**After a verified compression, move the originals to the Recycle Bin** (Compression) is on; even then nothing is ever deleted for good.

## Profiles

| Profile | Sample windows | Trial rungs | Candidates | Search evals | Block cap | CM | Dedup | Description |
|---|---|---|---|---|---|---|---|---|
| `store` | 0 | — | 1 | 0 | 64M | no | Off | No compression; packaging only. |
| `fast` | 1 | — | 1 | 0 | 16M | no | Off | zstd level 3; quick to make and to open. |
| `balanced` | 16 | — | 1 | 0 | 32M | no | Files | Smart per-type defaults (LZMA / PPMd / zstd with filters). Fast to decompress. |
| `max` | 32 | 1M | 4 | 0 | 256M | no | Files | Trial-compresses a few candidates per group; no context mixing, so it still opens fast. |
| `ultra` | 64 | 1M → 4M | 10 | 24 | 512M | yes (mem 22) | Files | Context mixing allowed, two sample rungs and parameter search. |
| `insane` | 128 | 256K → 1M → 4M → 16M | 40 | 48 | 1G | yes (mem 24) | Chunks | Everything: up to 40 candidates, 4 rungs, deep search, top-2 race on full blocks, chunk dedup. Very slow; CM archives also decompress slowly. |
| `exhaustive` | 128 | 16M | 200 | 96 | 1G | yes (mem 24) | Chunks | EXPERIMENTAL: every candidate is compressed on the full data, then hill-climbed. Can take days. |

## Codec and filter parameters

Use them in pipeline strings (`lzma(dict=128M,lc=0,lp=2,pb=2)`), in rules (`"params": { "lzma.dict": "256M" }`) or in the Tune dialog.
Sizes accept K/M/G suffixes. "Searched" parameters are explored by the hill-climbing optimizer.

### `store` — Store (codec, id 0)

No compression. Used for already-compressed data (JPEG, MP4, ZIP…).

No parameters.

### `deflate` — Deflate (codec, id 1) · output may differ between library versions

Classic ZIP/gzip algorithm. Included as a baseline; rarely the smallest.

| Parameter | Default | Range | Searched values | Meaning |
|---|---|---|---|---|
| `level` | 9 | 1 – 9 | 1, 6, 9 | Level. zlib compression level. |

### `brotli` — Brotli (codec, id 2) · output may differ between library versions

Strong on web text thanks to its built-in dictionary; extremely fast to decode.

| Parameter | Default | Range | Searched values | Meaning |
|---|---|---|---|---|
| `quality` | 11 | 0 – 11 | 9, 10, 11 | Quality. Brotli quality; 10–11 are dramatically slower but smaller. |
| `window` | 24 | 10 – 24 | 20, 22, 24 | Window (log2). Sliding window size as a power of two. |

### `zstd` — Zstandard (codec, id 3)

Very fast decoding with a configurable long window; good all-rounder.

| Parameter | Default | Range | Searched values | Meaning |
|---|---|---|---|---|
| `level` | 19 | 1 – 22 | 3, 9, 15, 19, 21, 22 | Level. Compression level; 20–22 are 'ultra' levels. |
| `wlog` | 0 | 0 – 31 | 0, 24, 27, 30 | Window (log2). Match window as a power of two; 0 = automatic (fits the block, 20–27). |
| `ldm` | 0 | 0 – 1 | 0, 1 | Long-distance matching. Finds repeats far apart (large backups, VM images). |
| `ldmmin` | 0 | 0 – 4,096 | — | LDM minimum match. 0 = default (64). Smaller finds more but costs speed. **(experimental)** |
| `strategy` | 0 | 0 – 9 | 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 | Strategy. 0 = level default; 9 = btultra2. **(experimental)** |

### `lzma` — LZMA (codec, id 4)

The 7-Zip algorithm. Excellent ratio with fast decoding; lc/lp/pb tune it to the data.

| Parameter | Default | Range | Searched values | Meaning |
|---|---|---|---|---|
| `dict` | 64M | 64K – 512M | — (resource/structure) | Dictionary. Match window. Automatically shrunk to the block size. Encoder needs ~11.5× this in RAM (bt4). |
| `lc` | 3 | 0 – 8 | 0, 1, 2, 3, 4, 8 | Literal context bits. High bits of the previous byte used to predict literals. 0 suits binaries, 3–4 text. |
| `lp` | 0 | 0 – 4 | 0, 1, 2 | Literal position bits. Alignment of literals. 2 suits 32-bit records. |
| `pb` | 2 | 0 – 4 | 0, 1, 2 | Position bits. Alignment of matches. 0 suits text, 2 binaries. |
| `fb` | 273 | 5 – 273 | 32, 64, 128, 273 | Fast bytes. Longer is slower and usually smaller. |
| `mf` | 1 | 0 – 1 | 0, 1 | Match finder. bt2 (less RAM) or bt4 (better). |

### `bzip2` — BZip2 (codec, id 5)

Burrows–Wheeler block sorting (900 KB blocks). Occasionally wins on text-like data.

No parameters.

### `ppmd` — PPMd (var. I) (codec, id 6)

Prediction by partial matching. Excellent on text; decoding is as slow as encoding.

| Parameter | Default | Range | Searched values | Meaning |
|---|---|---|---|---|
| `order` | 8 | 2 – 16 | 4, 6, 8, 10, 12, 16 | Model order. Context length. 6–8 for general text, up to 16 for very repetitive text. |
| `mem` | 64M | 1M – 256M | — (resource/structure) | Model memory. Memory for the context tree (same for compression and decompression). |

### `cm` — Osmium CM (context mixing) (codec, id 7)

Osmium's own context-mixing compressor: usually the smallest output, but slow (~0.5 MB/s) in BOTH directions.

| Parameter | Default | Range | Searched values | Meaning |
|---|---|---|---|---|
| `mem` | 22 | 16 – 26 | — (resource/structure) | Model memory (log2 of 64-byte buckets). Hash-table size: 20 = 64 MiB, 22 = 256 MiB, 24 = 1 GiB, 26 = 4 GiB. Same memory is needed to decompress. |
| `models` | 767 | 0 – 4,095 | 767, 1,599, 975, 4,095 | Model set (flags). Bit flags selecting context models: orders 2/3/4/5/6/8, word, word pair, sparse A/B/C, match, record. |
| `limit` | 255 | 2 – 1,023 | 60, 127, 255, 1,023 | Counter adaptation limit. Higher = more stationary statistics, lower = adapts faster to changing data. |
| `lr` | 16 | 1 – 64 | 8, 12, 16, 24 | Mixer learning rate. First-layer mixer learning rate. |
| `lr2` | 10 | 1 – 64 | 6, 10, 16 | Final mixer learning rate. Second-layer mixer learning rate. |
| `minlen` | 6 | 3 – 32 | 4, 6, 8, 12 | Match model minimum length. Shortest repeat the match model will follow. |
| `stride` | 0 | 0 – 65,536 | — (resource/structure) | Record stride. Record length for the record model (0 = off; set automatically when a stride is detected). |
| `apm` | 7 | 4 – 9 | 6, 7 | SSE adaptation rate. Adaptation shift of the secondary estimation stages. |

### `cm2` — Osmium CM2 (context mixing, v2) (codec, id 8)

Osmium's second-generation context-mixing compressor (bit histories, x86/x64 contexts): usually the smallest output, but slow (~0.3 MB/s) in BOTH directions. Needs Osmium 0.2.0 or newer to decompress.

| Parameter | Default | Range | Searched values | Meaning |
|---|---|---|---|---|
| `mem` | 22 | 16 – 24 | — (resource/structure) | Model memory (log2 of 64-byte lines). Hash-table size: 20 = 64 MiB, 22 = 256 MiB, 24 = 1 GiB. Same memory is needed to decompress. |
| `models` | 767 | 0 – 12,287 | 767, 1,599, 975, 9,167, 12,287 | Model set (flags). Bit flags selecting context models: orders 2/3/4/5/6/8, word, word pair, sparse A/B/C, match, record, x86 (8192). |
| `limit` | 255 | 2 – 1,023 | 60, 127, 255, 1,023 | Counter adaptation limit. Order-0/1 counters: higher = more stationary statistics, lower = adapts faster to changing data. |
| `lr` | 16 | 1 – 64 | 8, 12, 16, 24 | Mixer learning rate. First-layer mixer learning rate. |
| `lr2` | 10 | 1 – 64 | 6, 10, 16 | Final mixer learning rate. Second-layer mixer learning rate. |
| `minlen` | 6 | 3 – 32 | 4, 6, 8, 12 | Match model minimum length. Shortest repeat the match model will follow. |
| `stride` | 0 | 0 – 65,536 | — (resource/structure) | Record stride. Record length for the record model (0 = off; set automatically when a stride is detected). |
| `apm` | 7 | 4 – 9 | 6, 7 | SSE adaptation rate. Adaptation shift of the secondary estimation stages. |

### `x86` — x86/x64 branch converter (filter, id 64)

Converts relative CALL/JMP targets to absolute addresses so repeated calls look identical. For .exe/.dll (not .NET IL).

No parameters.

### `arm64` — ARM64 branch converter (filter, id 65)

Converts relative BL targets to absolute so repeated calls match. For ARM64 executables.

No parameters.

### `delta` — Delta (filter, id 66)

Stores differences between bytes that are 'stride' apart. Great for tables, raw audio, sensor logs.

| Parameter | Default | Range | Searched values | Meaning |
|---|---|---|---|---|
| `stride` | 1 | 1 – 256 | 1, 2, 3, 4, 6, 8, 12, 16 | Stride (bytes). Distance to the byte this one is predicted from. |

### `transpose` — Byte-plane transpose (filter, id 67)

Groups the k-th byte of every record together. Helps numeric arrays, floats, UTF-16.

| Parameter | Default | Range | Searched values | Meaning |
|---|---|---|---|---|
| `stride` | 4 | 2 – 64 | 2, 3, 4, 8, 12, 16 | Record size (bytes). Splits records into byte planes (all first bytes, then all second bytes…). |

### `pcm16` — PCM audio predictor (16-bit) (filter, id 68)

Predicts each audio sample from previous ones and stores only the error. For uncompressed .wav.

| Parameter | Default | Range | Searched values | Meaning |
|---|---|---|---|---|
| `offset` | 0 | 0 – 2,147,483,647 | — (resource/structure) | Region offset. Start of the PCM data in the block. |
| `length` | 2,147,483,647 | 0 – 2,147,483,647 | — (resource/structure) | Region length. Length of the PCM data (clipped to the block). |
| `channels` | 2 | 1 – 8 | — (resource/structure) | Channels. Interleaved channel count. |
| `lms` | 16 | 0 – 32 | 0, 8, 16, 32 | LMS order. Adaptive filter taps (0 = fixed predictor only). |
| `split` | 1 | 0 – 1 | 0, 1 | Split byte planes. Store low bytes then high bytes of the residuals. |

### `image` — Image predictor (2-D) (filter, id 69)

Predicts each pixel from its left, upper and upper-left neighbours (MED). For uncompressed .bmp.

| Parameter | Default | Range | Searched values | Meaning |
|---|---|---|---|---|
| `offset` | 0 | 0 – 2,147,483,647 | — (resource/structure) | Pixel data offset. Start of the pixel array in the block. |
| `width` | 1 | 1 – 1,048,576 | — (resource/structure) | Width (pixels). Image width. |
| `rows` | 1 | 1 – 1,048,576 | — (resource/structure) | Rows. Image height. |
| `bpp` | 3 | 1 – 4 | — (resource/structure) | Bytes per pixel. 3 = 24-bit BGR, 4 = 32-bit BGRA, 1 = grayscale/indexed. |
| `rowbytes` | 4 | 1 – 16,777,216 | — (resource/structure) | Row stride (bytes). Bytes per row including padding. |
| `green` | 1 | 0 – 1 | 0, 1 | Subtract green. Decorrelate colour channels before prediction (bpp ≥ 3). |

### `text` — Text dictionary + capitalization (filter, id 70)

Replaces frequent words with short codes and folds capital letters into flags. For English-like text.

No parameters.

### `x86v2` — x86/x64 branch and RIP-relative converter (filter, id 71)

Converts x64 RIP-relative addresses and CALL/JMP targets to absolute ones, following the instructions. For .exe/.dll (not .NET IL); needs Osmium 0.2.0 or newer to decompress.

| Parameter | Default | Range | Searched values | Meaning |
|---|---|---|---|---|
| `calls` | 1 | 0 – 1 | 0, 1 | Convert CALL/JMP. Also convert CALL/JMP rel32 (E8/E9) targets. Helps native code; .NET ReadyToRun code calls mostly through RIP-relative cells. |

