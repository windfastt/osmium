#!/usr/bin/env python3
"""Fetch and check the pinned upstream sources of the osmium_native library.

Run it with isolated Python:  python3 -I native/fetch_sources.py [options]

What it does, in this order, and nothing else:
  1. Reads native/sources.lock (one archive per line) and native/sources.files (every file taken from them).
  2. For each archive: uses a cached copy, a copy from --from, or downloads it; checks its size and SHA-256 BEFORE
     anything is extracted. A cached or provided archive that does not match is refused, never used.
  3. Extracts only the files listed in sources.files, by exact path, into a new folder, refusing anything that is not
     a plain regular file at a plain relative path; checks each file's size and SHA-256; then puts the folder in place
     as <out>/<name>-<version>/.
It never runs a script, makefile, test or generator from an archive. The LZMA SDK's .7z is read with the 7-Zip
program, which streams each listed file to this script (nothing is written by 7-Zip); the zstd .tar.gz is read with
the tarfile module, whose extraction filter checks each member.

Exit status: 0 when every check passed, 1 otherwise (the reason is printed).
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
MAX_ARCHIVE = 64 << 20          # far above both archives; a download larger than its pinned size is refused anyway
MAX_FILE = 16 << 20             # every listed source file is far smaller
ROLES = ("lib", "test", "doc")

_HEX64 = re.compile(r"^[0-9a-f]{64}$")
_HEX40 = re.compile(r"^[0-9a-f]{40}$")
_NAME = re.compile(r"^[a-z0-9][a-z0-9-]*$")
_VERSION = re.compile(r"^[0-9][0-9.]*$")
_SEGMENT = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9._+-]*$")


class Refused(Exception):
    """A check failed; the message says which and why."""


class Archive:
    def __init__(self, name: str, version: str, url: str, sha256: str, size: int, tag: str, commit: str):
        self.name, self.version, self.url, self.sha256, self.size, self.tag, self.commit = (
            name, version, url, sha256, size, tag, commit)
        self.files: list[tuple[str, str, str]] = []  # (path, sha256, role)

    @property
    def file_name(self) -> str:
        return self.url.rsplit("/", 1)[-1]

    @property
    def folder(self) -> str:
        return f"{self.name}-{self.version}"


def plain_relative(path: str) -> str:
    """Returns the path if it is plain and relative (segments of safe characters, '/' only); refuses it otherwise."""
    segments = path.split("/")
    if not path or any(not _SEGMENT.match(s) or s in (".", "..") for s in segments):
        raise Refused(f"'{path}' is not a plain relative path")
    return path


def read_lines(path: Path) -> list[list[str]]:
    rows = []
    for n, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = line.strip()
        if line and not line.startswith("#"):
            rows.append([str(n)] + line.split())
    return rows


def load_pins(lock: Path, files: Path) -> list[Archive]:
    archives: dict[str, Archive] = {}
    for row in read_lines(lock):
        n, fields = row[0], row[1:]
        if len(fields) != 7:
            raise Refused(f"{lock.name}:{n}: expected 7 fields, found {len(fields)}")
        name, version, url, sha, size, tag, commit = fields
        if not _NAME.match(name) or not _VERSION.match(version) or not url.startswith("https://") \
                or not _HEX64.match(sha) or not size.isdigit() or not _HEX40.match(commit) or name in archives:
            raise Refused(f"{lock.name}:{n}: malformed or repeated line")
        plain_relative(url.rsplit("/", 1)[-1])
        archives[name] = Archive(name, version, url, sha, int(size), tag, commit)
    seen: set[tuple[str, str]] = set()
    for row in read_lines(files):
        n, fields = row[0], row[1:]
        if len(fields) != 4:
            raise Refused(f"{files.name}:{n}: expected 4 fields, found {len(fields)}")
        sha, name, role, path = fields
        if not _HEX64.match(sha) or name not in archives or role not in ROLES:
            raise Refused(f"{files.name}:{n}: malformed line (unknown archive or role?)")
        plain_relative(path)
        if (name, path.lower()) in seen:
            raise Refused(f"{files.name}:{n}: '{path}' is listed twice")
        seen.add((name, path.lower()))
        archives[name].files.append((path, sha, role))
    for a in archives.values():
        if not a.files:
            raise Refused(f"{files.name} lists no file of {a.name}")
    return list(archives.values())


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def check_archive(a: Archive, path: Path) -> bool:
    return path.is_file() and not path.is_symlink() and path.stat().st_size == a.size and sha256_file(path) == a.sha256


def download(a: Archive, dest: Path) -> None:
    part = dest.with_name(dest.name + ".part")
    req = urllib.request.Request(a.url, headers={"User-Agent": "osmium-fetch-sources"})
    total = 0
    h = hashlib.sha256()
    with urllib.request.urlopen(req, timeout=120) as r, part.open("wb") as f:
        if not r.geturl().startswith("https://"):
            raise Refused(f"{a.url} redirected to a non-HTTPS address")
        while True:
            chunk = r.read(1 << 16)
            if not chunk:
                break
            total += len(chunk)
            if total > a.size or total > MAX_ARCHIVE:
                raise Refused(f"{a.file_name}: the download is larger than the pinned {a.size} bytes")
            h.update(chunk)
            f.write(chunk)
    if total != a.size or h.hexdigest() != a.sha256:
        part.unlink()
        raise Refused(f"{a.file_name}: downloaded {total} bytes with SHA-256 {h.hexdigest()}, "
                      f"pinned {a.size} bytes with {a.sha256}")
    os.replace(part, dest)


def obtain(a: Archive, cache: Path, source: Path | None) -> Path:
    """Returns a path to the archive whose size and SHA-256 match the pin."""
    if source is not None:
        p = source / a.file_name
        if not check_archive(a, p):
            raise Refused(f"{p} is missing or does not match the pinned size and SHA-256")
        print(f"  {a.file_name}: from {source} (size and SHA-256 match)")
        return p
    cache.mkdir(parents=True, exist_ok=True)
    p = cache / a.file_name
    if p.exists():
        if check_archive(a, p):
            print(f"  {a.file_name}: cached copy (size and SHA-256 match)")
            return p
        print(f"  {a.file_name}: cached copy does not match the pin; removing it and downloading again")
        p.unlink()
    print(f"  {a.file_name}: downloading {a.url}")
    download(a, p)
    if not check_archive(a, p):
        raise Refused(f"{a.file_name}: the downloaded archive does not match the pin")
    print(f"  {a.file_name}: downloaded, size and SHA-256 match")
    return p


def check_bytes(a: Archive, path: str, sha: str, data: bytes) -> bytes:
    got = hashlib.sha256(data).hexdigest()
    if got != sha:
        raise Refused(f"{a.name}: {path} has SHA-256 {got}, sources.files pins {sha}")
    return data


def read_tar(a: Archive, archive: Path, scratch: Path) -> dict[str, bytes]:
    if not hasattr(tarfile, "data_filter"):
        raise Refused("this Python has no tarfile extraction filters; use Python 3.12 or later")
    wanted = {path: sha for path, sha, _ in a.files}
    found: dict[str, bytes] = {}
    with tarfile.open(archive, mode="r:gz") as t:
        for member in t:
            # The 'data' filter refuses absolute paths, '..', links pointing outside and special files. Members are
            # checked with it even though only listed regular files are read (and nothing is written by tarfile).
            try:
                member = tarfile.data_filter(member, str(scratch))
            except tarfile.FilterError as e:
                raise Refused(f"{a.file_name}: unsafe member '{member.name}': {e}") from e
            if member.name not in wanted:
                continue
            if member.name in found:
                raise Refused(f"{a.file_name}: '{member.name}' occurs twice")
            if not member.isreg() or member.size > MAX_FILE:
                raise Refused(f"{a.file_name}: '{member.name}' is not a regular file of a plausible size")
            f = t.extractfile(member)
            if f is None:
                raise Refused(f"{a.file_name}: '{member.name}' cannot be read")
            found[member.name] = check_bytes(a, member.name, wanted[member.name], f.read(MAX_FILE + 1))
    missing = sorted(set(wanted) - set(found))
    if missing:
        raise Refused(f"{a.file_name}: listed files are missing: {', '.join(missing)}")
    return found


def seven_zip() -> str:
    candidates = [shutil.which(n) for n in ("7z", "7za", "7zz")]
    if os.name == "nt":
        for env in ("ProgramFiles", "ProgramW6432", "ProgramFiles(x86)"):
            base = os.environ.get(env)
            if base:
                candidates.append(str(Path(base) / "7-Zip" / "7z.exe"))
    for c in candidates:
        if c and Path(c).is_file():
            return c
    raise Refused("the 7-Zip program (7z) is needed to read the LZMA SDK archive")


def seven_zip_version(exe: str) -> str:
    out = subprocess.run([exe], stdin=subprocess.DEVNULL, capture_output=True, timeout=60).stdout
    for line in out.decode("utf-8", "replace").splitlines():
        if line.strip():
            return line.strip()
    return "unknown"


def read_7z(a: Archive, archive: Path) -> dict[str, bytes]:
    exe = seven_zip()
    print(f"  7-Zip: {seven_zip_version(exe)} ({exe})")
    found: dict[str, bytes] = {}
    for path, sha, _ in a.files:
        # x -so: write the file's bytes to standard output only. -spd: the name is a literal path, no wildcards.
        r = subprocess.run([exe, "x", "-so", "-spd", "-bd", "-y", str(archive), path.replace("/", os.sep)],
                           stdin=subprocess.DEVNULL, capture_output=True, timeout=300)
        if r.returncode != 0:
            raise Refused(f"{a.file_name}: 7-Zip could not read '{path}' (exit {r.returncode})")
        if len(r.stdout) > MAX_FILE:
            raise Refused(f"{a.file_name}: '{path}' is larger than any listed source file can be")
        found[path] = check_bytes(a, path, sha, r.stdout)
    return found


def extract(a: Archive, archive: Path, out: Path) -> Path:
    out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=f".{a.folder}-", dir=out) as tmp_name:
        tmp = Path(tmp_name)
        scratch = tmp / "scratch"
        scratch.mkdir()
        if a.file_name.endswith(".tar.gz"):
            data = read_tar(a, archive, scratch)
        elif a.file_name.endswith(".7z"):
            data = read_7z(a, archive)
        else:
            raise Refused(f"{a.file_name}: unknown archive type")
        tree = tmp / "tree"
        for path, sha, _ in a.files:
            dest = tree.joinpath(*path.split("/"))
            dest.parent.mkdir(parents=True, exist_ok=True)
            with dest.open("xb") as f:
                f.write(data[path])
        verify_tree(a, tree)
        final = out / a.folder
        if final.is_symlink() or (final.exists() and not final.is_dir()):
            raise Refused(f"{final} exists and is not a folder")
        if final.exists():
            shutil.rmtree(final)
        os.replace(tree, final)
    return final


def verify_tree(a: Archive, tree: Path) -> None:
    """Every listed file present with its pinned hash, and nothing else in the folder (no links, no extra files)."""
    listed = {path: sha for path, sha, _ in a.files}
    seen = set()
    for dirpath, dirnames, filenames in os.walk(tree):
        for d in dirnames:
            if (Path(dirpath) / d).is_symlink():
                raise Refused(f"{a.folder}: {d} is a link")
        for name in filenames:
            p = Path(dirpath) / name
            rel = p.relative_to(tree).as_posix()
            if p.is_symlink() or rel not in listed:
                raise Refused(f"{a.folder}: unexpected file {rel}")
            if sha256_file(p) != listed[rel]:
                raise Refused(f"{a.folder}: {rel} does not match sources.files")
            seen.add(rel)
    missing = sorted(set(listed) - seen)
    if missing:
        raise Refused(f"{a.folder}: missing {', '.join(missing)}")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description="Fetch and check the pinned upstream sources of osmium_native.")
    ap.add_argument("--lock", type=Path, default=HERE / "sources.lock")
    ap.add_argument("--files", type=Path, default=HERE / "sources.files")
    ap.add_argument("--cache", type=Path, default=ROOT / "artifacts" / "native-cache",
                    help="folder for downloaded archives (kept between runs; every copy is checked before use)")
    ap.add_argument("--from", dest="source", type=Path, default=None,
                    help="take the archives from this folder instead of downloading them")
    ap.add_argument("--out", type=Path, default=ROOT / "artifacts" / "native-src",
                    help="where <name>-<version>/ folders are created")
    ap.add_argument("--verify-only", action="store_true",
                    help="only check the folders already in --out against sources.files")
    args = ap.parse_args(argv)
    try:
        archives = load_pins(args.lock, args.files)
        for a in archives:
            print(f"{a.name} {a.version}: {a.file_name}, SHA-256 {a.sha256}, tag {a.tag} ({a.commit[:12]})")
            if args.verify_only:
                verify_tree(a, args.out / a.folder)
                print(f"  {a.folder}: {len(a.files)} files match sources.files")
                continue
            archive = obtain(a, args.cache, args.source)
            final = extract(a, archive, args.out)
            print(f"  {final}: {len(a.files)} files extracted and checked")
    except Refused as e:
        print(f"fetch_sources: refused: {e}", file=sys.stderr)
        return 1
    except (OSError, subprocess.SubprocessError, tarfile.TarError) as e:
        print(f"fetch_sources: failed: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
