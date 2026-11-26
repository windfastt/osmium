using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Archives.Foreign.Zip;

/// <summary>One entry as the central directory and its local header describe it.</summary>
internal sealed class ZipRecord
{
    public int Index;
    public byte[] NameBytes = [];
    public string Name = "";
    public ushort VersionMadeBy, Flags, Method, DosTime, DosDate;
    public uint Crc, ExternalAttributes;
    public long CompressedSize, UncompressedSize, LocalOffset;
    public DateTime? ModifiedUtc;
    public int AesStrength, AesVersion;
    public ushort AesMethod;
    // From the local header.
    public long DataStart = -1, RangeEnd = -1;
    public ushort LocalDosTime;
    public string? Damaged, Unsupported;
    public bool Overlaps;

    public bool Encrypted => (Flags & 1) != 0;
    public bool IsAes => Method == 99;
    /// <summary>The compression method, inside the AES wrapper when there is one.</summary>
    public ushort RealMethod => IsAes ? AesMethod : Method;
    public int Host => VersionMadeBy >> 8;
}

/// <summary>
/// Reads a ZIP's end of central directory, central directory and every local header (Osmium's own parser, the only
/// one that decides which bytes an entry is). It refuses multi-disk archives and central-directory encryption,
/// checks the entry count against the limit before any entry is read, and computes every entry's byte range (local
/// header, name, extra field, data, data descriptor): entries whose ranges overlap, the shape of the overlapping-entry
/// bomb, are marked and never decoded.
/// </summary>
internal static class ZipDirectory
{
    public sealed record Result(List<ZipRecord> Records, byte[] Comment, bool Zip64);

    private const uint EocdSig = 0x06054b50, Eocd64Sig = 0x06064b50, Locator64Sig = 0x07064b50, CentralSig = 0x02014b50, LocalSig = 0x04034b50, DescriptorSig = 0x08074b50;

    private static CorruptDataException Corrupt(string why) => new($"damaged data ({why})");

