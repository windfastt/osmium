# Third-party components

Osmium bundles these managed libraries (all used unmodified from NuGet):

| Component | Use | License |
|---|---|---|
| [ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp) 0.8.8 | Zstandard codec (managed port of zstd) | MIT (zstd itself: BSD) |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) 1.0.0 | BZip2 and PPMd (variant I) codecs | MIT |
| [LZMA-SDK](https://www.nuget.org/packages/LZMA-SDK) 22.1.1 | LZMA codec (C# port of the 7-Zip LZMA SDK) | Public domain |
| System.IO.Hashing 10.0 | XxHash3 / XxHash128 / CRC-32 | MIT (.NET Foundation) |
| .NET 10 runtime + Windows Desktop runtime | Runtime, Brotli, Deflate, AES-GCM, ECDH, HKDF | MIT (.NET Foundation) |

The Osmium CM context-mixing codec, all filters, the `.osa` container, planner and cluster protocol are original code
in this repository (MIT, see LICENSE). CM is in the PAQ/lpaq family of techniques but contains no PAQ code.
