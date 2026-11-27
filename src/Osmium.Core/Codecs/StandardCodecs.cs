using System.IO.Compression;
using System.Runtime.InteropServices;
using Osmium.Core.Pipeline;
using SharpCompress.Compressors;
using CompressionMode = System.IO.Compression.CompressionMode;
using SharpMode = SharpCompress.Compressors.CompressionMode;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.PPMd;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Osmium.Core.Codecs;

internal static class CodecIo
{
    public const int Chunk = 1 << 20;

    public static MemoryStream ReadOnlyStream(ReadOnlyMemory<byte> m) =>
        MemoryMarshal.TryGetArray(m, out var seg)
            ? new MemoryStream(seg.Array!, seg.Offset, seg.Count, writable: false)
            : new MemoryStream(m.ToArray(), writable: false);

    public static byte[] ToExactArray(MemoryStream ms) =>
        ms.GetBuffer().Length == ms.Length ? ms.GetBuffer() : ms.ToArray();

    /// <summary>Reads exactly <paramref name="n"/> bytes from a decoding stream into a new array.</summary>
    public static byte[] ReadExact(Stream s, int n, CodecContext ctx)
    {
        var buf = GC.AllocateUninitializedArray<byte>(n);
        int total = 0;
        while (total < n)
        {
            int r = s.Read(buf, total, Math.Min(Chunk, n - total));
            if (r <= 0) throw new InvalidDataException($"Compressed stream ended early ({total} of {n} bytes).");
            total += r;
            ctx.Checkpoint(total);
        }
        return buf;
    }

    public static int Log2Ceil(long v)
    {
        int b = 0;
        while ((1L << b) < v) b++;
        return b;
    }
}

public sealed class StoreCodec : MethodBase
{
    public override int Id => MethodIds.Store;
    public override string Name => "store";
    public override string DisplayName => "Store";
    public override MethodKind Kind => MethodKind.Codec;
    public override string Description => "No compression. Used for already-compressed data (JPEG, MP4, ZIP…).";
    public override double EncodeSpeed(ParamValues p) => 2000;
    public override double DecodeSpeed(ParamValues p) => 2000;
    public override long EncodeMemory(ParamValues p, long len) => len;
    public override long DecodeMemory(ParamValues p, long len) => len;

    public override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx)
    {
        ctx.Checkpoint(input.Length);
        return input.ToArray();
    }

    public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx)
    {
        if (input.Length != outputLength) throw new InvalidDataException("Stored block has the wrong length.");
        ctx.Checkpoint(outputLength);
        return input.ToArray();
    }
}

public sealed class DeflateCodec : MethodBase
{
    private static readonly ParamDef[] Defs =
    [
        new("level", "Level", ParamType.Int, 1, 9, 9, "zlib compression level.", [1, 6, 9]),
    ];
    public override int Id => MethodIds.Deflate;
    public override string Name => "deflate";
    public override string DisplayName => "Deflate";
    public override MethodKind Kind => MethodKind.Codec;
    public override string Description => "Classic ZIP/gzip algorithm. Included as a baseline; rarely the smallest.";
    public override bool Deterministic => false; // native zlib-ng: version-dependent output
    public override IReadOnlyList<ParamDef> Params => Defs;
    public override double EncodeSpeed(ParamValues p) => 20;
    public override double DecodeSpeed(ParamValues p) => 300;

    public override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx)
    {
        var ms = new MemoryStream(Math.Max(256, input.Length / 2));
        using (var ds = new DeflateStream(ms, new ZLibCompressionOptions { CompressionLevel = (int)Get(p, "level") }, leaveOpen: true))
            Chunked(input.Span, ctx, CodecIo.Chunk, ds.Write);
        return CodecIo.ToExactArray(ms);
    }

    public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx)
    {
        using var ds = new DeflateStream(CodecIo.ReadOnlyStream(input), CompressionMode.Decompress);
        return CodecIo.ReadExact(ds, outputLength, ctx);
    }
}

