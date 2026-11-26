using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Osmium.Core.Archives.Foreign.Compression;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;
using Osmium.Core.Text;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Deflate64;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.PPMd;

namespace Osmium.Core.Archives.Foreign.Zip;

/// <summary>
/// A ZIP archive, read with Osmium's own directory parser (<see cref="ZipDirectory"/>) and the decoders of the BCL
/// and SharpCompress: Stored, Deflate, Deflate (64 KiB window), BZip2, LZMA, PPMd, Zstandard and xz, encrypted with
/// ZipCrypto or AES. Every file is checked against its CRC-32 (and, for AES, its authentication code, which Osmium
/// computes; AE-2 entries store no CRC). Shrink, Reduce, Implode and PKWARE strong encryption are refused per entry.
/// </summary>
internal sealed class ZipSource : ForeignSource
{
    private static readonly AsyncLocal<int?> OemOverride = new();
    private readonly SafeFileHandle _handle;
    private List<ZipRecord> _records = [];
    private byte[] _comment = [];
    private byte[]? _zipCryptoPassword;

    /// <summary>For tests (this async flow only): the OEM code page for names without the UTF-8 flag.</summary>
    internal static int? OemCodePageOverride { get => OemOverride.Value; set => OemOverride.Value = value; }

    private ZipSource(string path, ArchiveOpenOptions options) : base(path, options) =>
        _handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    public static ZipSource Open(string path, ArchiveOpenOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var s = new ZipSource(path, options);
        try
        {
            s.Load();
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    public override ArchiveKind Kind => ArchiveKind.Zip;
    public override string FormatName => "ZIP";
    private string Name => System.IO.Path.GetFileName(Path);

    private void Load()
    {
        using var file = new FileSlice(_handle, 0, FileLength, 4096);
        var dir = ForeignErrors.Guard(Name, () => ZipDirectory.Read(file, Options.Limits, OemCodePageOverride ?? ZipDirectory.SystemOemCodePage()));
        _records = dir.Records;
        _comment = dir.Comment;
        SetEntries(_records.ConvertAll(Make), complete: true);
    }

    internal static string MethodName(int method) => method switch
    {
        0 => "Stored",
        1 => "Shrink",
        2 or 3 or 4 or 5 => "Reduce",
        6 => "Implode",
        8 => "Deflate",
        9 => "Deflate (64 KiB window)",
        12 => "BZip2",
        14 => "LZMA",
        93 => "Zstandard",
        95 => "xz",
        98 => "PPMd",
        _ => $"method {method}",
    };

    private static bool Readable(int method) => method is 0 or 8 or 9 or 12 or 14 or 93 or 95 or 98;

    private ForeignEntry Make(ZipRecord r)
    {
        int host = r.Host;
        bool dosAttributes = host is 0 or 10 or 14 or 11;
        uint mode = r.ExternalAttributes >> 16;
        bool dir = r.Name.EndsWith('/') || (dosAttributes && (r.ExternalAttributes & 0x10) != 0 && r.UncompressedSize == 0 && r.CompressedSize == 0)
            || (mode & 0xF000) == 0x4000;
        var type = (mode & 0xF000) == 0xA000 || (dosAttributes && (r.ExternalAttributes & 0x400) != 0) ? EntryType.SymbolicLink
            : (mode & 0xF000) is 0x1000 or 0x2000 or 0x6000 or 0xC000 ? EntryType.Special
            : dir ? EntryType.Directory
            : EntryType.File;
        string? unsupported = null;
        if (type == EntryType.File)
        {
            if ((r.Flags & 0x40) != 0) unsupported = "it uses a strong-encryption method Osmium cannot read";
            else if (r.IsAes && (r.AesStrength is < 1 or > 3 || r.AesVersion is not (1 or 2))) unsupported = "it uses an AES encryption variant Osmium cannot read";
            else if (!Readable(r.RealMethod)) unsupported = $"it uses compression method {r.RealMethod} ({MethodName(r.RealMethod)}), which Osmium cannot read";
            else if (r.Encrypted && r.RealMethod == 95) unsupported = "it is xz data inside encryption, which Osmium cannot read";
        }
        else if (type == EntryType.Special) unsupported = "a device, FIFO or socket";
        bool readOnly = dosAttributes ? (r.ExternalAttributes & 1) != 0 : mode != 0 && (mode & 0x80) == 0;
        return new ForeignEntry
        {
            Index = r.Index,
            Path = type == EntryType.Directory ? r.Name.TrimEnd('/') : r.Name,
            Type = type,
            Size = type == EntryType.File ? r.UncompressedSize : null,
            PackedSize = type == EntryType.File ? r.CompressedSize : null,
            ModifiedUtc = r.ModifiedUtc,
            Encrypted = r.Encrypted && type == EntryType.File,
            Method = type == EntryType.File ? MethodName(r.RealMethod) : null,
            ReadOnly = type == EntryType.File && readOnly,
            Hidden = dosAttributes && (r.ExternalAttributes & 2) != 0,
            Unsupported = unsupported,
            Unsafe = r.Overlaps ? "it overlaps another entry in the archive (the shape of a ZIP bomb)" : null,
        };
    }

    protected override void Run(Operation op)
    {
        foreach (var e in Entries)
        {
            op.Token.ThrowIfCancellationRequested();
            if (op.Selected(e) is not { } rel) continue;
            var r = _records[e.Index];
            if (e.HasData && r.Damaged is { } damaged && e.Unsafe is null && e.Unsupported is null)
            {
                op.Sink.Fail(e, $"damaged data ({damaged})");
                continue;
            }
            try { op.Sink.Accept(e, rel, e.HasData ? () => Content(r, e, op) : null); }
            catch (EntryDataException) { } // counted and reported; the next entry is independent
        }
    }

    private static byte CheckByte(ZipRecord r) => (r.Flags & 8) != 0 ? (byte)(r.LocalDosTime >> 8) : (byte)(r.Crc >> 24);

    /// <summary>The entry's data: decrypted, decoded, counted, and checked at its end.</summary>
    private EntryContent Content(ZipRecord r, ForeignEntry e, Operation op)
    {
        var raw = new FileSlice(_handle, r.DataStart, r.CompressedSize) { OnRead = op.Read };
        Stream s = raw;
        WinZipAesStream? aes = null;
        if (r.Encrypted)
        {
            string password = CurrentPassword ?? throw new PasswordRequiredException();
            if (r.IsAes)
            {
                var keys = WinZipAes.Keys(raw, password, r.AesStrength)
                    ?? throw new CorruptDataException("damaged data (wrong password or damaged file: the password verifier does not match)");
                long cipher = r.CompressedSize - WinZipAes.Overhead(r.AesStrength);
                if (cipher < 0) throw new CorruptDataException("damaged data (the entry is too short)");
                s = aes = new WinZipAesStream(raw, keys.Key, keys.MacKey, cipher);
            }
            else
            {
                var zc = new ZipCryptoStream(raw, _zipCryptoPassword ?? Encoding.UTF8.GetBytes(password), CheckByte(r));
                if (!zc.CheckByteMatches) throw new CorruptDataException("damaged data (wrong password or damaged file)");
                s = zc;
            }
        }
        s = Decoder(r, s, e.Path);
        s = new VerifyingStream(new CountingStream(s, op.Guard), r.UncompressedSize, r.IsAes && r.AesVersion == 2 ? null : r.Crc, "the file");
        if (aes is not null) s = WinZipAesStream.CheckAtEnd(s, aes.Finish);
        return new EntryContent(s, Verified: true);
    }

    /// <summary>The decoder for the entry's method, after the memory pre-checks (decoders allocate what they declare).</summary>
    private Stream Decoder(ZipRecord r, Stream s, string name)
    {
        var limits = Options.Limits;
        if (r.RealMethod != 0) s = new EndGuardStream(s, r.RealMethod == 14 ? 0 : 8);
        switch (r.RealMethod)
        {
            case 0: return s;
            case 8: return new DeflateStream(s, CompressionMode.Decompress);
            case 9: return new Deflate64Stream(s, SharpCompress.Compressors.CompressionMode.Decompress);
            case 12: return new BZip2Stream(s, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: false);
            case 14:
            {
                Span<byte> head = stackalloc byte[4];
                s.ReadExactly(head);
                if (BinaryPrimitives.ReadUInt16LittleEndian(head[2..]) != 5) throw new CorruptDataException("damaged data (LZMA properties)");
                var props = new byte[5];
                s.ReadExactly(props);
                long dict = BinaryPrimitives.ReadUInt32LittleEndian(props.AsSpan(1));
                long need = Math.Min(dict, Math.Max(4096, r.UncompressedSize));
                LimitGuard.CheckMemory(name, need + (1 << 20), limits);
                BinaryPrimitives.WriteUInt32LittleEndian(props.AsSpan(1), (uint)need);
                try { return new LzmaStream(props, s, r.CompressedSize - 9, (r.Flags & 2) != 0 ? -1 : r.UncompressedSize); }
                catch (OutOfMemoryException) { throw LimitGuard.MemoryRefusal(name, dict); }
            }
            case 98:
            {
                Span<byte> p = stackalloc byte[2];
                s.ReadExactly(p);
                int v = BinaryPrimitives.ReadUInt16LittleEndian(p);
                int order = (v & 0x0F) + 1, restore = v >> 12;
                long memory = (long)(((v >> 4) & 0xFF) + 1) << 20;
                if (order < 2 || restore > 1) throw new CorruptDataException("damaged data (PPMd properties)");
                LimitGuard.CheckMemory(name, memory + (1 << 20), limits);
                try { return new PpmdStream(new PpmdProperties(p), s, false); }
                catch (OutOfMemoryException) { throw LimitGuard.MemoryRefusal(name, memory); }
            }
            case 93:
                if (s is FileSlice slice) return ZstdFrames.Open(slice, ZstdFrames.Walk(slice, limits, name), limits);
                return ZstdFrames.Open(s, new ZstdFrames.Layout(1, null, false, Math.Min(limits.MemoryBudgetBytes, 1L << 31)), limits);
            case 95:
                return new XzDecoder(s, limits, name);
            default:
                throw new NotSupportedException($"{DisplayText.Escape(name)} uses compression method {r.RealMethod}, which Osmium cannot read.");
        }
    }

    /// <summary>
    /// Rule 1 before anything is written. AES: the verifier, then the authentication code over the encrypted bytes
    /// (read once, nothing decrypted or decoded). ZipCrypto: the check byte, then the whole entry decoded into a
    /// discarding sink and its CRC-32 compared, trying the password as UTF-8 and then in the OEM code page.
    /// </summary>
    protected override bool VerifyPassword(IReadOnlyList<ForeignEntry> encrypted, string password, Operation op)
    {
        // The first selected encrypted entry that can be read decides; damaged ones are passed over (they fail on
        // their own when extracted) and, when every one is damaged, nothing proves the password.
        foreach (var entry in encrypted)
        {
            var r = _records[entry.Index];
            if (r.Damaged is not null || r.DataStart < 0) continue;
            bool? right;
            try { right = r.IsAes ? CheckAes(r, password, op) : CheckZipCrypto(r, entry, password, op); }
            catch (Exception ex) when (ForeignErrors.IsDecoderFailure(ex)) { continue; }
            if (right == true) return true;
            if (right == false) throw new WrongPasswordException();
        }
        return false;
    }

    /// <summary>The verifier, then the authentication code over the encrypted bytes (read once, nothing decoded).</summary>
    private bool? CheckAes(ZipRecord r, string password, Operation op)
    {
        using var raw = new FileSlice(_handle, r.DataStart, r.CompressedSize) { OnRead = op.Read };
        long cipher = r.CompressedSize - WinZipAes.Overhead(r.AesStrength);
        if (cipher < 0) return null;
        var keys = WinZipAes.Keys(raw, password, r.AesStrength);
        return keys is not null && WinZipAes.Authentic(raw, keys.Value.MacKey, cipher, op.Token);
    }

    /// <summary>The check byte, then the whole entry decoded into a discarding sink and its CRC-32 compared, with the
    /// password as UTF-8 and then in the OEM code pages.</summary>
    private bool? CheckZipCrypto(ZipRecord r, ForeignEntry entry, string password, Operation op)
    {
        if (r.CompressedSize < 12) return null;
        var candidates = new List<byte[]> { Encoding.UTF8.GetBytes(password) };
        ArchiveSources.EnsureCodePages();
        foreach (int cp in new[] { OemCodePageOverride ?? ZipDirectory.SystemOemCodePage(), 437, 850, 1252 })
        {
            byte[] b;
            try { b = Encoding.GetEncoding(cp, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback).GetBytes(password); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
            if (!candidates.Any(c => c.AsSpan().SequenceEqual(b))) candidates.Add(b);
        }
        var buf = new byte[1 << 16];
        foreach (var candidate in candidates)
        {
            using var raw = new FileSlice(_handle, r.DataStart, r.CompressedSize) { OnRead = op.Read };
            var zc = new ZipCryptoStream(raw, candidate, CheckByte(r));
            if (!zc.CheckByteMatches) continue;
            try
            {
                using var check = new VerifyingStream(new CountingStream(Decoder(r, zc, entry.Path), op.Guard), r.UncompressedSize, r.Crc, "the file");
                var sink = new DiscardSink();
                int n;
                while ((n = check.Read(buf, 0, buf.Length)) > 0)
                {
                    sink.Write(buf, 0, n);
                    op.Progress.Checking(sink.Written, r.UncompressedSize);
                }
                _zipCryptoPassword = candidate;
                return true;
            }
            catch (Exception ex) when (ForeignErrors.IsDecoderFailure(ex)) { } // not this one
        }
        return false;
    }

    protected override IReadOnlyList<ArchiveDetail> Describe()
    {
        var files = Entries.Where(e => e.HasData).ToList();
        var d = new List<ArchiveDetail>
        {
            new("Format", FormatName),
            new("Files", Count(files.Count)),
            new("Folders", Count(Entries.Count(e => e.Type == EntryType.Directory))),
        };
        int links = Entries.Count(e => e.Type == EntryType.SymbolicLink), specials = Entries.Count(e => e.Type == EntryType.Special);
        if (links > 0) d.Add(new("Links", $"{Count(links)} (not extracted)"));
        if (specials > 0) d.Add(new("Other entries", $"{Count(specials)} (not extracted)"));
        if (TotalSize is { } total) d.Add(new("Original size", Bytes(total)));
        d.Add(new("Archive size", Bytes(FileLength)));
        var methods = files.Select(e => e.Method).Distinct().ToList();
        if (methods.Count > 0) d.Add(new("Methods", string.Join(", ", methods)));
        var kinds = _records.Where(r => r.Encrypted).Select(r => r.IsAes ? $"AES-{WinZipAes.KeyLength(Math.Clamp(r.AesStrength, 1, 3)) * 8}" : "ZipCrypto").Distinct().ToList();
        d.Add(new("Encrypted", kinds.Count == 0 ? "no" : $"yes ({string.Join(", ", kinds)})"));
        int overlapping = Entries.Count(e => e.Unsafe is not null);
        if (overlapping > 0) d.Add(new("Overlapping entries", $"{Count(overlapping)} (refused)"));
        if (_comment.Length > 0)
        {
            string comment = ZipDirectory.Oem(_comment, OemCodePageOverride ?? ZipDirectory.SystemOemCodePage());
            if (_comment.AsSpan().IndexOfAnyExceptInRange((byte)0, (byte)0x7F) >= 0 && TryUtf8(_comment) is { } utf8) comment = utf8;
            if (comment.Length > 500) comment = comment[..500] + "…";
            d.Add(new("Comment", DisplayText.Escape(comment)));
        }
        return d;
    }

    private static string? TryUtf8(byte[] b)
    {
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { return null; }
    }

    public override void Dispose() => _handle.Dispose();
}
