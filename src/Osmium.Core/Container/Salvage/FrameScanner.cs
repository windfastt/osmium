using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;
using Osmium.Core.Crypto;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Container;

/// <summary>Positional, thread-safe reads over the archive being salvaged.</summary>
internal sealed class SalvageSource : IDisposable
{
    private readonly SafeFileHandle _handle;

    public string Path { get; }
    public long Length { get; }

    public SalvageSource(string path)
    {
        Path = path;
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
    }

    /// <summary>Reads exactly <paramref name="dst"/>.Length bytes; the caller has checked the bounds.</summary>
    public void Read(long offset, Span<byte> dst)
    {
        int total = 0;
        while (total < dst.Length)
        {
            int n = RandomAccess.Read(_handle, dst[total..], offset + total);
            if (n <= 0) throw new EndOfStreamException("The archive became shorter while it was being salvaged.");
            total += n;
        }
    }

    public byte[] Read(long offset, int count)
    {
        var buf = GC.AllocateUninitializedArray<byte>(count);
        Read(offset, buf);
        return buf;
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>A frame header whose marker and CRC check out (FORMAT §2).</summary>
internal readonly record struct FrameHeader(long Offset, uint Flags, ulong Number, long BodyLength);

/// <summary>
/// The key, archive id and header bytes that authenticate an encrypted archive's frames. The index frame binds the exact
/// header bytes; when the header is damaged, <paramref name="OtherHeaderBytes"/> are rebuilt candidates to try for it.
/// </summary>
internal sealed record KeyMaterial(ArchiveKey Key, byte[] ArchiveId, byte[] HeaderBytes, IReadOnlyList<byte[]>? OtherHeaderBytes = null);

/// <summary>
/// The key fields of a damaged encrypted header, read from their fixed places (FORMAT §1, §8) whatever happened to the
/// marker, version, flags, lengths, record types and algorithm ids around them: the archive id at offset 16, and the
/// key-derivation settings, salt and verifier in the first extension record, whose type, length and kdf id take one
/// byte each (offsets 36 to 38). They are used only once they authenticate a frame.
/// </summary>
/// <param name="KdfByteIntact">The kdf byte read 1 or 2, so only its layout was read; otherwise the fields were read in
/// both layouts, each a candidate of its own.</param>
internal sealed record KeyFields(byte[] ArchiveId, EncryptionInfo Encryption, int EncryptionRecordEnd, byte[] Head, bool KdfByteIntact)
{
    /// <summary>The header bytes salvage reads: the fixed part, the record's three bytes, the longest settings (three
    /// 10-byte varints), salt, cipher id and verifier, a creator record of up to 127 bytes and the CRC.</summary>
    private const int HeadBytes = 36 + 3 + 30 + 32 + 1 + 16 + 2 + 127 + 4;

    /// <summary>
    /// The candidates: the layout of the kdf byte when it reads 1 or 2, else Argon2id first (what Osmium writes since
    /// 0.2.0), then PBKDF2. A reading outside the limits a reader accepts is no candidate.
    /// </summary>
    public static List<KeyFields> Read(SalvageSource src)
    {
        var found = new List<KeyFields>();
        if (src.Length < 36 + 3 + 1 + 32 + 1 + 16) return found;
        var head = src.Read(0, (int)Math.Min(src.Length, HeadBytes));
        byte kdf = head[38];
        bool intact = kdf is (byte)KdfAlgorithm.Pbkdf2Sha512 or (byte)KdfAlgorithm.Argon2id;
        var layouts = intact ? [(KdfAlgorithm)kdf] : new[] { KdfAlgorithm.Argon2id, KdfAlgorithm.Pbkdf2Sha512 };
        foreach (var layout in layouts)
        {
            EncryptionInfo info;
            int consumed;
            try { info = EncryptionRecord.ReadFields(head.AsSpan(39), layout, checkCipher: false, out consumed); }
            catch (CorruptDataException) { continue; }
            if (info.Kdf.Check().Kind == KdfVerdictKind.Invalid) continue;
            // The record as the writer made it: these bytes as they lie, with the kdf id of this layout and the only
            // cipher there is (either byte may be what was damaged).
            var record = new byte[1 + consumed];
            record[0] = (byte)layout;
            head.AsSpan(39, consumed).CopyTo(record.AsSpan(1));
            record[^(EncryptionRecord.VerifierBytes + 1)] = EncryptionRecord.Aes256Gcm;
            found.Add(new KeyFields(head.AsSpan(16, 16).ToArray(), info with { RecordBytes = record }, 39 + consumed, head, intact));
        }
        return found;
    }

    /// <summary>
    /// The header as a writer makes it from these fields, with the creator text found where it lies when the header
    /// ends at <paramref name="headerEnd"/> (where the first frame starts): candidates for the bytes the index binds.
    /// The minor version is the one stored in the damaged header, this version's, or 0 (archives of Osmium 0.1.x).
    /// </summary>
    public IEnumerable<byte[]> Rebuilds(byte[] archiveId, long headerEnd)
    {
        long creatorAt = EncryptionRecordEnd + 2, creatorLength = headerEnd - 4 - creatorAt;
        string? creator = creatorLength >= 0 && creatorLength < 128 && headerEnd <= Head.Length
            ? System.Text.Encoding.UTF8.GetString(Head, (int)creatorAt, (int)creatorLength) : null;
        var minors = new[] { BinaryPrimitives.ReadUInt16LittleEndian(Head.AsSpan(10)), Osa.Minor, (ushort)0 }.Distinct();
        foreach (ushort minor in minors)
            foreach (string? c in creator is null ? new string?[] { null } : [creator, null])
                foreach (uint flags in new[] { Osa.FlagEncrypted, Osa.FlagEncrypted | Osa.FlagExperimental })
                    yield return new ArchiveHeader { Minor = minor, Flags = flags, ArchiveId = archiveId, Encryption = Encryption, Creator = c }.Serialize();
    }
}

/// <summary>An intact footer (FORMAT §4).</summary>
internal readonly record struct FooterInfo(long IndexOffset, long IndexLength, ulong IdPrefix)
{
    public static FooterInfo? At(SalvageSource src, long at)
    {
        if (at < 0 || at + Osa.FooterSize > src.Length) return null;
        var b = src.Read(at, Osa.FooterSize);
        if (!b.AsSpan(28, 4).SequenceEqual(Osa.FooterMagic) || Osa.Crc(b.AsSpan(0, 24)) != BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24)))
            return null;
        return new FooterInfo(BinaryPrimitives.ReadInt64LittleEndian(b), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16)));
    }
}