    public static Result Read(FileSlice file, ImportLimits limits, int oemCodePage)
    {
        long length = file.Length;
        int tailLength = (int)Math.Min(length, 22 + 65535 + 20);
        var tail = new byte[tailLength];
        file.ReadAt(length - tailLength, tail);
        int at = -1;
        for (int i = tailLength - 22; i >= 0; i--)
        {
            if (Le.U32(tail, i) != EocdSig) continue;
            int commentLength = Le.U16(tail, i + 20);
            if (i + 22 + commentLength == tailLength) { at = i; break; }
            if (at < 0 && i + 22 + commentLength <= tailLength) at = i;
        }
        if (at < 0) throw Corrupt("the end of the ZIP central directory is missing; the file is truncated or not a ZIP");
        long eocdPos = length - tailLength + at;
        var e = tail.AsSpan(at);
        long disk = Le.U16(e, 4), cdDisk = Le.U16(e, 6), onDisk = Le.U16(e, 8), total = Le.U16(e, 10);
        long cdSize = Le.U32(e, 12), cdOffset = Le.U32(e, 16);
        var comment = e.Slice(22, Math.Min(Le.U16(e, 20), e.Length - 22)).ToArray();
        bool zip64 = false;
        long cdEnd = eocdPos;
        if (total == 0xFFFF || onDisk == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF || disk == 0xFFFF)
        {
            if (eocdPos < 20) throw Corrupt("damaged end record of a large archive");
            var loc = new byte[20];
            file.ReadAt(eocdPos - 20, loc);
            if (Le.U32(loc, 0) == Locator64Sig)
            {
                if (Le.U32(loc, 16) > 1) throw new NotSupportedException("Multi-volume ZIP archives are not supported.");
                long pos = (long)Le.U64(loc, 8);
                if (pos < 0 || pos + 56 > eocdPos) throw Corrupt("damaged end record of a large archive");
                var r = new byte[56];
                file.ReadAt(pos, r);
                if (Le.U32(r, 0) != Eocd64Sig) throw Corrupt("damaged end record of a large archive");
                zip64 = true;
                disk = Le.U32(r, 16);
                cdDisk = Le.U32(r, 20);
                onDisk = (long)Math.Min(Le.U64(r, 24), long.MaxValue);
                total = (long)Math.Min(Le.U64(r, 32), long.MaxValue);
                cdSize = (long)Math.Min(Le.U64(r, 40), long.MaxValue);
                cdOffset = (long)Math.Min(Le.U64(r, 48), long.MaxValue);
                cdEnd = pos;
            }
        }
        if (disk != 0 || cdDisk != 0 || onDisk != total) throw new NotSupportedException("Multi-volume ZIP archives are not supported.");
        LimitGuard.CheckEntryCount(total, limits);
        if (cdSize > cdEnd || total > cdSize / 46) throw Corrupt("the central directory is shorter than its entry count says");
        // Data before the archive (a self-extractor's program) shifts every offset.
        long shift = cdEnd - cdSize - cdOffset;
        if (shift < 0) throw Corrupt("the central directory lies outside the file");
        long cdStart = cdOffset + shift;

        var records = new List<ZipRecord>((int)Math.Min(total, 1 << 16));
        using var cd = new FileSlice(file.Handle, cdStart, cdSize);
        var fixedPart = new byte[46];
        long nameChars = 0;
        for (long i = 0; i < total; i++)
        {
            if (cd.Remaining < 46) throw Corrupt("the central directory is shorter than its entry count says");
            cd.ReadExactly(fixedPart);
            if (Le.U32(fixedPart, 0) != CentralSig) throw Corrupt("damaged central directory");
            var rec = new ZipRecord
            {
                Index = (int)i,
                VersionMadeBy = Le.U16(fixedPart, 4),
                Flags = Le.U16(fixedPart, 8),
                Method = Le.U16(fixedPart, 10),
                DosTime = Le.U16(fixedPart, 12),
                DosDate = Le.U16(fixedPart, 14),
                Crc = Le.U32(fixedPart, 16),
                CompressedSize = Le.U32(fixedPart, 20),
                UncompressedSize = Le.U32(fixedPart, 24),
                ExternalAttributes = Le.U32(fixedPart, 38),
                LocalOffset = Le.U32(fixedPart, 42),
            };
            int nameLength = Le.U16(fixedPart, 28), extraLength = Le.U16(fixedPart, 30), commentLength = Le.U16(fixedPart, 32);
            long diskStart = Le.U16(fixedPart, 34);
            if (cd.Remaining < nameLength + extraLength + commentLength) throw Corrupt("damaged central directory");
            rec.NameBytes = new byte[nameLength];
            cd.ReadExactly(rec.NameBytes);
            var extra = new byte[extraLength];
            cd.ReadExactly(extra);
            cd.Position += commentLength;
            if ((rec.Flags & 0x2000) != 0) throw new NotSupportedException("This archive encrypts its list of files with a strong-encryption method Osmium cannot read.");
            string? unicodeName = null;
            DateTime? ntfsTime = null, unixTime = null;
            foreach (var (id, data) in Extras(extra))
            {
                switch (id)
                {
                    case 0x0001:
                    {
                        int p = 0;
                        if (rec.UncompressedSize == 0xFFFFFFFF && p + 8 <= data.Length) { rec.UncompressedSize = (long)Math.Min(Le.U64(data, p), long.MaxValue); p += 8; }
                        if (rec.CompressedSize == 0xFFFFFFFF && p + 8 <= data.Length) { rec.CompressedSize = (long)Math.Min(Le.U64(data, p), long.MaxValue); p += 8; }
                        if (rec.LocalOffset == 0xFFFFFFFF && p + 8 <= data.Length) { rec.LocalOffset = (long)Math.Min(Le.U64(data, p), long.MaxValue); p += 8; }
                        if (diskStart == 0xFFFF && p + 4 <= data.Length) diskStart = Le.U32(data, p);
                        break;
                    }
                    case 0x9901 when data.Length >= 7:
                        rec.AesVersion = Le.U16(data, 0);
                        rec.AesStrength = data[4];
                        rec.AesMethod = Le.U16(data, 5);
                        break;
                    case 0x7075 when data.Length >= 5 && data[0] == 1:
                        if (Crc32.HashToUInt32(rec.NameBytes) == Le.U32(data, 1)) unicodeName = Strict(data.AsSpan(5));
                        break;
                    case 0x000A when data.Length >= 32:
                        for (int p = 4; p + 4 <= data.Length;)
                        {
                            int tag = Le.U16(data, p), size = Le.U16(data, p + 2);
                            if (tag == 1 && size >= 24 && p + 12 <= data.Length) ntfsTime = FileTime((long)Le.U64(data, p + 4));
                            p += 4 + size;
                        }
                        break;
                    case 0x5455 when data.Length >= 5 && (data[0] & 1) != 0:
                        unixTime = DateTime.UnixEpoch.AddSeconds((int)Le.U32(data, 1));
                        break;
                }
            }
            if (diskStart != 0) throw new NotSupportedException("Multi-volume ZIP archives are not supported.");
            rec.Name = (rec.Flags & 0x800) != 0 ? Lenient(rec.NameBytes) : unicodeName ?? Oem(rec.NameBytes, oemCodePage);
            rec.Name = rec.Name.Replace('\\', '/');
            nameChars += rec.Name.Length;
            if (nameChars * 2 > limits.MemoryBudgetBytes / 4) throw new EntryLimitException("The file list of this archive is larger than Osmium accepts.");
            rec.ModifiedUtc = ntfsTime ?? unixTime ?? DosTime(rec.DosDate, rec.DosTime);
            if (rec.LocalOffset > long.MaxValue - shift) throw Corrupt("damaged central directory");
            rec.LocalOffset += shift;
            records.Add(rec);
        }

        ReadLocalHeaders(file, records, cdStart);
        MarkOverlaps(records);
        return new Result(records, comment, zip64);
    }

