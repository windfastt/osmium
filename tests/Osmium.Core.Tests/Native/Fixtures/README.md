# Encoder generation fixtures

`encoders-<generation>.osa` is a small archive (at most 64 KiB) written with one encoder generation's LZMA and
Zstandard encoders: `encoders-managed-1.osa` with the built-in encoders, `encoders-native-1.osa` with the native
library. Every build of Osmium, with or without the native library, must open, test and extract them to the inputs
that `GenerationFixtureTests` generates (`VectorInputs`, integer arithmetic only). They stay in the repository for as
long as Osmium reads `.osa`.

Each file of the archive has its own block, with the pipeline named in `GenerationFixtureTests.Files`:

| File | Pipeline | Input |
|---|---|---|
| `text-lzma-bt4.txt` | `lzma(dict=1M)` | 20 KiB of text |
| `text-lzma-bt2-lc0-pb0.txt` | `lzma(dict=1M,lc=0,pb=0,fb=32,mf=0)` | 12 KiB of text |
| `records-lzma-lp2.dat` | `lzma(dict=1M,lc=0,lp=2,pb=2)` | 32 KiB of 32-byte records |
| `records-lzma-lc8-lp4.dat` | `lzma(dict=1M,lc=8,lp=4,pb=4)` | 16 KiB of records |
| `mixed-lzma-lc4.bin` | `lzma(dict=1M,lc=4,pb=0)` | 12 KiB of mixed data |
| `repeated-lzma-1m.txt` | `lzma(dict=4M)` | 1 MiB + 333 bytes of a repeated text piece (large enough for the helper thread) |
| `text-zstd3.txt` | `zstd(level=3)` | 20 KiB of text |
| `text-zstd19.txt` | `zstd(level=19)` | 20 KiB of text |
| `repeated-zstd19-ldm.txt` | `zstd(level=19,ldm=1)` | 1 MiB of a repeated text piece |
| `records-zstd22.dat` | `zstd(level=22)` | 16 KiB of records |

A fixture is written once, by the test itself, in a process of that generation:

```
OSMIUM_ENCODERS=managed OSMIUM_REGENERATE_FIXTURES=managed-1 dotnet test -c Release --project tests/Osmium.Core.Tests --filter-class Osmium.Core.Tests.Native.GenerationFixtureTests
OSMIUM_ENCODERS=native OSMIUM_REGENERATE_FIXTURES=native-1 dotnet test -c Release --project tests/Osmium.Core.Tests --filter-class Osmium.Core.Tests.Native.GenerationFixtureTests
```

The fixture of a generation marked shipped in `EncoderGeneration.cs` is never rewritten; a new generation adds its
own fixture.
