# osmium_native: native LZMA and Zstandard encoders

`osmium_native` is a small C library that compresses LZMA and Zstandard blocks with the original C code of the
**LZMA SDK 26.06** (`LzmaEnc`) and of **Zstandard 1.5.7** (`lib/compress`), built by Osmium's CI from the published
upstream sources. Osmium.Core calls it through function pointers. When it is missing, cannot be loaded, or is not
exactly the file Osmium.Core was built with, Osmium uses its built-in (managed) encoders, as before.

The method ids, parameters and stream formats do not change: a block's method and parameters define its bytes, and
every conforming decoder reads it, whichever encoder wrote it. Every archive stays readable by every Osmium version
that knows its methods, 0.1.1 included.

## What it contains, and what it does not

- **Encoders only.** From the LZMA SDK: `LzmaEnc.c`, `LzFind.c`, `LzFindMt.c`, `LzFindOpt.c`, `Threads.c`,
  `CpuArch.c`, `Alloc.c` and their headers. From Zstandard: `lib/common/*.c` (without `threading.c` when it compiles
  to nothing) and `lib/compress/*.c` with their headers. Plus Osmium's own interface layer, `src/osmium_native.c`.
- **No decoder.** Not `LzmaDec.c`, not `lib/decompress`, `lib/legacy`, `lib/dictBuilder` or `lib/deprecated`, no
  PPMd, BCJ2, XZ or AES. Decoding stays in Osmium's managed code, so no byte of an archive reaches native code.
  The binary checks prove it on every build (no `ZSTD_decompress*`, `ZSTD_createDCtx*`, `ZSTD_DCtx*`,
  `ZSTD_decodeFrame*` or `LzmaDec_*` symbol). The decoder files listed as `test` in `sources.files` are linked into
  test programs only, to check round trips.
- **No dictionaries, no worker threads in Zstandard.** The interface never calls `ZSTD_CCtx_loadDictionary*`,
  `ZSTD_CCtx_refPrefix*`, `ZSTD_CCtx_refCDict`, `ZSTD_createCCtxParams` or thread-pool functions, and the library is
  built without `ZSTD_MULTITHREAD` (`check_sources.py` fails otherwise).
- What the encoders do see: **the content of the files being compressed**, which can be chosen by whoever made
  those files (downloads, mail attachments, folders extracted from other archives, blocks sent by cluster peers).
  The encoders parse nothing (no container, header or frame), but their match finders and entropy coders run on
  those bytes. That is why the interface checks every argument before upstream code sees it, bounds all output and
  memory, and is fuzzed with sanitizers (`fuzz/`).

## Files

| File | What it is |
|---|---|
| `sources.lock` | The two upstream archives: version, URL, SHA-256, size, git tag and commit, and how each pin was checked |
| `sources.files` | The SHA-256 of every upstream file that is compiled or included, by its path inside the archive |
| `fetch_sources.py` | Downloads (or takes from a cache or a folder) the archives, checks them, extracts only the listed files |
| `generation.txt` | The encoder generation id, the interface (ABI) number and the expected upstream versions |
| `include/osmium_native.h`, `src/` | Osmium's interface layer (C11) |
| `CMakeLists.txt` | Every source file listed by name; flags per platform |
| `osmium_native.def`, `osmium_native.map` | The exported functions (`osn_*` only) on Windows and Linux |
| `build.sh`, `build.ps1` | Fetch, configure, build, check, write `build-info.txt` and the library into `artifacts/native/<rid>/` |
| `check_binary.sh`, `check_binary.ps1` | Imports, hardening flags, exports, and that no decoder is present |
| `check_sources.py` | Licence and copyright lines of every upstream file, and forbidden references in the interface layer |
| `tests/`, `fuzz/`, `sweep/` | The self-test, the fetch checks, the fuzz harnesses and the cross-platform output sweep |
| `bench/` | Measurement tools for `native-bench.yml`: library builds compared in one process by per-thread CPU cycles (`osn_bench.c`), and the encoder generations timed against 7-Zip (`s8.ps1`) |

## Building

The library is optional for contributors: without it, Osmium builds and runs with the built-in encoders.

- **Linux** (gcc or clang, CMake 3.20 or later, Ninja, Python 3.12 or later, 7-Zip as `7z`):
  `native/build.sh` builds `libosmium_native-x64.so` into `artifacts/native/linux-x64/` (for tests only; Osmium ships
  no Linux build).
- **Windows** (Visual Studio 2022 or later with the x64 and ARM64 build tools and the Spectre-mitigated libraries,
  CMake, Ninja, Python, 7-Zip): `native/build.ps1 -Arch x64` or `-Arch arm64` builds `osmium_native-x64.dll` or
  `osmium_native-arm64.dll` into `artifacts/native/win-<arch>/`.