public sealed class BrotliCodec : MethodBase
{
    private static readonly ParamDef[] Defs =
    [
        new("quality", "Quality", ParamType.Int, 0, 11, 11, "Brotli quality; 10–11 are dramatically slower but smaller.", [9, 10, 11]),
        new("window", "Window (log2)", ParamType.Int, 10, 24, 24, "Sliding window size as a power of two.", [20, 22, 24]),
    ];
    public override int Id => MethodIds.Brotli;
    public override string Name => "brotli";
    public override string DisplayName => "Brotli";
    public override MethodKind Kind => MethodKind.Codec;
    public override string Description => "Strong on web text thanks to its built-in dictionary; extremely fast to decode.";
    public override bool Deterministic => false; // native library
    public override IReadOnlyList<ParamDef> Params => Defs;
    public override double EncodeSpeed(ParamValues p) => Get(p, "quality") >= 10 ? 1 : 20;
    public override double DecodeSpeed(ParamValues p) => 300;
    public override long EncodeMemory(ParamValues p, long len) => (10L << (int)Get(p, "window")) + len * 2;
    public override long DecodeMemory(ParamValues p, long len) => (1L << (int)Get(p, "window")) + len;

    public override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx)
    {
        using var enc = new BrotliEncoder((int)Get(p, "quality"), (int)Get(p, "window"));
        var outBuf = GC.AllocateUninitializedArray<byte>(BrotliEncoder.GetMaxCompressedLength(Math.Max(1, input.Length)));
        int written = 0, pos = 0;
        var src = input.Span;
        while (true)
        {
            bool final = pos >= src.Length;
            var chunk = final ? ReadOnlySpan<byte>.Empty : src.Slice(pos, Math.Min(CodecIo.Chunk, src.Length - pos));
            var status = enc.Compress(chunk, outBuf.AsSpan(written), out int consumed, out int w, isFinalBlock: final);
            pos += consumed;
            written += w;
            if (status == System.Buffers.OperationStatus.InvalidData) throw new InvalidOperationException("Brotli encoder error.");
            if (status == System.Buffers.OperationStatus.DestinationTooSmall)
            {
                Array.Resize(ref outBuf, outBuf.Length * 2);
                continue;
            }
            ctx.Checkpoint(pos);
            if (final && status == System.Buffers.OperationStatus.Done) break;
        }
        return outBuf.AsSpan(0, written).ToArray();
    }

    public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx)
    {
        var outBuf = GC.AllocateUninitializedArray<byte>(outputLength);
        using var dec = new BrotliDecoder();
        // An empty output buffer makes the decoder report DestinationTooSmall even for a valid empty stream,
        // so give it a 1-byte scratch buffer in that case and require that nothing is written.
        Span<byte> dst = outputLength == 0 ? stackalloc byte[1] : outBuf;
        var status = dec.Decompress(input.Span, dst, out int consumed, out int written);
        if (status != System.Buffers.OperationStatus.Done || written != outputLength)
            throw new InvalidDataException($"Brotli stream is corrupt ({status}, {written}/{outputLength} bytes).");
        ctx.Checkpoint(outputLength);
        return outBuf;
    }
}

public sealed class ZstdCodec : MethodBase
{
    private static readonly ParamDef[] Defs =
    [
        new("level", "Level", ParamType.Int, 1, 22, 19, "Compression level; 20–22 are 'ultra' levels.", [3, 9, 15, 19, 21, 22]),
        new("wlog", "Window (log2)", ParamType.Int, 0, 31, 0, "Match window as a power of two; 0 = automatic (fits the block, 20–27).", [0, 24, 27, 30]),
        new("ldm", "Long-distance matching", ParamType.Bool, 0, 1, 0, "Finds repeats far apart (large backups, VM images)."),
        new("ldmmin", "LDM minimum match", ParamType.Int, 0, 4096, 0, "0 = default (64). Smaller finds more but costs speed.", Experimental: true),
        new("strategy", "Strategy", ParamType.Int, 0, 9, 0, "0 = level default; 9 = btultra2.", Experimental: true),
    ];
    public override int Id => MethodIds.Zstd;
    public override string Name => "zstd";
    public override string DisplayName => "Zstandard";
    public override MethodKind Kind => MethodKind.Codec;
    public override string Description => "Very fast decoding with a configurable long window; good all-rounder.";
    public override IReadOnlyList<ParamDef> Params => Defs;

    public override double EncodeSpeed(ParamValues p)
    {
        long l = Get(p, "level");
        return l <= 3 ? 150 : l <= 9 ? 40 : l <= 15 ? 12 : l <= 19 ? 4 : 2;
    }

