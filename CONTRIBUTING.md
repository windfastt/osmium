# Contributing to Osmium

Thank you for helping. This page explains how to build and test Osmium, the rules every change follows and how to
propose a change. Contributions are accepted under the MIT License, as described [at the end](#license-of-contributions).

## Before you start

- **Security problems** are never reported in issues or pull requests. Follow the [security policy](SECURITY.md).
- **Small fixes**, such as a bug fix, a clearer message or a documentation correction, can go straight to a pull
  request.
- **Larger changes** start with an issue, so that the approach is agreed before you spend time on it: a new codec or
  filter, anything that touches the `.osa` format, the planner's choices or the cluster protocol, a new dependency,
  new settings or a new page in the app.
- Check the [roadmap](ROADMAP.md) and the open issues first; the work may already be under way.

## Build and test

You need a .NET 10 SDK (10.0.100 or later, as `global.json` sets) and, for the website generator, Python 3.

```bash
dotnet build Osmium.sln -c Release
dotnet test -c Release --project tests/Osmium.Core.Tests
dotnet test -c Release --project tests/Osmium.Desktop.Tests
dotnet test -c Release --project tests/Osmium.Cli.Tests
```

The tests use xUnit v3 on Microsoft.Testing.Platform (selected in `global.json`), which is why `dotnet test` takes
`--project`. To run one test class:

```bash
dotnet test -c Release --project tests/Osmium.Core.Tests --filter-class Osmium.Core.Tests.GoldenVectorTests
```

Everything except the Windows app runs on Linux as well as Windows. The app (`src/Osmium.App`, Windows Forms)
compiles on both but runs only on Windows. CI exercises it on Windows x64 with
`Osmium.App.exe --selftest-ui <folder> [dark]`, which does real work on a generated sample folder and renders every
page and dialog to PNG.

Windows packaging needs PowerShell 7:

```powershell
pwsh build/publish.ps1 -Arch x64    # or -Arch arm64; optional -Version 1.2.3 and -SkipInstaller
```

The output goes to `artifacts/`. Unless you pass `-SkipInstaller`, the script also builds the installer with
Inno Setup 6 and installs Inno Setup with Chocolatey if it is missing.

### Generated files

Never edit these by hand: change the source, then regenerate. CI fails when they are out of date.

| File | Generated from | Regenerate with |
|---|---|---|
| `docs/index.html` (the website) | `tools/site/template.html` and data in the repository | `python3 tools/site/build.py` |
| `docs/SETTINGS.md` | the `[Setting]` metadata in `src/Osmium.Desktop/Settings/OsmiumSettings.cs` and the codec and filter parameters | `dotnet run --project tools/settingsdoc -c Release` |

## Project layout

| Path | Contents |
|---|---|
| `src/Osmium.Core` | The engine: `.osa` container, codecs, filters, analysis, planner, cluster |
| `src/Osmium.Desktop` | App logic without Windows Forms (settings, jobs, IPC, Explorer registration), unit-tested on Linux |
| `src/Osmium.App` | The Windows Forms tray app, `Osmium.App.exe` |
| `src/Osmium.Cli` | The command-line tool, `osmium.exe` |
| `tests/` | `Osmium.Core.Tests`, `Osmium.Desktop.Tests` and `Osmium.Cli.Tests` |
| `build/`, `installer/` | Windows packaging script and Inno Setup script |
| `tools/` | Website and settings-reference generators, benchmark scripts |

## Rules for every change

### Existing archives must keep decoding

- **Never change the output of Osmium CM or of a filter.** An archive stores only each block's pipeline (method ids
  and parameters), and their decoders mirror the encoders exactly, so different output from the same pipeline would
  make existing archives decode wrongly. A faster or cleaner implementation must produce the same bytes. A codec
  library (LZMA, PPMd, Zstandard, BZip2) may be updated only if every existing archive still decodes.
- **Golden vectors** in `tests/Osmium.Core.Tests/GoldenVectorTests.cs` (and the CM hash in `ArchiveTests.cs`) pin that
  output for fixed inputs. They must pass on Linux, Windows x64 and Windows ARM64. Never update an existing golden
  hash to make a test pass: a changed hash is a format change, not a refactoring.
- **Method ids are frozen.** Codec ids 0–7 and filter ids 64–70 are stored in archives. Never renumber them, and
  never give an id that was once used to another method. Ids from 128 up are reserved for experimental methods.
  Changed behavior means a new method with a new id and its own golden vectors, next to the old one.
