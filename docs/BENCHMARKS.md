# Benchmarks

Osmium 0.1.1 against eight other compressors, each at the strongest setting people commonly use. Every archive was
unpacked again and compared with the original.

## Method

- **Inputs.** Two folders, described with each table below. Stream compressors (bzip2, Brotli, Zstandard, xz,
  lrzip) received a tar of the folder; archivers (zip, 7-Zip, ZPAQ, Osmium) archived the folder itself.
- **Verification.** Stream outputs had to reproduce the tar byte for byte. Archivers had to restore a file tree whose
  every file has the original SHA-256. All runs below passed.
- **Threads.** xz and Zstandard ran with `-T1`, because their multi-threaded modes cut the input into independent
  blocks and lose ratio. Every other tool used its default threading; Osmium used all four cores.
- **Machine.** 4 vCPU Intel Xeon @ 2.3 GHz, 16 GB RAM, Linux 6.18, .NET 10.0. Times are wall-clock seconds on this
  machine. Osmium's codecs are deterministic and CI checks a golden hash on Linux and Windows, so its sizes are the
  same on Windows; its times will differ with the hardware.
- **Insane** ran only on the mixed folder. The script skips it on folders above 64 MiB (`--insane-max-mib`) to keep
  the run to about an hour.
- **Reproduce** with `python3 tools/crosscheck.py --osmium "osmium" --json results.json --md results.md <folders…>`.

| Tool | Version | Command |
|---|---|---|
| zip | Info-ZIP 3.0 | `zip -r -9` |
| bzip2 | 1.0.8 | `tar \| bzip2 -9` |
| Brotli | 1.1.0 | `tar \| brotli -q 11 -w 24` |
| Zstandard | 1.5.5 | `tar \| zstd --ultra -22 --long=31 -T1` |
| xz | 5.4.5 | `tar \| xz -9e -T1` |
| 7-Zip | 23.01 | `7z a -mx=9 -m0=lzma2 -mfb=273 -md=256m -ms=on`; `7z a -m0=PPMd:mem=1024m:o=32 -ms=on` |
| lrzip | 0.651 | `tar \| lrzip -z -L 9` (ZPAQ back end) |
| ZPAQ | 7.15 | `zpaq a -m5` |
| Osmium | 0.1.1 | `osmium a --profile fast \| balanced \| max \| ultra \| insane` |

## Results

### Mixed folder — 16.0 MiB, 33 files

20 license texts (4 of them duplicates) and a 1.3 MiB CSV; a .NET IL assembly (6.3 MiB) and two x64 ELF binaries; 1.2 MB of 12-byte telemetry records; a 16-bit stereo WAV; a 24-bit BMP; three PNG and three gzip files; an empty file and a file with a non-ASCII name.

| Tool | Size (bytes) | Of original | vs `xz -9e` | Compress | Extract | Round trip |
|---|---:|---:|---:|---:|---:|---|
| **Osmium Insane** | 4,944,191 | 29.52 % | −18.9 % | 17 min 29 s | 33.9 s | ✔ identical |
| **Osmium Ultra** | 4,965,074 | 29.64 % | −18.6 % | 5 min 40 s | 28.0 s | ✔ identical |
| tar \| lrzip -z (ZPAQ back end) | 5,303,256 | 31.66 % | −13.0 % | 24.3 s | 26.4 s | ✔ identical |
| ZPAQ -m5 | 5,322,286 | 31.78 % | −12.7 % | 40.9 s | 42.4 s | ✔ identical |
| **Osmium Max** | 5,407,507 | 32.28 % | −11.3 % | 10.5 s | 0.5 s | ✔ identical |
| **Osmium Balanced** | 5,604,115 | 33.46 % | −8.1 % | 6.9 s | 0.7 s | ✔ identical |
| 7-Zip LZMA2 -mx=9 (256 MB dictionary) | 5,831,544 | 34.82 % | −4.4 % | 2.0 s | 0.3 s | ✔ identical |
| tar \| xz -9e | 6,097,804 | 36.41 % | — | 6.0 s | 0.3 s | ✔ identical |
| tar \| brotli -q 11 -w 24 | 6,490,625 | 38.75 % | +6.4 % | 38.0 s | 0.1 s | ✔ identical |
| tar \| zstd --ultra -22 --long=31 | 7,009,249 | 41.85 % | +14.9 % | 5.6 s | < 0.1 s | ✔ identical |
| 7-Zip PPMd (order 32, 1 GB) | 7,384,240 | 44.09 % | +21.1 % | 3.9 s | 3.9 s | ✔ identical |
| tar \| bzip2 -9 | 7,975,689 | 47.62 % | +30.8 % | 1.1 s | 0.8 s | ✔ identical |
| zip -9 | 7,988,941 | 47.70 % | +31.0 % | 1.7 s | 0.1 s | ✔ identical |
| **Osmium Fast** | 8,227,600 | 49.12 % | +34.9 % | 0.5 s | 0.2 s | ✔ identical |

