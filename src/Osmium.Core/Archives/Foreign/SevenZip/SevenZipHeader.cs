using System.Buffers.Binary;
using System.Text;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Archives.Foreign.SevenZip;

internal sealed class SzCoder
{
    public byte[] MethodId = [];
    public int NumIn = 1, NumOut = 1;
    public byte[] Props = [];
    public ulong Id => MethodId.Aggregate(0UL, (v, b) => (v << 8) | b);
}

internal sealed class SzFolder
{
    public List<SzCoder> Coders = [];
    public List<(int In, int Out)> BindPairs = [];
    /// <summary>The coder input streams fed by packed streams, in the order of the packed streams.</summary>
    public List<int> PackedStreams = [];
    /// <summary>The size of every coder output stream.</summary>
    public List<long> UnpackSizes = [];
    public uint? UnpackCrc;
    public int FirstPackStream;
    public long NumUnpackStreams = 1;
    public List<long> SubSizes = [];
    public List<uint?> SubCrcs = [];

    public int TotalOut => Coders.Sum(c => c.NumOut);
    public int TotalIn => Coders.Sum(c => c.NumIn);

    /// <summary>The output stream no bind pair consumes: the folder's result.</summary>
    public int MainOut()
    {
        for (int i = 0; i < TotalOut; i++)
            if (!BindPairs.Any(b => b.Out == i)) return i;
        throw SevenZipHeader.Corrupt("a folder without a result stream");
    }

    public long UnpackSize => UnpackSizes[MainOut()];
    public bool IsEncrypted => Coders.Any(c => c.Id == SevenZipHeader.AesId);
}

internal sealed class SzFile
{
    public string Name = "";
    public bool HasStream = true, IsDir, IsAnti;
    public DateTime? ModifiedUtc;
    public uint? Attributes;
    public long Size;
    public uint? Crc;
    public int Folder = -1;
}

internal sealed class SzDatabase
{
    public long PackPos;
    public List<long> PackSizes = [];
    public List<SzFolder> Folders = [];
    public List<SzFile> Files = [];
    /// <summary>Absolute offset in the file of each packed stream.</summary>
    public long PackOffset(int packStream) => 32 + PackPos + PackSizes.Take(packStream).Sum();
}

/// <summary>
/// Reads the 7z header (7-Zip's published format description, written here): the signature header, the next header
/// and, when it is packed (and possibly encrypted), its folder decoded first. Every count is checked against the bytes
/// that remain before anything is allocated for it, lists grow as items are read, and the decoded header has a size
/// limit, so a small file cannot make the reader allocate much.
/// </summary>
internal static class SevenZipHeader
{
    public const ulong AesId = 0x06F10701;
    private const int MaxCoders = 64, MaxStreams = 64;

    internal static CorruptDataException Corrupt(string why) => new($"damaged data ({why})");

