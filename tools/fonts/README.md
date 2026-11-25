# The app's brand fonts

The Windows app shows Osmium's own words (page titles, card headings, spec lines, tags and the big numbers in result
tiles) in the typefaces of the product page. It ships four small static fonts made from the upstream variable fonts:

| File | Family name | From | Axes | Used for |
|---|---|---|---|---|
| `AnybodyTitle.ttf` | Anybody Title | Anybody | wght 740, wdth 75 | page titles, the wordmark |
| `AnybodyHeading.ttf` | Anybody Heading | Anybody | wght 680, wdth 86 | card and dialog headings |
| `MartianMonoSpec.ttf` | Martian Mono Spec | Martian Mono | wght 500, wdth 87.5 | spec lines, tags |
| `InstrumentSansNumber.ttf` | Instrument Sans Number | Instrument Sans | wght 650, wdth 100 | big values in result tiles |

All three typefaces are under the SIL Open Font License 1.1 without Reserved Font Names. The static, subset files are
modified versions and stay under that licence; each keeps the upstream copyright notice (name ID 0) and the licence
fields (name IDs 13 and 14), and its licence text lies next to it in `src/Osmium.App/Fonts/*-OFL.txt`. The licence file
starts with the font's own copyright notice; where the upstream licence names another year, both lines are kept.

They cover Basic Latin, Latin-1, Latin Extended-A, general punctuation and a few symbols (see `unicodes` in
`fonts.json`). Text in other scripts, file names, paths and messages are shown in the system's own fonts.

## Rebuilding

```
python3 -m pip install -r tools/fonts/requirements.txt
python3 tools/fonts/make_app_fonts.py
```

The script downloads the variable fonts and their `OFL.txt` from the google/fonts repository at the commit recorded in
`fonts.json`, checks each download against its recorded SHA-256, makes the static instances with
`fontTools.varLib.instancer`, subsets and renames them, and writes them with their licences to `src/Osmium.App/Fonts`.
It fails when a download or a result differs from the hashes in `fonts.json`. After changing the commit, the
coordinates or the characters, run it with `--record` to record the new hashes, and check the result in the app.
