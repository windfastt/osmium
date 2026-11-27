# Osmium — Checklist

Legend: ✅ done and verified in this repository (tests or end-to-end runs) · 🪟 done, compiles, verified only on
Windows by CI (`--selftest-ui`, installer compile, Windows test run) · ⬜ backlog (see IMPROVISATION.md §4).

## Planning
- ✅ Feasibility spikes (SDK, WinForms cross-compile, self-contained publish, managed codecs)
- ✅ Three parallel architect reports (engine · analysis/distributed · Windows UX/installer)
- ✅ Synthesized blueprint, format spec, phased plan
- ✅ Adversarial critic review; findings triaged into IMPROVISATION.md
- ✅ Measured CM tuning decisions before freezing the format (IMPROVISATION.md §5)

## Engine
- ✅ Pipeline model: filters + codec, named parameters, canonical text form `x86+lzma(dict=64M,lc=0)`
- ✅ Codecs: Store, Deflate, Brotli, Zstd (window/LDM/strategy), LZMA (dict/lc/lp/pb/fb/mf), BZip2, PPMd, **Osmium CM**
- ✅ Osmium CM: orders 0–8, word, word-pair, sparse, record (stride), match model, 2-layer integer mixer, 3 APMs, learned final stage
- ✅ CM is integer-only and pinned by a golden hash (cross-platform determinism checked again on Windows CI)
- ✅ Filters: x86 E8/E9 (exhaustively tested), ARM64 BL, delta, transpose, PCM16 (+LMS), image (MED + subtract-green), text (dictionary + capitalization, fuzzed)
- ✅ Store fallback when compression does not help; per-block decode-and-compare verification
- ✅ Decompression-bomb limits (1 GiB blocks, decoder windows/tables clamped to block size, expansion check)

## Container (.osa)
- ✅ Header with critical/ignorable extensions, CRC-protected frames, length-prefixed index records, footer
- ✅ Blocks written in plan order (reproducible archives); files reference (frame, offset, length) segments
- ✅ AES-256-GCM encryption of every block body and the index (names hidden), Argon2id (PBKDF2-SHA512 for Compatible) + HKDF, password verifier, header bound into the index AAD
- ✅ Every single-byte corruption of a test archive is detected (exhaustive test)
- ✅ Extraction: Windows path rules on every OS, verify-then-rename, Mark-of-the-Web propagation (Windows), overwrite policies
- ⬜ Salvage tool, recovery records, multi-volume, append/update

## Analysis & planning
- ✅ Sniffer: PE (x86/x64/ARM64/.NET IL), ELF, WAV, BMP, text/UTF-16, 20+ compressed formats, PDF, SQLite/OLE
- ✅ Statistics: entropy, printable/zero ratios, zstd-1 probe, stride detection with harmonic suppression
- ✅ Profiles Store → Insane (+ Exhaustive, experimental)
- ✅ Successive-halving trials on deterministic samples, hill-climbing over tunable parameters, top-2 race
- ✅ Planner fixture tests: random → Store, text → text codec, int32 records → delta/transpose, x64 PE → x86 filter,
  WAV → PCM predictor with parsed geometry, BMP → image predictor with parsed geometry, rule-forced pipeline wins
- ✅ Whole-file dedup (SHA-256 confirmed) and FastCDC chunk dedup
- ✅ Rules/patches with layers, priorities, provenance, `rules check`, `explain`
- ⬜ MinHash similarity ordering, intra-file segmentation, GA search, persistent caches

## Distributed
- ✅ Pairing-key clusters, ECDH + HKDF + AES-GCM channel, transcript-bound HMAC auth, replay rejection
- ✅ Engine fingerprint check, heartbeats, chunked transfers, worker memory caps
- ✅ Authenticated UDP discovery
- ✅ Distributed trials with deterministic spot checks; remote blocks verified locally; quarantine
- ✅ Verified end-to-end: two workers, identical blocks to a local-only run, wrong key refused
- ⬜ PAKE passphrases, IPv6 discovery, cross-worker verification of CM blocks

## CLI (`osmium.exe`)
- ✅ a / x / l / t / info / explain / analyze / lab / tune / methods / profiles / rules check / worker / keygen

## Windows app (`Osmium.App.exe`)
- ✅ Desktop logic (testable on Linux): settings model (60+ settings, 11 categories), job queue, IPC, coalescer, registry plan, tray text, watch-folder engine
- 🪟 Tray icon with progress ring, menu (profiles, recent jobs, pause, watch folders, lend PC), balloons
- 🪟 Single instance + named-pipe forwarding of Explorer invocations
- 🪟 Pages: Compress (drag & drop, per-file patches), Jobs, Archives, Lab, Rules, Cluster, Settings (metadata-driven, Experimental gating, search), Log
- 🪟 Tune dialog: manual pipeline builder from engine metadata, quick test, deep auto-tune
- 🪟 Dark/light theme, DWM title bar, PerMonitorV2 DPI
- 🪟 Explorer verbs, `.osa` association, startup, Send To (installer)
- ⬜ Windows 11 top-level menu (signed sparse package), toasts with actions, out-of-process job host

## Packaging & CI
- ✅ Co-published self-contained App + CLI (shared runtime), single-file CLI, portable ZIP — built here with `build/publish.ps1`
- 🪟 Inno Setup installer (per-user/all-users, tasks, graceful close, clean uninstall)
- 🪟 GitHub Actions: Linux + Windows tests, publish x64/arm64, UI self-test screenshots (light + dark), CLI smoke test, installer, releases on tags
- ⬜ Code signing (needs a certificate)

## Release 0.1.1
- ✅ Cross-check against zip, bzip2, Brotli, Zstandard, xz, 7-Zip (LZMA2, PPMd), lrzip and ZPAQ with every round trip verified ([BENCHMARKS.md](BENCHMARKS.md), `tools/crosscheck.py`)
- ✅ Groups planned concurrently (identical plans and archives, Ultra planning 428 s → 292 s on the mixed test folder)
- ✅ Release notes taken from CHANGELOG.md by the release job
- ✅ Product website (`docs/index.html`, ready for GitHub Pages from `/docs`)
- ⬜ Hill-climb screening on smaller samples (Ultra/Insane planning time; see IMPROVISATION.md §5.4)
- ⬜ Stronger x86 modelling in CM: ZPAQ -m5 is 6.2 % smaller than Ultra on program files ([BENCHMARKS.md](BENCHMARKS.md))