    /// <summary>Reads the database. <paramref name="decodeHeader"/> decodes a packed header's folder into bytes.</summary>
    public static SzDatabase Read(FileSlice file, ImportLimits limits, Func<SzDatabase, SzFolder, long, byte[]> decodeHeader)
    {
        if (file.Length < 32) throw Corrupt("the file is too short to be a 7z archive");
        var sig = new byte[32];
        file.ReadAt(0, sig);
        if (sig[6] != 0) throw new NotSupportedException("This 7z archive was made by a version of the format Osmium does not know.");
        if (System.IO.Hashing.Crc32.HashToUInt32(sig.AsSpan(12, 20)) != BinaryPrimitives.ReadUInt32LittleEndian(sig.AsSpan(8))) throw Corrupt("damaged start header");
        long nextOffset = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(sig.AsSpan(12)), long.MaxValue);
        long nextSize = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(sig.AsSpan(20)), long.MaxValue);
        uint nextCrc = BinaryPrimitives.ReadUInt32LittleEndian(sig.AsSpan(28));
        if (nextSize == 0) return new SzDatabase(); // an empty archive
        if (nextOffset > file.Length - 32 || nextSize > file.Length - 32 - nextOffset) throw Corrupt("the archive is truncated (its header is missing)");
        long cap = MaxHeader(limits);
        if (nextSize > cap) throw new EntryLimitException("The file list of this archive is larger than Osmium accepts.");
        var header = new byte[nextSize];
        file.ReadAt(32 + nextOffset, header);
        if (System.IO.Hashing.Crc32.HashToUInt32(header) != nextCrc) throw Corrupt("damaged header");

        for (int level = 0; ; level++)
        {
            var r = new Reader(header);
            ulong id = r.Number();
            if (id == 0x01) return ReadHeader(r, limits);
            if (id != 0x17 || level >= 4) throw Corrupt("damaged header");
            // A packed header: its streams info, then decode its folder.
            var packed = new SzDatabase();
            ReadStreamsInfo(r, packed, limits);
            if (packed.Folders.Count == 0) throw Corrupt("damaged header");
            var folder = packed.Folders[0];
            long size = folder.UnpackSize;
            if (size < 0 || size > cap) throw new EntryLimitException("The file list of this archive is larger than Osmium accepts.");
            header = decodeHeader(packed, folder, size);
            if (header.Length != size) throw Corrupt("damaged header");
            if (folder.UnpackCrc is { } crc && System.IO.Hashing.Crc32.HashToUInt32(header) != crc) throw new HeaderCheckException();
        }
    }

    /// <summary>The decoded header's CRC does not match: damage, or (with encrypted headers) a wrong password.</summary>
    internal sealed class HeaderCheckException() : CorruptDataException("damaged data (the header's CRC does not match)");

    public static long MaxHeader(ImportLimits limits) => Math.Min(1L << 30, Math.Max(16L << 20, limits.MemoryBudgetBytes / 4));

    private static SzDatabase ReadHeader(Reader r, ImportLimits limits)
    {
        var db = new SzDatabase();
        ulong id = r.Number();
        if (id == 0x02) { SkipProperties(r); id = r.Number(); }
        if (id == 0x03) { ReadStreamsInfo(r, new SzDatabase(), limits); id = r.Number(); }
        if (id == 0x04) { ReadStreamsInfo(r, db, limits); id = r.Number(); }
        if (id == 0x05) { ReadFiles(r, db, limits); id = r.Number(); }
        if (id != 0x00) throw Corrupt("damaged header");
        AssignStreams(db);
        return db;
    }

    private static void SkipProperties(Reader r)
    {
        while (true)
        {
            ulong type = r.Number();
            if (type == 0) return;
            r.Skip(r.Count("property"));
        }
    }

    private static void ReadStreamsInfo(Reader r, SzDatabase db, ImportLimits limits)
    {
        bool subStreams = false;
        while (true)
        {
            ulong id = r.Number();
            switch (id)
            {
                case 0x00:
                    if (!subStreams)
                        foreach (var f in db.Folders) DefaultSubStreams(f);
                    return;
                case 0x06: ReadPackInfo(r, db); break;
                case 0x07: ReadUnpackInfo(r, db); break;
                case 0x08: ReadSubStreams(r, db); subStreams = true; break;
                default: throw Corrupt("damaged header");
            }
        }
    }

    private static void DefaultSubStreams(SzFolder f)
    {
        f.NumUnpackStreams = 1;
        f.SubSizes = [f.UnpackSize];
        f.SubCrcs = [f.UnpackCrc];
    }

    private static void ReadPackInfo(Reader r, SzDatabase db)
    {
        db.PackPos = r.Long();
        int n = r.Count("packed stream");
        while (true)
        {
            ulong id = r.Number();
            if (id == 0x00) break;
            if (id == 0x09) for (int i = 0; i < n; i++) db.PackSizes.Add(r.Long());
            else if (id == 0x0A) ReadDigests(r, n);
            else throw Corrupt("damaged header");
        }
        if (db.PackSizes.Count != n) throw Corrupt("damaged header");
    }

    private static void ReadUnpackInfo(Reader r, SzDatabase db)
    {
        if (r.Number() != 0x0B) throw Corrupt("damaged header");
        int n = r.Count("folder");
        if (r.Byte() != 0) throw new NotSupportedException("This 7z archive keeps its folder list elsewhere, which Osmium cannot read.");
        int pack = 0;
        for (int i = 0; i < n; i++)
        {
            var f = ReadFolder(r);
            f.FirstPackStream = pack;
            pack += f.PackedStreams.Count;
            db.Folders.Add(f);
        }
        if (r.Number() != 0x0C) throw Corrupt("damaged header");
        foreach (var f in db.Folders)
            for (int i = 0; i < f.TotalOut; i++) f.UnpackSizes.Add(r.Long());
        while (true)
        {
            ulong id = r.Number();
            if (id == 0x00) break;
            if (id != 0x0A) throw Corrupt("damaged header");
            var crcs = ReadDigests(r, db.Folders.Count);
            for (int i = 0; i < db.Folders.Count; i++) db.Folders[i].UnpackCrc = crcs[i];
        }
    }

    private static SzFolder ReadFolder(Reader r)
    {
        var f = new SzFolder();
        int coders = r.Count("coder");
        if (coders is 0 or > MaxCoders) throw Corrupt("damaged header");
        for (int i = 0; i < coders; i++)
        {
            byte main = r.Byte();
            if ((main & 0x80) != 0) throw new NotSupportedException("This 7z archive uses alternative coders, which Osmium cannot read.");
            var c = new SzCoder { MethodId = r.Bytes(main & 0x0F) };
            if ((main & 0x10) != 0)
            {
                c.NumIn = r.Count("stream");
                c.NumOut = r.Count("stream");
                if (c.NumIn is 0 or > MaxStreams || c.NumOut is 0 or > MaxStreams) throw Corrupt("damaged header");
            }
            if ((main & 0x20) != 0) c.Props = r.Bytes(r.Count("property"));
            f.Coders.Add(c);
        }
        int totalOut = f.TotalOut, totalIn = f.TotalIn;
        if (totalOut > MaxStreams || totalIn > MaxStreams) throw Corrupt("damaged header");
        for (int i = 0; i < totalOut - 1; i++)
        {
            int inIndex = r.Count("stream"), outIndex = r.Count("stream");
            if (inIndex >= totalIn || outIndex >= totalOut) throw Corrupt("damaged header");
            f.BindPairs.Add((inIndex, outIndex));
        }
        int packed = totalIn - (totalOut - 1);
        if (packed < 1) throw Corrupt("damaged header");
        if (packed == 1)
        {
            for (int i = 0; i < totalIn; i++)
                if (!f.BindPairs.Any(b => b.In == i)) { f.PackedStreams.Add(i); break; }
            if (f.PackedStreams.Count != 1) throw Corrupt("damaged header");
        }
        else
            for (int i = 0; i < packed; i++)
            {
                int s = r.Count("stream");
                if (s >= totalIn) throw Corrupt("damaged header");
                f.PackedStreams.Add(s);
            }
        return f;
    }

    private static void ReadSubStreams(Reader r, SzDatabase db)
    {
        foreach (var f in db.Folders) f.NumUnpackStreams = 1;
        ulong id = r.Number();
        if (id == 0x0D)
        {
            foreach (var f in db.Folders) f.NumUnpackStreams = r.Long();
            id = r.Number();
        }
        long total = db.Folders.Aggregate(0L, (s, f) => LimitGuard.Add(s, f.NumUnpackStreams));
        if (total > int.MaxValue) throw Corrupt("damaged header");
        bool sizes = id == 0x09;
        foreach (var f in db.Folders)
        {
            if (f.NumUnpackStreams == 0) continue;
            long sum = 0;
            if (sizes)
                for (long i = 1; i < f.NumUnpackStreams; i++)
                {
                    long s = r.Long();
                    sum += s;
                    if (sum < 0 || sum > f.UnpackSize) throw Corrupt("damaged header");
                    f.SubSizes.Add(s);
                }
            else if (f.NumUnpackStreams > 1) throw Corrupt("damaged header");
            f.SubSizes.Add(f.UnpackSize - sum);
        }
        if (sizes) id = r.Number();
        foreach (var f in db.Folders)
        {
            f.SubCrcs = [.. Enumerable.Repeat<uint?>(null, f.SubSizes.Count)];
            if (f.NumUnpackStreams == 1 && f.UnpackCrc is { } crc) f.SubCrcs[0] = crc;
        }
        while (id != 0x00)
        {
            if (id != 0x0A) throw Corrupt("damaged header");
            int unknown = db.Folders.Sum(f => f.NumUnpackStreams == 1 && f.UnpackCrc is not null ? 0 : f.SubSizes.Count);
            var crcs = ReadDigests(r, unknown);
            int k = 0;
            foreach (var f in db.Folders)
            {
                if (f.NumUnpackStreams == 1 && f.UnpackCrc is not null) continue;
                for (int i = 0; i < f.SubSizes.Count; i++) f.SubCrcs[i] = crcs[k++];
            }
            id = r.Number();
        }
    }

    private static List<uint?> ReadDigests(Reader r, int n)
    {
        var defined = r.AllOrBits(n);
        var list = new List<uint?>(Math.Min(n, 1 << 16));
        for (int i = 0; i < n; i++) list.Add(defined[i] ? r.UInt32() : null);
        return list;
    }

    private static void ReadFiles(Reader r, SzDatabase db, ImportLimits limits)
    {
        long count = r.Long();
        LimitGuard.CheckEntryCount(count, limits);
        // Each file either has a data stream (counted in the folders) or takes at least one bit of what remains.
        long streams = db.Folders.Sum(f => (long)f.SubSizes.Count);
        if (count > streams + (long)r.Remaining * 8) throw Corrupt("damaged header (file count)");
        int n = (int)count;
        var files = new List<SzFile>(Math.Min(n, 1 << 16));
        for (int i = 0; i < n; i++) files.Add(new SzFile());
        bool[]? emptyStream = null, emptyFile = null, anti = null;
        long nameChars = 0;
        while (true)
        {
            ulong type = r.Number();
            if (type == 0) break;
            int size = r.Count("property");
            var p = r.Sub(size);
            switch (type)
            {
                case 0x0E: emptyStream = p.Bits(n); break;
                case 0x0F: emptyFile = p.Bits(emptyStream?.Count(b => b) ?? 0); break;
                case 0x10: anti = p.Bits(emptyStream?.Count(b => b) ?? 0); break;
                case 0x11:
                    if (p.Byte() != 0) throw new NotSupportedException("This 7z archive keeps its file names elsewhere, which Osmium cannot read.");
                    for (int i = 0; i < n; i++)
                    {
                        files[i].Name = p.Utf16z();
                        nameChars += files[i].Name.Length;
                        if (nameChars * 2 > limits.MemoryBudgetBytes / 4) throw new EntryLimitException("The file list of this archive is larger than Osmium accepts.");
                    }
                    break;
                case 0x14:
                {
                    var defined = p.AllOrBits(n);
                    if (p.Byte() != 0) throw Corrupt("damaged header");
                    for (int i = 0; i < n; i++)
                        if (defined[i]) files[i].ModifiedUtc = FileTime((long)p.UInt64());
                    break;
                }
                case 0x15:
                {
                    var defined = p.AllOrBits(n);
                    if (p.Byte() != 0) throw Corrupt("damaged header");
                    for (int i = 0; i < n; i++)
                        if (defined[i]) files[i].Attributes = p.UInt32();
                    break;
                }
                default: break; // times we do not restore, start positions, padding
            }
        }
        int e = 0;
        for (int i = 0; i < n; i++)
        {
            var f = files[i];
            if (emptyStream is not null && emptyStream[i])
            {
                f.HasStream = false;
                bool isEmptyFile = emptyFile is not null && e < emptyFile.Length && emptyFile[e];
                f.IsDir = !isEmptyFile;
                f.IsAnti = anti is not null && e < anti.Length && anti[e];
                e++;
            }
            if (f.Attributes is { } a && (a & 0x10) != 0 && !f.HasStream) f.IsDir = true;
        }
        db.Files = files;
    }

    /// <summary>Gives each file with data its folder, size and CRC; the counts must agree exactly.</summary>
    private static void AssignStreams(SzDatabase db)
    {
        int folder = 0, sub = 0;
        foreach (var f in db.Files)
        {
            if (!f.HasStream) continue;
            while (folder < db.Folders.Count && sub >= db.Folders[folder].SubSizes.Count) { folder++; sub = 0; }
            if (folder >= db.Folders.Count) throw Corrupt("more files than data streams");
            f.Folder = folder;
            f.Size = db.Folders[folder].SubSizes[sub];
            f.Crc = db.Folders[folder].SubCrcs[sub];
            sub++;
        }
        while (folder < db.Folders.Count && sub >= db.Folders[folder].SubSizes.Count) { folder++; sub = 0; }
        if (folder < db.Folders.Count) throw Corrupt("more data streams than files");
        int packTotal = db.Folders.Sum(f => f.PackedStreams.Count);
        if (packTotal > db.PackSizes.Count) throw Corrupt("damaged header");
    }

    private static DateTime? FileTime(long ft)
    {
        try { return ft is > 0 and < 2650467743999999999 ? DateTime.FromFileTimeUtc(ft) : null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>A cursor over header bytes; every read checks what remains.</summary>
    internal sealed class Reader(byte[] data, int start = 0, int end = -1)
    {
        private int _p = start;
        private readonly int _end = end < 0 ? data.Length : end;

        public int Remaining => _end - _p;

        public byte Byte()
        {
            if (_p >= _end) throw Corrupt("the header ends early");
            return data[_p++];
        }

        public ulong Number()
        {
            byte first = Byte();
            byte mask = 0x80;
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((first & mask) == 0)
                {
                    ulong high = (ulong)(first & (mask - 1));
                    return value | (high << (8 * i));
                }
                value |= (ulong)Byte() << (8 * i);
                mask >>= 1;
            }
            return value;
        }

        public long Long()
        {
            ulong v = Number();
            if (v > long.MaxValue) throw Corrupt("damaged header");
            return (long)v;
        }

        /// <summary>A count of items that each need at least one byte of what remains (or are bits).</summary>
        public int Count(string what)
        {
            ulong v = Number();
            if (v > (ulong)Math.Max(Remaining, 0) + 64 || v > int.MaxValue) throw Corrupt($"damaged header ({what} count)");
            return (int)v;
        }

        public byte[] Bytes(int n)
        {
            if (n < 0 || n > Remaining) throw Corrupt("the header ends early");
            var b = data.AsSpan(_p, n).ToArray();
            _p += n;
            return b;
        }

        public void Skip(int n)
        {
            if (n < 0 || n > Remaining) throw Corrupt("the header ends early");
            _p += n;
        }

        public Reader Sub(int n)
        {
            if (n < 0 || n > Remaining) throw Corrupt("the header ends early");
            var sub = new Reader(data, _p, _p + n);
            _p += n;
            return sub;
        }

        public uint UInt32()
        {
            if (Remaining < 4) throw Corrupt("the header ends early");
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_p));
            _p += 4;
            return v;
        }

        public ulong UInt64()
        {
            if (Remaining < 8) throw Corrupt("the header ends early");
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(_p));
            _p += 8;
            return v;
        }

        public bool[] Bits(int n)
        {
            if ((n + 7) / 8 > Remaining) throw Corrupt("the header ends early");
            var bits = new bool[n];
            byte b = 0;
            for (int i = 0; i < n; i++)
            {
                if (i % 8 == 0) b = Byte();
                bits[i] = (b & (0x80 >> (i % 8))) != 0;
            }
            return bits;
        }

        public bool[] AllOrBits(int n)
        {
            if (Byte() != 0)
            {
                var all = new bool[n];
                Array.Fill(all, true);
                return all;
            }
            return Bits(n);
        }

        public string Utf16z()
        {
            int s = _p;
            while (true)
            {
                if (_p + 2 > _end) throw Corrupt("the header ends early");
                if (data[_p] == 0 && data[_p + 1] == 0) break;
                _p += 2;
            }
            string name = Encoding.Unicode.GetString(data, s, _p - s);
            _p += 2;
            return name;
        }
    }
}
