#!/usr/bin/env python3
"""Cross-check Osmium against other compressors.

For every input folder, each tool compresses it at its strongest commonly used setting; the result is decompressed
again and verified (stream tools must reproduce the exact tar; archivers must restore an identical file tree).
Results are written as JSON after every run (so partial results survive) and as Markdown tables at the end.

Usage:
  python3 tools/crosscheck.py --osmium "dotnet path/to/osmium.dll" --json results.json --md BENCHMARKS.part.md DIR [DIR...]

Tools are skipped (and reported) when not installed. Timings are wall-clock on whatever machine runs the script;
every tool runs with its own default threading except xz/zstd, which are pinned to one thread because their
multi-threaded modes split the input and lose ratio.
"""
import argparse
import hashlib
import json
import os
import shlex
import shutil
import subprocess
import sys
import tempfile
import time


def tree_digest(root):
    """relative path -> sha256 for every regular file under root."""
    out = {}
    for dirpath, _, files in os.walk(root):
        for f in files:
            p = os.path.join(dirpath, f)
            h = hashlib.sha256()
            with open(p, 'rb') as fh:
                for chunk in iter(lambda: fh.read(1 << 20), b''):
                    h.update(chunk)
            out[os.path.relpath(p, root)] = h.hexdigest()
    return out


