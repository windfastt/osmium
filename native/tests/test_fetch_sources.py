#!/usr/bin/env python3
"""Checks that fetch_sources.py refuses everything it must refuse. Run: python3 -I native/tests/test_fetch_sources.py"""
from __future__ import annotations

import hashlib
import importlib.util
import io
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("fetch_sources", HERE.parent / "fetch_sources.py")
fs = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(fs)

GOOD = b"int answer(void) { return 42; }\r\n"
OTHER = b"int answer(void) { return 41; }\r\n"


def sha(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def make_tar(path: Path, members: list[tuple[str, bytes | None, str | None]]) -> bytes:
    """members: (name, data, symlink target or None)."""
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz") as t:
        for name, data, link in members:
            info = tarfile.TarInfo(name)
            info.mtime = 0
            if link is not None:
                info.type = tarfile.SYMTYPE
                info.linkname = link
                t.addfile(info)
            else:
                info.size = len(data)
                t.addfile(info, io.BytesIO(data))
    raw = buf.getvalue()
    path.write_bytes(raw)
    return raw


class FetchSourcesTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="fetch-sources-test-"))
        self.src = self.tmp / "src"
        self.src.mkdir()
        self.out = self.tmp / "out"

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def pins(self, raw: bytes, files: list[tuple[str, str]], name="demo-1.0.tar.gz") -> tuple[Path, Path]:
        lock = self.tmp / "sources.lock"
        lock.write_text(f"demo 1.0 https://example.invalid/{name} {sha(raw)} {len(raw)} v1.0 {'0' * 40}\n")
        lst = self.tmp / "sources.files"
        lst.write_text("".join(f"{h}  demo  lib  {p}\n" for p, h in files))
        return lock, lst

    def run_main(self, lock: Path, lst: Path, *extra: str) -> int:
        return fs.main(["--lock", str(lock), "--files", str(lst), "--from", str(self.src), "--out", str(self.out),
                        "--cache", str(self.tmp / "cache"), *extra])

    def test_good_archive_extracts_only_listed_files(self):
        raw = make_tar(self.src / "demo-1.0.tar.gz", [("demo-1.0/a.c", GOOD, None), ("demo-1.0/build.sh", b"rm -rf /", None)])
        lock, lst = self.pins(raw, [("demo-1.0/a.c", sha(GOOD))])
        self.assertEqual(self.run_main(lock, lst), 0)
        self.assertEqual((self.out / "demo-1.0" / "demo-1.0" / "a.c").read_bytes(), GOOD)
        self.assertFalse((self.out / "demo-1.0" / "demo-1.0" / "build.sh").exists())

    def test_tampered_archive_is_refused_before_extraction(self):
        raw = make_tar(self.src / "demo-1.0.tar.gz", [("demo-1.0/a.c", GOOD, None)])
        lock, lst = self.pins(raw, [("demo-1.0/a.c", sha(GOOD))])
        bad = bytearray(raw)
        bad[len(bad) // 2] ^= 1
        (self.src / "demo-1.0.tar.gz").write_bytes(bytes(bad))
        self.assertEqual(self.run_main(lock, lst), 1)
        self.assertFalse((self.out / "demo-1.0").exists())

    def test_file_with_another_hash_is_refused(self):
        raw = make_tar(self.src / "demo-1.0.tar.gz", [("demo-1.0/a.c", OTHER, None)])
        lock, lst = self.pins(raw, [("demo-1.0/a.c", sha(GOOD))])
        self.assertEqual(self.run_main(lock, lst), 1)
        self.assertFalse((self.out / "demo-1.0").exists())

    def test_dot_dot_member_is_refused(self):
        raw = make_tar(self.src / "demo-1.0.tar.gz", [("../evil.c", GOOD, None), ("demo-1.0/a.c", GOOD, None)])
        lock, lst = self.pins(raw, [("demo-1.0/a.c", sha(GOOD))])
        self.assertEqual(self.run_main(lock, lst), 1)
        self.assertFalse((self.tmp / "evil.c").exists())
        self.assertFalse((self.out / "evil.c").exists())

    def test_link_member_is_refused(self):
        raw = make_tar(self.src / "demo-1.0.tar.gz", [("demo-1.0/a.c", None, "/etc/passwd")])
        lock, lst = self.pins(raw, [("demo-1.0/a.c", sha(GOOD))])
        self.assertEqual(self.run_main(lock, lst), 1)

    def test_listed_link_inside_the_tree_is_refused(self):
        raw = make_tar(self.src / "demo-1.0.tar.gz", [("demo-1.0/b.c", GOOD, None), ("demo-1.0/a.c", None, "b.c")])
        lock, lst = self.pins(raw, [("demo-1.0/a.c", sha(GOOD))])
        self.assertEqual(self.run_main(lock, lst), 1)

    def test_repeated_member_is_refused(self):
        raw = make_tar(self.src / "demo-1.0.tar.gz", [("demo-1.0/a.c", GOOD, None), ("demo-1.0/a.c", OTHER, None)])
        lock, lst = self.pins(raw, [("demo-1.0/a.c", sha(GOOD))])
        self.assertEqual(self.run_main(lock, lst), 1)

    def test_missing_listed_file_is_refused(self):
        raw = make_tar(self.src / "demo-1.0.tar.gz", [("demo-1.0/a.c", GOOD, None)])
        lock, lst = self.pins(raw, [("demo-1.0/a.c", sha(GOOD)), ("demo-1.0/b.c", sha(GOOD))])
        self.assertEqual(self.run_main(lock, lst), 1)

    def test_unsafe_listed_paths_are_refused(self):
        raw = make_tar(self.src / "demo-1.0.tar.gz", [("demo-1.0/a.c", GOOD, None)])
        for bad in ["../a.c", "/etc/a.c", "demo-1.0/./a.c", "demo-1.0//a.c", "C:\\a.c", "demo-1.0\\a.c", ".hidden/a"]:
            lock, lst = self.pins(raw, [(bad, sha(GOOD))])
            self.assertEqual(self.run_main(lock, lst), 1, bad)

    def test_poisoned_cache_is_replaced_by_a_checked_download(self):
        raw = make_tar(self.tmp / "good.tar.gz", [("demo-1.0/a.c", GOOD, None)])
        lock, lst = self.pins(raw, [("demo-1.0/a.c", sha(GOOD))])
        cache = self.tmp / "cache"
        cache.mkdir()
        poisoned = bytearray(raw)
        poisoned[-1] ^= 0xFF
        (cache / "demo-1.0.tar.gz").write_bytes(bytes(poisoned))
        a = fs.load_pins(lock, lst)[0]
        fetched = []

        def fake_download(arch, dest):
            fetched.append(arch.url)
            dest.write_bytes(raw)

        real = fs.download
        fs.download = fake_download
        try:
            p = fs.obtain(a, cache, None)
        finally:
            fs.download = real
        self.assertEqual(fetched, [a.url])
        self.assertEqual(p.read_bytes(), raw)

    def test_verify_only_refuses_an_extra_or_changed_file(self):
        raw = make_tar(self.src / "demo-1.0.tar.gz", [("demo-1.0/a.c", GOOD, None)])
        lock, lst = self.pins(raw, [("demo-1.0/a.c", sha(GOOD))])
        self.assertEqual(self.run_main(lock, lst), 0)
        self.assertEqual(self.run_main(lock, lst, "--verify-only"), 0)
        extra = self.out / "demo-1.0" / "demo-1.0" / "injected.h"
        extra.write_bytes(b"#define X 1\n")
        self.assertEqual(self.run_main(lock, lst, "--verify-only"), 1)
        extra.unlink()
        (self.out / "demo-1.0" / "demo-1.0" / "a.c").write_bytes(OTHER)
        self.assertEqual(self.run_main(lock, lst, "--verify-only"), 1)

    def test_7z_member_with_another_hash_is_refused(self):
        try:
            exe = fs.seven_zip()
        except fs.Refused:
            self.skipTest("the 7-Zip program is not installed")
        stage = self.tmp / "stage" / "C"
        stage.mkdir(parents=True)
        (stage / "a.c").write_bytes(OTHER)
        arch = self.src / "demo.7z"
        subprocess.run([exe, "a", "-bd", "-y", str(arch), "C"], cwd=stage.parent, check=True, capture_output=True)
        raw = arch.read_bytes()
        lock, lst = self.pins(raw, [("C/a.c", sha(GOOD))], name="demo.7z")
        self.assertEqual(self.run_main(lock, lst), 1)
        lock, lst = self.pins(raw, [("C/a.c", sha(OTHER))], name="demo.7z")
        self.assertEqual(self.run_main(lock, lst), 0)

    def test_the_real_pins_parse(self):
        archives = fs.load_pins(HERE.parent / "sources.lock", HERE.parent / "sources.files")
        self.assertEqual(sorted(a.name for a in archives), ["lzma-sdk", "zstd"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