    public override double DecodeSpeed(ParamValues p) => 500;

    /// <summary>Window used by BOTH encoder and decoder: the requested (or default 27) window, never larger than the
    /// block. The decoder caps <c>windowLogMax</c> at the same value, so a hostile frame cannot demand a 2 GiB window.</summary>
    internal static int Wlog(ParamValues p, long len, long wl) =>
        Math.Clamp(Math.Min(wl > 0 ? (int)wl : 27, CodecIo.Log2Ceil(Math.Max(1, len))), 10, 31);

    /// <summary>zstd's hash and chain table sizes (log2) and strategy for levels 1–22, from its table for inputs of
    /// unknown size or over 256 KiB.</summary>
    private static readonly (byte Hash, byte Chain, byte Strategy)[] LevelTables =
    [
        (14, 13, 1), (16, 15, 1), (17, 16, 2), (18, 18, 2), (19, 18, 3), (19, 18, 4), (20, 19, 4), (20, 19, 5),
        (21, 20, 5), (22, 21, 5), (22, 21, 5), (23, 22, 5), (22, 22, 6), (23, 22, 6), (23, 23, 6), (22, 22, 7),
        (22, 23, 7), (22, 23, 8), (22, 24, 9), (23, 25, 9), (24, 26, 9), (25, 27, 9),
    ];

    /// <summary>
    /// What encoding <paramref name="len"/> bytes takes besides the input. The encoder is not told the input's size,
    /// so zstd sizes its hash and chain tables by the level alone, whatever the window (level 22 takes 640 MiB even for
    /// a small sample); a long-distance table joins them when asked for, and on its own for the optimal strategies
    /// (levels 16–22) once the window reaches 128 MiB. Then the encoder's copy of the window, the output while it grows
    /// (twice the input at worst), an eighth more than the tables, and 16 MiB for the rest.
    /// </summary>
    public override long EncodeMemory(ParamValues p, long len)
    {
        int w = Wlog(p, len, Get(p, "wlog"));
        var (hash, chain, strategy) = LevelTables[Math.Clamp((int)Get(p, "level"), 1, LevelTables.Length) - 1];
        long chosen = Get(p, "strategy");
        if (chosen > 0) strategy = (byte)chosen;
        // The hash and chain tables, the row match finder's tags and the optimal parser's state.
        long tables = (4L << hash) + (4L << chain) + (1L << hash) + (1L << 20);
        if (Get(p, "ldm") != 0 || strategy >= 7 && w >= 27) tables += 1L << Math.Max(20, w - 1);
        long window = (1L << w) + (128L << 10);
        return tables + tables / 8 + window + 2 * len + (16L << 20);
    }

    public override long DecodeMemory(ParamValues p, long len) => Math.Min(1L << Wlog(p, len, Get(p, "wlog")), len) + len + (8L << 20);

    public override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx)
    {
        var ms = new MemoryStream(Math.Max(256, input.Length / 3));
        using (var cs = new CompressionStream(ms, (int)Get(p, "level"), leaveOpen: true))
        {
            cs.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, Wlog(p, input.Length, Get(p, "wlog")));
            if (Get(p, "ldm") != 0)
            {
                cs.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
                long mm = Get(p, "ldmmin");
                if (mm > 0) cs.SetParameter(ZSTD_cParameter.ZSTD_c_ldmMinMatch, (int)Math.Clamp(mm, 4, 4096));
            }
            long st = Get(p, "strategy");
            if (st > 0) cs.SetParameter(ZSTD_cParameter.ZSTD_c_strategy, (int)st);
            cs.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 0);
            Chunked(input.Span, ctx, CodecIo.Chunk, cs.Write);
        }
        return CodecIo.ToExactArray(ms);
    }

    public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx)
    {
        using var d = new Decompressor();
        d.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, Wlog(p, outputLength, Get(p, "wlog")));
        var outBuf = GC.AllocateUninitializedArray<byte>(outputLength);
        int n;
        try { n = d.Unwrap(input.Span, outBuf); }
        catch (ZstdException e) { throw new InvalidDataException("Zstd stream is corrupt: " + e.Message, e); }
        if (n != outputLength) throw new InvalidDataException($"Zstd produced {n} bytes, expected {outputLength}.");
        ctx.Checkpoint(outputLength);
        return outBuf;
    }
}

