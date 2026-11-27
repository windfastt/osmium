using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Osmium.Core.Crypto;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Container;

public sealed class PasswordRequiredException() : Exception("This archive is encrypted. A password is required.");
public sealed class WrongPasswordException() : Exception("Wrong password.");

/// <summary>How <see cref="OsaReader.Open(string, OsaOpenOptions)"/> opens an archive.</summary>
public sealed record OsaOpenOptions
{
    /// <summary>Called only for an encrypted archive, and only after every check that needs no password; return null
    /// to cancel (<see cref="PasswordRequiredException"/>).</summary>
    public Func<string?>? Password { get; init; }
    public MethodRegistry? Registry { get; init; }
    /// <summary>The most memory the key derivation may use (the decode memory limit); above it,
    /// <see cref="KdfMemoryLimitException"/> before the password is asked for.</summary>
    public long KdfMemoryLimit { get; init; } = MemoryBudgets.Default();
    /// <summary>Whether protection weaker than any Osmium preset, and unprotected data beside the encrypted parts, are
    /// refused (the default) or allowed after the user agreed.</summary>
    public ProtectionPolicy Protection { get; init; } = ProtectionPolicy.Default;
    /// <summary>Allow a key derivation that takes more work than the strongest preset (the user agreed).</summary>
    public bool AllowSlowKdf { get; init; }
    /// <summary>Cancels the key derivation.</summary>
    public CancellationToken Cancellation { get; init; }
    /// <summary>Progress of the key derivation, 0 to 1 ("Checking the password…").</summary>
    public IProgress<double>? KdfProgress { get; init; }
    /// <summary>Keys derived before for this archive (no password and no derivation are needed when one is here and
    /// matches the header's verifier); a key this open derives is added.</summary>
    public ArchiveKeyCache? KeyCache { get; init; }
    /// <summary>Tests: (offset, count) of a read that fails with an I/O error, as a bad sector does.</summary>
    internal Func<long, int, bool>? TestUnreadable { get; init; }
}

/// <summary>Random-access reader. Thread-safe for concurrent block reads.</summary>
public sealed class OsaReader : IDisposable
{
    private readonly ArchiveFile _handle;
    private readonly ArchiveKey? _key;
    private readonly MethodRegistry _registry;
    private readonly ArchiveFooter _footer;
    private readonly Spares? _spares;
    private readonly List<string> _warnings;

    public string Path { get; }
    public ArchiveHeader Header { get; }
    public ArchiveIndex Index { get; }
    public long FileLength { get; }
    public IReadOnlyList<ArchiveEntry> Entries => Index.Entries;
    public IReadOnlyList<BlockInfo> Blocks => Index.Blocks;
    /// <summary>Problems found while opening that did not stop it, e.g. a damaged spare copy (one line each).</summary>
    public IReadOnlyList<string> Warnings => _warnings;
    /// <summary>The archive has spare copies of its index frame and header (container 1.1, FORMAT §4.1) and the trailer
    /// of the header copy checks out. <see cref="VerifySpareCopies"/> compares them byte for byte.</summary>
    public bool HasSpareCopies => _spares is not null;
    /// <summary>Whether the archive has spare copies, and what state they are in (a newer writer's are not damage).</summary>
    public SpareCopyStatus SpareCopyStatus { get; private init; }

    internal const string SpareDamaged = "The spare copy of the file list is damaged; the archive is complete. Copy it to a healthy disk.";
    internal const string SpareHeaderDamaged = "The spare copy of the header is damaged; the archive is complete. Copy it to a healthy disk.";
    internal const string UsedSpareIndex = "The file list of this archive was damaged; Osmium used its spare copy. Every file can be checked with Test.";
    internal const string UsedSpareHeader = "The header of this archive was damaged; Osmium used its spare copy.";
    internal const string EndDamaged = "The end of this archive is damaged. Try Recover files in the app or osmium salvage.";

    /// <summary>How far before the index frame the spare copy of it may start: its own length, plus the header copy
    /// (at most the longest header, the most padding readers accept and the trailer), plus a margin.</summary>
    private const long SpareSearchMargin = (1 << 20) + (72 << 10);

    private readonly bool _usedSpareIndex, _usedSpareHeader;

    /// <summary>Where the spare copies are: B (the copy of the index frame), followed by H, whose trailer this is.</summary>
    private sealed record Spares(long IndexCopyOffset, long HeaderCopyOffset, SpareTrailer Trailer);

