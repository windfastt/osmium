using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using Osmium.Core.Archives.Foreign.Filters;
using Osmium.Core.Pipeline;
using SharpCompress.Compressors.LZMA;

namespace Osmium.Core.Archives.Foreign.Compression;

/// <summary>CRC-64 as xz uses it: the reflected ECMA-182 polynomial (0xC96C5795D7870F42 reflected), initial value
/// and final XOR all ones. (<see cref="System.IO.Hashing.Crc64"/> is the non-reflected form, so it does not fit.)</summary>
internal sealed class Crc64Xz
{
    private static readonly ulong[] Table = MakeTable();
    private ulong _crc = ulong.MaxValue;

    private static ulong[] MakeTable()
    {
        var t = new ulong[256];
        for (uint i = 0; i < 256; i++)
        {
            ulong c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xC96C5795D7870F42UL : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        ulong c = _crc;
        foreach (byte b in data) c = Table[(byte)(c ^ b)] ^ (c >> 8);
        _crc = c;
    }

    public ulong Value => ~_crc;

    public static ulong Compute(ReadOnlySpan<byte> data)
    {
        var c = new Crc64Xz();
        c.Append(data);
        return c.Value;
    }
}

/// <summary>
/// Decodes an .xz file, every stream of it, checking what SharpCompress does not: each block's CRC-32, CRC-64 or
/// SHA-256 against the value stored after it, the index of each stream against the blocks, and that nothing but
/// stream padding follows. The layout is read from the end first (the input is seekable), so the exact output size
/// is known before decoding, and every LZMA2 dictionary is checked against the memory budget before it is allocated.
/// </summary>
internal sealed class XzDecoder : ReadOnlyStream
{
    private const int MaxIndexBytes = 64 << 20;

    private sealed record Block(long Start, long UnpaddedSize, long UncompressedSize);
    private sealed record XzStreamInfo(long Start, int CheckType, List<Block> Blocks);

    private readonly Stream _input;
    private readonly ImportLimits _limits;
    private readonly string _name;
    private readonly List<XzStreamInfo> _streams;
    private readonly List<(XzStreamInfo Stream, Block Block)> _blocks = [];
    private int _next;
    private Stream? _current;
    private SubStream? _slice;
    private (XzStreamInfo Stream, Block Block) _currentBlock;
    private long _currentOut;
    private Func<byte[]>? _finishHash;
    private Action<ReadOnlySpan<byte>>? _hash;

    /// <param name="input">The .xz data, seekable, from position 0 to its length.</param>
    public XzDecoder(Stream input, ImportLimits limits, string name)
    {
        _input = input;
        _limits = limits;
        _name = name;
        _streams = ReadLayout(input);
        foreach (var s in _streams)
            foreach (var b in s.Blocks) _blocks.Add((s, b));
        UncompressedSize = _blocks.Aggregate(0L, (sum, b) => LimitGuard.Add(sum, b.Block.UncompressedSize));
        Unchecked = _streams.Any(s => s.CheckType is not (1 or 4 or 10) && s.Blocks.Count > 0);
    }

    /// <summary>The exact size of the decoded data, from the indexes.</summary>
    public long UncompressedSize { get; }
    /// <summary>Some block has no check (check type None) or one Osmium cannot compute.</summary>
    public bool Unchecked { get; }
    public int StreamCount => _streams.Count;

    /// <summary>Only the first bytes, for detection: no layout from the end, no checks.</summary>
    public static byte[] Peek(Stream input, int count, ImportLimits limits, string name)
    {
        input.Position = 0;
        Span<byte> header = stackalloc byte[12];
        input.ReadExactly(header);
        int checkType = ParseStreamHeader(header);
        // Only `count` bytes are decoded, so a dictionary of `count` bytes holds every distance they can use.
        var block = new Block(12, -1, -1);
        using var s = OpenBlock(input, block, checkType, limits, name, out _, out _, shrinkTo: count);
        var buf = new byte[count];
        int n = s.ReadAtLeast(buf, count, throwOnEndOfStream: false);
        return buf[..n];
    }

    private static int ParseStreamHeader(ReadOnlySpan<byte> h)
    {
        if (!h[..6].SequenceEqual((ReadOnlySpan<byte>)[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00])) throw Corrupt("not an xz stream");
        if (Crc32.HashToUInt32(h.Slice(6, 2)) != BinaryPrimitives.ReadUInt32LittleEndian(h[8..])) throw Corrupt("damaged stream header");
        if (h[6] != 0 || (h[7] & 0xF0) != 0) throw new NotSupportedException("This xz file uses features Osmium does not know.");
        return h[7] & 0x0F;
    }