/// <summary>What was found where the archive header should be, and the footer at the end of the file.</summary>
internal sealed record HeaderProbe(ArchiveHeader? Header, long Offset, ArchiveHeader? Repaired, string Status, FooterInfo? EndFooter)
{
    /// <summary>The first 8 bytes of this archive's id, from the header or else from the footer at the end of the file.</summary>
    public ulong? IdPrefix => Header is { } h ? BinaryPrimitives.ReadUInt64LittleEndian(h.ArchiveId) : EndFooter?.IdPrefix;

    /// <summary>
    /// Looks for an intact header at offset 0, then (for data prepended to an archive) anywhere before the first frame.
    /// A header after offset 0 is used only when the footer at the end of the file names the same archive: an archive
    /// stored in a block whose start is damaged has an intact header too. When no header is used,
    /// <see cref="Repaired"/> holds the fields of the damaged header at offset 0 read with its CRC recomputed: they are
    /// used only when something else confirms them (the password verifier and frame tags). An intact header at offset 0
    /// that this version cannot read (a newer format or feature) is refused, as opening it would be.
    /// </summary>
    public static HeaderProbe Find(SalvageSource src, long firstFrame)
    {
        var footer = FooterInfo.At(src, src.Length - Osa.FooterSize);
        var header = ReadAt(src, 0, repair: false, out string problem, out var unreadable);
        if (header is not null) return new(header, 0, null, "intact", footer);
        if (unreadable is not null) ExceptionDispatchInfo.Throw(unreadable);
        string? untied = null;
        foreach (long at in Occurrences(src, Osa.Magic.ToArray(), 1, Math.Min(firstFrame, src.Length), limit: 64))
        {
            var h = ReadAt(src, at, repair: false, out _, out var newer);
            if (h is null && newer is null) continue; // not a header
            if (footer is not { } ft || BinaryPrimitives.ReadUInt64LittleEndian(src.Read(at + 16, 8)) != ft.IdPrefix)
            {
                untied ??= $"; the intact header at offset {at:N0} was not used, because "
                    + (footer is null ? "nothing ties it to this file" : "the footer at the end of the file names another archive");
                continue;
            }
            if (h is not null) return new(h, at, null, $"intact at offset {at:N0} (the {at:N0} bytes before it are not part of the archive)", footer);
            ExceptionDispatchInfo.Throw(newer!); // this archive's own header, written by a newer version
        }
        var repaired = ReadAt(src, 0, repair: true, out _, out _);
        return new(null, 0, repaired, $"damaged ({problem}){untied}", footer);
    }

