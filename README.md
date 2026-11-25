<p align="center">
  <img src="docs/img/logo.svg" width="88" height="88" alt="">
</p>
<h1 align="center">Osmium</h1>
<p align="center">A file compressor for Windows that plans a compression pipeline for each kind of data.</p>
<p align="center">
  <a href="https://github.com/windfastt/osmium/actions/workflows/ci.yml?query=branch%3Amain"><img src="https://github.com/windfastt/osmium/actions/workflows/ci.yml/badge.svg?branch=main&amp;event=push" alt="CI status"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-3a2a96" alt="License: MIT"></a>
  <a href="https://github.com/windfastt/osmium/releases/latest"><img src="https://img.shields.io/github/v/release/windfastt/osmium?color=3a2a96" alt="Latest release"></a>
</p>
<p align="center">
  <a href="https://windfastt.github.io/osmium/">Website</a> ·
  <a href="https://github.com/windfastt/osmium/releases/latest">Download</a> ·
  <a href="#documentation">Documentation</a> ·
  <a href="ROADMAP.md">Roadmap</a> ·
  <a href="CHANGELOG.md">Changelog</a>
</p>

Osmium packs files and folders into `.osa` archives, a format only Osmium reads. It works out what each file
contains, groups files by kind of data and chooses filters and a codec for each group; by default it decodes and
checks every block before writing it. It runs as a notification-area app with Explorer menu entries and as the
`osmium` command-line tool, and it is free software under the MIT License.

## Highlights

- **A pipeline for each kind of data.** Files are sorted by content (text, x86/x64 and ARM64 code, fixed-size records,
  16-bit audio, bitmaps, other binary data, already-compressed files). Each group gets optional reversible filters and
  one of the codecs: Osmium CM2 (context mixing, with models for x86/x64 machine code; the first Osmium CM is kept for
  reading older archives), LZMA, PPMd, Zstandard, Brotli, BZip2, Deflate or Store.
- **Seven profiles.** Store, Fast and Balanced (default) use fixed choices; Max trial-compresses candidates on samples;
  Ultra and Insane also tune parameters and may use context mixing, which extracts slowly. Exhaustive is experimental.
- **Checked before it is written.** By default every block is decoded and compared with its input before it is
  written. Archives are written to a temporary file, read back and only then renamed into place.
- **Optional password encryption.** AES-256-GCM with a key from PBKDF2-HMAC-SHA512 (600,000 iterations by default).
  File names, sizes and times are encrypted too; the Osmium version that wrote the archive, the key-derivation
  settings and the number and sizes of its blocks stay visible. A lost password cannot be recovered.
- **Duplicates stored once:** whole files from Balanced to Ultra, content-defined chunks in Insane and Exhaustive.
- **Windows integration.** A notification-area app with a progress ring, Explorer menu entries, the `.osa` file
  association and optional watch folders, plus the `osmium` command-line tool, also available as a single file.
- **Per-file control.** JSON rules force or forbid pipelines, override codec parameters, and store or exclude
  files. In the app, right-click a file on the *Compress* page for *Tune…* or *Deep auto-tune…*.
- **Optional help from other PCs.** Off by default. PCs that share a pairing key can run trial compressions and
  some blocks for your jobs. Your PC decodes and checks every block they return and recomputes about 5 % of their
  trial results; a worker caught returning a wrong result is not used for the rest of the job.

## Results

Osmium 0.1.1 and eight other tools in nine settings, on two folders. Each cell gives the archive size as a share of
the original (smaller is better) and, below it, the wall-clock time to compress / extract.