Both scripts first run `python3 -I native/fetch_sources.py`, which checks each archive's size and SHA-256 before
extracting anything and then checks every extracted file. No upstream build script, makefile, test or generator is
ever run, and no prebuilt binary is used. `build-info.txt` beside the library records the compilers and tools, the
flags, the source hashes and the library's SHA-256. When Osmium.Core is built, it compiles the SHA-256 from
`build-info.txt` into itself (`NativeExpected.g.cs`); a library built later, or by someone else, is refused.

Shipped libraries are built by CI only. Each is built twice on one runner, from checkouts in two different folders,
and the two must be byte-identical; it is also compared with the build from another runner whenever both runners
have the same toolset (runner images roll forward, so they sometimes do not).

## How Osmium loads it

The decision to load is made before any code of the file runs:

1. Osmium confirms its own folder: the folder the runtime reports (`AppContext.BaseDirectory`) must be the folder of
   the running program file (`Environment.ProcessPath`), both resolved to their real path (links, junctions, short
   8.3 names and letter case as the file system stores them). Normal, single-file and native AOT builds pass. A
   bundle that extracts itself to a temporary folder, or a program started through another host (`dotnet x.dll`),
   does not, and then nothing is looked for: the built-in encoders are used, and `osmium version` says why. A
   single-file program, such as the standalone `osmium-<version>-<rid>.exe` that usually sits in Downloads, is checked
   like any other: a library planted beside it fails step 4 and never runs.
2. Osmium.Core knows the SHA-256 of the library it was built with. If it was built without one, nothing is loaded.
3. Exactly one path is opened: the program's own folder plus `osmium_native-<arch>.dll` for the process's
   architecture (an x64 process on an ARM64 PC loads the x64 file). Never the current folder, `PATH`, the temporary
   folder, the user's profile or a single-file extraction folder; never a bare name.
4. The file is opened so that nobody can write or delete it, read whole and hashed. Only if the hash matches is it
   loaded from that path, with the file still held, and with `LOAD_LIBRARY_SEARCH_SYSTEM32` for its own imports. The
   library imports nothing but `KERNEL32.dll` (static C runtime), so there is no second DLL to plant.
5. After loading, the loaded module's file must be the same file (volume and file id) as the one that was hashed;
   every export must be present; and the library must report the expected interface number, encoder generation and
   upstream versions. Any difference means the built-in encoders are used, with the reason.

The library is never unloaded and is loaded at most once per process, on first use. A replaced or stale library (an
older install, a local build, a portable folder someone else can write to) fails the hash check and is never run.
In a per-user install the library is exactly as protected as `Osmium.Core.dll` beside it: someone who can replace
one can replace the other.

## Choosing the encoders

| Mode | Meaning |
|---|---|
| `auto` (default) | Native encoders if the library loads, otherwise the built-in ones |
| `native` | Native encoders required: if the library does not load, LZMA and Zstandard compression fail with the reason (used by CI, so a missing library can never pass unnoticed) |
| `managed` | Built-in encoders; the library is never loaded |

