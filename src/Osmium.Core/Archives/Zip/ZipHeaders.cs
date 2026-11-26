using System.Buffers.Binary;

namespace Osmium.Core.Archives.Zip;

/// <summary>
/// Reads a ZIP file's central directory and checks each entry's local header against it: the header that a reader
/// which reads the file from its start (a streaming reader) relies on, while System.IO.Compression reads the central
/// directory only.
/// </summary>
internal static class ZipHeaders
{
    private const uint LocalSignature = 0x04034B50, CentralSignature = 0x02014B50, EndSignature = 0x06054B50;
    private const uint Zip64EndSignature = 0x06064B50, Zip64LocatorSignature = 0x07064B50;
    private const ushort Zip64Extra = 0x0001, DataDescriptorFlag = 1 << 3, Stored = 0;

    /// <summary>The name of the first entry whose local header does not match the central directory (method, name,
    /// and the CRC and sizes unless a data descriptor follows the data), or of a stored entry that only a data descriptor
    /// ends; "its file list" when the central directory itself cannot be read; null when every header matches.</summary>
    public static string? FirstMismatch(Stream s)
    {
        try
        {
            var (count, at) = CentralDirectory(s);
            for (long i = 0; i < count; i++)
            {
                var c = Read(s, at, 46);
                if (U32(c, 0) != CentralSignature) return "its file list";
                ushort method = U16(c, 10);
                uint crc = U32(c, 16);
                long csize = U32(c, 20), usize = U32(c, 24), local = U32(c, 42);
                int nameLength = U16(c, 28), extraLength = U16(c, 30), commentLength = U16(c, 32);
                var name = Read(s, at + 46, nameLength);
                var extra = Read(s, at + 46 + nameLength, extraLength);
                // ZIP64 fields come in this order, each only when its 32-bit field is 0xFFFFFFFF.
                if (FindExtra(extra, Zip64Extra) is { } z)
                {
                    int p = 0;
                    if (usize == uint.MaxValue) { usize = (long)U64(z, p); p += 8; }
                    if (csize == uint.MaxValue) { csize = (long)U64(z, p); p += 8; }
                    if (local == uint.MaxValue) local = (long)U64(z, p);
                }
                string shown = System.Text.Encoding.UTF8.GetString(name);
                if (!LocalMatches(s, local, method, crc, csize, usize, name)) return shown;
                at += 46 + nameLength + extraLength + commentLength;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return "its file list";
        }
    }

    private static bool LocalMatches(Stream s, long at, ushort method, uint crc, long csize, long usize, byte[] name)
    {
        var h = Read(s, at, 30);
        if (U32(h, 0) != LocalSignature || U16(h, 8) != method) return false;
        ushort localFlags = U16(h, 6);
        int nameLength = U16(h, 26), extraLength = U16(h, 28);
        if (!Read(s, at + 30, nameLength).AsSpan().SequenceEqual(name)) return false;
        if ((localFlags & DataDescriptorFlag) != 0)
            return method != Stored; // a stored entry has no end of its own: a reader from the start cannot find it
        long localC = U32(h, 18), localU = U32(h, 22);
        if ((localC == uint.MaxValue || localU == uint.MaxValue) && FindExtra(Read(s, at + 30 + nameLength, extraLength), Zip64Extra) is { } z)
        {
            int p = 0;
            if (localU == uint.MaxValue) { localU = (long)U64(z, p); p += 8; }
            if (localC == uint.MaxValue) localC = (long)U64(z, p);
        }
        return U32(h, 14) == crc && localC == csize && localU == usize;
    }

    /// <summary>The number of entries and the central directory's offset, from the end records (ZIP64 when needed).</summary>
    private static (long Count, long Offset) CentralDirectory(Stream s)
    {
        long tailLength = Math.Min(s.Length, 22 + 0xFFFF);
        var tail = Read(s, s.Length - tailLength, (int)tailLength);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0 && end < 0; i--) if (U32(tail, i) == EndSignature) end = i;
        if (end < 0) throw new IOException("No end of central directory.");
        long count = U16(tail, end + 10), offset = U32(tail, end + 16);
        if ((count == ushort.MaxValue || offset == uint.MaxValue) && end >= 20 && U32(tail, end - 20) == Zip64LocatorSignature)
        {
            var z = Read(s, (long)U64(tail, end - 20 + 8), 56);
            if (U32(z, 0) != Zip64EndSignature) throw new IOException("No ZIP64 end of central directory.");
            count = (long)U64(z, 32);
            offset = (long)U64(z, 48);
        }
        return (count, offset);
    }

    private static byte[]? FindExtra(byte[] extra, ushort id)
    {
        for (int p = 0; p + 4 <= extra.Length;)
        {
            ushort tag = U16(extra, p), size = U16(extra, p + 2);
            if (p + 4 + size > extra.Length) return null;
            if (tag == id) return extra[(p + 4)..(p + 4 + size)];
            p += 4 + size;
        }
        return null;
    }

    private static byte[] Read(Stream s, long at, int count)
    {
        var b = new byte[count];
        s.Position = at;
        s.ReadExactly(b);
        return b;
    }

    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));
}