    private static void ReadLocalHeaders(FileSlice file, List<ZipRecord> records, long cdStart)
    {
        using var local = new FileSlice(file.Handle, 0, file.Length, 512);
        var h = new byte[30];
        foreach (var r in records)
        {
            if (r.LocalOffset + 30 > cdStart) { r.Damaged = "its local header lies outside the entries"; continue; }
            local.ReadAt(r.LocalOffset, h);
            if (Le.U32(h, 0) != LocalSig) { r.Damaged = "its local header is missing"; continue; }
            ushort flags = Le.U16(h, 6), method = Le.U16(h, 8);
            uint crc = Le.U32(h, 14), csize = Le.U32(h, 18), usize = Le.U32(h, 22);
            int nameLength = Le.U16(h, 26), extraLength = Le.U16(h, 28);
            r.LocalDosTime = Le.U16(h, 10);
            long dataStart = r.LocalOffset + 30 + nameLength + extraLength;
            long dataEnd = dataStart + r.CompressedSize;
            if (r.CompressedSize < 0 || dataEnd > cdStart || dataEnd < dataStart) { r.Damaged = "its data runs past the end of the entries"; continue; }
            if (method != r.Method) { r.Damaged = "its local header disagrees with the central directory"; continue; }
            bool descriptor = (flags & 8) != 0;
            if (!descriptor && (crc != r.Crc && !(r.IsAes && crc == 0)
                || csize != 0xFFFFFFFF && csize != r.CompressedSize
                || usize != 0xFFFFFFFF && usize != r.UncompressedSize))
            {
                r.Damaged = "its local header disagrees with the central directory";
                continue;
            }
            r.DataStart = dataStart;
            long end = dataEnd;
            if (descriptor)
            {
                bool zip64 = r.CompressedSize >= 0xFFFFFFFF || r.UncompressedSize >= 0xFFFFFFFF || csize == 0xFFFFFFFF;
                long len = zip64 ? 20 : 12;
                if (dataEnd + 4 <= cdStart)
                {
                    var sig = new byte[4];
                    local.ReadAt(dataEnd, sig);
                    if (Le.U32(sig, 0) == DescriptorSig) len += 4;
                }
                end = Math.Min(cdStart, dataEnd + len);
            }
            r.RangeEnd = end;
        }
    }

    /// <summary>Entries whose byte ranges overlap another entry's are marked (both of each pair).</summary>
    private static void MarkOverlaps(List<ZipRecord> records)
    {
        ZipRecord? widest = null;
        foreach (var r in records.Where(r => r.DataStart >= 0).OrderBy(r => r.LocalOffset))
        {
            if (widest is not null && r.LocalOffset < widest.RangeEnd)
            {
                r.Overlaps = true;
                widest.Overlaps = true;
            }
            if (widest is null || r.RangeEnd > widest.RangeEnd) widest = r;
        }
    }

    private static IEnumerable<(int Id, byte[] Data)> Extras(byte[] extra)
    {
        var list = new List<(int, byte[])>();
        for (int p = 0; p + 4 <= extra.Length;)
        {
            int id = Le.U16(extra, p), size = Le.U16(extra, p + 2);
            if (p + 4 + size > extra.Length) break;
            list.Add((id, extra.AsSpan(p + 4, size).ToArray()));
            p += 4 + size;
        }
        return list;
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    private static string? Strict(ReadOnlySpan<byte> bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    private static string Lenient(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>A name without the UTF-8 flag: the system's OEM code page, or code page 437 when it has none.</summary>
    internal static string Oem(byte[] bytes, int codePage)
    {
        ArchiveSources.EnsureCodePages();
        Encoding enc;
        try { enc = Encoding.GetEncoding(codePage); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { enc = Encoding.GetEncoding(437); }
        return enc.GetString(bytes);
    }

    /// <summary>The OEM code page of the system's language (437 for English, 850, 866, 932…).</summary>
    public static int SystemOemCodePage()
    {
        int cp = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        return cp is 0 or 1 or 65001 ? 437 : cp;
    }

    private static DateTime? DosTime(ushort date, ushort time)
    {
        int year = 1980 + (date >> 9), month = (date >> 5) & 15, day = date & 31;
        int hour = time >> 11, minute = (time >> 5) & 63, second = (time & 31) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59) return null;
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        try { return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local); }
        catch (ArgumentException) { return DateTime.SpecifyKind(local, DateTimeKind.Utc); }
    }

    private static DateTime? FileTime(long ft)
    {
        try { return ft is > 0 and < 2650467743999999999 ? DateTime.FromFileTimeUtc(ft) : null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
