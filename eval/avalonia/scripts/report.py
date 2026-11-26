"""UI evaluation summary from the CI artifacts: one Markdown page with every item per architecture, the sizes and licences.

Usage: python3 -I report.py <artifacts dir> <out.md>
Expects <artifacts>/ui-eval-x64/results.json, <artifacts>/ui-eval-arm64/results.json (either may be missing),
<artifacts>/ui-eval-x64/sizes.md, <artifacts>/ui-eval-linux-reference/{render-summary.json,archives-bench-headless.json,licences.md}.
"""
import json
import sys
from pathlib import Path

art, out = Path(sys.argv[1]), Path(sys.argv[2])
arches = {}
for a in ("x64", "arm64"):
    for cand in (art / f"ui-eval-{a}" / "results.json", art / f"ui-eval-{a}" / "results.partial.json"):
        if cand.exists():
            arches[a] = json.loads(cand.read_text(encoding="utf-8"))
            break


def g(r, *path, default="–"):
    for p in path:
        if not isinstance(r, dict) or p not in r:
            return default
        r = r[p]
    return default if r is None else r


def verdict(v):
    return {True: "PASS", False: "FAIL"}.get(v, "n/a")


lines = ["# UI evaluation: Avalonia 12 preview on Windows runners", ""]
ref = art / "ui-eval-linux-reference"
if (ref / "render-summary.json").exists():
    rs = json.loads((ref / "render-summary.json").read_text())
    lines.append(f"Linux reference: Avalonia {rs.get('avalonia')}, SkiaSharp {rs.get('skiasharp')}, {rs.get('automation_peers')} UIA peers.")
for a, r in arches.items():
    e = r.get("env", {})
    lines.append(f"- **{a}**: {e.get('os')} ({e.get('osArch')}), image {e.get('imageOS')} {e.get('imageVersion')}, "
                 f"{e.get('cpus')} CPUs, desktop '{e.get('inputDesktop')}', explorer {e.get('explorer')}, display "
                 f"{g(r, 'display', 'after')} ({g(r, 'display', 'scale')}); CPU {e.get('cpu')}, {e.get('cpuBusyPercent')} % busy before the run, "
                 f"SHA-256 of 64 MiB in {e.get('workloadMs')} ms")
lines += ["", "| Item | " + " | ".join(arches) + " |", "|---|" + "---|" * len(arches)]


def row(label, fn):
    lines.append(f"| {label} | " + " | ".join(fn(r) for r in arches.values()) + " |")


row("Launches without a crash (7)", lambda r: f"{verdict(g(r, 'startup', 'launchPass', default=None))}: "
    f"{g(r, 'startup', 'avaloniaLaunches')} first frames, {g(r, 'startup', 'avaloniaCrashes')} crashes")
row("First frame, median (≤ 1000 ms, ≤ 1.5× WinForms)", lambda r: f"{verdict(g(r, 'startup', 'firstFramePass', default=None))}: "
    f"Avalonia {g(r, 'startup', 'avaloniaMedianMs')} ms vs WinForms {g(r, 'startup', 'winformsMedianMs')} ms "
    f"(×{g(r, 'startup', 'ratio')}); runs A {g(r, 'startup', 'avaloniaFirstFrameMs')} / W {g(r, 'startup', 'winformsPaintedMs')}")
row("First frame cross-check: screen pixels complete", lambda r: f"{verdict(g(r, 'startup_visual', 'pass', default=None))}: "
    f"A {g(r, 'startup_visual', 'avaloniaMedianMs')} ms vs W {g(r, 'startup_visual', 'winformsMedianMs')} ms (×{g(r, 'startup_visual', 'ratio')})")
row("Idle tray private bytes (≤ 1.5× WinForms)", lambda r: f"{verdict(g(r, 'memory', 'pass', default=None))}: "
    f"A {g(r, 'memory', 'avaloniaIdleMiB')} MiB vs W {g(r, 'memory', 'winformsIdleMiB')} MiB (×{g(r, 'memory', 'ratio')})")
row("Tray (Shell_NotifyIcon): icon, menu items, light/dark surface, balloon", lambda r: f"{verdict(g(r, 'tray_shell', 'pass', default=None))}: "
    f"icon {g(r, 'tray_shell', 'iconOk')}, items {g(r, 'tray_shell', 'light', 'uiaItemNames')}, light {g(r, 'tray_shell', 'lightSurfaceOk')}, "
    f"dark {g(r, 'tray_shell', 'darkSurfaceOk')} {g(r, 'tray_shell', 'dark', 'topColours')}, balloon {g(r, 'tray_shell', 'balloonOk')} {g(r, 'tray_shell', 'balloonEvents')}")
row("Tray host (native menu, two processes)", lambda r: f"{verdict(g(r, 'tray_host', 'pass', default=None))}: "
    f"light {g(r, 'tray_host', 'light', 'menu', 'topColours')}, dark {g(r, 'tray_host', 'dark', 'menu', 'topColours')}")
row("Tray (Avalonia TrayIcon)", lambda r: f"{verdict(g(r, 'tray_avalonia', 'pass', default=None))}: icon {g(r, 'tray_avalonia', 'iconOk')}, "
    f"light {g(r, 'tray_avalonia', 'lightSurfaceOk')}, dark {g(r, 'tray_avalonia', 'darkSurfaceOk')}")
