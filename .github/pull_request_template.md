<!-- Thank you for contributing. Security problems: do not open a pull request; follow SECURITY.md instead. -->

## What and why

<!-- What does this change, and why? Link the issue it addresses, for example "Fixes #12". Larger changes should have
     been agreed in an issue first (see CONTRIBUTING.md). -->

## How it was tested

<!-- The commands you ran and where (Linux, Windows x64, Windows ARM64). For compression changes: the data, the
     settings, and the sizes and times before and after. -->

## Checklist

- [ ] `dotnet build Osmium.sln -c Release` finishes with 0 warnings.
- [ ] The test suites pass: `dotnet test -c Release --project tests/Osmium.Core.Tests`,
  `tests/Osmium.Desktop.Tests` and `tests/Osmium.Cli.Tests`.
- [ ] The change comes with tests; a fixed bug that can be reproduced on Linux has a regression test.
- [ ] Existing golden vectors are unchanged and no method id was renumbered or reused. (If an output had to change,
  it is a new method with a new id and its own golden vectors, explained above.)
- [ ] Generated files were regenerated, not edited by hand: `python3 tools/site/build.py` for `docs/index.html`,
  `dotnet run --project tools/settingsdoc -c Release` for `docs/SETTINGS.md`.
- [ ] User-visible changes are in CHANGELOG.md under the unreleased version, and the affected documentation is updated.
- [ ] Any new third-party code or package is managed code under an MIT-compatible license, with an entry in
  THIRD-PARTY-NOTICES.md.
- [ ] No personal data (names, email addresses, user or computer names, local paths) in code, docs, test data or
  screenshots, apart from third-party copyright and license notices kept as published.
- [ ] I agree that this contribution is licensed under the MIT License, as CONTRIBUTING.md describes.
