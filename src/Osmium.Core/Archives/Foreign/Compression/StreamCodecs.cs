using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using Osmium.Core.Pipeline;
using SharpCompress.Compressors.BZip2;

namespace Osmium.Core.Archives.Foreign.Compression;

/// <summary>The compression around a TAR or a single file.</summary>
internal enum StreamCompression { None, GZip, BZip2, Xz, Zstd }

/// <summary>What a pass over a compressed file learned without decoding it.</summary>
/// <param name="ExactSize">The decoded size when the format states it exactly (the xz index, every zstd frame's
/// content size); null otherwise (the gzip ISIZE field is modulo 2^32 and not trusted).</param>
/// <param name="Unchecked">Part of the data has no check Osmium verifies (zstd frames without a checksum, xz blocks
/// with check type None).</param>
internal sealed record StreamLayout(long? ExactSize, bool Unchecked, string Detail);

/// <summary>Opens decoders for the single-stream formats, each one decoding every member, stream or frame and
/// refusing data after the last one.</summary>
internal static class StreamCodecs
{
    public static string Name(StreamCompression c) => c switch
    {
        StreamCompression.GZip => "gzip",
        StreamCompression.BZip2 => "bzip2",
        StreamCompression.Xz => "xz",
        StreamCompression.Zstd => "Zstandard",
        _ => "none",
    };

    /// <summary>Reads what can be known without decoding (and runs the memory pre-checks). <paramref name="input"/>
    /// is seekable and covers exactly the compressed data.</summary>
    public static StreamLayout Inspect(StreamCompression c, Stream input, ImportLimits limits, string name)
    {
        switch (c)
        {
            case StreamCompression.Xz:
                using (var xz = new XzDecoder(input, limits, name))
                    return new StreamLayout(xz.UncompressedSize, xz.Unchecked, xz.StreamCount == 1 ? "1 stream" : $"{xz.StreamCount} streams");
            case StreamCompression.Zstd:
                var z = ZstdFrames.Walk(input, limits, name);
                return new StreamLayout(z.ContentSize, !z.AllChecksummed, z.Frames == 1 ? "1 frame" : $"{z.Frames} frames");
            default:
                return new StreamLayout(null, false, "");
        }
    }

    /// <summary>A decoder over <paramref name="input"/> (seekable, exactly the compressed data, owned by the result).</summary>
    public static Stream Open(StreamCompression c, Stream input, ImportLimits limits, string name)
    {
        input.Position = 0;
        return c switch
        {
            StreamCompression.None => input,
            StreamCompression.GZip => new GZipChecked(input),
            StreamCompression.BZip2 => new BZip2Stream(input, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: true),
            StreamCompression.Xz => new XzDecoder(input, limits, name),
            StreamCompression.Zstd => ZstdFrames.Open(input, ZstdFrames.Walk(input, limits, name), limits),
            _ => throw new ArgumentOutOfRangeException(nameof(c)),
        };
    }

    /// <summary>The first <paramref name="count"/> decoded bytes (format detection). Memory pre-checks apply; the
    /// declared dictionary or window is never allocated before them.</summary>
    public static byte[] Peek(StreamCompression c, Stream input, int count, ImportLimits limits, string name)
    {
        if (c == StreamCompression.Xz) return XzDecoder.Peek(input, count, limits, name);
        input.Position = 0;
        Stream s = c switch
        {
            StreamCompression.GZip => new GZipStream(input, CompressionMode.Decompress, leaveOpen: true),
            StreamCompression.BZip2 => new BZip2Stream(new NonClosing(input), SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: false),
            StreamCompression.Zstd => OpenZstdPeek(input, limits, name),
            _ => new NonClosing(input),
        };
        using (s)
        {
            var buf = new byte[count];
            int n = s.ReadAtLeast(buf, count, throwOnEndOfStream: false);
            return buf[..n];
        }
    }

    private static Stream OpenZstdPeek(Stream input, ImportLimits limits, string name)
    {
        // Detection never needs a large window: frames that declare more than 128 MiB are not sniffed further.
        var first = ZstdFrames.FirstFrame(input, limits with { MemoryBudgetBytes = Math.Min(limits.MemoryBudgetBytes, 128L << 20) }, name);
        input.Position = 0;
        return ZstdFrames.Open(new NonClosing(input), first, limits);
    }

    private sealed class NonClosing(Stream inner) : ReadOnlyStream
    {
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
    }
}