def file_sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def sh(cmd, cwd=None):
    """Run a shell command, return elapsed seconds; raise on failure."""
    t = time.perf_counter()
    r = subprocess.run(cmd, cwd=cwd, shell=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    if r.returncode != 0:
        raise RuntimeError(f"{cmd!r} failed ({r.returncode}): {r.stderr.decode(errors='replace')[-400:]}")
    return time.perf_counter() - t


def tools(osmium, profiles):
    q = shlex.quote
    # (label, family, needs, kind, extension, compress(src_tar, name, out), decompress(out, dest_or_tar, name))
    # kind "stream": compress a tar; "dir": archive the folder (run from its parent directory).
    # The extension matters: zpaq and osmium append their own when the output name lacks it.
    t = [
        ("zip -9 (Deflate)", "zip", "zip", "dir", ".zip",
         lambda tar, name, out: f"zip -q -r -9 {q(out)} {q(name)}",
         lambda out, dest, name: f"7z x -y -bso0 -bsp0 -o{q(dest)} {q(out)}"),
        ("bzip2 -9", "bzip2", "bzip2", "stream", ".bz2",
         lambda tar, name, out: f"bzip2 -9 -c {q(tar)} > {q(out)}",
         lambda out, dest, name: f"bzip2 -dc {q(out)} > {q(dest)}"),
        ("Brotli -q 11 -w 24", "brotli", "brotli", "stream", ".br",
         lambda tar, name, out: f"brotli -q 11 -w 24 -c {q(tar)} > {q(out)}",
         lambda out, dest, name: f"brotli -dc {q(out)} > {q(dest)}"),
        ("Zstandard -22 --ultra --long=31", "zstd", "zstd", "stream", ".zst",
         lambda tar, name, out: f"zstd -q --ultra -22 --long=31 -T1 -c {q(tar)} > {q(out)}",
         lambda out, dest, name: f"zstd -q -d --long=31 -c {q(out)} > {q(dest)}"),
        ("xz -9e", "xz", "xz", "stream", ".xz",
         lambda tar, name, out: f"xz -9e -T1 -c {q(tar)} > {q(out)}",
         lambda out, dest, name: f"xz -dc {q(out)} > {q(dest)}"),
        ("7-Zip -mx9 (LZMA2, 256 MB dict, solid)", "7z", "7z", "dir", ".7z",
         lambda tar, name, out: f"7z a -t7z -mx=9 -m0=lzma2 -mfb=273 -md=256m -ms=on -bso0 -bsp0 {q(out)} {q(name)}",
         lambda out, dest, name: f"7z x -y -bso0 -bsp0 -o{q(dest)} {q(out)}"),
        ("7-Zip PPMd (order 32, 1 GB)", "7z-ppmd", "7z", "dir", ".7z",
         lambda tar, name, out: f"7z a -t7z -m0=PPMd:mem=1024m:o=32 -ms=on -bso0 -bsp0 {q(out)} {q(name)}",
         lambda out, dest, name: f"7z x -y -bso0 -bsp0 -o{q(dest)} {q(out)}"),
        ("lrzip -z (ZPAQ back end)", "lrzip", "lrzip", "stream", ".lrz",
         lambda tar, name, out: f"lrzip -q -z -L 9 -o {q(out)} {q(tar)}",
         lambda out, dest, name: f"lrzip -q -d -o {q(dest)} {q(out)}"),
        ("ZPAQ -m5 (context mixing)", "zpaq", "zpaq", "dir", ".zpaq",
         lambda tar, name, out: f"zpaq a {q(out)} {q(name)} -m5",
         lambda out, dest, name: f"zpaq x {q(out)} {q(name)} -to {q(os.path.join(dest, name))}"),
    ]
    for p in profiles:
        t.append((f"Osmium {p}", f"osmium-{p}", None, "dir", ".osa",
                  (lambda prof: lambda tar, name, out: f"{osmium} a {q(out)} {q(name)} --profile {prof} -q --overwrite")(p),
                  lambda out, dest, name: f"{osmium} x {q(out)} {q(dest)} -q"))
    return t


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--osmium", required=True, help='command prefix, e.g. "dotnet src/Osmium.Cli/bin/Release/net10.0/osmium.dll"')
    ap.add_argument("--profiles", default="fast,balanced,max,ultra,insane")
    ap.add_argument("--insane-max-mib", type=int, default=64, help="skip Osmium insane on folders larger than this")
    ap.add_argument("--json", required=True)
    ap.add_argument("--md", required=True)
    ap.add_argument("dirs", nargs="+")
    a = ap.parse_args()

    results = json.load(open(a.json)) if os.path.exists(a.json) else []
    done = {(r["dataset"], r["tool"]) for r in results if r.get("ok")}
    work = tempfile.mkdtemp(prefix="crosscheck-")
    try:
        for d in a.dirs:
            d = os.path.abspath(d)
            name, parent = os.path.basename(d), os.path.dirname(d)
            files = sum(len(f) for _, _, f in os.walk(d))
            size = sum(os.path.getsize(os.path.join(p, f)) for p, _, fs in os.walk(d) for f in fs)
            tar = os.path.join(work, name + ".tar")
            subprocess.run(["tar", "-cf", tar, "-C", parent, name], check=True)
            tar_sha = file_sha(tar)
            original = tree_digest(d)
            for label, fam, need, kind, ext, comp, decomp in tools(a.osmium, a.profiles.split(",")):
                if (name, label) in done:
                    continue
                rec = {"dataset": name, "dataset_bytes": size, "files": files, "tool": label, "family": fam}
                if need and shutil.which(need) is None:
                    rec.update(ok=False, note=f"{need} not installed")
                elif fam == "osmium-insane" and size > a.insane_max_mib << 20:
                    rec.update(ok=False, note=f"skipped: folder larger than {a.insane_max_mib} MiB")
                else:
                    out = os.path.join(work, f"{name}.{fam}{ext}")
                    dest = os.path.join(work, f"{name}.{fam}.dest")
                    try:
                        if os.path.exists(out):
                            os.remove(out)
                        ct = sh(comp(tar, name, out), cwd=parent)
                        packed = os.path.getsize(out)
                        if kind == "stream":
                            dt = sh(decomp(out, dest, name))
                            ok = file_sha(dest) == tar_sha
                            os.remove(dest)
                        else:
                            shutil.rmtree(dest, ignore_errors=True)
                            os.makedirs(dest)
                            dt = sh(decomp(out, dest, name))
                            ok = tree_digest(os.path.join(dest, name)) == original
                            shutil.rmtree(dest, ignore_errors=True)
                        os.remove(out)
                        rec.update(ok=ok, bytes=packed, ratio=packed / size, compress_s=round(ct, 2), decompress_s=round(dt, 2),
                                   note="" if ok else "ROUND TRIP MISMATCH")
                    except Exception as e:  # noqa: BLE001 - report every failure in the table
                        rec.update(ok=False, note=str(e)[:300])
                print(f"{name:12} {label:42} " + (f"{rec['bytes']:>12,} {rec['ratio']:7.2%}  c {rec['compress_s']:8.1f}s  d {rec['decompress_s']:8.1f}s  {'OK' if rec['ok'] else rec['note']}"
                                                  if "bytes" in rec else rec.get("note", "")), flush=True)
                results = [r for r in results if not (r["dataset"] == name and r["tool"] == label)] + [rec]
                with open(a.json, "w") as fh:
                    json.dump(results, fh, indent=1)
            os.remove(tar)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    lines = []
    for ds in dict.fromkeys(r["dataset"] for r in results):
        rows = [r for r in results if r["dataset"] == ds]
        base = next((r for r in rows if r["family"] == "xz" and r.get("ok")), None)
        meta = rows[0]
        lines.append(f"### `{ds}` — {meta['dataset_bytes'] / 2**20:.1f} MiB, {meta['files']} files\n")
        lines.append("| Tool | Size (bytes) | % of original | vs. xz -9e | Compress | Decompress | Round trip |")
        lines.append("|---|---:|---:|---:|---:|---:|---|")
        for r in sorted(rows, key=lambda r: r.get("bytes", 1 << 62)):
            if "bytes" not in r:
                lines.append(f"| {r['tool']} | — | — | — | — | — | {r.get('note', '')} |")
                continue
            vs = "—" if base is None or r is base else f"{(r['bytes'] / base['bytes'] - 1) * 100:+.1f} %"
            star = "**" if r["family"].startswith("osmium") else ""
            lines.append(f"| {star}{r['tool']}{star} | {r['bytes']:,} | {r['ratio'] * 100:.2f} % | {vs} | {r['compress_s']:.1f} s | {r['decompress_s']:.1f} s | {'✔ identical' if r['ok'] else '✘ ' + r.get('note', '')} |")
        lines.append("")
    with open(a.md, "w") as fh:
        fh.write("\n".join(lines))
    print(f"wrote {a.md}")


if __name__ == "__main__":
    sys.exit(main())
