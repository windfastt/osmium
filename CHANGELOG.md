# Changelog

All notable changes to Osmium. Versions follow [Semantic Versioning](https://semver.org); the `.osa` format version
(1.0) is independent of the app version and has not changed.

## [0.1.1] — 2026-11-24 — patch release

### Fixed
- **Analysis:** files without a byte-order mark were classified as UTF-16 text whenever ≥ 40 % of odd bytes were zero
  and ≥ 40 % of even bytes printable — which little-endian integer tables satisfy, so they never got the delta and
  transpose candidates meant for them. Thresholds are now 90 %/90 %, with regression tests both ways.
- **Planner:** hill-climbing no longer "optimizes" resource limits (CM memory, LZMA dictionary, PPMd memory) on small
  samples, where a smaller table could win by hash-collision noise and then lose on the full block.
- **Settings that had no effect are now wired:** *CM: all context models* and *Distribute symmetric codecs*
  (Experimental), the *Ask* and *Folder* output modes for Explorer quick-compress, *Remember passwords until Osmium
  exits*, automatic Explorer re-registration when Integration settings change, and a worker restart when its port,
  slots, memory or key change. Theme and invocation batching are marked "restart required".
- **Tray app:** the tray menu could stay closed on first open (empty `ContextMenuStrip`); the Tune dialog could throw
  on open (`BeginInvoke` before handle creation); timer errors could pop a dialog every half second; fonts were
  allocated per settings row and per menu open (GDI leak); settings re-rendering skipped disposing half its controls;
  exit could stall ~2 s waiting on the IPC server; list columns did not scale with DPI; the Rules splitter was clamped
  before layout; *Copy all* on an empty log threw.
- **Robustness:** the final atomic renames (archive commit, extracted files) retry for up to 3 s when antivirus or
  indexers briefly lock a just-written file; encrypted archives derive their key once instead of twice after writing.
- **CLI:** the summary line reports total time including planning (Insane spends most of its time planning).
- **Installer:** Pascal identifiers renamed to avoid clashes with built-ins.

### Changed
- **Planner:** independent groups (text, code, audio, …) are now planned concurrently through one shared pool of
  trial slots instead of one after another. Plans and archives are identical; on the 16 MiB mixed test folder,
  Ultra's planning time fell from 428 s to 292 s on 4 cores (483 s → 346 s in total).
- **CI:** a `v*` tag publishes its release with this changelog's section as the release notes.

### Added
- Planner fixture tests (random → Store, text → text codecs, records → delta/transpose, x64 PE → branch converter,
  WAV/BMP → predictors with parsed geometry, rule-forced pipelines win). 60 core + 15 desktop tests.
- `docs/SETTINGS.md` — every setting, profile and codec/filter parameter, generated from the code.
- `tools/crosscheck.py` and `docs/BENCHMARKS.md` — reproducible comparison with zip, bzip2, Brotli, Zstandard, xz,
  7-Zip (LZMA2 and PPMd), lrzip and ZPAQ, with every round trip verified.
- Product website: `docs/index.html` (GitHub Pages serves it from `/docs`; pages ask search engines not to index them).
- `ROADMAP.md`: next steps, starting with the gaps the benchmarks found (x86 code against ZPAQ, planning time).

## [0.1.0] — 2026-11-24 — first implementation

- `.osa` container: per-block pipelines, CRC-protected frames, length-prefixed index, dedup segments,
  AES-256-GCM encryption with hidden file names, salvage-friendly frame headers.
- Codecs: Osmium CM (original context mixing), LZMA, PPMd, Zstandard, Brotli, BZip2, Deflate, Store.
- Filters: x86/x64 and ARM64 branch converters, delta, byte-plane transpose, 16-bit PCM (+LMS), 2-D image (MED),
  text dictionary + capitalization.
- Planner: signature/structure analysis, stride detection, profiles Store → Insane (+ Exhaustive), successive-halving
  trials, hill-climbing, top-2 race, whole-file and FastCDC chunk dedup, rules/patches with provenance and explain.
- Cluster: pairing-key workers, ECDH + HKDF + AES-GCM channel, LAN discovery, distributed trials and blocks with
  local verification and quarantine.
- CLI (`osmium.exe`), WinForms tray app (`Osmium.App.exe`), Inno Setup installer, GitHub Actions CI.
