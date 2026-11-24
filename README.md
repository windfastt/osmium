# Osmium

**A Windows file compressor that lives in your notification area and goes as far as you let it to make files smaller.**

Osmium (named after the densest natural element) picks a compression pipeline *per group of files*: it detects what
each file is, applies reversible pre-processing (x86/ARM64 branch converters, delta/transpose for records, audio and
image predictors, a text dictionary), races candidate codecs on samples, hill-climbs their parameters, and can bring
in an original context-mixing codec and other PCs on your network when you ask for the smallest possible output.

| | |
|---|---|
| **Tray app** | progress ring in the notification area, quick profile switch, pause/resume, recent jobs, watch folders |
| **Explorer** | right-click → *Osmium* → *Add to archive…* / *Compress to .osa (profile)*; *Extract here / to folder / Test* on `.osa` |
| **Profiles** | Store · Fast · Balanced · Max · Ultra · Insane · Exhaustive (experimental) |
| **Codecs** | Osmium CM (context mixing), LZMA, PPMd, Zstandard (long range), Brotli, BZip2, Deflate, Store |
| **Filters** | x86/x64, ARM64, delta, byte-plane transpose, 16-bit PCM (+LMS), 2-D image (MED), text dictionary + capitalization |
| **Per-file control** | right-click a file → *Tune…* to build its pipeline by hand, or *Deep auto-tune*; save as a rule |
| **Rules** | JSON rules: force/forbid pipelines, override parameters (`"lzma.dict": "256M"`), store, exclude, own block |
| **Dedup** | whole-file (SHA-256 confirmed) and content-defined chunking (FastCDC) |
| **Multi-PC** | lend idle PCs as workers; trials and heavy blocks are distributed over an authenticated, encrypted channel |
| **Safety** | every block decoded and compared before writing; atomic writes; hashes on every block and file; AES-256-GCM encryption |
| **Settings** | 63 settings in 11 categories (searchable, [reference](docs/SETTINGS.md)) plus 37 codec/filter parameters; an *Experimental* page behind a master switch |

## Results

Mixed 16 MiB folder (texts, CSV, ELF and .NET binaries, WAV, BMP, PNG/JPEG/gzip, binary records, a duplicate):

| Tool | Size | vs. `tar \| xz -9e` |
|---|---|---|
| `tar \| xz -9e` | 6 095 132 | — |
| Osmium **balanced** (6 s) | 5 604 113 | −8.1 % |
| Osmium **max** (6 s) | 5 407 508 | −11.3 % |
| Osmium **ultra** (53 s) | 4 983 958 | −18.2 % |
| Osmium **insane** (~10 min) | 4 927 730 | **−19.2 %** |

BENCHMARK_PAYLOAD