| Tool and setting | Mixed folder, 16.0 MiB | Program files, 124.0 MiB |
|---|---:|---:|
| Osmium Insane | **29.52 %**<br><sub>17 min 29 s / 33.9 s</sub> | not run |
| Osmium Ultra | 29.64 %<br><sub>5 min 40 s / 28.0 s</sub> | 27.30 %<br><sub>18 min 47 s / 7 min 39 s</sub> |
| lrzip `-z -L 9`, on a tar | 31.66 %<br><sub>24.3 s / 26.4 s</sub> | 26.49 %<br><sub>68.2 s / 63.8 s</sub> |
| ZPAQ `-m5` | 31.78 %<br><sub>40.9 s / 42.4 s</sub> | **25.61 %**<br><sub>2 min 38 s / 2 min 31 s</sub> |
| Osmium Max | 32.28 %<br><sub>10.5 s / 0.5 s</sub> | 28.83 %<br><sub>2 min 17 s / 5.2 s</sub> |
| Osmium Balanced (default) | 33.46 %<br><sub>6.9 s / 0.7 s</sub> | 29.06 %<br><sub>69.4 s / 3.0 s</sub> |
| 7-Zip LZMA2 `-mx=9`, 256 MB dictionary | 34.82 %<br><sub>2.0 s / 0.3 s</sub> | 28.19 %<br><sub>33.0 s / 1.9 s</sub> |
| xz `-9e -T1`, on a tar | 36.41 %<br><sub>6.0 s / 0.3 s</sub> | 28.81 %<br><sub>65.4 s / 2.1 s</sub> |
| Brotli `-q 11 -w 24`, on a tar | 38.75 %<br><sub>38.0 s / 0.1 s</sub> | 30.53 %<br><sub>5 min 09 s / 0.5 s</sub> |
| Zstandard `--ultra -22 --long=31 -T1`, on a tar | 41.85 %<br><sub>5.6 s / &lt; 0.1 s</sub> | 31.21 %<br><sub>53.8 s / 0.2 s</sub> |
| 7-Zip PPMd, order 32, 1 GB | 44.09 %<br><sub>3.9 s / 3.9 s</sub> | 35.55 %<br><sub>27.8 s / 28.0 s</sub> |
| bzip2 `-9`, on a tar | 47.62 %<br><sub>1.1 s / 0.8 s</sub> | 40.96 %<br><sub>9.3 s / 4.4 s</sub> |
| zip `-r -9` (extracted with 7-Zip) | 47.70 %<br><sub>1.7 s / 0.1 s</sub> | 41.01 %<br><sub>10.6 s / 0.9 s</sub> |
| Osmium Fast | 49.12 %<br><sub>0.5 s / 0.2 s</sub> | 38.91 %<br><sub>1.3 s / 0.3 s</sub> |

**Conditions.** One run per folder on 24 November 2026, on one machine (4 vCPU Intel Xeon at 2.3 GHz, 16 GB RAM,
Linux 6.18, .NET 10), not on Windows. Tool versions and the compression commands are in
[docs/BENCHMARKS.md](docs/BENCHMARKS.md); [tools/crosscheck.py](tools/crosscheck.py), which ran the comparison, has
the exact compression and extraction commands. The *mixed folder* (33 files, 16.0 MiB) holds license texts (some
duplicated), a CSV, a .NET assembly, two x64 ELF binaries, generated 12-byte records, a WAV, a BMP, and PNG and gzip
files. *Program files* (284 files, 124.0 MiB) is Osmium's own self-contained Windows x64 build, mostly .NET
ReadyToRun DLLs. xz and Zstandard ran single-threaded (their multi-threaded modes lose ratio), the other tools with
their default threading and Osmium on all four cores, so compression times are not like for like. Tools marked
"on a tar" were timed on a ready-made tar; making and unpacking the tar is not included. Every output was unpacked
and checked against the original: 27 of 27 runs were identical. Insane was not run on the program files (the script
skips it above 64 MiB). Other data, versions and hardware will give different results.

- Mixed folder: Insane's archive is 18.9 % smaller than xz's and 6.8 % smaller than lrzip's (the smallest other
  result), but Insane took 17 min 29 s to compress against 24.3 s for lrzip. Fast is larger than zip there.
- Program files: ZPAQ and lrzip made smaller archives than Ultra (Ultra's is 6.6 % larger than ZPAQ's). Max is
  0.05 % larger than xz, and Balanced, the default, is larger than xz and 7-Zip LZMA2.
- Context mixing decodes about as fast as it encodes, one thread per block: Ultra's archive of the program files took
  7 min 39 s to extract, against 2.1 s for xz and 2 min 31 s for ZPAQ. After compressing, `osmium` prints an
  estimate of the extraction time and memory, and `osmium info` shows it for any archive.

## Download