    private static int CheckSize(int type) => type switch
    {
        0 => 0,
        <= 3 => 4,
        <= 6 => 8,
        <= 9 => 16,
        <= 12 => 32,
        _ => 64,
    };

    private static CorruptDataException Corrupt(string why) => new($"damaged data ({why})");

    private static List<XzStreamInfo> ReadLayout(Stream input)
    {
        long pos = input.Length;
        if (pos % 4 != 0) throw Corrupt("the file size is not a multiple of four bytes");
        var streams = new List<XzStreamInfo>();
        var four = new byte[4];
        while (pos > 0)
        {
            // Stream padding: groups of four zero bytes.
            while (pos >= 4)
            {
                input.Position = pos - 4;
                input.ReadExactly(four);
                if (BinaryPrimitives.ReadUInt32LittleEndian(four) != 0) break;
                pos -= 4;
            }
            if (pos == 0) break;
            if (pos < 24) throw Corrupt("unexpected data after the end of the compressed stream");
            var footer = new byte[12];
            input.Position = pos - 12;
            input.ReadExactly(footer);
            if (footer[10] != 0x59 || footer[11] != 0x5A) throw Corrupt(streams.Count == 0 ? "the stream is truncated" : "unexpected data after the end of the compressed stream");
            if (Crc32.HashToUInt32(footer.AsSpan(4, 6)) != BinaryPrimitives.ReadUInt32LittleEndian(footer)) throw Corrupt("damaged stream footer");
            long indexSize = ((long)BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(4)) + 1) * 4;
            long indexStart = pos - 12 - indexSize;
            if (indexStart < 12) throw Corrupt("damaged stream footer");
            if (indexSize > MaxIndexBytes) throw new NotSupportedException("This xz file's index is larger than Osmium accepts.");
            var index = new byte[indexSize];
            input.Position = indexStart;
            input.ReadExactly(index);
            var records = ParseIndex(index);
            long blocksSize = 0;
            foreach (var (unpadded, _) in records) blocksSize = LimitGuard.Add(blocksSize, (unpadded + 3) & ~3L);
            long streamStart = indexStart - blocksSize - 12;
            if (streamStart < 0) throw Corrupt("the index does not match the file");
            var header = new byte[12];
            input.Position = streamStart;
            input.ReadExactly(header);
            int checkType = ParseStreamHeader(header);
            if (header[6] != footer[8] || header[7] != footer[9]) throw Corrupt("stream header and footer disagree");
            var blocks = new List<Block>(records.Count);
            long at = streamStart + 12;
            foreach (var (unpadded, uncompressed) in records)
            {
                blocks.Add(new Block(at, unpadded, uncompressed));
                at += (unpadded + 3) & ~3L;
            }
            streams.Insert(0, new XzStreamInfo(streamStart, checkType, blocks));
            pos = streamStart;
        }
        if (streams.Count == 0) throw Corrupt("no xz stream");
        return streams;
    }