/// <summary>
/// gzip through the BCL decoder (which checks each member's CRC-32 and size and decodes concatenated members), made
/// strict where the BCL is lenient: it returns a short result without error for a truncated file and ignores data
/// after the last member. An extra member with a random payload is fed after the real data; the output must end
/// with exactly that payload, which happens only when the real data ended cleanly at a member boundary and nothing
/// else followed (the BCL stops at the first bytes that do not start a member, which then hides the extra member).
/// </summary>
internal sealed class GZipChecked : ReadOnlyStream
{
    private const int PayloadLength = 16;
    private readonly GZipStream _gzip;
    private readonly byte[] _payload = RandomNumberGenerator.GetBytes(PayloadLength);
    private readonly byte[] _buf = new byte[(1 << 16) + PayloadLength];
    private int _start, _count;
    private bool _done;

    public GZipChecked(Stream input) => _gzip = new GZipStream(new Appended(input, Member(_payload)), CompressionMode.Decompress);

    /// <summary>A complete gzip member holding <paramref name="payload"/> in a stored deflate block.</summary>
    private static byte[] Member(byte[] payload)
    {
        var m = new byte[10 + 5 + payload.Length + 8];
        m[0] = 0x1F; m[1] = 0x8B; m[2] = 8; m[9] = 255;
        m[10] = 1; // final stored block
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(11), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(13), (ushort)~payload.Length);
        payload.CopyTo(m, 15);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(15 + payload.Length), Crc32.HashToUInt32(payload));
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(19 + payload.Length), (uint)payload.Length);
        return m;
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        while (true)
        {
            // The last PayloadLength bytes decoded so far may be the extra member's: never hand them out.
            int safe = _count - PayloadLength;
            if (safe > 0)
            {
                int k = Math.Min(safe, buffer.Length);
                _buf.AsSpan(_start, k).CopyTo(buffer);
                _start += k;
                _count -= k;
                return k;
            }
            if (_done) return 0;
            Buffer.BlockCopy(_buf, _start, _buf, 0, _count);
            _start = 0;
            int n = _gzip.Read(_buf, _count, _buf.Length - _count);
            if (n > 0) { _count += n; continue; }
            _done = true;
            if (_count != PayloadLength || !_buf.AsSpan(0, PayloadLength).SequenceEqual(_payload))
                throw new CorruptDataException("damaged data (the compressed stream is truncated or followed by unexpected data)");
            _count = 0;
            return 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _gzip.Dispose();
        base.Dispose(disposing);
    }

    /// <summary><paramref name="first"/>, then <paramref name="tail"/>.</summary>
    private sealed class Appended(Stream first, byte[] tail) : ReadOnlyStream
    {
        private int _tailPos = -1;

        public override int Read(Span<byte> buffer)
        {
            if (_tailPos < 0)
            {
                int n = first.Read(buffer);
                if (n > 0) return n;
                _tailPos = 0;
            }
            int k = Math.Min(buffer.Length, tail.Length - _tailPos);
            tail.AsSpan(_tailPos, k).CopyTo(buffer);
            _tailPos += k;
            return k;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) first.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>Zstandard frames: a walk over the frame and block headers (no decoding) for the window sizes, content
/// sizes and checksum flags of every frame, and the decoder set up within the memory budget.</summary>
internal static class ZstdFrames
{
    public sealed record Layout(int Frames, long? ContentSize, bool AllChecksummed, long MaxWindow);

    private const uint FrameMagic = 0xFD2FB528;

    public static Layout Walk(Stream input, ImportLimits limits, string name) => Walk(input, limits, name, firstOnly: false);

    public static Layout FirstFrame(Stream input, ImportLimits limits, string name) => Walk(input, limits, name, firstOnly: true);

    private static Layout Walk(Stream input, ImportLimits limits, string name, bool firstOnly)
    {
        long len = input.Length, pos = 0;
        int frames = 0;
        long? content = 0;
        bool allChecked = true;
        long maxWindow = 0;
        Span<byte> b = stackalloc byte[18];
        while (pos < len)
        {
            if (len - pos < 4) throw Trailing(frames);
            Read(input, pos, b[..4]);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(b);
            if ((magic & 0xFFFFFFF0) == 0x184D2A50)
            {
                if (len - pos < 8) throw Corrupt("damaged skippable frame");
                Read(input, pos + 4, b[..4]);
                pos += 8 + BinaryPrimitives.ReadUInt32LittleEndian(b);
                if (pos > len) throw Corrupt("damaged skippable frame");
                continue;
            }
            if (magic != FrameMagic) throw Trailing(frames);
            pos += 4;
            if (pos >= len) throw Corrupt("the stream is truncated");
            Read(input, pos, b[..1]);
            byte fhd = b[0];
            int fcsFlag = fhd >> 6;
            bool single = (fhd & 0x20) != 0, checksum = (fhd & 4) != 0;
            if ((fhd & 8) != 0) throw Corrupt("damaged frame header");
            int dictBytes = (fhd & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
            int fcsBytes = fcsFlag switch { 0 => single ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
            int headerLen = 1 + (single ? 0 : 1) + dictBytes + fcsBytes;
            if (pos + headerLen > len) throw Corrupt("the stream is truncated");
            Read(input, pos, b[..headerLen]);
            int p = 1;
            long window = 0;
            if (!single)
            {
                byte wd = b[p++];
                int windowLog = 10 + (wd >> 3);
                long windowBase = 1L << windowLog;
                window = windowBase + (windowBase / 8) * (wd & 7);
            }
            long dictId = 0;
            for (int i = 0; i < dictBytes; i++) dictId |= (long)b[p++] << (8 * i);
            if (dictId != 0) throw new NotSupportedException("This Zstandard file needs a dictionary, which Osmium does not have.");
            long? fcs = null;
            if (fcsBytes > 0)
            {
                ulong v = 0;
                for (int i = 0; i < fcsBytes; i++) v |= (ulong)b[p + i] << (8 * i);
                if (fcsBytes == 2) v += 256;
                fcs = v > long.MaxValue ? long.MaxValue : (long)v;
            }
            if (single) window = fcs ?? 0;
            maxWindow = Math.Max(maxWindow, window);
            LimitGuard.CheckMemory(name, LimitGuard.Add(Math.Min(window, fcs ?? window), 2L << 20), limits);
            content = content is { } c && fcs is { } f ? LimitGuard.Add(c, f) : null;
            allChecked &= checksum;
            frames++;
            pos += headerLen;
            if (firstOnly) break;
            // Blocks: a 3-byte header each, then the block's bytes (one byte for RLE).
            while (true)
            {
                if (pos + 3 > len) throw Corrupt("the stream is truncated");
                Read(input, pos, b[..3]);
                uint bh = (uint)(b[0] | b[1] << 8 | b[2] << 16);
                pos += 3;
                int type = (int)((bh >> 1) & 3);
                long size = bh >> 3;
                pos += type switch { 0 or 2 => size, 1 => 1, _ => throw Corrupt("damaged block") };
                if (pos > len) throw Corrupt("the stream is truncated");
                if ((bh & 1) != 0) break;
            }
            if (checksum) pos += 4;
            if (pos > len) throw Corrupt("the stream is truncated");
        }
        if (frames == 0) throw Corrupt("no Zstandard frame");
        return new Layout(frames, content, allChecked, maxWindow);
    }

    private static void Read(Stream s, long pos, Span<byte> dest)
    {
        s.Position = pos;
        s.ReadExactly(dest);
    }

    private static CorruptDataException Corrupt(string why) => new($"damaged data ({why})");

    private static CorruptDataException Trailing(int frames) =>
        Corrupt(frames == 0 ? "not a Zstandard stream" : "unexpected data after the end of the compressed stream");

    /// <summary>The decoder, allowed the window the frames need (the walk checked it against the budget).</summary>
    /// <param name="walked">The frames were walked, so <paramref name="layout"/> holds the largest window they declare,
    /// already checked against the budget. Otherwise (data inside encryption cannot be walked) the decoder is allowed
    /// only the largest power-of-two window within the budget, and refuses frames that need more.</param>
    public static Stream Open(Stream input, Layout layout, ImportLimits limits, bool walked = true)
    {
        if (input.CanSeek) input.Position = 0;
        var d = new ZstdSharp.DecompressionStream(input, 1 << 17, checkEndOfStream: true, leaveOpen: false);
        int log = 10;
        if (walked)
            while (log < 31 && (1L << log) < layout.MaxWindow) log++;
        else
        {
            long cap = Math.Min(limits.MemoryBudgetBytes, 1L << 31);
            while (log < 31 && (1L << (log + 1)) <= cap) log++;
        }
        d.SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, log);
        return d;
    }
}
