# Osmium — Implementation Plan

This is the phased build plan that turned the [blueprint](BLUEPRINT.md) into code. The
[checklist](CHECKLIST.md) tracks item-level status; [IMPROVISATION.md](IMPROVISATION.md)
records the critic review and the changes it caused.

## 0. How the plan was produced (multi-agent planning)

| Step | Agent | Output | Outcome |
|---|---|---|---|
| 0 | Lead (me) | Feasibility spikes: .NET 10 SDK install, WinForms cross-compile from Linux, self-contained `win-x64` publish, managed codec round-trips (zstd, LZMA SDK, BZip2, PPMd, Brotli), Wine GUI attempt | Stack confirmed. Wine crashes in the sandbox (`free(): invalid pointer` before DLL load) → replaced by a `--selftest-ui` mode that runs on real Windows in CI |
| 1a | Architect A — engine | Container format, codec/filter interfaces, CM codec design, filters with invertibility arguments, dedup, verification | Merged into BLUEPRINT §3–§6 and FORMAT.md |
| 1b | Architect B — analysis/tuning/distributed | Sniffer, stats, profiles, successive halving + hill-climb, rules + precedence, job system, secure wire protocol, scheduling | Merged into BLUEPRINT §7–§10 |
| 1c | Architect C — Windows UX/installer | Tray, single-instance IPC + coalescer, pages, attribute-driven settings, theming/DPI, Explorer verbs, watch folders, Inno Setup, CI | Merged into BLUEPRINT §11–§13 |
| 2 | Lead | Synthesis: resolve conflicts, cut scope, write blueprint | BLUEPRINT.md, FORMAT.md |
| 3 | Critic agent | Adversarial review of the synthesized blueprint (correctness, data-loss, security, scope, testability) | IMPROVISATION.md; blueprint amended |
| 4 | Lead | Implementation in the phases below, with tests at each phase | This repository |

Architects ran in parallel and never edited files; they returned design reports. The critic
ran after synthesis so it reviewed one coherent design instead of three partial ones.

## 1. Guiding constraints

1. **Lossless above all.** Every archive is verified (decode + hash) before it replaces anything;
   output is written to a temp file and atomically renamed. Sources are never modified.
2. **Determinism.** Codecs produce byte-identical output on every CPU/OS (integer-only CM,
   single-threaded per task). This makes distributed trials checkable and archives portable.
3. **Linux-testable core.** Everything except WinForms views lives in `net10.0` projects with
   managed-only dependencies, so the whole engine, planner, protocol, IPC and settings model
   are unit-tested in the dev container. The WinForms layer is thin and exercised on Windows CI
   by `--selftest-ui`.
4. **Cut scope before cutting quality.** Items that could not be finished properly were moved
   to the backlog rather than shipped half-working.

## 2. Phases

### Phase 1 — Engine foundations
- Varint/blob encoding, hashing helpers (XxHash3/XxHash128, SHA-256).
- `PipelineSpec` (filters + codec + params) with canonical binary/JSON forms and validation.
- Codec registry and wrappers: Store, Deflate, Brotli, Zstd (incl. LDM/windowLog), LZMA
  (full lc/lp/pb/fb/dict/matchfinder), BZip2, PPMd, Osmium CM.
- Filters: x86 E8/E9, ARM64 BL, Delta(stride), Transpose(stride), PCM16 prediction,
  BMP 2D (MED) prediction, Text (capitalization + word dictionary).
- **Exit criteria:** randomized + adversarial round-trip tests pass for every codec and filter,
  including empty/1-byte/odd-length inputs.

### Phase 2 — Container
- `.osa` writer (streaming, blocks out of order, trailing index, inline block headers for salvage),
  reader (random access), extractor, tester.
- Whole-file and content-defined-chunk (Gear/FastCDC) dedup, segment references.
- AES-256-GCM encryption of blocks + index (PBKDF2-SHA256 key derivation).
- Atomic write (`.tmp` → verify → rename).
- **Exit criteria:** archive round-trip tests for trees with empty files, empty dirs, duplicates,
  large files spanning blocks, unicode names, encryption on/off, wrong password, corrupted bytes
  (must be detected, never silently mis-extracted).

### Phase 3 — Analysis, planner, rules
- Sniffer (signatures + structure hints), sampled statistics, stride detection.
- Candidate generation per compatibility class; profiles Store → Insane (+ Exhaustive, experimental).
- Successive halving over sample rungs; hill-climb parameter search; full-data race in Insane.
- Rules/patches model, precedence, provenance, `explain` output stored in the archive.
- **Exit criteria:** planner picks the known-best pipeline on synthetic fixtures (text → CM/PPMd,
  4-byte records → transpose/delta, x86 code → E8/E9 filter, random → Store).

### Phase 4 — Jobs, CLI, distributed
- Job runner: progress snapshots, pause/resume (checkpoint gate), cancel, memory budget.
- CLI: `a` (add), `x` (extract), `l` (list), `t` (test), `analyze`, `explain`, `bench`/`lab`,
  `tune`, `worker`, `rules check`.
- Distributed: ECDH + HKDF + AES-GCM framed channel with HMAC-authenticated handshake,
  UDP discovery, scheduler with local fallback, remote result verification.
- **Exit criteria:** loopback cluster test (coordinator + 2 workers in-process) produces an
  archive byte-identical to a local-only run; tampered frames are rejected; a lying worker is
  quarantined.

### Phase 5 — Windows desktop
- `Osmium.Desktop` (testable): settings model + attribute descriptors, IPC protocol, invocation
  coalescer, shell registration plan, tray text helpers, watch-folder settle engine.
- `Osmium.App` (WinForms): tray context + ring progress icon, main window with nav rail
  (Compress, Jobs, Archives, Lab, Rules, Cluster, Settings, Log), TuneDialog, metadata-driven
  settings renderer, dark/light theme, `--register/--unregister`, `--selftest-ui`.
- **Exit criteria:** compiles with zero errors from Linux; desktop logic tests pass;
  self-test renders every page on Windows CI.

### Phase 6 — Packaging and release
- Inno Setup script (per-user or all-users, context menu, association, startup, Send To, PATH).
- GitHub Actions: build + test (Linux and Windows), publish win-x64/win-arm64, UI self-test,
  installer, portable ZIP, release on tag.
- **Exit criteria:** workflow file validated; local `dotnet publish -r win-x64` succeeds.

## 3. Verification strategy

| Layer | How it is verified here | How it is verified on Windows |
|---|---|---|
| Codecs/filters | xUnit round-trips, randomized inputs, edge sizes | same tests on `windows-latest` |
| Determinism | golden output hashes for fixed inputs | same golden hashes must match on Windows |
| Container | archive round-trip/corruption tests | same |
| Planner | fixture tests | same |
| Distributed | in-process loopback cluster tests | same |
| CLI | end-to-end shell runs on real files | CI smoke step |
| Desktop logic | xUnit (Osmium.Desktop.Tests) | same |
| WinForms views | compile only | `Osmium.App.exe --selftest-ui` renders every page to PNG (uploaded as CI artifacts) |
| Installer | script review | `ISCC` compile in CI |