- **The format only grows.** The `.osa` format is version 1.0, independent of the app version, and changes are
  additive: length-prefixed records, flags and extensions marked critical or ignorable, and values read from an
  archive checked, never clamped. An archive written by a newer version must still open, list and extract every block
  this version understands. Discuss format changes in an issue first and describe them in
  [docs/FORMAT.md](docs/FORMAT.md).

### Determinism

- Osmium CM and every filter use integer arithmetic only, with tables built from integer constants, and run
  single-threaded per block. Their output must be the same on every operating system, CPU and thread count.
- Deflate and Brotli wrap native libraries and are marked `Deterministic => false`. Nothing may depend on their exact
  output.
- Plans are deterministic: sampling is seeded by file size, times come from a fixed cost model rather than the clock,
  and ties are broken by the pipeline string. The same files and settings must give the same plan, whatever the
  thread count and whichever trial finishes first.

### Code

- .NET 10, C# latest, nullable reference types on. **The build must stay at 0 warnings.** Check the build output
  yourself: CI does not fail on warnings yet.
- Managed code only. A new dependency needs an issue first, a license compatible with MIT, and an entry in
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) with its copyright lines and full license text.
- Write your own code. Copy code from another project only if its license is compatible with MIT, and record it in
  THIRD-PARTY-NOTICES.md. Osmium CM uses techniques described for the PAQ/lpaq family of compressors. Never copy,
  port, translate or closely paraphrase GPL-licensed code, including PAQ, lpaq and paq8px: work from published
  descriptions of an algorithm, not from GPL source, and say in the pull request where a technique comes from.
- Keep Windows Forms code thin. Put logic in `src/Osmium.Desktop`, where it can be unit-tested on Linux, and keep UI
  changes small, compile-checked and defensive.
- Text that users see is plain and specific, has no apologies, and names things the way users see them.

### Tests

- Every change comes with tests, and every fixed bug that can be reproduced on Linux gets a regression test.
- Codecs, filters and archive code: random and structured data, empty, 1-byte and odd-length inputs, and corruption.
- Planner: the scripted trial runner (`ScriptedTrials` in `PlannerFixtures.cs`) and generated inputs, never timing.
- Cluster: an in-process loopback cluster. New cluster code should also cover wrong keys, tampering and workers that
  return wrong results; today only the wrong-key case is tested.
- Test data is generated by the tests. Never add real files from your own computer.

### Documentation and changelog

- Add every user-visible change to [CHANGELOG.md](CHANGELOG.md), under the unreleased version at the top, in
  `### Fixed`, `### Added` or `### Changed`. The release notes are taken from that section.
- Update the documentation the change affects: the README, [docs/FORMAT.md](docs/FORMAT.md),
  [docs/SETTINGS.md](docs/SETTINGS.md) (regenerated, see above) or [docs/BENCHMARKS.md](docs/BENCHMARKS.md). Larger
  design changes also update [docs/BLUEPRINT.md](docs/BLUEPRINT.md).
- Claims about size or speed state their conditions next to the claim or through a link: the data, the settings, the
  tool versions, the machine and the date. No superlatives (best, fastest, smallest, maximum) without the measurement
  that backs them. Comparisons with other tools are measured with `tools/crosscheck.py` on the folders that
  `tools/bench/make_corpora.py` builds, and never stated without that measurement.

### Privacy

- Keep personal data out of the repository: no names, email addresses, user names, computer names or local paths in
  code, comments, test data, logs or screenshots. The exception is copyright and license notices that third-party
  code or licenses require: keep them exactly as published.
- Commits are public and carry the name and email address from your Git configuration: use a name you are happy to
  publish and the noreply address that GitHub offers in its email settings.

## Pull requests

- One topic per pull request, and one commit per logical change. A commit message says what was wrong and how the
  change fixes it.
- Fill in the pull request template. CI must pass on Linux, Windows x64 and Windows ARM64.
- Changes to the format, the codecs, the filters or the planner get the closest review.

## License of contributions

Osmium is licensed under the [MIT License](LICENSE), and contributions are accepted under the same license
("inbound = outbound"). By submitting a contribution, you agree that it is licensed under the MIT License and you
confirm that you have the right to submit it. There is no contributor license agreement to sign. You keep the
copyright in your contribution; the license names the copyright holders together as "Osmium contributors".

Product names used here are the property of their respective owners (see the trademark notice in the
[README](README.md#license-and-legal)); Osmium is not affiliated with or endorsed by them.