Files in the [latest release](https://github.com/windfastt/osmium/releases/latest), where `<arch>` is `x64` or `arm64`:

| File | What it is |
|---|---|
| `Osmium-<version>-win-<arch>-setup.exe` | Installer for the app and the `osmium` command-line tool |
| `Osmium-<version>-win-<arch>-portable.zip` | The app and the command-line tool to unzip anywhere; settings stay in a `data` folder next to them |
| `osmium-<version>-win-<arch>.exe` | The command-line tool alone, as one self-contained file; enough to extract archives on a PC without Osmium |
| `SHA256SUMS.txt` | SHA-256 checksums of the files above |

The installer requires Windows 10 version 1809 or later, or Windows 11; no separate .NET installation is needed. It
installs for the current user without an admin prompt (*Install for all users* needs admin rights). *Start with
Windows*, the Explorer menu and the `.osa` association are ticked by default; a desktop icon, *Send to* (per-user
installs only), adding `osmium` to PATH and a firewall rule for the cluster (all-users installs only) are not.
Uninstalling removes Osmium's registrations and asks before deleting settings, rules, job history and logs.

**Testing.** Automated tests run on Linux and on GitHub's Windows Server x64 runners, not on Windows 10 or on a
Windows 11 x64 PC. The ARM64 downloads of 0.1.1 were built on x64 and not run in CI; on `main`, CI also tests the
engine and the command-line tool on Windows 11 ARM64.

**Not code-signed yet.** Windows SmartScreen may warn before the installer or the program runs. You can first compare
the file's checksum with `SHA256SUMS.txt` from the same release (in PowerShell: `Get-FileHash <file>`): a match shows
that the file is complete and is the one attached to the release, but it is not a signature. From 0.2.0 on, every
release file also carries a build provenance attestation: `gh attestation verify <file> --repo windfastt/osmium` (with
the [GitHub CLI](https://cli.github.com/)) checks that the file was built by this repository's own workflow from the
published source, so a modified copy fails the check. Running unsigned software is your decision. Signed builds are on
the [roadmap](ROADMAP.md), once a code-signing certificate is available.

## Quick start

**App.** Osmium runs in the notification area. Click its icon, drop files or folders on the *Compress* page, choose a
profile and press *Compress*. *Jobs* shows progress and the planner's decisions; *Archives* opens, tests and extracts
`.osa` files. To encrypt, tick *Encrypt (AES-256, names hidden)*.

**Explorer.** Right-click files or folders and choose *Osmium* → *Add to archive…* or *Compress to .osa (Balanced)*.
Right-click an `.osa` file for *Extract here*, *Extract to folder* or *Test archive*. On Windows 11 these entries are
under *Show more options*.

**Command line.** `osmium` is on PATH only if you ticked that option in the installer.

```powershell
osmium a backup.osa C:\Projects --profile ultra    # compress (the default profile is balanced)
osmium t backup.osa                                # test: decode every block, check every file's hash
osmium x backup.osa C:\Restore                     # extract; keeps existing files, renames the extracted copy
osmium info backup.osa                             # pipelines, sizes, extraction time and memory
osmium a logs.osa D:\logs --rules rules.json --explain   # per-file rules; show the decisions
osmium help                                        # every command and option
```

`--skip` makes `x` skip files that already exist, and `--overwrite` replaces them. To encrypt on the command line,
pipe the password to `--password-stdin`; `x`, `t` and `l` ask for it when needed.
A rules file looks like this. The app keeps its rules in `%APPDATA%\Osmium\rules.json` (portable: `data\rules.json`);
the command-line tool reads only the file given with `--rules`. Check one with `osmium rules check rules.json`.

```json
{ "rules": [
  { "id": "logs", "match": { "globs": ["**/*.log"] }, "actions": { "params": { "lzma.dict": "256M" } } },
  { "id": "game-data", "match": { "path": "data/level01.pak" },
    "actions": { "pipeline": "delta(stride=4)+lzma", "solid": "own" } },
  { "id": "skip-temp", "match": { "globs": ["**/*.tmp"] }, "actions": { "exclude": true } }
] }
```

### Using several PCs (optional)

Off by default. PCs that share a pairing key can run trial compressions for your jobs and, while your own cores are
busy, blocks whose codec decodes much faster than it encodes, such as LZMA and Zstandard. The automated test runs over
loopback on one machine; LAN discovery and use across several PCs are not covered. In the app, save the key on the
*Cluster* page, then turn on *Lend this PC to the cluster* (tray menu) on the helpers and *Use cluster workers for my
jobs* (Settings) where you compress. On the command line:

```powershell
osmium keygen                                  # once: prints a new OSM1-… key; keep it secret
osmium worker --key OSM1-…                     # on each helper PC
osmium a big.osa D:\data --profile insane --workers auto --cluster-key OSM1-…
```

`osmium worker` and `osmium a` also read the key from the `OSMIUM_CLUSTER_KEY` environment variable, which keeps it
off the command line, where other programs on the PC can read it.

- Both sides prove they hold the key (HMAC-SHA256 over an ECDH P-256 handshake), and job data is encrypted with
  AES-256-GCM. PC names, core counts and memory sizes are sent unencrypted during the handshake. A helper sends its
  own to any PC on the network that presents the cluster's identifier (visible in discovery broadcasts), before that
  PC has proven it holds the key.
- Workers see the content of the samples and blocks they compress, never file names or the password: pair only PCs
  you trust. Encrypted jobs stay local unless you allow them (`--allow-remote-encrypted`, or a setting in the app).
  Your PC decodes and checks every block a worker returns and spot-checks about 5 % of its trial results (none for
  Deflate or Brotli); a worker caught returning a wrong result is not used for the rest of the job.
- Helper PCs must accept TCP 46510 (changeable) and, for discovery (`--workers auto`), UDP 46511 (IPv4 broadcast on
  the local network). The installer's optional firewall rule covers only the tray app, on private networks; in 0.1.1
  it opens TCP 46510 only.

## How it works

1. **Analyze.** Each file is identified by its signature and by statistics of its content, such as entropy and stride.
2. **Group.** Rules apply, duplicates are set aside, and files of the same kind go into solid blocks.
3. **Try candidates** (Max and higher; lower profiles use fixed choices). Candidate pipelines are compressed on
   samples of each group; higher profiles try more of them and move the best on to larger samples.
4. **Search parameters** (Ultra and higher). From the winner, Osmium tries one step up or down on each parameter and
   adding or removing a filter, keeping what helps. Insane also compresses each block with the best two pipelines
   and keeps the smaller result.
5. **Compress, verify, commit.** Blocks are compressed in parallel within a memory budget. By default each block is
   decoded and compared with its input before it is written, and a block that does not get smaller is stored. The
   archive is written to a temporary file, read back and renamed into place. `osmium explain <archive>` shows the
   planner's decisions, which the archive stores by default (`--no-plan-log` leaves them out); `osmium a … --explain`
   prints them while compressing.

## Documentation

| Document | Contents |
|---|---|
| [Changelog](CHANGELOG.md) | What changed in each release; the 0.2.0 section is unreleased |
| [Roadmap](ROADMAP.md) | What 0.2.0 is building and what is planned after it |
| [Benchmarks](docs/BENCHMARKS.md) | Method, tool versions, commands and full results of the 0.1.1 comparison |
| [Settings reference](docs/SETTINGS.md) | Every setting, profile and codec or filter parameter, generated from the code |
| [Archive format](docs/FORMAT.md) | The `.osa` 1.0 format: header, frames, pipelines, index, encryption, integrity checks |
| [Architecture](docs/BLUEPRINT.md) | How Osmium is built: container, codecs, filters, analysis, planner, cluster, app, security |
| [Contributing](CONTRIBUTING.md) | How to build and test, and the rules for changes |
| [Security policy](SECURITY.md) | How to report a vulnerability privately |
| [Third-party notices](THIRD-PARTY-NOTICES.md) | Third-party components and their license texts |

## Building from source

You need a .NET 10 SDK (10.0.100 or later). Everything builds on Windows and Linux; the app runs only on Windows.
The .NET SDK collects usage data by default (this is the SDK, not Osmium); set `DOTNET_CLI_TELEMETRY_OPTOUT=1` to
turn it off, as CI does.

```bash
dotnet build Osmium.sln -c Release
dotnet test -c Release --project tests/Osmium.Core.Tests      # codecs, filters, format, planner, cluster
dotnet test -c Release --project tests/Osmium.Desktop.Tests   # settings, jobs, IPC, Explorer registration
dotnet test -c Release --project tests/Osmium.Cli.Tests       # the osmium command-line tool
```

On Windows, `pwsh build/publish.ps1 -Arch x64` (or `-Arch arm64`, with optional `-Version 1.2.3` and
`-SkipInstaller`) writes the self-contained app and command-line tool, the single-file tool, the license files, the
portable ZIP and the installer to `artifacts/`. If Inno Setup 6 is missing, the script installs it with Chocolatey.

CI builds and tests on Linux, Windows x64 and natively on Windows 11 ARM64, checks `docs/index.html` and
`docs/SETTINGS.md` against their generators, builds the downloads, and on Windows x64 runs a UI self-test (real work
on a generated sample folder, every page and dialog in light and dark) and a command-line round trip. Golden vectors
pin the bytes Osmium CM and every filter produce for fixed inputs. A `v*` tag publishes a release with `SHA256SUMS.txt`.

## Status

The latest release is **0.1.1** (24 November 2026). **0.2.0** is in progress on `main` and not in any download yet;
builds from `main` still report 0.1.1. The work includes license texts in every download and `osmium license`, the UDP
firewall rule, app fixes and better handling of archives from newer versions; still being built are a context-mixing
codec for x86/x64 code, a cheaper parameter search and `osmium salvage` for damaged archives. See the
[roadmap](ROADMAP.md) and the unreleased section of the [changelog](CHANGELOG.md).

Limitations today:
- Osmium reads and writes only its own `.osa` format.
- Already-compressed files (JPEG, PNG, MP4, ZIP, 7z and similar) are stored as they are; recompressing them is planned.
- Archives that use context mixing (possible from Ultra up) extract slowly.

## License and legal

Osmium is free software under the [MIT License](LICENSE), copyright Osmium contributors. From the license:

> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
> THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
> TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

Keep backups, and test archives with `osmium t` before you delete the originals.

**Third-party software.** The downloads contain ZstdSharp.Port, SharpCompress (with its Bouncy Castle notice), the
LZMA SDK, System.IO.Hashing and the .NET runtimes (including Windows Forms), under their own licenses; the installer
is built with Inno Setup. They are listed with their license texts in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
From 0.2.0 the installer and the portable ZIP contain `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` and the .NET
runtime's `dotnet-LICENSE.txt` and `dotnet-THIRD-PARTY-NOTICES.txt`; the single-file tool prints its notices with
`osmium license` and has the .NET runtime's license and notices attached to the release next to it. Osmium CM is a context-mixing codec that uses techniques described for the
PAQ/lpaq family of compressors.

**Trademarks.** Microsoft, Windows, SmartScreen, PowerShell and .NET are trademarks of the Microsoft group of
companies. GitHub is a trademark of GitHub, Inc. Intel and Xeon are trademarks of Intel Corporation or its
subsidiaries. Arm is a trademark of Arm Limited (or its subsidiaries). Linux, 7-Zip, ZPAQ, xz, Zstandard, Brotli,
bzip2, lrzip, Info-ZIP, Inno Setup, Chocolatey and the other product, project and format names here are the property
of their respective owners and are used only to identify them. Osmium is an independent project, not affiliated with,
sponsored or endorsed by any of them, or by other projects named Osmium.

**Privacy.** Osmium has no telemetry, analytics, crash reporting or update check; Windows itself may report crashes to
Microsoft, depending on its diagnostic settings. It opens network connections only for the optional cluster, which is
off by default, and then only to PCs that answer its local-network discovery or that you list yourself. While this PC
lends itself to the cluster (*Lend this PC to the cluster*, or `osmium worker`), Osmium also listens on TCP 46510 (or
the port you set) on all network interfaces and answers discovery probes made with the same key on UDP 46511;
discovery sends IPv4 broadcasts to the local network. Settings, rules, job history and logs are stored in files
(`%APPDATA%\Osmium` and `%LOCALAPPDATA%\Osmium`, or `data` in a portable copy); the cluster key is stored there in
plain text, and `%APPDATA%` follows Windows roaming profiles where they are used. The
[product page](https://windfastt.github.io/osmium/) sets no cookies, runs no analytics and loads nothing from other
sites; the Markdown documents GitHub Pages renders from `/docs` load one script from cdnjs.cloudflare.com. The site is
hosted on GitHub Pages, and GitHub may log visits.
