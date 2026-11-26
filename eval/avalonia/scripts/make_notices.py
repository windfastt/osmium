"""Writes the UI part of the preview's third-party notices from the restored packages (exact licence texts).

Usage: python3 -I make_notices.py <eval/avalonia dir> <output file>

Reads the versions from src/Proto.Desktop/packages.lock.json and the texts from the NuGet global packages folder
(restore first). Texts that a package does not carry (Avalonia's own MIT licence, MicroCom's) are kept in
notices/texts/, fetched from the packages' source commits named in their nuspecs.
"""
import json
import os
import re
import sys
from pathlib import Path

root, out = Path(sys.argv[1]), Path(sys.argv[2])
lock = json.loads((root / "src/Proto.Desktop/packages.lock.json").read_text(encoding="utf-8"))
deps = lock["dependencies"]["net10.0"]
ver = {k.lower(): v.get("resolved") for k, v in deps.items() if v.get("resolved")}
nuget = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget" / "packages")
texts = root / "notices" / "texts"


def pkg(name, *rel):
    return (nuget / name.lower() / ver[name.lower()]).joinpath(*rel)


def read(p):
    return Path(p).read_text(encoding="utf-8-sig").replace("\r\n", "\n").rstrip() + "\n"


def section(notices, name):
    """One component's block from the SkiaSharp/HarfBuzzSharp notices file (between its banner and END)."""
    m = re.search(r"#{80}\n# " + re.escape(name) + r"\n(?:# http[^\n]*\n)?#{80}\n(.*?)\n#{80}\n# END: " + re.escape(name), notices, re.S)
    return m.group(1).strip("\n") + "\n"


skia_notices = read(pkg("SkiaSharp.NativeAssets.Win32", "THIRD-PARTY-NOTICES.txt"))
apache = section(skia_notices, "etc1")
fonts = root / "src/Proto.Views/Assets/Fonts"

parts = []


def add(title, origin, body):
    parts.append(f"{'=' * 100}\n{title}\n{origin}\n{'=' * 100}\n\n{body.rstrip()}\n")


v = lambda n: ver[n.lower()]
add(f"Avalonia {v('Avalonia')} (Avalonia, Avalonia.Win32, Avalonia.Skia, Avalonia.HarfBuzz, Avalonia.Themes.Fluent, "
    f"Avalonia.Remote.Protocol)", "https://github.com/AvaloniaUI/Avalonia — MIT", read(texts / "avalonia-licence.md"))
add(f"Avalonia.Controls.DataGrid {v('Avalonia.Controls.DataGrid')}", "https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid — MIT",
    read(texts / "datagrid-licence.md"))
add(f"MicroCom.Runtime {v('MicroCom.Runtime')}", "https://github.com/kekekeks/MicroCom — MIT", read(texts / "microcom-LICENSE"))
add(f"SkiaSharp {v('SkiaSharp')} and SkiaSharp.NativeAssets.Win32 (libSkiaSharp.dll)", "https://github.com/mono/SkiaSharp — MIT",
    read(pkg("SkiaSharp", "LICENSE.txt")))
add("Skia (inside libSkiaSharp.dll)", "https://github.com/google/skia — BSD-3-Clause", section(skia_notices, "skia"))
add(f"HarfBuzzSharp {v('HarfBuzzSharp')} and HarfBuzzSharp.NativeAssets.Win32 (libHarfBuzzSharp.dll)",
    "https://github.com/mono/SkiaSharp — MIT", read(pkg("HarfBuzzSharp", "LICENSE.txt")))
add("HarfBuzz (inside libHarfBuzzSharp.dll)", "https://github.com/harfbuzz/harfbuzz — \"Old MIT\"", section(skia_notices, "HarfBuzz"))
add(f"ANGLE, Avalonia build {v('Avalonia.Angle.Windows.Natives')} (av_libglesv2.dll)",
    "https://github.com/AvaloniaUI/angle — BSD-3-Clause (the package's LICENSE file)", read(pkg("Avalonia.Angle.Windows.Natives", "LICENSE")))
add("Abseil (abseil-cpp, compiled into av_libglesv2.dll by ANGLE's build)", "https://github.com/abseil/abseil-cpp — Apache-2.0; "
    "Copyright The Abseil Authors", apache)
for fam, ofl in (("Anybody (static instances Anybody Title, Anybody Heading)", "Anybody-OFL.txt"),
                 ("Instrument Sans (static instances Instrument Sans Body, Instrument Sans Number)", "InstrumentSans-OFL.txt"),
                 ("Martian Mono (static instance Martian Mono Spec)", "MartianMono-OFL.txt")):
    add(f"Font: {fam}", "SIL Open Font License 1.1 (applies to the font files only)", read(fonts / ofl))

header = f"""Osmium UI preview — third-party notices, user-interface layer

The preview is MIT-licensed (LICENSE.txt). Besides the components listed in the Osmium engine's notices (further down
in this file) and the .NET runtime (dotnet-LICENSE.txt, dotnet-THIRD-PARTY-NOTICES.txt), it contains the user-interface
components below, used unmodified, with their licence texts. SkiaSharp-THIRD-PARTY-NOTICES.txt is the notices file
SkiaSharp and HarfBuzzSharp ship for the code they build into libSkiaSharp.dll and libHarfBuzzSharp.dll; it is
included unchanged.

| Component | Version | Files | Licence |
|---|---|---|---|
| Avalonia (core, Win32, Skia, HarfBuzz, Fluent theme) | {v('Avalonia')} | Avalonia*.dll | MIT |
| Avalonia.Controls.DataGrid | {v('Avalonia.Controls.DataGrid')} | Avalonia.Controls.DataGrid.dll | MIT |
| MicroCom.Runtime | {v('MicroCom.Runtime')} | MicroCom.Runtime.dll | MIT |
| SkiaSharp | {v('SkiaSharp')} | SkiaSharp.dll, libSkiaSharp.dll | MIT; Skia BSD-3-Clause; others in SkiaSharp-THIRD-PARTY-NOTICES.txt |
| HarfBuzzSharp | {v('HarfBuzzSharp')} | HarfBuzzSharp.dll, libHarfBuzzSharp.dll | MIT; HarfBuzz "Old MIT" |
| Avalonia.Angle.Windows.Natives (ANGLE) | {v('Avalonia.Angle.Windows.Natives')} | av_libglesv2.dll | BSD-3-Clause; Abseil Apache-2.0 |
| Anybody, Instrument Sans, Martian Mono (static instances) | as bundled | embedded in Proto.Views.dll | OFL-1.1 |

"""
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(header + "\n".join(parts), encoding="utf-8")
print(f"wrote {out} ({out.stat().st_size} bytes, {len(parts)} components)")