row("UIA: 22 named peers", lambda r: f"{verdict(g(r, 'uia_peers', 'pass', default=None))}: missing {g(r, 'uia_peers', 'missing')}")
row("Capture vs Linux: boxes ≤ 1 px, token colours", lambda r: f"{verdict(g(r, 'capture', 'pass', default=None))}: "
    f"max box delta light {g(r, 'capture', 'light', 'maxBoxDeltaPx')} px, dark {g(r, 'capture', 'dark', 'maxBoxDeltaPx')} px; "
    f"colours {g(r, 'capture', 'light', 'coloursPass')}/{g(r, 'capture', 'dark', 'coloursPass')}; mean diff {g(r, 'capture', 'light', 'meanAbsDiff')}")
row("Fonts: bundled, Segoe UI Variable weights, CJK", lambda r: f"{verdict(g(r, 'fonts', 'pass', default=None))}: bundled {g(r, 'fonts', 'bundledOk')}, "
    f"Segoe UI Variable {g(r, 'fonts', 'segoeVariableOk')}, static Segoe UI {g(r, 'fonts', 'segoeStaticOk')}, CJK {g(r, 'fonts', 'cjkOk')} ({g(r, 'fonts', 'cjk', 'typeface')})")
row("Theme switch followed", lambda r: f"{verdict(g(r, 'theme_follow', 'pass', default=None))}: dark {g(r, 'theme_follow', 'toDark', 'detectedMs')} ms, light {g(r, 'theme_follow', 'toLight', 'detectedMs')} ms")
row("High contrast detected", lambda r: f"{verdict(g(r, 'high_contrast', 'pass', default=None))}: live {g(r, 'high_contrast', 'detectedLive')}, at start {g(r, 'high_contrast', 'detectedAtStart')}")
row("CF_HDROP drag of a row", lambda r: f"{verdict(g(r, 'drag', 'pass', default=None))}: formats {g(r, 'drag', 'formats')}; desktop: {g(r, 'drag', 'desktopDrop')}")
row("Tab order", lambda r: f"{verdict(g(r, 'keyboard', 'pass', default=None))}: {g(r, 'keyboard', 'distinct')} distinct stops")
row("DPI 150 % / 175 % / 200 %", lambda r: f"{verdict(g(r, 'dpi', 'pass', default=None))}: {g(r, 'dpi', 'method')}; {g(r, 'dpi', 'scaleInfo')}; " + "; ".join(
    f"{l.get('percent')} %: " + (f"scaling {l.get('renderScaling')}, px vs layout×scale {l.get('maxPxVsLayoutTimesScale')}, top boxes {l.get('maxTopBoxDeltaDip')} dip, client {l.get('clientAtScale')}" if 'renderScaling' in l else str(l.get('real')))
    for l in g(r, 'dpi', 'levels', default=[])))
row("Archive browser, 100,000 entries", lambda r: f"{verdict(g(r, 'archives', 'pass', default=None))}: " + "; ".join(
    f"{s['step']} {s['ms']} ms" for s in g(r, 'archives', 'table', default=[])))
row("trimmed build starts", lambda r: f"{verdict(g(r, 'trimmed_startup', 'pass', default=None))}: median {g(r, 'trimmed_startup', 'medianMs')} ms")

for a, r in arches.items():
    bd = r.get("startup_breakdown") or {}
    if bd.get("variants"):
        stages = ["main", "platform-ready", "setup-done", "window-ctor-done", "opened", "first-frame"]
        lines += ["", f"## First frame in detail ({a}, medians of 5, ms since process creation)", "",
                  "| Variant | " + " | ".join(stages) + " | JIT ms (methods) |", "|---|" + "---:|" * (len(stages) + 1)]
        for v in bd["variants"]:
            st = v.get("stagesMedianMs", {})
            lines.append(f"| {v['variant']} | " + " | ".join(str(st.get(x, "–")) for x in stages) + f" | {v.get('jitMsMedian')} ({v.get('jitMethodsMedian')}) |")
        lines.append(f"\nWarm open (already in the notification area): Avalonia {bd.get('warmOpenAvaloniaMedianMs')} ms {bd.get('warmOpenAvaloniaMs')}, "
                     f"WinForms {bd.get('warmOpenWinFormsMedianMs')} ms {bd.get('warmOpenWinFormsMs')}. {bd.get('warmOpenNote')}")
    mem = r.get("memory") or {}
    if mem.get("table"):
        lines += ["", f"## Idle memory and its options ({a})", "", "| Variant | Open MiB | Idle MiB | × WinForms | Reopen ms | Idle after reopen MiB | GC committed / native heaps / other private MiB |",
                  "|---|---:|---:|---:|---|---:|---|"]
        for t in mem["table"]:
            b = t.get("breakdown") or {}
            lines.append(f"| {t.get('kind')} #{t.get('run')} | {t.get('openMiB', '–')} | {t.get('idleMiB', '–')} | {t.get('ratioToWinForms', '–')} | {t.get('reopenMs', '–')} | "
                         f"{t.get('idleAfterReopenMiB', '–')} | {b.get('gcCommittedMiB', '–')} / {b.get('nativeHeapsMiB', '–')} / {b.get('otherPrivateMiB', '–')} |")

for a in arches:
    s = art / f"ui-eval-{a}" / "sizes.md"
    if s.exists():
        lines += ["", f"## Sizes ({a}, .pdb left out)", "", s.read_text(encoding="utf-8")]
if (ref / "licences.md").exists():
    lines += ["", "## Licences of the shipped packages", "", (ref / "licences.md").read_text(encoding="utf-8")]
out.write_text("\n".join(lines) + "\n", encoding="utf-8")
print("\n".join(lines))
