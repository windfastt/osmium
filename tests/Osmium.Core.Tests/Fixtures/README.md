# Test fixtures

The tests write their own archives wherever an in-process writer exists (System.IO.Compression, System.Formats.Tar,
SharpCompress, ZstdSharp). The files here are the exceptions: archives that nothing in-process can write (7z, xz,
encrypted ZIP, and ZIP with Deflate (64 KiB window), LZMA, PPMd or BZip2). Each one was made from the generated
content below with the command next to it, holds only that content, and is at most 4 KB. No file here comes from a
third party. `FixtureTests` reads every one of them, and CI makes each again from the same commands and compares the
listing and the content (encrypted archives differ byte for byte, because their salt and IV are random).

Tools: 7-Zip 23.01 (x64), XZ Utils 5.4.5, GNU tar 1.35, Zip 3.0 (Info-ZIP), run on Linux with `TZ=UTC`.

## Content

```sh
export TZ=UTC
mkdir -p in/sub/empty
seq 1 300 > in/sub/numbers.txt
for i in $(seq 1 40); do printf 'Hello from the Osmium test fixtures.\n'; done > in/hello.txt
: > in/empty.txt
(for i in $(seq 1 64); do printf '\350\001\002\000\000\220\220\220\001\000\000\353\000\360\001\370\001\000\000\224\001\000\000\220\110\000\000\001\100\000\000\001'; done) > code.bin
touch -d @0 in/hello.txt in/empty.txt in/sub/numbers.txt in/sub/empty in/sub in code.bin
```

`code.bin` repeats one 32-byte pattern that holds a branch instruction for each filter: an x86 CALL, an ARM BL, an
ARM Thumb BL, an ARM64 BL and ADRP, a PowerPC B and a SPARC CALL.

## 7z (`7z/`)

With `S="-bso0 -bsp0 -mtc=off -mta=off"`:

| File | Command |
|---|---|
| `lzma2-solid.7z` | `7z a $S -t7z -m0=LZMA2 -ms=on 7z/lzma2-solid.7z in` |
| `lzma.7z` | `7z a $S -t7z -m0=LZMA -ms=off 7z/lzma.7z in` |
| `ppmd.7z` | `7z a $S -t7z -m0=PPMd -ms=on 7z/ppmd.7z in` |
| `bzip2.7z` | `7z a $S -t7z -m0=BZip2 7z/bzip2.7z in` |
| `deflate.7z` | `7z a $S -t7z -m0=Deflate 7z/deflate.7z in` |
| `copy.7z` | `7z a $S -t7z -m0=Copy 7z/copy.7z in` |
| `bcj.7z` | `7z a $S -t7z -m0=BCJ -m1=LZMA2 7z/bcj.7z code.bin` |
| `arm.7z` | `7z a $S -t7z -m0=ARM -m1=LZMA2 7z/arm.7z code.bin` |
| `armt.7z` | `7z a $S -t7z -m0=ARMT -m1=LZMA2 7z/armt.7z code.bin` |
| `arm64.7z` | `7z a $S -t7z -m0=ARM64 -m1=LZMA2 7z/arm64.7z code.bin` |
| `ppc.7z` | `7z a $S -t7z -m0=PPC -m1=LZMA2 7z/ppc.7z code.bin` |
| `sparc.7z` | `7z a $S -t7z -m0=SPARC -m1=LZMA2 7z/sparc.7z code.bin` |
| `delta.7z` | `7z a $S -t7z -m0=Delta:4 -m1=LZMA2 7z/delta.7z in/sub/numbers.txt` |
| `bcj2.7z` | `7z a $S -t7z -m0=BCJ2 -m1=LZMA:d=64k -m2=LZMA:d=64k -m3=LZMA:d=64k -mb0:1 -mb0s1:2 -mb0s2:3 7z/bcj2.7z code.bin` |
| `aes.7z` | `7z a $S -t7z -m0=LZMA2 -mhe=off '-pPässwörd ü' 7z/aes.7z in` |
| `aes-headers.7z` | `7z a $S -t7z -m0=LZMA2 -mhe=on '-pPässwörd ü' 7z/aes-headers.7z in` |

## xz (`xz/`)

| File | Command |
|---|---|
| `crc64.xz` | `xz -c -9 in/hello.txt > xz/crc64.xz` |
| `crc32.xz` | `xz -c --check=crc32 in/hello.txt > xz/crc32.xz` |
| `sha256.xz` | `xz -c --check=sha256 in/hello.txt > xz/sha256.xz` |
| `none.xz` | `xz -c --check=none in/hello.txt > xz/none.xz` |
| `x86.xz` | `xz -c --x86 --lzma2 code.bin > xz/x86.xz` |
| `arm64.xz` | `xz -c --arm64 --lzma2 code.bin > xz/arm64.xz` |
| `delta.xz` | `xz -c --delta=dist=4 --lzma2 in/sub/numbers.txt > xz/delta.xz` |
| `multiblock.xz` | `xz -c --block-size=512 in/sub/numbers.txt > xz/multiblock.xz` |
| `concat.xz` | `(xz -c in/hello.txt; xz -c in/sub/numbers.txt) > xz/concat.xz` |
| `tree.tar.xz` | `tar --owner=0 --group=0 --numeric-owner --mtime=@0 --sort=name -cf - in \| xz -c > xz/tree.tar.xz` |

## ZIP (`zip/`)

With `S="-bso0 -bsp0 -tzip"`:

| File | Command |
|---|---|
| `deflate64.zip` | `7z a $S -mm=Deflate64 zip/deflate64.zip in` |
| `lzma.zip` | `7z a $S -mm=LZMA zip/lzma.zip in` |
| `ppmd.zip` | `7z a $S -mm=PPMd zip/ppmd.zip in` |
| `bzip2.zip` | `7z a $S -mm=BZip2 zip/bzip2.zip in` |
| `aes128.zip` | `7z a $S -mem=AES128 -psecret zip/aes128.zip in` |
| `aes192.zip` | `7z a $S -mem=AES192 -psecret zip/aes192.zip in` |
| `aes256.zip` | `7z a $S -mem=AES256 -psecret zip/aes256.zip in` |
| `zipcrypto.zip` | `7z a $S -mem=ZipCrypto -psecret zip/zipcrypto.zip in` |
| `zipcrypto-nonascii.zip` | `zip -q -X -r -P 'Pässwörd ü' zip/zipcrypto-nonascii.zip in` |
| `infozip.zip` | `zip -q -X -r zip/infozip.zip in` |

7-Zip refuses passwords outside ASCII for ZIP files, so the non-ASCII password cases are `aes.7z`, `aes-headers.7z`
(AES in 7z, UTF-16 password) and `zipcrypto-nonascii.zip` (Info-ZIP, UTF-8 password bytes). The AE-1 variant of
AES-encrypted ZIP is made in the tests by patching `aes256.zip`.