The mode is chosen once per process, before the first compression, and never changes afterwards, so one archive
never mixes encoders: `NativeEncoders.Configure(mode)` (the app calls it with its setting *Use only the built-in
encoders*; the command line with its option), or else the `OSMIUM_ENCODERS` environment variable (`auto`, `native`,
`managed`). `Configure` after the first use throws, so a program can never believe a setting applied when it did
not. Asking which encoders are active (the cluster's engine check, `osmium version`, About) also decides the mode.

Everything that compresses with LZMA or Zstandard uses the active encoders: blocks, the planner's trial
compressions and the archive's index.

## If Osmium closes while compressing

An access violation in native code ends the whole process, which .NET cannot catch, so every running job is lost.
The app therefore records when native compression is running; if the next start finds that record from a process
that is gone, it uses the built-in encoders for that session, writes a line in the Log and shows a notice.

What an interrupted compression leaves: the archive being written, `<name>.partial-<8 hex digits>`, next to the
destination. It never has the destination's name, so it is never mistaken for a finished archive, and an existing
destination is replaced only by a complete, verified archive. Nothing removes the partial file later; watch folders
and the default exclusions skip `*.partial-*` files, so they are not compressed again.

Every block is decoded again by the built-in decoder and compared before it is written, as long as *Verify every
block* is on (the default). With it off, an encoder fault would go unnoticed, as with the built-in encoders.

## Memory, threads and checks

- Every call allocates through its own counting allocator, which refuses any allocation past the call's cap. The
  cap is the method's memory estimate minus the bound-sized output buffer, plus 1 MiB: the input is counted by the
  pipeline, and the exact-length copy of the output is made after the native memory is freed, so input, output and
  native memory together stay within what Osmium reserves for the block.
- Zstandard reads the input in place (`ZSTD_c_stableInBuffer`, one context per call, never reused or reset) and gets
  it in whole blocks, with one final call that adds nothing; so its output does not depend on how the input is fed,
  and no window-sized copy of the input is allocated.
- LZMA encodes with one thread. The library can run the match finder on a helper thread, but for some inputs (40 MiB
  of mixed data at a 32 MiB dictionary) the LZMA SDK then writes other bytes, deterministic but not those of one
  thread; so Osmium never uses it, and a block's bytes never depend on what else runs at the time. The two-thread mode
  stays tested (the self-test, the thread sanitizer, the leftover-thread test) in case a later generation uses it
  for every block alike.
- The self-test runs under the thread sanitizer (parallel encodes with the helper thread) and under the address and
  undefined-behaviour sanitizers. Known upstream idioms are excused, each for the LZMA SDK's files only and each
  explained where it is set: the helper thread's stop flag (`tests/tsan.supp`), and unaligned loads and shifts of
  negative offsets (`CMakeLists.txt`).

## Encoder generations

An encoder generation is a fixed set of encoder implementations and settings with its own golden vectors:

- `managed-1`: the built-in encoders (LZMA SDK for C# 22.1.1, ZstdSharp 0.8.8).
- `native-1`: this library, LZMA SDK 26.06 and Zstandard 1.5.7, with the settings in `src/osmium_native.c`.

The two generations write different bytes for the same block (native LZMA blocks are usually slightly smaller;
native Zstandard frames record the content size, 3 bytes more), and both decode to the same data with any
decoder. Within a generation the output never changes, on any platform: the same vectors (small and large) and the
same 2,000-case sweep per codec must pass on Linux x64, Windows x64 and Windows ARM64, for every Zstandard chunk
size.

**Rule: every change to a setting of the interface layer bumps both the generation id and the ABI number in
`generation.txt`** (the match finder cycles `mc`, `contentSizeFlag`, the chunk size if it ever affected output, the
`LzFindPrepare` call, any parameter clamp, and every upstream file). The library reports both, and Osmium.Core
refuses a library whose values differ from its own.

The cluster's engine check includes the active generation, so a computer using native encoders and one using the
built-in ones refuse to work together; each says which generation it uses.

## Upgrading an upstream library

An upgrade is a new generation (`native-2`), never a changed hash:

1. Pin a release that is at least 14 days old; read its change notes and the advisories (OSV, NVD, GitHub).
2. Download it twice from different places; compare the files with the git tag; check the signature where one is
   published, with a key fingerprint obtained elsewhere. Record everything in `sources.lock`; regenerate
   `sources.files`.
3. Bump the generation id and the ABI number in `generation.txt` and add the generation to `EncoderGeneration.cs`.
4. Write its vector files (small and large) with the documented command; the old generation's files stay.
5. Fuzz each harness for at least 2 CPU-hours; re-audit the licence and copyright lines (`check_sources.py`, then
   THIRD-PARTY-NOTICES.md); repeat the measurements; add a CHANGELOG line.

## Security fixes

An upstream fix that touches a file in `sources.files` is handled as an upgrade (above). The target is a patch
release within 14 days of an upstream fix rated high or critical that affects the compiled encoder code, otherwise
the next release. Until then the workaround is the built-in encoders: the app's setting *Use only the built-in
encoders* or the command line's `--encoders managed`, which the advisory names.

## When a check fails

- **A vector differs after a runner image update**: the output is integer-only, so a compiler update should not
  change it; if it does, pin the previous toolset (or runner image) and report the difference upstream. Never
  regenerate the vectors of a shipped generation.
- **An archive or file hash differs**: stop. Never pin a hash that was not checked against a second source.
- **Two builds of the same commit differ**: when their toolset records differ, the comparison is inconclusive and is
  re-run; otherwise it is a reproducibility failure to investigate.
- **The memory cap is reached**: Osmium's memory estimate for that block is wrong. The block fails with a message
  that names the setting and the option for the built-in encoders; the estimate must be corrected, not the cap.

## Licences

The LZMA SDK is in the public domain (`DOC/lzma-sdk.txt`). Zstandard is dual-licensed under the BSD license and the
GPLv2; Osmium uses it under the BSD license (`LICENSE` in the Zstandard archive). Both texts are reproduced in
THIRD-PARTY-NOTICES.md. Osmium's own files here are MIT, like the rest of Osmium.