Every archive above passed `osmium t` and extracted byte-for-byte identical. Ultra/Insane may choose context mixing,
which decompresses about as slowly as it compresses (~0.5 MB/s); Osmium tells you the expected extraction time and
memory before and after compressing. Details: [docs/IMPROVISATION.md §5](docs/IMPROVISATION.md#5-measured-decisions).

## Install

* **Installer:** `Osmium-<version>-win-x64-setup.exe` (or `-arm64`) from the CI artifacts / Releases. Per-user by
  default (no admin prompt); choose *Install for all users* to install machine-wide. Tasks: start with Windows,
  Explorer menu, `.osa` association, Send To, add `osmium` to PATH, firewall rule for cluster workers.
* **Portable:** unzip `Osmium-<version>-win-x64-portable.zip`; settings live in `.\data` (marker file `osmium.portable`).
* **CLI only:** the single-file `osmium-<version>-win-x64.exe` also works as a stand-alone extractor.

Windows 10 1809+ or Windows 11, x64 or ARM64. No separate .NET install is needed.

## Use

**Tray / window** — drop files on the *Compress* page, pick a profile, press *Compress*. Right-click a file to patch
its pipeline. *Jobs* shows progress, ratio, ETA and the planner's log; *Archives* browses, tests and extracts;
*Lab* benchmarks every candidate on one file; *Rules* edits per-file rules; *Cluster* manages workers.

**Command line**

```powershell
osmium a backup.osa C:\Projects --profile ultra            # compress
osmium a logs.osa D:\logs --rules rules.json --explain       # with per-file rules, show decisions
osmium a secret.osa docs --password-stdin                    # AES-256-GCM, file names hidden
osmium x backup.osa C:\Restore                               # extract (verified)
osmium t backup.osa                                          # full test: every block and file hash
osmium info backup.osa                                       # pipelines, sizes, extraction time and memory
osmium lab big.wav --profile insane                          # benchmark all candidates on one file
osmium tune game.pak --export-rule rules.json                # search the best pipeline, save it as a rule
osmium a out.osa data --pipeline "delta(stride=4)+lzma(dict=128M,lc=0,lp=2,pb=2)"
```

**Rules** (`%APPDATA%\Osmium\rules.json`, or `--rules`):

```json
{ "rules": [
  { "id": "logs", "match": { "globs": ["**/*.log"], "minSize": 1048576 },
    "actions": { "params": { "ppmd.order": "16", "lzma.dict": "256M" } } },
  { "id": "game-data", "match": { "path": "data/level01.pak" },
    "actions": { "pipeline": "delta(stride=4)+cm(mem=24)", "solid": "own" } },
  { "id": "no-cm-for-exe", "match": { "kinds": ["CodeX86"] }, "actions": { "forbidCodecs": ["cm"] } },
  { "id": "skip-temp", "match": { "globs": ["**/*.tmp", "node_modules/**"] }, "actions": { "exclude": true } }
] }
```

**Several PCs**

```powershell
osmium keygen                                             # once; share the OSM1-… key
osmium worker --key OSM1-…                                 # on each helper PC (or: tray → Lend this PC)
osmium a big.osa D:\data --profile insane --workers auto --cluster-key OSM1-…
```

Workers authenticate with the key (mutual HMAC over an ECDH handshake, AES-256-GCM traffic), must run the same engine
version, and never receive file names — but they do see file **contents**, so only pair machines you trust. Every
remote result is re-verified locally; a worker that returns a wrong result is quarantined. Ports: TCP 46510, UDP 46511.

## How it works

`scan → analyze (signatures, entropy, stride) → rules → dedup → group by data class → aligned solid blocks →
choose a pipeline per group (sample trials with successive halving, parameter hill-climbing, optional top-2 race)
→ compress blocks in parallel within a memory budget (locally or on workers) → decode-and-compare every block →
write frames in order → re-read the archive → atomic rename`.

* [docs/BLUEPRINT.md](docs/BLUEPRINT.md) — architecture
* [docs/FORMAT.md](docs/FORMAT.md) — the `.osa` binary format
* [docs/SETTINGS.md](docs/SETTINGS.md) — every setting, profile and codec/filter parameter (generated from the code)
* [docs/PLAN.md](docs/PLAN.md) — how it was planned (three architect agents + a critic) and built
* [docs/IMPROVISATION.md](docs/IMPROVISATION.md) — critic findings, what changed, measurements, backlog
* [docs/CHECKLIST.md](docs/CHECKLIST.md) — status of every feature

## Build

```bash
dotnet build Osmium.sln -c Release                       # any OS with the .NET 10 SDK
dotnet test -c Release --project tests/Osmium.Core.Tests     # 60 engine, container, planner and cluster tests
dotnet test -c Release --project tests/Osmium.Desktop.Tests  # 15 settings, IPC, jobs, shell-registration tests
```

```powershell
pwsh build/publish.ps1 -Arch x64            # Windows: publish + portable ZIP + installer (Inno Setup 6)
```

CI (`.github/workflows/ci.yml`) runs the tests on Linux and Windows (including a golden-hash test that proves the
context-mixing codec produces identical output on both), publishes x64 and ARM64, renders every window of the app to
PNG with `Osmium.App.exe --selftest-ui` (light and dark), smoke-tests the CLI, builds the installers and publishes a
release for `v*` tags.

## Status and limitations

* The engine, container, planner, cluster, CLI and the desktop logic are tested on Linux in this repository.
  The WinForms UI and the installer compile here but are exercised on Windows by CI (self-test screenshots,
  installer compile); first run on a real desktop is the remaining validation step.
* Windows 11 shows the Explorer verbs under *Show more options* (a top-level entry needs a signed MSIX sparse package).
* Already-compressed media (JPEG, PNG, MP4, ZIP) is stored as-is; recompressing them losslessly (preflate/packJPG
  style) is on the backlog.
* Osmium writes and reads its own `.osa` format only.

## License

MIT — see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