    private static ArchiveHeader? ReadAt(SalvageSource src, long at, bool repair, out string problem, out Exception? unreadable)
    {
        problem = "";
        unreadable = null;
        if (src.Length - at < 40) { problem = "the file is too short to hold one"; return null; }
        var fixedPart = src.Read(at, 36);
        if (!fixedPart.AsSpan(0, 8).SequenceEqual(Osa.Magic)) { problem = "its marker is missing"; return null; }
        uint extLen = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(32));
        if (extLen > 1 << 20 || 40 + extLen > src.Length - at) { problem = "its length field is invalid"; return null; }
        var raw = src.Read(at, 40 + (int)extLen);
        int crcAt = raw.Length - 4;
        uint crc = Osa.Crc(raw.AsSpan(0, crcAt));
        bool crcOk = crc == BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(crcAt));
        if (!crcOk)
        {
            problem = "CRC mismatch";
            if (!repair) return null;
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(crcAt), crc);
        }
        try
        {
            return ArchiveHeader.Read(new MemoryStream(raw, writable: false));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            if (crcOk) unreadable = ex; // intact, so not damage: a newer format, or a feature this version lacks
            problem = ex.Message.TrimEnd('.');
            return null;
        }
    }

    /// <summary>The first <paramref name="limit"/> offsets of <paramref name="needle"/> in [from, until).</summary>
    private static List<long> Occurrences(SalvageSource src, byte[] needle, long from, long until, int limit)
    {
        var found = new List<long>();
        var buf = new byte[1 << 20];
        long pos = from;
        while (pos + needle.Length <= until && found.Count < limit)
        {
            int n = (int)Math.Min(buf.Length, until - pos);
            src.Read(pos, buf.AsSpan(0, n));
            for (int i = 0; found.Count < limit;)
            {
                int j = buf.AsSpan(i, n - i).IndexOf(needle);
                if (j < 0) break;
                found.Add(pos + i + j);
                i += j + 1;
            }
            if (pos + n >= until) break;
            pos += n - (needle.Length - 1);
        }
        return found;
    }
}

/// <summary>
/// Finds frames by their headers (marker + CRC) rather than through the index or the footer, and verifies their
/// bodies: CRC and payload hash for plain frames, the GCM tag for encrypted ones. Decoding and the content hash come
/// later (<see cref="Decode"/>), so that the scan needs little memory.
/// </summary>
internal sealed class FrameScanner
{
    private const int ChunkSize = 1 << 20;
    /// <summary>Longest possible block-body prefix: descriptor (≤ 64 KiB, 3-byte length) + hashes + varint + CRC.</summary>
    private const int MaxPrefix = 3 + (1 << 16) + 16 + 8 + 10 + 4;
    /// <summary>The reader's limit on a frame body.</summary>
    private const long MaxBody = BlockLimits.MaxRawSize * 2;
    /// <summary>The index holds at most 2^24 blocks.</summary>
    internal const ulong MaxBlockFrames = 1 << 24;