    private static List<(long Unpadded, long Uncompressed)> ParseIndex(byte[] index)
    {
        int p = 0;
        if (index[p++] != 0) throw Corrupt("damaged index");
        long count = ReadVarint(index, ref p);
        if (count > index.Length / 2) throw Corrupt("damaged index");
        var list = new List<(long, long)>((int)count);
        for (long i = 0; i < count; i++)
        {
            long unpadded = ReadVarint(index, ref p), uncompressed = ReadVarint(index, ref p);
            if (unpadded < 5 || unpadded > long.MaxValue / 2) throw Corrupt("damaged index");
            list.Add((unpadded, uncompressed));
        }
        while (p % 4 != 0)
            if (p >= index.Length - 4 || index[p++] != 0) throw Corrupt("damaged index");
        if (p != index.Length - 4) throw Corrupt("damaged index");
        if (Crc32.HashToUInt32(index.AsSpan(0, p)) != BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(p))) throw Corrupt("damaged index");
        return list;
    }

    internal static long ReadVarint(ReadOnlySpan<byte> data, ref int p)
    {
        long value = 0;
        for (int i = 0; i < 9; i++)
        {
            if (p >= data.Length) throw Corrupt("damaged header");
            byte b = data[p++];
            value |= (long)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0)
            {
                if (b == 0 && i > 0) throw Corrupt("damaged header");
                if (value < 0) throw Corrupt("damaged header");
                return value;
            }
        }
        throw Corrupt("damaged header");
    }

    /// <summary>The dictionary size an LZMA2 property byte declares (0–40).</summary>
    internal static long Lzma2DictSize(byte prop) => prop == 40 ? 0xFFFFFFFFL : (2L | (prop & 1)) << (prop / 2 + 11);

    /// <summary>The smallest LZMA2 property byte whose dictionary holds <paramref name="bytes"/> and does not pass
    /// <paramref name="declared"/>: LZMA2 data never refers back further than its own output, so a dictionary as large
    /// as the output is enough, and a tiny file declaring a huge dictionary costs nothing.</summary>
    internal static byte ShrinkLzma2Prop(byte declared, long bytes)
    {
        if (bytes < 0) return declared;
        for (byte p = 0; p < declared; p++)
            if (Lzma2DictSize(p) >= bytes) return p;
        return declared;
    }

    /// <summary>Opens one block's filter chain; the result yields the block's uncompressed bytes.</summary>
    private static Stream OpenBlock(Stream input, Block block, int checkType, ImportLimits limits, string name, out long compressedSize, out SubStream slice, long shrinkTo = -1)
    {
        input.Position = block.Start;
        int sizeByte = input.ReadByte();
        if (sizeByte <= 0) throw Corrupt("damaged block header");
        int headerSize = (sizeByte + 1) * 4;
        var h = new byte[headerSize];
        h[0] = (byte)sizeByte;
        input.ReadExactly(h.AsSpan(1));
        if (Crc32.HashToUInt32(h.AsSpan(0, headerSize - 4)) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(headerSize - 4))) throw Corrupt("damaged block header");
        byte flags = h[1];
        if ((flags & 0x3C) != 0) throw new NotSupportedException("This xz file uses features Osmium does not know.");
        int p = 2;
        long declaredCompressed = (flags & 0x40) != 0 ? ReadVarint(h.AsSpan(0, headerSize - 4), ref p) : -1;
        long declaredUncompressed = (flags & 0x80) != 0 ? ReadVarint(h.AsSpan(0, headerSize - 4), ref p) : -1;
        int filterCount = (flags & 3) + 1;
        var filters = new List<(long Id, byte[] Props)>();
        for (int i = 0; i < filterCount; i++)
        {
            long id = ReadVarint(h.AsSpan(0, headerSize - 4), ref p);
            long propsSize = ReadVarint(h.AsSpan(0, headerSize - 4), ref p);
            if (propsSize > headerSize - 4 - p) throw Corrupt("damaged block header");
            filters.Add((id, h.AsSpan(p, (int)propsSize).ToArray()));
            p += (int)propsSize;
        }
        for (; p < headerSize - 4; p++)
            if (h[p] != 0) throw Corrupt("damaged block header");
        if (block.UnpaddedSize >= 0)
        {
            compressedSize = block.UnpaddedSize - headerSize - CheckSize(checkType);
            if (compressedSize <= 0 || (declaredCompressed >= 0 && declaredCompressed != compressedSize)) throw Corrupt("the block does not match the index");
            if (declaredUncompressed >= 0 && declaredUncompressed != block.UncompressedSize) throw Corrupt("the block does not match the index");
        }
        else compressedSize = declaredCompressed >= 0 ? declaredCompressed : input.Length - input.Position;
        long outSize = block.UncompressedSize >= 0 ? block.UncompressedSize : declaredUncompressed;

        if (filters[^1].Id != 0x21) throw new NotSupportedException("This xz file uses a filter Osmium cannot read.");
        var lzma2Props = filters[^1].Props;
        if (lzma2Props.Length != 1 || lzma2Props[0] > 40) throw Corrupt("damaged block header");
        long dict = Lzma2DictSize(lzma2Props[0]);
        byte prop = ShrinkLzma2Prop(lzma2Props[0], shrinkTo >= 0 ? shrinkTo : outSize);
        LimitGuard.CheckMemory(name, Math.Min(dict, Lzma2DictSize(prop)) + (1 << 20), limits);

        slice = new SubStream(input, input.Position, compressedSize);
        Stream s;
        try { s = new LzmaStream([prop], new EndGuardStream(slice, allowedEndReads: 0), compressedSize, outSize, null, isLzma2: true); }
        catch (OutOfMemoryException) { throw LimitGuard.MemoryRefusal(name, dict); }
        for (int i = filters.Count - 2; i >= 0; i--)
        {
            var (id, props) = filters[i];
            if (id == 0x21) throw Corrupt("damaged block header");
            s = id switch
            {
                0x03 when props.Length == 1 => new DeltaFilterStream(s, props[0] + 1),
                0x04 => new BranchFilterStream(s, BranchConverter.X86(), StartOffset(props)),
                0x05 => new BranchFilterStream(s, BranchConverter.PowerPc(), StartOffset(props)),
                0x07 => new BranchFilterStream(s, BranchConverter.Arm(), StartOffset(props)),
                0x08 => new BranchFilterStream(s, BranchConverter.ArmThumb(), StartOffset(props)),
                0x09 => new BranchFilterStream(s, BranchConverter.Sparc(), StartOffset(props)),
                0x0A => new BranchFilterStream(s, BranchConverter.Arm64(), StartOffset(props)),
                _ => throw new NotSupportedException("This xz file uses a filter Osmium cannot read."),
            };
        }
        return s;
    }

    private static uint StartOffset(byte[] props) => props.Length switch
    {
        0 => 0,
        4 => BinaryPrimitives.ReadUInt32LittleEndian(props),
        _ => throw Corrupt("damaged block header"),
    };

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            if (_current is null)
            {
                if (_next >= _blocks.Count) return 0;
                _currentBlock = _blocks[_next++];
                _current = OpenBlock(_input, _currentBlock.Block, _currentBlock.Stream.CheckType, _limits, _name, out _, out _slice);
                _currentOut = 0;
                StartHash(_currentBlock.Stream.CheckType);
            }
            int n = buffer.IsEmpty ? 0 : _current.Read(buffer);
            if (n > 0)
            {
                _currentOut += n;
                if (_currentOut > _currentBlock.Block.UncompressedSize) throw Corrupt("a block holds more data than the index says");
                _hash?.Invoke(buffer[..n]);
                return n;
            }
            if (buffer.IsEmpty) return 0;
            FinishBlock();
        }
    }

    private void StartHash(int checkType)
    {
        switch (checkType)
        {
            case 1:
                var crc = new Crc32();
                _hash = d => crc.Append(d);
                _finishHash = () => { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, crc.GetCurrentHashAsUInt32()); return b; };
                break;
            case 4:
                var crc64 = new Crc64Xz();
                _hash = d => crc64.Append(d);
                _finishHash = () => { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, crc64.Value); return b; };
                break;
            case 10:
                var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                _hash = d => sha.AppendData(d);
                _finishHash = () => { var b = sha.GetHashAndReset(); sha.Dispose(); return b; };
                break;
            default:
                _hash = null;
                _finishHash = null;
                break;
        }
    }

    private void FinishBlock()
    {
        var (stream, block) = _currentBlock;
        _current!.Dispose();
        _current = null;
        if (_currentOut != block.UncompressedSize) throw Corrupt("a block holds less data than the index says");
        if (_slice is { Remaining: > 0 }) throw Corrupt("unexpected data at the end of a block");
        int checkSize = CheckSize(stream.CheckType);
        long checkAt = block.Start + block.UnpaddedSize - checkSize;
        long dataEnd = checkAt; // header + compressed data, before padding
        long paddedEnd = block.Start + ((block.UnpaddedSize - checkSize + 3) & ~3L);
        var tail = new byte[paddedEnd - dataEnd + checkSize];
        _input.Position = dataEnd;
        _input.ReadExactly(tail);
        for (int i = 0; i < paddedEnd - dataEnd; i++)
            if (tail[i] != 0) throw Corrupt("damaged block padding");
        if (_finishHash is { } finish)
        {
            var want = tail.AsSpan((int)(paddedEnd - dataEnd), checkSize);
            if (!finish().AsSpan().SequenceEqual(want)) throw Corrupt("the block check does not match");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _current?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A read-only window <c>[start, start + length)</c> of a seekable stream; it moves the base stream's
/// position, so only one window of a base stream is read at a time.</summary>
internal sealed class SubStream(Stream inner, long start, long length) : ReadOnlyStream
{
    private long _pos;

    public long Remaining => length - _pos;

    public override int Read(Span<byte> buffer)
    {
        if (_pos >= length || buffer.IsEmpty) return 0;
        if (buffer.Length > length - _pos) buffer = buffer[..(int)(length - _pos)];
        inner.Position = start + _pos;
        int n = inner.Read(buffer);
        if (n <= 0) throw new CorruptDataException("damaged data (the file ends early)");
        _pos += n;
        return n;
    }
}