    private OsaReader(string path, ArchiveFile handle, ArchiveHeader header, ArchiveKey? key, ArchiveIndex index, long length,
        MethodRegistry reg, ArchiveFooter footer, Spares? spares, List<string> warnings, bool usedSpareHeader, bool usedSpareIndex)
    {
        Path = path; _handle = handle; Header = header; _key = key; Index = index; FileLength = length; _registry = reg;
        _footer = footer; _spares = spares; _warnings = warnings; _usedSpareHeader = usedSpareHeader; _usedSpareIndex = usedSpareIndex;
    }

    /// <summary>
    /// Opens an archive. When the index frame or the header is damaged and the archive has spare copies of them
    /// (container 1.1, FORMAT §4.1), the copy is used and <see cref="Warnings"/> says so; when the copy fails too, the
    /// error is the original one. A wrong password and a feature of a newer version are never taken for damage.
    /// </summary>
    /// <param name="password">Called only when the archive is encrypted; return null to cancel.</param>
    public static OsaReader Open(string path, Func<string?>? password = null, MethodRegistry? registry = null) =>
        Open(path, new OsaOpenOptions { Password = password, Registry = registry });

    /// <param name="unreadable">Tests: (offset, count) of a read that fails with an I/O error, as a bad sector does.</param>
    internal static OsaReader Open(string path, Func<string?>? password, MethodRegistry? registry, Func<long, int, bool>? unreadable) =>
        Open(path, new OsaOpenOptions { Password = password, Registry = registry, TestUnreadable = unreadable });