    private readonly SalvageSource _src;
    private readonly MethodRegistry _registry;
    private readonly Action<SalvageProgress>? _progress;
    private readonly CancellationToken _ct;
    // The bytes the header search read last (kept: after a frame that fails, the search resumes one byte later),
    // and a separate buffer for hashing payloads.
    private readonly byte[] _chunk = new byte[ChunkSize], _work = new byte[ChunkSize];
    private long _chunkStart;
    private int _chunkLength;
    private long _bytesChecked; // read for verification: payloads hashed and bodies decrypted
    private long _lastReport;

    public FrameScanner(SalvageSource src, MethodRegistry registry, Action<SalvageProgress>? progress, CancellationToken ct)
    {
        _src = src;
        _registry = registry;
        _progress = progress;
        _ct = ct;
    }

    /// <summary>Bytes read for verification so far: payloads hashed and bodies decrypted.</summary>
    internal long BytesChecked => _bytesChecked;

    /// <summary>Whether this archive's frames are encrypted (a frame of the other kind is not this archive's).</summary>
    public bool ArchiveEncrypted { get; set; }
    public KeyMaterial? Key { get; set; }
    /// <summary>Why encrypted frames cannot be decrypted when <see cref="Key"/> is null.</summary>
    public string? KeyProblem { get; set; }

    internal static string Location(SalvageFrame f) => f.IsIndex ? "the index" : $"block {f.Number}";

    /// <summary>Finds the next frame header at or after <paramref name="from"/>.</summary>
    public bool NextFrameHeader(long from, out FrameHeader header)
    {
        Span<byte> h = stackalloc byte[Osa.FrameHeaderSize];
        // Fast path: in an intact archive the next frame starts where the previous one ended.
        if (from >= 0 && from + Osa.FrameHeaderSize <= _src.Length)
        {
            _src.Read(from, h);
            if (TryParse(h, from, out header)) return true;
        }
        long pos = Math.Max(0, from);
        while (pos + Osa.FrameHeaderSize <= _src.Length)
        {
            _ct.ThrowIfCancellationRequested();
            if (pos < _chunkStart || pos + Osa.FrameMagic.Length > _chunkStart + _chunkLength)
            {
                _chunkLength = (int)Math.Min(_chunk.Length, _src.Length - pos);
                _src.Read(pos, _chunk.AsSpan(0, _chunkLength));
                _chunkStart = pos;
            }
            int n = _chunkLength;
            for (int i = (int)(pos - _chunkStart); ;)
            {
                int j = _chunk.AsSpan(i, n - i).IndexOf(Osa.FrameMagic);
                if (j < 0) break;
                long at = _chunkStart + i + j;
                if (at + Osa.FrameHeaderSize > _src.Length) { header = default; return false; }
                if (i + j + Osa.FrameHeaderSize > n) _src.Read(at, h); // the header straddles the chunk's end
                if (TryParse(i + j + Osa.FrameHeaderSize <= n ? _chunk.AsSpan(i + j, Osa.FrameHeaderSize) : h, at, out header)) return true;
                i += j + 1;
            }
            if (_chunkStart + n >= _src.Length) break;
            pos = _chunkStart + n - (Osa.FrameMagic.Length - 1); // fewer bytes than a marker left: read on
            Report("Scanning", pos, _src.Length);
        }
        header = default;
        return false;
    }

    /// <summary>The frame header at <paramref name="at"/>, if one is there.</summary>
    public FrameHeader? ReadFrameHeader(long at)
    {
        if (at < 0 || at + Osa.FrameHeaderSize > _src.Length) return null;
        Span<byte> h = stackalloc byte[Osa.FrameHeaderSize];
        _src.Read(at, h);
        return TryParse(h, at, out var header) ? header : null;
    }