### Windows application folder — 124.0 MiB, 284 files

Osmium's own self-contained win-x64 publish output: 273 DLLs (.NET ReadyToRun assemblies, which carry IL plus precompiled x64 code, and native runtime DLLs), 3 EXEs, 4 PDBs and 4 JSON files — the kind of payload an installer ships.

| Tool | Size (bytes) | Of original | vs `xz -9e` | Compress | Extract | Round trip |
|---|---:|---:|---:|---:|---:|---|
| ZPAQ -m5 | 33,297,191 | 25.61 % | −11.1 % | 2 min 38 s | 2 min 31 s | ✔ identical |
| tar \| lrzip -z (ZPAQ back end) | 34,431,228 | 26.49 % | −8.1 % | 68.2 s | 63.8 s | ✔ identical |
| **Osmium Ultra** | 35,483,188 | 27.30 % | −5.3 % | 18 min 47 s | 7 min 39 s | ✔ identical |
| 7-Zip LZMA2 -mx=9 (256 MB dictionary) | 36,645,043 | 28.19 % | −2.2 % | 33.0 s | 1.9 s | ✔ identical |
| tar \| xz -9e | 37,456,232 | 28.81 % | — | 65.4 s | 2.1 s | ✔ identical |
| **Osmium Max** | 37,474,430 | 28.83 % | +0.0 % | 2 min 17 s | 5.2 s | ✔ identical |
| **Osmium Balanced** | 37,771,434 | 29.06 % | +0.8 % | 69.4 s | 3.0 s | ✔ identical |
| tar \| brotli -q 11 -w 24 | 39,687,784 | 30.53 % | +6.0 % | 5 min 09 s | 0.5 s | ✔ identical |
| tar \| zstd --ultra -22 --long=31 | 40,574,485 | 31.21 % | +8.3 % | 53.8 s | 0.2 s | ✔ identical |
| 7-Zip PPMd (order 32, 1 GB) | 46,207,473 | 35.55 % | +23.4 % | 27.8 s | 28.0 s | ✔ identical |
| **Osmium Fast** | 50,583,901 | 38.91 % | +35.0 % | 1.3 s | 0.3 s | ✔ identical |
| tar \| bzip2 -9 | 53,246,143 | 40.96 % | +42.2 % | 9.3 s | 4.4 s | ✔ identical |
| zip -9 | 53,310,076 | 41.01 % | +42.3 % | 10.6 s | 0.9 s | ✔ identical |

## What the numbers say

- **Mixed data is where planning per kind of data pays.** On the mixed folder every Osmium profile from Balanced up
  beats 7-Zip and xz, and Ultra and Insane beat every other tool: Insane is −18.9 % against
  `xz -9e` and −6.8 % against lrzip, the smallest other result. Max gets
  −11.3 % against xz without context mixing, so it still extracts in 0.5 s.
- **On program files, ZPAQ and lrzip are still smaller.** On the Windows application folder ZPAQ -m5 reaches
  25.6 % and lrzip 26.5 % of the original against 27.3 % for Osmium Ultra
  (+6.6 % relative to ZPAQ). Their context-mixing models for x86 code are stronger than Osmium CM's today;
  closing that gap is on the backlog (see [IMPROVISATION.md](IMPROVISATION.md) §3).
- **LZMA is LZMA.** On machine code Max (28.8 %) lands next to `xz -9e` (28.8 %) because both
  are LZMA with a branch filter; 7-Zip's LZMA2 with a 256 MB dictionary and fast bytes 273 is slightly smaller
  (28.2 %).
- **Fast is for speed.** It is zstd level 3: larger than `zip -9` on the mixed folder (49.1 % against
  47.7 %), smaller on the application folder (38.9 % against 41.0 %), and much faster than both.
- **Context mixing is slow to extract.** CM decodes at about the speed it encodes, one thread per block. The
  application folder's Ultra archive keeps about 112 MiB of x64 code in one block and took 7 min 39 s
  to extract, against 2 min 31 s for ZPAQ and 63.8 s for lrzip. The mixed folder's seven smaller
  blocks extract in parallel (28.0 s). Most of Ultra's and Insane's compression time is planning;
  see [IMPROVISATION.md](IMPROVISATION.md) §5.4.