    /// <summary>
    /// Opens an archive, in this order: the header and the footer; for an encrypted archive the checks of its key
    /// derivation that need no password (<see cref="PasswordKdf.Admit"/>: too weak, more memory than
    /// <see cref="OsaOpenOptions.KdfMemoryLimit"/>, more work than the strongest preset); only then the password, which
    /// must be text a password can be; one derivation; the verifier (<see cref="WrongPasswordException"/>); the index.
    /// A refusal or a question therefore never costs a password prompt. The spare copies stand in for damage only,
    /// never for any of these refusals.
    /// </summary>
    public static OsaReader Open(string path, OsaOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var registry = options.Registry ?? MethodRegistry.Default;
        // FileShare.Delete: Explorer can still rename or delete an archive that is open; reads go on through the handle.
        var handle = new ArchiveFile(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess),
            options.TestUnreadable);
        ArchiveKey? key = null;
        try
        {
            long length = RandomAccess.GetLength(handle.Handle);
            if (length < Osa.FooterSize + 36) throw new CorruptDataException("File is too small to be an Osmium archive.");
            var warnings = new List<string>();
            ArchiveHeader? header = null;
            Exception? headerError = null;
            try { header = ReadHeader(handle, 0, length); }
            catch (Exception e) when (IsDamage(e)) { headerError = e; }
            var footer = ReadFooter(handle, length);
            if (footer is null)
            {
                if (headerError is not null) Rethrow(headerError);
                throw new CorruptDataException(EndDamaged);
            }
            var ft = footer.Value;
            bool usedSpareHeader = false;
            if (header is null)
            {
                header = HeaderFromCopy(handle, ft, length);
                if (header is null) Rethrow(headerError!);
                warnings.Add(UsedSpareHeader);
                usedSpareHeader = true;
            }
            if (ft.IdPrefix != BinaryPrimitives.ReadUInt64LittleEndian(header!.ArchiveId))
                throw new CorruptDataException("Footer does not belong to this archive.");
            if (ft.IndexOffset < header.Length || ft.IndexOffset + ft.IndexLength + Osa.FooterSize != length)
                throw new CorruptDataException("Index location is invalid (truncated or appended file?).");

            if (header.Encrypted)
            {
                var enc = header.Encryption!;
                var run = new KdfRun
                {
                    MemoryLimitBytes = options.KdfMemoryLimit, Policy = options.Protection, AllowSlow = options.AllowSlowKdf,
                    Cancellation = options.Cancellation, Progress = options.KdfProgress,
                };
                PasswordKdf.Admit(enc.Kdf, run);
                if (options.KeyCache?.TryGet(enc, out var cached) == true)
                {
                    key = cached;
                    if (!key.Matches(enc.Verifier)) throw new WrongPasswordException(); // a wrong key is a wrong password
                }
                else
                {
                    var pw = options.Password?.Invoke() ?? throw new PasswordRequiredException();
                    PasswordKdf.CheckPasswordText(pw, creating: false);
                    key = ArchiveKey.Derive(pw, enc, run);
                    if (!key.Matches(enc.Verifier)) throw new WrongPasswordException();
                    options.KeyCache?.Add(enc, key);
                }
            }
            ArchiveIndex index;
            bool usedSpareIndex = false;
            try
            {
                index = ReadIndex(handle, ft.IndexOffset, ft.IndexLength, ft.IndexOffset, header, key, registry);
            }
            catch (Exception e) when (IsDamage(e))
            {
                index = IndexFromCopy(handle, ft, header, key, registry) ?? Rethrow<ArchiveIndex>(e);
                warnings.Add(UsedSpareIndex);
                usedSpareIndex = true;
            }
            var spares = FindSpares(handle, index, ft, header, out bool damaged, out bool newer);
            if (damaged && !usedSpareIndex) warnings.Add(SpareDamaged);
            return new OsaReader(System.IO.Path.GetFullPath(path), handle, header, key, index, length, registry, ft, spares, warnings,
                usedSpareHeader, usedSpareIndex)
            {
                SpareCopyStatus = spares is not null ? SpareCopyStatus.Present : newer ? SpareCopyStatus.Newer
                    : damaged ? SpareCopyStatus.Damaged : SpareCopyStatus.None,
            };
        }
        catch
        {
            key?.Dispose();
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Damage the spare copies can stand in for: bytes that do not check out, or that cannot be read at all (a bad
    /// sector), but never a wrong password, a missing one, a feature of a newer version, a refusal of an archive that is
    /// not protected as Osmium protects archives, or a refusal or question about its key derivation (all of those are
    /// <see cref="IOException"/>s, so they are excluded by name).
    /// </summary>
    private static bool IsDamage(Exception e) =>
        e is InvalidDataException
        || (e is IOException && e is not (NewerVersionException or ModifiedArchiveException or KdfMemoryLimitException or KdfCostException or WeakProtectionException));

    /// <summary>The open archive file. Every read goes through it, so that a test can make a range unreadable.</summary>
    private sealed class ArchiveFile(SafeFileHandle handle, Func<long, int, bool>? unreadable) : IDisposable
    {
        public SafeFileHandle Handle => handle;

        public int Read(Span<byte> buffer, long offset)
        {
            if (unreadable?.Invoke(offset, buffer.Length) == true) throw new IOException($"The archive cannot be read at offset {offset:N0}.");
            return RandomAccess.Read(handle, buffer, offset);
        }

        public void Dispose() => handle.Dispose();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Rethrow(Exception e) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e);

    private static T Rethrow<T>(Exception e)
    {
        Rethrow(e);
        return default!;
    }

    /// <summary>The header at <paramref name="offset"/>: 36 fixed bytes, the extensions, the CRC (FORMAT §1).</summary>
    private static ArchiveHeader ReadHeader(ArchiveFile handle, long offset, long length)
    {
        var fixedPart = new byte[36];
        if (offset + 36 > length || !TryRead(handle, offset, fixedPart)) throw new CorruptDataException("Unexpected end of archive.");
        long extLen = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(32));
        // Checked by ArchiveHeader.Read as well; here so that a damaged length never sizes a buffer.
        if (extLen > 1 << 20 || offset + 40 + extLen > length) return ArchiveHeader.Read(new MemoryStream(fixedPart));
        var all = new byte[40 + extLen];
        if (!TryRead(handle, offset, all)) throw new CorruptDataException("Unexpected end of archive.");
        return ArchiveHeader.Read(new MemoryStream(all, writable: false));
    }

    /// <summary>The footer at the end of the file, when its marker and CRC check out.</summary>
    private static ArchiveFooter? ReadFooter(ArchiveFile handle, long length)
    {
        var b = new byte[Osa.FooterSize];
        if (!TryRead(handle, length - Osa.FooterSize, b)) return null;
        if (!b.AsSpan(28, 4).SequenceEqual(Osa.FooterMagic) || Osa.Crc(b.AsSpan(0, 24)) != BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24)))
            return null;
        return new ArchiveFooter(BinaryPrimitives.ReadInt64LittleEndian(b), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16)));
    }

    /// <summary>
    /// The header copy H, used when the header at offset 0 fails: the footer is intact and places the index frame at
    /// the end of the file, H's trailer just before it checks out, the header it holds checks out as a header, and the
    /// archive id is the footer's. Null otherwise. A header copy of a newer version throws, as the header would.
    /// </summary>
    private static ArchiveHeader? HeaderFromCopy(ArchiveFile handle, ArchiveFooter footer, long length)
    {
        if (footer.IndexOffset <= 0 || footer.IndexLength <= 0 || footer.IndexOffset + footer.IndexLength + Osa.FooterSize != length) return null;
        if (ReadTrailer(handle, footer.IndexOffset) is not { Newer: false } t) return null;
        long at = footer.IndexOffset - t.CopyLength;
        if (at < 0) return null;
        ArchiveHeader h;
        try { h = ReadHeader(handle, at, at + t.HeaderLength); }
        catch (Exception e) when (IsDamage(e)) { return null; }
        if (h.Length != t.HeaderLength || BinaryPrimitives.ReadUInt64LittleEndian(h.ArchiveId) != footer.IdPrefix) return null;
        return h;
    }

    /// <summary>
    /// The index from its spare copy B, used when the index frame fails: B is where H's trailer says, or (with the
    /// trailer damaged) found by its frame header in the bytes before the index frame. It is read with every check
    /// of the index frame, and must name its own offset as the spare copy's. Null when no copy is usable.
    /// </summary>
    private static ArchiveIndex? IndexFromCopy(ArchiveFile handle, ArchiveFooter footer, ArchiveHeader header, ArchiveKey? key, MethodRegistry registry)
    {
        var candidates = new List<long>();
        if (ReadTrailer(handle, footer.IndexOffset) is { Newer: false } t && footer.IndexOffset - t.CopyLength - footer.IndexLength is var b and >= 0)
            candidates.Add(b);
        candidates.AddRange(IndexFrameHeadersBefore(handle, footer, header).Where(c => !candidates.Contains(c)));
        foreach (long at in candidates)
        {
            try
            {
                var index = ReadIndex(handle, at, footer.IndexLength, at, header, key, registry);
                if (index.Metadata.TryGetValue(ArchiveIndex.SparesKey, out var v) && v.Length == 8 && BinaryPrimitives.ReadUInt64LittleEndian(v) == (ulong)at)
                    return index;
            }
            catch (Exception e) when (IsDamage(e) || e is NewerVersionException) { }
        }
        return null;
    }

    /// <summary>Offsets, nearest first, of frame headers that check out and could be the spare copy of the index frame:
    /// the index flag, the index frame number and the index frame's length.</summary>
    private static IEnumerable<long> IndexFrameHeadersBefore(ArchiveFile handle, ArchiveFooter footer, ArchiveHeader header)
    {
        long hi = footer.IndexOffset - footer.IndexLength - SpareCopies.TrailerSize; // the latest start that leaves room for a trailer
        long lo = Math.Max(header.Length, footer.IndexOffset - footer.IndexLength - SpareSearchMargin);
        if (hi < lo) yield break;
        // Read in pieces on 4 KiB boundaries of the file, and a piece that fails sector by sector: what cannot be read
        // stays zero (no frame header is zeros), and the rest is still searched.
        var window = new byte[hi - lo + Osa.FrameHeaderSize];
        ReadAround(handle, lo, window, 4096);
        int found = 0;
        for (int i = (int)(hi - lo); i >= 0 && found < 8; i--)
        {
            var fh = window.AsSpan(i, Osa.FrameHeaderSize);
            if (!fh[..4].SequenceEqual(Osa.FrameMagic) || Osa.Crc(fh[..24]) != BinaryPrimitives.ReadUInt32LittleEndian(fh[24..])) continue;
            if ((BinaryPrimitives.ReadUInt32LittleEndian(fh[4..]) & Osa.FrameFlagIndex) == 0 || BinaryPrimitives.ReadUInt64LittleEndian(fh[8..]) != Osa.IndexFrameNumber
                || BinaryPrimitives.ReadInt64LittleEndian(fh[16..]) != footer.IndexLength - Osa.FrameHeaderSize) continue;
            found++;
            yield return lo + i;
        }
    }

    /// <summary>Reads, decrypts, decodes and checks an index frame at <paramref name="offset"/>; its blocks must end by
    /// <paramref name="blocksEnd"/>.</summary>
    private static ArchiveIndex ReadIndex(ArchiveFile handle, long offset, long frameLength, long blocksEnd, ArchiveHeader header,
        ArchiveKey? key, MethodRegistry registry)
    {
        var body = ReadFrame(handle, offset, frameLength, Osa.IndexFrameNumber, header, key, registry, isIndex: true);
        var raw = PipelineExecutor.Decode(body.Descriptor, body.Payload, CodecContext.None);
        if (BlockBody.Hash128(raw) != body.RawHash) throw new CorruptDataException("Archive index is corrupt (hash mismatch).");
        var index = ArchiveIndex.Deserialize(raw, header.Length, registry);
        foreach (var b in index.Blocks)
            if (b.Offset + b.FrameLength > blocksEnd) throw new CorruptDataException("Block table points past the index.");
        return index;
    }

    /// <summary>
    /// The spare copies the index names (<see cref="ArchiveIndex.SparesKey"/>), when H's trailer just before the index
    /// frame checks out and the offsets add up: B, then H, then the index frame. <paramref name="damaged"/> is set when
    /// the index names spare copies that are not there as it says. A trailer of a newer writer is not used and is no
    /// damage (<paramref name="newer"/>).
    /// </summary>
    private static Spares? FindSpares(ArchiveFile handle, ArchiveIndex index, ArchiveFooter footer, ArchiveHeader header, out bool damaged, out bool newer)
    {
        damaged = newer = false;
        if (!index.Metadata.TryGetValue(ArchiveIndex.SparesKey, out var value)) return null;
        var trailer = ReadTrailer(handle, footer.IndexOffset);
        if (trailer is { Newer: true })
        {
            newer = true;
            return null;
        }
        damaged = true;
        if (value.Length != 8 || trailer is not { } t) return null;
        // Unsigned: a crafted offset must not wrap around to pass the check.
        ulong b = BinaryPrimitives.ReadUInt64LittleEndian(value), end = (ulong)footer.IndexOffset;
        ulong between = (ulong)footer.IndexLength + (ulong)t.CopyLength;
        if (b > end || end - b != between || b < (ulong)header.Length) return null;
        if (index.Blocks.Count > 0 && index.Blocks.Max(k => k.Offset + k.FrameLength) > (long)b) return null;
        damaged = false;
        return new Spares((long)b, (long)b + footer.IndexLength, t);
    }

    /// <summary>H's trailer: the 32 bytes before the index frame, if they are one.</summary>
    private static SpareTrailer? ReadTrailer(ArchiveFile handle, long indexOffset)
    {
        if (indexOffset < SpareCopies.TrailerSize) return null;
        var t = new byte[SpareCopies.TrailerSize];
        return TryRead(handle, indexOffset - SpareCopies.TrailerSize, t) ? SpareTrailer.Read(t) : null;
    }

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/> in pieces of <paramref name="piece"/> bytes
    /// on the file's own boundaries; a piece that cannot be read is read again in 512-byte sectors, and what still fails
    /// is left zero.</summary>
    private static void ReadAround(ArchiveFile handle, long offset, Span<byte> buffer, int piece)
    {
        for (int at = 0; at < buffer.Length;)
        {
            int n = (int)Math.Min(buffer.Length - at, piece - (offset + at) % piece);
            var part = buffer.Slice(at, n);
            if (!TryRead(handle, offset + at, part))
            {
                if (piece > 512) ReadAround(handle, offset + at, part, 512);
                else part.Clear();
            }
            at += n;
        }
    }

    /// <summary>Reads exactly <paramref name="buffer"/>; false when the file ends first or the bytes cannot be read.</summary>
    private static bool TryRead(ArchiveFile handle, long offset, Span<byte> buffer)
    {
        try
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = handle.Read(buffer[read..], offset + read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Compares the spare copies with what they copy: B with the index frame byte for byte, H's header bytes with the
    /// header at offset 0, and H's padding with zeros. Returns one warning line per problem, including a copy that had
    /// to stand in for a damaged index frame or header when the archive was opened (none when the archive has no spare
    /// copies). Reads the index twice, so it runs in Test, not on every open.
    /// </summary>
    public IReadOnlyList<string> VerifySpareCopies()
    {
        var problems = new List<string>();
        if (_usedSpareHeader) problems.Add(UsedSpareHeader);
        if (_usedSpareIndex) problems.Add(UsedSpareIndex);
        if (!Index.Metadata.ContainsKey(ArchiveIndex.SparesKey)) return problems;
        if (_spares is not { } s)
        {
            if (_warnings.Contains(SpareDamaged)) problems.Add(SpareDamaged);
            return problems;
        }
        try
        {
            // What stood in for a damaged copy differs from it, as it was already reported.
            if (!_usedSpareIndex && !SameBytes(s.IndexCopyOffset, _footer.IndexOffset, _footer.IndexLength)) problems.Add(SpareDamaged);
            bool headerCopyIntact = s.Trailer.HeaderLength == Header.Length
                && (_usedSpareHeader || SameBytes(s.HeaderCopyOffset, 0, Header.Length))
                && Zeros(s.HeaderCopyOffset + s.Trailer.HeaderLength, s.Trailer.PaddingLength);
            if (!headerCopyIntact) problems.Add(SpareHeaderDamaged);
        }
        catch (IOException)
        {
            problems.Add(SpareDamaged);
        }
        return problems;
    }

    private bool SameBytes(long a, long b, long count)
    {
        var x = new byte[(int)Math.Min(count, 1 << 20)];
        var y = new byte[x.Length];
        for (long done = 0; done < count;)
        {
            int n = (int)Math.Min(x.Length, count - done);
            if (!TryRead(_handle, a + done, x.AsSpan(0, n)) || !TryRead(_handle, b + done, y.AsSpan(0, n))) return false;
            if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n))) return false;
            done += n;
        }
        return true;
    }

    private bool Zeros(long offset, long count)
    {
        var x = new byte[(int)Math.Min(Math.Max(count, 1), 1 << 16)];
        for (long done = 0; done < count;)
        {
            int n = (int)Math.Min(x.Length, count - done);
            if (!TryRead(_handle, offset + done, x.AsSpan(0, n)) || x.AsSpan(0, n).ContainsAnyExcept((byte)0)) return false;
            done += n;
        }
        return true;
    }

    private static BlockBody ReadFrame(ArchiveFile h, long offset, long frameLength, ulong expectedFrame,
        ArchiveHeader header, ArchiveKey? key, MethodRegistry reg, bool isIndex)
    {
        if (frameLength < Osa.FrameHeaderSize || frameLength - Osa.FrameHeaderSize > BlockLimits.MaxRawSize * 2)
            throw new CorruptDataException("Invalid frame length.");
        var fh = new byte[Osa.FrameHeaderSize];
        if (h.Read(fh, offset) != fh.Length) throw new CorruptDataException("Unexpected end of archive.");
        if (!fh.AsSpan(0, 4).SequenceEqual(Osa.FrameMagic)) throw new CorruptDataException($"Frame {expectedFrame} has no frame marker.");
        if (Osa.Crc(fh.AsSpan(0, 24)) != BinaryPrimitives.ReadUInt32LittleEndian(fh.AsSpan(24)))
            throw new CorruptDataException($"Frame {expectedFrame} header is corrupt.");
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(fh.AsSpan(4));
        ulong number = BinaryPrimitives.ReadUInt64LittleEndian(fh.AsSpan(8));
        long bodyLen = BinaryPrimitives.ReadInt64LittleEndian(fh.AsSpan(16));
        string location = isIndex ? "the index" : $"block {expectedFrame}";
        if (number != expectedFrame) throw new CorruptDataException($"Expected frame {expectedFrame} but found {number}.");
        if (((flags & Osa.FrameFlagEncrypted) != 0) != header.Encrypted) throw new CorruptDataException("Frame encryption flag mismatch.");
        if (((flags & Osa.FrameFlagIndex) != 0) != isIndex) throw new CorruptDataException("Frame type mismatch.");
        // The frame header CRC has been checked: a flag this version does not know was set by a newer one.
        if ((flags & ~(Osa.FrameFlagEncrypted | Osa.FrameFlagIndex)) is var unknown and not 0)
            throw new NewerVersionException($"frame flag 0x{unknown:X}", location);
        if (bodyLen != frameLength - Osa.FrameHeaderSize) throw new CorruptDataException("Frame length mismatch.");
        FrameRead?.Invoke(expectedFrame);
        var body = GC.AllocateUninitializedArray<byte>(checked((int)bodyLen));
        int read = 0;
        while (read < body.Length)
        {
            int n = h.Read(body.AsSpan(read), offset + Osa.FrameHeaderSize + read);
            if (n <= 0) throw new CorruptDataException("Unexpected end of archive.");
            read += n;
        }
        byte[] plain = key is null ? body : key.Open(header.ArchiveId, expectedFrame, body, isIndex ? header.Raw : ReadOnlySpan<byte>.Empty);
        return BlockBody.Parse(plain, reg, location, PaddingAllowed(header, isIndex));
    }

    /// <summary>Zero padding after the payload (FORMAT §5): in the index frame of an Argon2id archive only.</summary>
    internal static bool PaddingAllowed(ArchiveHeader header, bool isIndex) =>
        isIndex && header.Encrypted && header.Encryption?.Kdf.Algorithm == KdfAlgorithm.Argon2id;

    private static readonly AsyncLocal<Action<ulong>?> s_frameRead = new();

    /// <summary>Tests: called with the frame number each time a frame's body is read.</summary>
    internal static Action<ulong>? FrameRead { get => s_frameRead.Value; set => s_frameRead.Value = value; }

    /// <summary>
    /// The first block this version cannot decode because a newer version of Osmium made it, or null. Such an archive
    /// still opens and lists (those blocks show a stand-in pipeline); reading those blocks throws this exception, and
    /// every other block extracts normally.
    /// </summary>
    public NewerVersionException? NewerVersion => Index.Blocks.Select(b => b.Descriptor.NewerVersion).FirstOrDefault(e => e is not null);

    /// <summary>Reads and structurally verifies one frame (CRCs, tag, payload hash) without decoding it. Its pipeline
    /// must be the one the index gives for it: memory is reserved and limits are checked from the index's copy.</summary>
    /// <exception cref="NewerVersionException">The block needs a newer version of Osmium.</exception>
    public BlockBody ReadBody(int frame)
    {
        var b = Blocks[frame];
        if (b.Descriptor.NewerVersion is { } newer) throw newer.Again();
        var body = ReadFrame(_handle, b.Offset, b.FrameLength, (ulong)frame, Header, _key, _registry, isIndex: false);
        if (!body.Descriptor.Serialize().AsSpan().SequenceEqual(b.Descriptor.Serialize()))
            throw new CorruptDataException($"Block {frame} does not match the file list (its pipeline differs).");
        return body;
    }

    /// <summary>Reads, decodes and hash-verifies one block's raw content.</summary>
    /// <exception cref="NewerVersionException">The block needs a newer version of Osmium.</exception>
    public byte[] ReadBlock(int frame, CodecContext? ctx = null)
    {
        var body = ReadBody(frame);
        if (body.Descriptor.RawSize != Blocks[frame].RawSize) throw new CorruptDataException($"Block {frame} size mismatch.");
        var raw = PipelineExecutor.Decode(body.Descriptor, body.Payload, ctx ?? CodecContext.None);
        if (BlockBody.Hash128(raw) != body.RawHash) throw new CorruptDataException($"Block {frame} is corrupt (content hash mismatch).");
        return raw;
    }

    public long TotalUnpacked => Index.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
    public long TotalStoredRaw => Index.Blocks.Sum(b => b.RawSize);
    /// <summary>Total file bytes ÷ stored raw block bytes. Large values mean heavy dedup — or an expansion bomb.</summary>
    public double ExpansionFactor => TotalStoredRaw == 0 ? 1 : (double)TotalUnpacked / TotalStoredRaw;
    public long MaxBlockDecodeMemory => Index.Blocks.Count == 0 ? 0 : Index.Blocks.Max(b => b.DecodeMemory + b.RawSize);
    /// <summary>Estimated decode time; blocks that need a newer Osmium cannot be decoded and are left out.</summary>
    public double EstimatedDecodeSeconds => Index.Blocks.Where(b => b.Descriptor.NewerVersion is null).Sum(b => b.Descriptor.Spec.EstimateDecodeSeconds(b.RawSize));

    /// <summary>Closes the archive and zeroes its key.</summary>
    public void Dispose()
    {
        _key?.Dispose();
        _handle.Dispose();
    }
}
