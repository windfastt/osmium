using System.Buffers.Binary;

namespace Osmium.Core.Container;

/// <summary>
/// The spare copies at the end of an archive of container version 1.1 (FORMAT §4.1):
/// <c>… [last block frame] [B: copy of the index frame] [H: header copy] [A: index frame] [footer]</c>.
/// B is a byte-identical copy of A; H is the header exactly as at offset 0, zero padding and a 32-byte trailer. The
/// index's metadata key <see cref="ArchiveIndex.SparesKey"/> holds B's offset. Readers of 1.0 never look at the bytes
/// between the last block and the index frame, so they open such archives unchanged.
/// </summary>
public static class SpareCopies
{
    /// <summary>Archives whose block frames total at least this many bytes get spare copies.</summary>
    public const long Threshold = 1 << 20;
    /// <summary>H is at least this long, so that B ends at least 4 KiB before A starts.</summary>
    public const int MinHeaderCopyLength = 4096;
    public const int TrailerSize = 32;
    public static ReadOnlySpan<byte> TrailerMagic => "OHDR"u8;
    /// <summary>The longest header the reader accepts (36 fixed bytes, at most 1 MiB of extensions, the CRC).</summary>
    internal const long MaxHeaderLength = 36 + (1 << 20) + 4;

    /// <summary>The header copy H: <paramref name="header"/> (the raw header bytes), zero padding up to
    /// <see cref="MinHeaderCopyLength"/>, then the trailer.</summary>
    public static byte[] HeaderCopy(ReadOnlySpan<byte> header)
    {
        int padding = Math.Max(0, MinHeaderCopyLength - header.Length - TrailerSize);
        var h = new byte[header.Length + padding + TrailerSize];
        header.CopyTo(h);
        new SpareTrailer(0, header.Length, padding).Write(h.AsSpan(h.Length - TrailerSize));
        return h;
    }

    /// <summary>The value of <see cref="ArchiveIndex.SparesKey"/>: B's offset as u64 little-endian.</summary>
    public static byte[] OffsetValue(long offset)
    {
        var v = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(v, (ulong)offset);
        return v;
    }
}

/// <summary>The 32-byte trailer at the end of the header copy H (FORMAT §4.1).</summary>
/// <param name="Flags">0. A reader ignores H when a bit is set (a newer writer), without calling it damaged.</param>
/// <param name="HeaderLength">Length of the header bytes at the start of H.</param>
/// <param name="PaddingLength">Zero bytes between the header bytes and the trailer.</param>
/// <param name="Reserved">0; like a flag, anything else means a newer writer.</param>
public readonly record struct SpareTrailer(uint Flags, long HeaderLength, long PaddingLength, uint Reserved = 0)
{
    /// <summary>H's whole length: header bytes, padding and trailer.</summary>
    public long CopyLength => HeaderLength + PaddingLength + SpareCopies.TrailerSize;

    /// <summary>A newer writer made this H (a flag or reserved bit is set): this version does not use it.</summary>
    public bool Newer => Flags != 0 || Reserved != 0;

    public void Write(Span<byte> t)
    {
        t[..SpareCopies.TrailerSize].Clear();
        SpareCopies.TrailerMagic.CopyTo(t);
        BinaryPrimitives.WriteUInt32LittleEndian(t[4..], Flags);
        BinaryPrimitives.WriteInt64LittleEndian(t[8..], HeaderLength);
        BinaryPrimitives.WriteInt64LittleEndian(t[16..], PaddingLength);
        BinaryPrimitives.WriteUInt32LittleEndian(t[24..], Reserved);
        BinaryPrimitives.WriteUInt32LittleEndian(t[28..], Osa.Crc(t[..28]));
    }

    /// <summary>
    /// Reads a trailer whose magic and CRC check out (null otherwise). A trailer of a newer writer
    /// (<see cref="Newer"/>) is returned as stored; any other must have lengths this format allows (null otherwise).
    /// </summary>
    public static SpareTrailer? Read(ReadOnlySpan<byte> t)
    {
        if (t.Length < SpareCopies.TrailerSize || !t[..4].SequenceEqual(SpareCopies.TrailerMagic)) return null;
        if (Osa.Crc(t[..28]) != BinaryPrimitives.ReadUInt32LittleEndian(t[28..])) return null;
        var trailer = new SpareTrailer(BinaryPrimitives.ReadUInt32LittleEndian(t[4..]), BinaryPrimitives.ReadInt64LittleEndian(t[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(t[16..]), BinaryPrimitives.ReadUInt32LittleEndian(t[24..]));
        if (trailer.Newer) return trailer;
        bool possible = trailer.HeaderLength is >= 40 and <= SpareCopies.MaxHeaderLength
            && trailer.PaddingLength is >= 0 and < SpareCopies.MinHeaderCopyLength;
        return possible ? trailer : null;
    }
}