public sealed class LzmaCodec : MethodBase
{
    private static readonly long[] DictSteps = [1 << 20, 4 << 20, 16 << 20, 64 << 20, 128 << 20, 256 << 20];

    private static readonly ParamDef[] Defs =
    [
        new("dict", "Dictionary", ParamType.Size, 1 << 16, 1 << 29, 64 << 20,
            "Match window. Automatically shrunk to the block size. Encoder needs ~11.5× this in RAM (bt4).", DictSteps, Tunable: false),
        new("lc", "Literal context bits", ParamType.Int, 0, 8, 3, "High bits of the previous byte used to predict literals. 0 suits binaries, 3–4 text.", [0, 1, 2, 3, 4, 8]),
        new("lp", "Literal position bits", ParamType.Int, 0, 4, 0, "Alignment of literals. 2 suits 32-bit records.", [0, 1, 2]),
        new("pb", "Position bits", ParamType.Int, 0, 4, 2, "Alignment of matches. 0 suits text, 2 binaries.", [0, 1, 2]),
        new("fb", "Fast bytes", ParamType.Int, 5, 273, 273, "Longer is slower and usually smaller.", [32, 64, 128, 273]),
        new("mf", "Match finder", ParamType.Choice, 0, 1, 1, "bt2 (less RAM) or bt4 (better).", [0, 1], ["bt2", "bt4"]),
    ];

    public override int Id => MethodIds.Lzma;
    public override string Name => "lzma";
    public override string DisplayName => "LZMA";
    public override MethodKind Kind => MethodKind.Codec;
    public override string Description => "The 7-Zip algorithm. Excellent ratio with fast decoding; lc/lp/pb tune it to the data.";
    public override IReadOnlyList<ParamDef> Params => Defs;
    public override double EncodeSpeed(ParamValues p) => 1.5;
    public override double DecodeSpeed(ParamValues p) => 40;

    internal static int EffectiveDict(long dict, long len) =>
        (int)Math.Max(1 << 16, Math.Min(dict, 1L << Math.Max(16, CodecIo.Log2Ceil(Math.Max(1, len)))));

    public override long EncodeMemory(ParamValues p, long len)
    {
        long d = EffectiveDict(Get(p, "dict"), len);
        return (long)(d * (Get(p, "mf") == 0 ? 9.5 : 11.5)) + len * 2 + (32L << 20);
    }

    public override long DecodeMemory(ParamValues p, long len) => EffectiveDict(Get(p, "dict"), len) + len + (8L << 20);

    private static byte[] Props(int lc, int lp, int pb, int dict)
    {
        var b = new byte[5];
        b[0] = (byte)((pb * 5 + lp) * 9 + lc);
        BitConverter.TryWriteBytes(b.AsSpan(1), dict);
        return b;
    }

    private sealed class Progress(CodecContext ctx) : SevenZip.ICodeProgress
    {
        public void SetProgress(long inSize, long outSize) => ctx.Checkpoint(inSize);
    }

    public override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx)
    {
        int dict = EffectiveDict(Get(p, "dict"), input.Length);
        var enc = new SevenZip.Compression.LZMA.Encoder();
        enc.SetCoderProperties(
        [
            SevenZip.CoderPropID.DictionarySize, SevenZip.CoderPropID.PosStateBits, SevenZip.CoderPropID.LitContextBits,
            SevenZip.CoderPropID.LitPosBits, SevenZip.CoderPropID.Algorithm, SevenZip.CoderPropID.NumFastBytes,
            SevenZip.CoderPropID.MatchFinder, SevenZip.CoderPropID.EndMarker,
        ],
        [
            dict, (int)Get(p, "pb"), (int)Get(p, "lc"), (int)Get(p, "lp"), 2, (int)Get(p, "fb"),
            Get(p, "mf") == 0 ? "bt2" : "bt4", false,
        ]);
        var ms = new MemoryStream(Math.Max(256, input.Length / 3));
        enc.Code(CodecIo.ReadOnlyStream(input), ms, input.Length, -1, new Progress(ctx));
        ctx.Checkpoint(input.Length);
        return CodecIo.ToExactArray(ms);
    }

    public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx)
    {
        int dict = EffectiveDict(Get(p, "dict"), outputLength);
        var dec = new SevenZip.Compression.LZMA.Decoder();
        dec.SetDecoderProperties(Props((int)Get(p, "lc"), (int)Get(p, "lp"), (int)Get(p, "pb"), dict));
        var outBuf = GC.AllocateUninitializedArray<byte>(outputLength);
        var os = new MemoryStream(outBuf, 0, outputLength, writable: true);
        try
        {
            dec.Code(CodecIo.ReadOnlyStream(input), os, input.Length, outputLength, new Progress(ctx));
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new InvalidDataException("LZMA stream is corrupt.", e);
        }
        if (os.Position != outputLength) throw new InvalidDataException("LZMA stream ended early.");
        return outBuf;
    }
}

