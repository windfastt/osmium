# Changelog

All notable changes to Osmium. Versions follow [Semantic Versioning](https://semver.org); the `.osa` format version
(1.0) is independent of the app version and has not changed.

## [0.2.0] — unreleased

### Fixed
- **Licensing:** every download now carries the license texts it must: `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` (now
  with full license texts and copyright lines for ZstdSharp/Zstandard, SharpCompress and its Bouncy Castle notice, the
  code SharpCompress carries from other projects (DotNetZip and zlib, Apache Ant's BZip2 with Ant's NOTICE lines,
  Info-ZIP, UnRAR, PPMd, SharpZipLib's LZW decoder, ADC), the LZMA SDK and its NuGet packaging, System.IO.Hashing, the
  .NET runtimes and Windows Forms, and Inno Setup) and the
  bundled .NET runtime's own license and notices (`dotnet-LICENSE.txt`, `dotnet-THIRD-PARTY-NOTICES.txt`). Before,
  the portable ZIP and the command-line executable shipped no license at all.
- **Installer:** the optional firewall rule also opens UDP 46511, which LAN discovery of cluster workers needs.
- **CLI password prompt:** arrow keys, Tab, Esc and other non-character keys no longer add hidden characters to the
  password; Ctrl+C cancels the prompt.
- **Settings page:** *Apply* saved a copy of the settings taken when the page was first shown, silently reverting
  anything changed elsewhere since (a cluster key saved on the Cluster page, the tray toggles for the default profile,
  watch folders and lending this PC). It now saves only the settings you edited. The *Verify every block* warning
  appeared when turning verification on instead of off.
- **Cluster keys:** a key pasted with line breaks, spaces, typographic dashes or invisible characters was rejected,
  and extra characters were ignored. Keys are now read the way people paste them, and errors say what is wrong (too
  short or too long, a likely typo, a look-alike character, a checksum mismatch). *Show*/*Hide* works and says what
  it does, *Generate* shows the new key, the key boxes fit a whole key, and *Copy* copies the canonical form. An
  invalid saved key, or a worker that stopped, is shown on the Cluster page, in the tray tooltip and once as a
  notification; *No workers answered* explains what discovery needs.
- **`osmium worker`** without `--key` generated a new random key on every start that matched no other PC; it now
  asks for the cluster key.
- **Explorer verbs:** the window or password prompt opened from Explorer stayed behind Explorer, so a password typed
  right away went to Explorer. The password prompt now stays open on a mismatch instead of closing and reopening.
- **Keyboard:** Enter in a single-line box beeped and did nothing; it now runs the box's action. The Tune dialog
  takes Enter and Esc; focus moves into the page after navigating; `&` in names no longer shows as an underline;
  lists support Ctrl+A, Ctrl+C and Enter; Ctrl+V on the Compress page adds files copied in Explorer; Ctrl+NumPad1–8
  navigate.
- **Display:** ports and counts no longer show thousands separators ("46,510"). The Tune dialog shows each codec
  parameter with a fitting editor (checkbox, list of choices, sizes such as `64M`) instead of raw numbers. The Rules
  page no longer discards unsaved edits when shown again, and the settings search waits until you stop typing.
- **Planner:** each of a sample's 16 slices read its whole block, so a 4 MiB sample of a 112 MiB block read about
  1.8 GB; slices now read only their own bytes (the samples are identical). The parameter search checked memory
  against the sample instead of the block, so it could choose a pipeline that exceeds the memory budget.
- **Archives made by a newer Osmium:** a block using a newer method or parameter made the whole archive fail to open
  with a message that read like corruption. Now the archive opens and lists, every block this version understands
  extracts, and the rest are reported as needing a newer version of Osmium. Values read from an archive are checked
  and never clamped.
- **Mark of the Web:** a file extracted from an archive downloaded from the internet lost Windows' "downloaded from
  the internet" mark when the archive marked it read-only (the mark was written after the attribute, and the failure
  was ignored). The mark is now written before any attribute, and a failure to write it is reported.
- **Extracting into a folder with links:** Osmium could write through a symbolic link or junction that was already in
  the destination; it now plans every target before writing and refuses to write through a link. Each file is
  written to a new part file that stays open until it is verified, then moved into place.
- **Canceling an extraction** left partly written `.osmium-part-…` files behind; they are now removed (files that were
  finished stay).
- **Extracting over an existing folder** could change that folder's attributes and dates; Osmium now sets them only on
  folders it creates, and never makes a folder it creates read-only or a system folder.
- **Names that differ only in case** (`Makefile`, `makefile`) could replace each other on Windows; the second is now
  renamed and reported. An entry that matches an existing file only through its short 8.3 name gets a free name
  instead of replacing it.
- **Renamed files could collide:** when extracting next to existing files, two files could be given the same new name,
  or a renamed file could take the name of another file in the same archive (`report (2).txt`). Every file now keeps
  its own name or gets one that nothing else uses.
- **Memory when extracting:** Osmium reserved memory only for the first step of a block's decoding, so some archives
  could make it use more memory than its limit; it now reserves for the largest step, and a block whose stored sizes
  or pipeline differ from the file list's is refused before it is decoded.
- **Passwords on the command line:** a password option without a value, or an empty password, no longer writes an
  archive without encryption, and `--password-stdin` reads UTF-8 (a byte order mark is ignored).

### Added
- **Spare copy of the file list and header.** Archives of 1 MiB and more whose file list is small next to their data
  (at most 2 % of it) keep a second copy of their file list and header at the end, which costs the size of the file
  list plus 4 KiB. If the main copy is damaged or unreadable, as with a bad sector, Osmium opens the archive from the
  spare and tells you so; *Test* also checks the spare. The .osa container is now version 1.1: Osmium 0.1.x opens,
  tests and extracts 1.1 archives made with the methods it knows. Archives that contain blocks compressed with the
  methods new in 0.2.0 need 0.2.0; 0.1.x does not open them and says they were made by a newer version.
- **Open other archive types from the command line.** `osmium l`, `x`, `t` and `info` work with ZIP, 7z, TAR (also
  .tar.gz/.tgz, .tar.bz2/.tbz2, .tar.xz/.txz and .tar.zst) and single compressed files (.gz, .bz2, .xz, .zst).
  Password-protected ZIP (ZipCrypto and AES) and 7z archives are supported. Archives are recognised by their content,
  so a renamed file still opens. RAR archives are not supported. The app's Archives page and Explorer menu gain these
  formats with the new interface.
- **Safe extraction for every format.** Osmium writes only inside the folder you chose, never creates or follows
  links, refuses reserved device names, and renames files whose names Windows does not allow, saying so. It checks
  each file against the checksum the format stores: CRC-32 for ZIP and 7z, the authentication code of AES-encrypted
  ZIP entries, and the gzip, bzip2, xz and Zstandard checks. TAR stores none, and `osmium t` says so. An archive that
  would expand to more than 1,000 times its size and more than 1 GiB stops unless `--force` is given. Files from
  downloaded archives keep the mark that they came from the internet. A password counts as right only when data it
  decrypted passes a checksum; a wrong password writes nothing, and it is reported after a few small checks rather than
  after decrypting the whole archive.
- **Writing ZIP files:** `osmium a name.zip` writes a ZIP file other tools open. The finished ZIP is read back and
  checked before it appears, and a file that changes while it is read is left out, never stored torn. ZIP files are
  not encrypted; for a password, use an encrypted .osa archive.
- **Verifiable downloads:** every release file carries a build provenance attestation, so
  `gh attestation verify <file> --repo windfastt/osmium` shows that it was built by the repository's own workflow from
  the published source; a modified copy fails the check. CI builds drop machine-specific paths, so a commit builds to
  the same files wherever it is built.
- **Osmium CM2**, a new context-mixing codec (method id 8): bit-history models, contexts from an approximate x86/x64
  instruction decoder (opcode, ModRM, prefixes, previous instruction) and a larger mixer. With the new **x86v2**
  filter (id 71), which converts RIP-relative operands as well as CALL/JMP targets, Ultra made a 124.1 MiB folder of
  Windows program files 22.98 % of its original size, against 27.29 % for the previous release on the same folder
  and 25.60 % for ZPAQ -m5 (sizes measured on one Linux machine; timings were not comparable because the machine
  was loaded). On the 16 MiB mixed test folder Ultra's archive is 8.3 % smaller. CM2 needs about 1.1 times the time
  of Osmium CM and the same memory. x86v2 also helps the faster profiles on x64 code (about 7 % smaller with LZMA).
  Archives that use CM2 or x86v2 need Osmium 0.2.0 or later; older versions open them, extract every other block and
  report that those blocks need a newer version. Osmium CM stays unchanged, so existing archives decode as before.
- `osmium license` prints the license and third-party notices; `osmium version` and the help text state the MIT
  license and that Osmium comes without warranty.
- **`osmium salvage <archive> <destination>`** recovers what can be trusted from a damaged archive. It scans for
  intact frames without relying on the footer or the index, writes only files whose blocks and own hash verify (or,
  when the index is lost, each verified block as `block-<n>.bin` with a report of its pipeline, sizes and hashes),
  and lists every file it could not recover and why. Encrypted archives need the password and an intact enough
  header; archives stored inside an archive are never mistaken for it. It refuses to write more than the expansion
  limit or the free space allows unless `--force` is given, and exits with 0 only when nothing was lost. It uses the
  spare copies of the file list and header when the main ones are lost, including the key of an encrypted archive
  whose header is destroyed, and writes recovered files by the same rules as extraction.
- **About Osmium** (tray menu and Settings page): version, engine fingerprint, license, and buttons that open the
  license and third-party notices.
- Exiting asks first when the Settings, Rules or Cluster page has unsaved edits.
- **Move the originals to the Recycle Bin after a verified compression** (Settings → Compression, off by default).
  An input moves only when the job finished with every block verified and no warnings, the archive holds it unchanged
  under a name Osmium can extract, a folder holds nothing that was not archived (hidden, system and excluded files
  count) and no links or cloud placeholders, the archive is not inside it, and it is on a fixed local drive. Each
  input is checked again just before it moves; Windows is never allowed to delete anything permanently or to take
  along items it was not given (such as a web page's `_files` folder). The job log names what moved and why the rest
  stayed; the job summary, history and notification count both.
- **Tests:** golden vectors that pin the output of every Osmium CM model set and every filter, planner tests with
  scripted trial results (successive halving, parameter search, Store preference, the top-two race, thread-count
  independence), sampling and memory-limit tests, tests for archives from a newer version, cluster-key parsing and
  settings-editing tests.

### Changed
- **Profiles:** Ultra, Insane and Exhaustive use CM2 instead of Osmium CM; x64 code gets the x86v2 filter, 32-bit x86
  code keeps the x86 filter and is grouped separately. Forbidding `cm` or `x86` in rules or settings also forbids the
  newer member of the family, and `cm.*` parameters apply to CM2. The CM memory setting goes up to 1 GiB (24); the
  experimental 4 GiB table option is gone, since CM2 at 1 GiB compresses better than Osmium CM at 4 GiB.
- **Robustness:** a truncated context-mixing stream now fails where it ends instead of decoding to its declared
  size, and a block whose data makes a decoder fail unexpectedly is reported as damaged on its own instead of
  stopping the whole extraction.
- **Website:** rebuilt from the real product. The mock-ups of the tray and the main window (with invented data) are
  gone; fonts are served from the site itself (no requests to other sites); claims are qualified; the footer states
  the license and warranty disclaimer, third-party notices, trademarks, benchmark conditions and privacy. The page is
  generated by `tools/site/build.py` from the repository's data.
- **Docs:** BLUEPRINT describes only what is built (unbuilt ideas are marked planned); README, BENCHMARKS and ROADMAP
  claims are qualified. The settings-reference generator (`tools/settingsdoc`) and benchmark corpus builder
  (`tools/bench`) are in the repository. ROADMAP lists what 0.2.0 is building.
- **Tools:** `tools/crosscheck.py --only` runs selected tool families and accepts a relative path to Osmium;
  `tools/site/fetch_screenshots.py` imports the screenshots CI renders with the real app.
- **CI:** tests also run natively on Windows ARM64 (with a CLI round trip on the ARM64 build); a failing test suite
  on Windows could pass unnoticed when another suite ran after it, now every suite's result counts; the UI self-test
  does real work on a generated sample folder (analysis, a compression job, the Lab, the Tune dialog) and renders
  every page and dialog at a fixed size in light and dark; the product page and the settings reference are checked
  against their generators.
- **Rules files** keep `&`, `<`, `+` and non-ASCII characters readable instead of writing `&`-style escapes.
- **Archives page:** an archive's details show its own .osa version and whether it has a spare copy of its file list.
- **Compressing:** the check of a new archive now also fails when the archive opens with any warning or its spare
  copies do not check out; such an archive is never kept.
- **CI:** a compatibility check builds Osmium 0.1.1 from its tag and has it test and extract archives made by this
  version.
- **`osmium a`** writes a ZIP file when the name ends in .zip (before, it wrote `name.zip.osa`). Names of types Osmium
  cannot write are refused with a reason.
- **Single compressed files** extract next to the archive. One without a compression extension extracts as
  "name (decompressed)", so it never replaces itself.
- **Stricter command line:** an unknown option is refused (and shown only by its first characters, so a mistyped
  password is never printed), `--` ends the options, and each command accepts only the options it uses. `osmium t`
  exits with 3 when every file verified but there were warnings, and with 2 after an error. More than 1,000 files of
  the same name in an archive of another format are refused.
- **Extraction, also of .osa archives:** the archive being read is never replaced, even when the destination reaches
  it through another path. A file already in the destination where the archive has a folder gives the folder a free
  name; a folder there is never replaced by a file. A long name that needs a free name is shortened to stay within
  255 characters, and a name that cannot be created fails at once instead of being retried.

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