    private static bool TryParse(ReadOnlySpan<byte> h, long at, out FrameHeader header)
    {
        header = default;
        if (!h[..4].SequenceEqual(Osa.FrameMagic)) return false;
        if (Osa.Crc(h[..24]) != BinaryPrimitives.ReadUInt32LittleEndian(h[24..])) return false;
        header = new FrameHeader(at, BinaryPrimitives.ReadUInt32LittleEndian(h[4..]), BinaryPrimitives.ReadUInt64LittleEndian(h[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(h[16..]));
        return true;
    }

    /// <summary>
    /// Scans from <paramref name="start"/> to the end of the file. A frame whose body verified is skipped as a whole
    /// (whatever it holds, e.g. another archive stored in it, is content). After a frame that did not verify, the scan
    /// resumes one byte later, so frames that follow a missing or shortened region are still found; frames that lie in
    /// the bytes such a frame claimed are marked as enclosed by it.
    /// </summary>
    public List<SalvageFrame> ScanAll(long start)
    {
        var frames = new List<SalvageFrame>();
        long pos = start, claimedEnd = 0, claimant = -1;
        // Frames inside the bytes a damaged frame claims are checked within a budget. Honest damage puts few frames
        // there, but a crafted file can stack thousands whose claimed bodies all run to the end of the file, and
        // checking each would cost the whole rest of the file. Frames outside such bytes never overlap.
        long budget = Math.Max(64L << 20, 4 * _src.Length), spent = 0;
        while (NextFrameHeader(pos, out var fh))
        {
            var f = new SalvageFrame
            {
                Offset = fh.Offset,
                Number = fh.Number,
                BodyLength = fh.BodyLength,
                IsIndex = (fh.Flags & Osa.FrameFlagIndex) != 0,
                Encrypted = (fh.Flags & Osa.FrameFlagEncrypted) != 0,
                EnclosingOffset = fh.Offset < claimedEnd ? claimant : null,
            };
            if (f.EnclosingOffset is not null && spent > budget)
                f.Reject(SalvageFrameState.Ignored, "not checked: the damaged frames overlapping here already cost too much to check");
            else
            {
                long before = _bytesChecked;
                Verify(f, fh.Flags);
                if (f.EnclosingOffset is not null && !f.Intact) spent += _bytesChecked - before;
            }
            frames.Add(f);
            bool plausible = fh.BodyLength >= 0 && fh.BodyLength <= MaxBody;
            long end = plausible ? Math.Min(_src.Length, fh.Offset + Osa.FrameHeaderSize + fh.BodyLength) : fh.Offset + 1;
            if (f.Intact) pos = end;
            else
            {
                if (plausible && end > claimedEnd) { claimedEnd = end; claimant = fh.Offset; }
                pos = fh.Offset + 1;
            }
            Report("Scanning", pos, _src.Length);
        }
        return frames;
    }

    private void Verify(SalvageFrame f, uint flags)
    {
        // The header CRC checked out, so a flag this version does not know was set by a newer one (as in OsaReader).
        if ((flags & ~(Osa.FrameFlagEncrypted | Osa.FrameFlagIndex)) is var unknown and not 0)
        {
            f.NeedsNewerVersion(new NewerVersionException($"frame flag 0x{unknown:X}", Location(f)));
            return;
        }
        if (f.IsIndex != (f.Number == Osa.IndexFrameNumber)) { f.Reject(SalvageFrameState.Damaged, "frame type and frame number disagree"); return; }
        if (!f.IsIndex && f.Number >= MaxBlockFrames) { f.Reject(SalvageFrameState.Damaged, "frame number out of range"); return; }
        if (f.BodyLength < 0 || f.BodyLength > MaxBody || f.BodyLength > Array.MaxLength)
        {
            f.Reject(SalvageFrameState.Damaged, "invalid frame length");
            return;
        }
        long available = _src.Length - f.Offset - Osa.FrameHeaderSize;
        if (f.BodyLength > available)
        {
            f.Reject(SalvageFrameState.Damaged, $"truncated: the file ends {available:N0} bytes into its {f.BodyLength:N0}-byte body");
            return;
        }
        if (f.Encrypted != ArchiveEncrypted)
        {
            f.Reject(SalvageFrameState.Ignored, ArchiveEncrypted
                ? "not encrypted, so it cannot be authenticated as part of this encrypted archive"
                : "encrypted, but this archive is not");
            return;
        }
        if (!f.Encrypted)
        {
            VerifyPlain(f);
            return;
        }
        if (Key is null)
        {
            f.Reject(SalvageFrameState.NotDecrypted, null); // why is said once, for the archive (KeyProblem)
            return;
        }
        var plain = TryOpen(f);
        if (plain is null)
        {
            f.Reject(SalvageFrameState.Damaged, "authentication failed (damaged or tampered)");
            return;
        }
        if (!TryParsePrefix(plain, plain.Length, out var p, out string why)) { f.Reject(SalvageFrameState.Damaged, why); return; }
        if (XxHash3.HashToUInt64(plain.AsSpan(p.PayloadOffset)) != p.PayloadHash)
        {
            f.Reject(SalvageFrameState.Damaged, "payload hash mismatch");
            return;
        }
        Accept(f, p);
    }

    /// <summary>Checks a plain frame's body without holding it in memory: prefix CRC, then the payload hash in chunks.</summary>
    private void VerifyPlain(SalvageFrame f)
    {
        long bodyStart = f.Offset + Osa.FrameHeaderSize;
        var prefix = _src.Read(bodyStart, (int)Math.Min(f.BodyLength, MaxPrefix));
        _bytesChecked += prefix.Length;
        if (!TryParsePrefix(prefix, f.BodyLength, out var p, out string why)) { f.Reject(SalvageFrameState.Damaged, why); return; }
        var hash = new XxHash3();
        long done = Math.Min(prefix.Length - p.PayloadOffset, p.PayloadLength);
        hash.Append(prefix.AsSpan(p.PayloadOffset, (int)done));
        while (done < p.PayloadLength)
        {
            _ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(_work.Length, p.PayloadLength - done);
            _src.Read(bodyStart + p.PayloadOffset + done, _work.AsSpan(0, n));
            hash.Append(_work.AsSpan(0, n));
            done += n;
            _bytesChecked += n;
            Report("Scanning", bodyStart + p.PayloadOffset + done, _src.Length);
        }
        if (hash.GetCurrentHashAsUInt64() != p.PayloadHash)
        {
            f.Reject(SalvageFrameState.Damaged, "payload hash mismatch");
            return;
        }
        Accept(f, p);
    }

    private void Accept(SalvageFrame f, in BodyPrefix p)
    {
        f.Intact = true;
        f.RawHash = p.RawHash;
        f.PayloadHash = p.PayloadHash;
        f.PayloadLength = p.PayloadLength;
        f.DescriptorBytes = p.Descriptor;
        BlockDescriptor d;
        try
        {
            // A pipeline of a newer version gets a stand-in (as in the index) that keeps the stored bytes and raw size.
            d = BlockDescriptor.DeserializeOrStandIn(p.Descriptor, _registry, Location(f));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            f.Reject(SalvageFrameState.Damaged, "invalid pipeline descriptor: " + ex.Message.TrimEnd('.'));
            return;
        }
        f.Descriptor = d;
        f.RawSize = d.RawSize;
        if (d.NewerVersion is { } newer)
        {
            f.NeedsNewerVersion(newer);
            return;
        }
        f.Pipeline = d.Spec.ToString();
        f.State = SalvageFrameState.Verified; // the body checks out; Decode verifies the content
    }

    private readonly record struct BodyPrefix(byte[] Descriptor, UInt128 RawHash, ulong PayloadHash, long PayloadLength, int PayloadOffset);

    /// <summary>Parses the block-body prefix (FORMAT §2.1) and checks its CRC and the payload length.</summary>
    private static bool TryParsePrefix(ReadOnlySpan<byte> data, long bodyLength, out BodyPrefix prefix, out string why)
    {
        prefix = default;
        try
        {
            var r = new BlobReader(data);
            var desc = r.Bytes(1 << 16).ToArray();
            var rawHash = BinaryPrimitives.ReadUInt128LittleEndian(r.Raw(16));
            ulong payloadHash = r.U64();
            ulong payloadLen = r.U();
            int prefixLen = r.Position;
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(r.Raw(4));
            if (Osa.Crc(data[..prefixLen]) != crc) { why = "block header CRC mismatch"; return false; }
            if (payloadLen != (ulong)(bodyLength - r.Position)) { why = "payload length does not match the frame length"; return false; }
            prefix = new BodyPrefix(desc, rawHash, payloadHash, (long)payloadLen, r.Position);
            why = "";
            return true;
        }
        catch (InvalidDataException)
        {
            why = "block header is unreadable";
            return false;
        }
    }

    private byte[]? TryOpen(SalvageFrame f)
    {
        var body = _src.Read(f.Offset + Osa.FrameHeaderSize, (int)f.BodyLength);
        _bytesChecked += body.Length;
        var key = Key!;
        foreach (var headerBytes in f.IsIndex ? [key.HeaderBytes, .. key.OtherHeaderBytes ?? []] : new[] { Array.Empty<byte>() })
        {
            try
            {
                var plain = key.Key.Open(key.ArchiveId, f.Number, body, headerBytes);
                // A rebuilt header that authenticates the index is the original one: keep it for decoding.
                if (f.IsIndex && headerBytes != key.HeaderBytes) Key = key with { HeaderBytes = headerBytes, OtherHeaderBytes = null };
                return plain;
            }
            catch (CorruptDataException) { }
        }
        return null;
    }

    /// <summary>
    /// Tries a candidate key on the first encrypted block frames. True when one authenticates, false when some were
    /// tried and none did, null when there was nothing to try.
    /// </summary>
    public bool? Authenticates(ArchiveKey key, byte[] archiveId)
    {
        int tried = 0;
        long pos = 0;
        while (tried < 8 && NextFrameHeader(pos, out var fh))
        {
            pos = fh.Offset + 1;
            bool usable = (fh.Flags & Osa.FrameFlagEncrypted) != 0 && (fh.Flags & Osa.FrameFlagIndex) == 0
                && fh.BodyLength >= 16 && fh.BodyLength <= MaxBody && fh.BodyLength <= _src.Length - fh.Offset - Osa.FrameHeaderSize;
            if (!usable) continue;
            tried++;
            var body = _src.Read(fh.Offset + Osa.FrameHeaderSize, (int)fh.BodyLength);
            try
            {
                key.Open(archiveId, fh.Number, body);
                return true;
            }
            catch (CorruptDataException) { }
        }
        return tried == 0 ? null : false;
    }

    /// <summary>
    /// Re-reads a frame whose body verified, decodes it with its pipeline and checks every stage size and the content
    /// hash. Throws when anything does not check out. Thread-safe.
    /// </summary>
    public byte[] Decode(SalvageFrame f, CancellationToken ct)
    {
        var body = _src.Read(f.Offset + Osa.FrameHeaderSize, (int)f.BodyLength);
        var plain = f.Encrypted ? Key!.Key.Open(Key.ArchiveId, f.Number, body, f.IsIndex ? Key.HeaderBytes : []) : body;
        var parsed = BlockBody.Parse(plain, _registry, Location(f));
        if (parsed.RawHash != f.RawHash) throw new CorruptDataException("the frame changed while it was being salvaged");
        var raw = PipelineExecutor.Decode(parsed.Descriptor, parsed.Payload, new CodecContext { Token = ct });
        if (BlockBody.Hash128(raw) != parsed.RawHash) throw new CorruptDataException("decoded content does not match its hash");
        return raw;
    }

    /// <summary>Reports progress at most every 100 ms.</summary>
    public void Report(string phase, long done, long total)
    {
        if (_progress is null) return;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastReport) < 100) return;
        Interlocked.Exchange(ref _lastReport, now);
        _progress(new SalvageProgress(phase, done, total));
    }
}