public sealed class BZip2Codec : MethodBase
{
    public override int Id => MethodIds.BZip2;
    public override string Name => "bzip2";
    public override string DisplayName => "BZip2";
    public override MethodKind Kind => MethodKind.Codec;
    public override string Description => "Burrows–Wheeler block sorting (900 KB blocks). Occasionally wins on text-like data.";
    public override double EncodeSpeed(ParamValues p) => 8;
    public override double DecodeSpeed(ParamValues p) => 25;
    public override long EncodeMemory(ParamValues p, long len) => len * 2 + (16L << 20);
    public override long DecodeMemory(ParamValues p, long len) => len + (8L << 20);

    public override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx)
    {
        var ms = new MemoryStream(Math.Max(256, input.Length / 3));
        using (var bz = new BZip2Stream(new NonClosing(ms), SharpMode.Compress, false))
            Chunked(input.Span, ctx, CodecIo.Chunk, bz.Write);
        return CodecIo.ToExactArray(ms);
    }

    public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx)
    {
        try
        {
            using var bz = new BZip2Stream(CodecIo.ReadOnlyStream(input), SharpMode.Decompress, false);
            return CodecIo.ReadExact(bz, outputLength, ctx);
        }
        catch (Exception e) when (e is not InvalidDataException and not OperationCanceledException)
        {
            throw new InvalidDataException("BZip2 stream is corrupt.", e);
        }
    }
}

public sealed class PpmdCodec : MethodBase
{
    private static readonly ParamDef[] Defs =
    [
        new("order", "Model order", ParamType.Int, 2, 16, 8, "Context length. 6–8 for general text, up to 16 for very repetitive text.", [4, 6, 8, 10, 12, 16]),
        new("mem", "Model memory", ParamType.Size, 1 << 20, 256 << 20, 64 << 20, "Memory for the context tree (same for compression and decompression).", [16 << 20, 64 << 20, 256 << 20], Tunable: false),
    ];
    public override int Id => MethodIds.Ppmd;
    public override string Name => "ppmd";
    public override string DisplayName => "PPMd (var. I)";
    public override MethodKind Kind => MethodKind.Codec;
    public override string Description => "Prediction by partial matching. Excellent on text; decoding is as slow as encoding.";
    public override IReadOnlyList<ParamDef> Params => Defs;
    public override double EncodeSpeed(ParamValues p) => 10;
    public override double DecodeSpeed(ParamValues p) => 10;
    public override long EncodeMemory(ParamValues p, long len) => Get(p, "mem") + len * 2 + (4L << 20);
    public override long DecodeMemory(ParamValues p, long len) => Get(p, "mem") + len + (4L << 20);

    private PpmdProperties Props(ParamValues p) => new((int)Get(p, "mem"), (int)Get(p, "order"));

    public override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx)
    {
        var ms = new MemoryStream(Math.Max(256, input.Length / 3));
        using (var s = new PpmdStream(Props(p), new NonClosing(ms), true))
            Chunked(input.Span, ctx, CodecIo.Chunk, s.Write);
        return CodecIo.ToExactArray(ms);
    }

    public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx)
    {
        try
        {
            using var s = new PpmdStream(Props(p), CodecIo.ReadOnlyStream(input), false);
            return CodecIo.ReadExact(s, outputLength, ctx);
        }
        catch (Exception e) when (e is not InvalidDataException and not OperationCanceledException)
        {
            throw new InvalidDataException("PPMd stream is corrupt.", e);
        }
    }
}

/// <summary>Wraps a stream so that disposing a compressor does not close the underlying MemoryStream.</summary>
internal sealed class NonClosing(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    protected override void Dispose(bool disposing) => Flush();
}
