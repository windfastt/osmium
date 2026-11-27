using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Osmium.Core.Archives;
using Osmium.Core.Crypto;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Container;

public sealed class PasswordRequiredException() : Exception("This archive is encrypted. A password is required.");
public sealed class WrongPasswordException() : Exception("Wrong password.");

/// <summary>
/// What <see cref="OsaReader.Peek"/> read from an archive's header: whether it claims to be encrypted, the key
/// derivation its header asks for, and a line to show for it ("Encrypted: password not yet checked (…)" or "Not
/// encrypted"). A claim only: nothing has been checked against a password or authenticated.
/// </summary>
public sealed record ArchivePeek(bool ClaimsEncryption, KdfParameters? Kdf, string ProtectionText);

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
    /// <summary>Limits on the file list, applied while it is decoded (the archive source passes its import limits).</summary>
    public IndexLimits IndexLimits { get; init; } = IndexLimits.Default;
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
    /// <summary>
    /// What made the archive, e.g. "Osmium 0.2.0": from the file list (since 0.2.0 every archive has it there, and an
    /// encrypted one only there, so that its release is not readable without the password), else from the header's
    /// Creator record (earlier archives). Null when neither has one. At most the first <see cref="MaxCreatorBytes"/>
    /// bytes of it: it is shown as one line, and a file list may hold a value of any length.
    /// </summary>
    public string? Creator =>
        Index.Metadata.TryGetValue(ArchiveIndex.CreatorKey, out var v) ? System.Text.Encoding.UTF8.GetString(v.AsSpan(0, Math.Min(v.Length, MaxCreatorBytes)))
        : Header.Creator is { Length: > MaxCreatorBytes } c ? c[..MaxCreatorBytes] : Header.Creator;

    /// <summary>The most of a creator text <see cref="Creator"/> returns.</summary>
    public const int MaxCreatorBytes = 256;
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
    /// never for any of these refusals. Null options are the defaults (no password), so that <c>Open(path, null)</c>
    /// means what it meant before this overload existed.
    /// </summary>
    public static OsaReader Open(string path, OsaOpenOptions? options)
    {
        options ??= new OsaOpenOptions();
        var registry = options.Registry ?? MethodRegistry.Default;
        // FileShare.Delete: Explorer can still rename or delete an archive that is open; reads go on through the handle.
        var handle = new ArchiveFile(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess),
            options.TestUnreadable);
        ArchiveKey? key = null;
        try
        {
            long length = RandomAccess.GetLength(handle.Handle);
            var warnings = new List<string>();
            var (header, ft, usedSpareHeader) = ReadEnds(handle, length);
            if (usedSpareHeader) warnings.Add(UsedSpareHeader);

            // Encrypted archives: every byte is header, frame header, authenticated ciphertext, an exact copy of one of
            // them, zero padding, the header-copy trailer or the footer (FORMAT §5). What else is found is refused before
            // anything is listed, unless the user agreed; then it is ignored, and the warnings say so.
            bool allowWeak = PasswordKdf.Resolve(options.Protection) == ProtectionPolicy.AllowWeak;
            var unprotected = new List<UnprotectedDataException>();
            var candidates = new List<ArchiveHeader> { header };
            if (header.Encrypted)
            {
                // An index frame that is not encrypted under a header that says encrypted, or that sets a flag this
                // version does not know, is refused before the password is asked for (a frame header whose CRC fails is
                // damage, left to the spare copy).
                if (ReadFrameHeader(handle, ft.IndexOffset) is { } ih && ((ih.Flags & Osa.FrameFlagEncrypted) == 0 || UnknownFrameFlags(ih.Flags) != 0))
                    throw new ModifiedArchiveException();
                if (header.UnprotectedRoom() is not null) unprotected.Add(new UnprotectedDataException(mayBeDamage: false));
                var different = CheckHeaderCopy(handle, ft, header, usedSpareHeader, unprotected);
                RefuseUnlessAllowed(unprotected, allowWeak);
                // Header and header copy both check out and differ: with the user's agreement, each is tried, and the one
                // whose bytes authenticate the file list is used.
                if (different is not null) candidates.Add(different);
            }

            string? password = null;
            ArchiveIndex? index = null;
            bool usedSpareIndex = false;
            Exception? firstError = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    key = candidate.Encrypted ? KeyFor(candidate) : null;
                    try
                    {
                        index = ReadIndex(handle, ft.IndexOffset, ft.IndexLength, ft.IndexOffset, candidate, key, registry, options.IndexLimits);
                    }
                    catch (Exception e) when (IsDamage(e))
                    {
                        index = IndexFromCopy(handle, ft, candidate, key, registry, options.IndexLimits) ?? Rethrow<ArchiveIndex>(e);
                        usedSpareIndex = true;
                    }
                    header = candidate;
                    break;
                }
                catch (Exception e) when (candidates.Count > 1 && (e is WrongPasswordException || IsDamage(e)))
                {
                    key?.Dispose();
                    key = null;
                    firstError ??= e;
                }
            }
            if (index is null) Rethrow(firstError!);
            if (header != candidates[0]) warnings.Add(UsedTheHeaderThatAuthenticates);
            if (usedSpareIndex) warnings.Add(UsedSpareIndex);
            if (header.Encrypted) CheckBlockFrameHeaders(handle, index);
            var spares = FindSpares(handle, index, ft, header, out bool damaged, out bool newer);
            if (header.Encrypted)
            {
                CheckLayout(handle, index, ft, header, spares, usedSpareIndex, unprotected);
                RefuseUnlessAllowed(unprotected, allowWeak);
                if (unprotected.Count > 0) warnings.Add(IgnoredUnprotectedData);
            }
            if (damaged && !usedSpareIndex) warnings.Add(SpareDamaged);
            return new OsaReader(System.IO.Path.GetFullPath(path), handle, header, key, index, length, registry, ft, spares, warnings,
                usedSpareHeader, usedSpareIndex)
            {
                // The trailer is not authenticated: in an encrypted archive it cannot say that a newer version made it.
                SpareCopyStatus = spares is not null ? SpareCopyStatus.Present : newer && !header.Encrypted ? SpareCopyStatus.Newer
                    : damaged || newer ? SpareCopyStatus.Damaged : SpareCopyStatus.None,
            };

            // The key for this header's settings: the checks that need no password first, then the cached key or the
            // password (asked at most once in this open), one derivation and the verifier.
            ArchiveKey KeyFor(ArchiveHeader h)
            {
                var enc = h.Encryption!;
                var run = new KdfRun
                {
                    MemoryLimitBytes = options.KdfMemoryLimit, Policy = options.Protection, AllowSlow = options.AllowSlowKdf,
                    Cancellation = options.Cancellation, Progress = options.KdfProgress,
                };
                PasswordKdf.Admit(enc.Kdf, run);
                if (options.KeyCache?.TryGet(enc, out var cached) == true)
                {
                    if (cached.Matches(enc.Verifier)) return cached;
                    cached.Dispose();
                    throw new WrongPasswordException(); // a wrong key is a wrong password
                }
                password ??= options.Password?.Invoke() ?? throw new PasswordRequiredException();
                PasswordKdf.CheckPasswordText(password, creating: false);
                var derived = ArchiveKey.Derive(password, enc, run);
                if (!derived.Matches(enc.Verifier))
                {
                    derived.Dispose();
                    throw new WrongPasswordException();
                }
                options.KeyCache?.Add(enc, derived);
                return derived;
            }
        }
        catch
        {
            key?.Dispose();
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The header and the footer, as every open reads them first: the footer must check out; the index frame's length
    /// is capped before anything else, as the frame is read whole before it can be authenticated; a damaged header is
    /// read from its copy H (<paramref name="usedSpareHeader"/>); the footer must name this archive and place the index
    /// frame at the end of the file.
    /// </summary>
    private static (ArchiveHeader Header, ArchiveFooter Footer, bool UsedSpareHeader) ReadEnds(ArchiveFile handle, long length)
    {
        if (length < Osa.FooterSize + 36) throw new CorruptDataException("File is too small to be an Osmium archive.");
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
        CheckIndexLength(ft);
        bool usedSpareHeader = header is null;
        header ??= HeaderFromCopy(handle, ft, length) ?? Rethrow<ArchiveHeader>(headerError!);
        if (ft.IdPrefix != BinaryPrimitives.ReadUInt64LittleEndian(header.ArchiveId))
            throw new CorruptDataException("Footer does not belong to this archive.");
        if (ft.IndexOffset < header.Length || ft.IndexOffset + ft.IndexLength + Osa.FooterSize != length)
            throw new CorruptDataException("Index location is invalid (truncated or appended file?).");
        return (header, ft, usedSpareHeader);
    }

    internal const string NotEncrypted = "Not encrypted";

    /// <summary>
    /// What an archive says about its protection, from its header and footer alone, with the checks
    /// <see cref="Open(string, OsaOpenOptions)"/> makes of them first (a damaged header is read from its copy, and the
    /// file list's length is checked against the cap): no key derivation, no password, nothing of the file list. An
    /// encrypted archive is "Encrypted: password not yet checked (the header asks for …)": that it is encrypted is
    /// known only once the password is checked and the file list authenticated (<see cref="ProtectionText"/>).
    /// </summary>
    public static ArchivePeek Peek(string path)
    {
        using var handle = new ArchiveFile(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), null);
        var (header, _, _) = ReadEnds(handle, RandomAccess.GetLength(handle.Handle));
        return header.Encryption is { } e
            ? new ArchivePeek(true, e.Kdf, $"Encrypted: password not yet checked (the header asks for {e.Kdf.Describe()})")
            : new ArchivePeek(false, null, NotEncrypted);
    }

    /// <summary>
    /// "Encrypted: AES-256-GCM, key from …; password verified", or "Not encrypted". An open reader of an encrypted
    /// archive has checked the password against the verifier and authenticated the file list (<see cref="Open(string, OsaOpenOptions)"/>).
    /// </summary>
    public string ProtectionText => Header.Encryption is { } e ? $"Encrypted: AES-256-GCM, key from {e.Kdf.KeyText()}; password verified" : NotEncrypted;

    /// <summary>
    /// Damage the spare copies can stand in for: bytes that do not check out, or that cannot be read at all (a bad
    /// sector), but never a wrong password, a missing one, a feature of a newer version, a refusal of an archive that is
    /// not protected as Osmium protects archives, a refusal or question about its key derivation, or a file list over
    /// the limits (all of those are <see cref="IOException"/>s, so they are excluded by name).
    /// </summary>
    private static bool IsDamage(Exception e) =>
        e is InvalidDataException
        || (e is IOException && e is not (NewerVersionException or ModifiedArchiveException or KdfMemoryLimitException or KdfCostException
            or WeakProtectionException or EntryLimitException));

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

    internal const string IndexTooLarge = "The file list of this archive is larger than Osmium accepts (more than 256 MiB).";
    internal const string IgnoredUnprotectedData = "This archive holds data outside its protected parts; Osmium ignored it.";
    internal const string UsedTheHeaderThatAuthenticates =
        "The header of this encrypted archive and its spare copy differ; Osmium used the one the file list authenticates.";

    /// <summary>Refuses with the first finding (one that cannot be damage before one that can), unless the user agreed.</summary>
    private static void RefuseUnlessAllowed(List<UnprotectedDataException> found, bool allowWeak)
    {
        if (found.Count > 0 && !allowWeak) throw found.OrderBy(e => e.MayBeDamage).First();
    }

    /// <summary>
    /// An encrypted archive's header copy H, when a trailer before the index frame checks out (whether the archive
    /// should have one, its index says; <see cref="CheckLayout"/>): a trailer of a newer writer, padding that is not
    /// zero, or header bytes that differ from an intact header at offset 0 are room for plaintext (and may be damage).
    /// Returns H's header when it checks out as this archive's header and differs from the one at offset 0.
    /// </summary>
    private static ArchiveHeader? CheckHeaderCopy(ArchiveFile handle, ArchiveFooter ft, ArchiveHeader header, bool usedSpareHeader, List<UnprotectedDataException> found)
    {
        if (ReadTrailer(handle, ft.IndexOffset) is not { } t) return null;
        if (t.Newer)
        {
            found.Add(new UnprotectedDataException(mayBeDamage: true));
            return null;
        }
        long at = ft.IndexOffset - t.CopyLength;
        var bytes = new byte[t.HeaderLength]; // at most the longest header (the trailer's lengths were checked)
        if (at < header.Length || !TryRead(handle, at, bytes))
        {
            found.Add(new UnprotectedDataException(mayBeDamage: true));
            return null;
        }
        if (!ZerosAt(handle, at + t.HeaderLength, t.PaddingLength)) found.Add(new UnprotectedDataException(mayBeDamage: true));
        if (usedSpareHeader || bytes.AsSpan().SequenceEqual(header.Raw)) return null; // standing in for a damaged header, or the same
        ArchiveHeader? copy = null;
        try { copy = ArchiveHeader.Read(new MemoryStream(bytes, writable: false)); }
        catch (Exception e) when (IsDamage(e) || e is NewerVersionException or ModifiedArchiveException) { }
        if (copy is { Encrypted: true } && copy.Length == t.HeaderLength && BinaryPrimitives.ReadUInt64LittleEndian(copy.ArchiveId) == ft.IdPrefix)
        {
            found.Add(new UnprotectedDataException(mayBeDamage: true, UnprotectedDataException.CopiesDifferMessage));
            return copy;
        }
        found.Add(new UnprotectedDataException(mayBeDamage: true));
        return null;
    }

    /// <summary>
    /// After the index of an encrypted archive authenticated: its blocks follow the header without gaps (gapBefore 0)
    /// and end where the spare copy B starts (the index names it) or, without spare copies, where the index frame
    /// starts; and B is a byte-for-byte copy of the index frame A. When A failed and B stood in for it, A is damage that
    /// B replaced, as a header copy replaces a damaged header.
    /// </summary>
    private static void CheckLayout(ArchiveFile handle, ArchiveIndex index, ArchiveFooter ft, ArchiveHeader header, Spares? spares,
        bool usedSpareIndex, List<UnprotectedDataException> found)
    {
        long end = header.Length;
        bool gap = false;
        foreach (var b in index.Blocks)
        {
            gap |= b.Offset != end;
            end = b.Offset + b.FrameLength;
        }
        if (gap) found.Add(new UnprotectedDataException(mayBeDamage: false));
        if (index.Metadata.TryGetValue(ArchiveIndex.SparesKey, out var v))
        {
            if (v.Length != 8 || (ulong)end != BinaryPrimitives.ReadUInt64LittleEndian(v)) found.Add(new UnprotectedDataException(mayBeDamage: false));
            if (spares is null || (!usedSpareIndex && !SameBytesAt(handle, spares.IndexCopyOffset, ft.IndexOffset, ft.IndexLength)))
                found.Add(new UnprotectedDataException(mayBeDamage: true));
        }
        else if (end != ft.IndexOffset) found.Add(new UnprotectedDataException(mayBeDamage: false));
    }

    private static bool SameBytesAt(ArchiveFile handle, long a, long b, long count)
    {
        var x = new byte[(int)Math.Min(Math.Max(count, 1), 1 << 20)];
        var y = new byte[x.Length];
        for (long done = 0; done < count;)
        {
            int n = (int)Math.Min(x.Length, count - done);
            if (!TryRead(handle, a + done, x.AsSpan(0, n)) || !TryRead(handle, b + done, y.AsSpan(0, n))) return false;
            if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n))) return false;
            done += n;
        }
        return true;
    }

    private static bool ZerosAt(ArchiveFile handle, long offset, long count)
    {
        var x = new byte[(int)Math.Min(Math.Max(count, 1), 1 << 16)];
        for (long done = 0; done < count;)
        {
            int n = (int)Math.Min(x.Length, count - done);
            if (!TryRead(handle, offset + done, x.AsSpan(0, n)) || x.AsSpan(0, n).ContainsAnyExcept((byte)0)) return false;
            done += n;
        }
        return true;
    }

    /// <summary>A frame header whose marker and CRC check out: flags, number and body length.</summary>
    private readonly record struct FrameHead(uint Flags, ulong Number, long BodyLength);

    /// <summary>The frame header at <paramref name="offset"/> when its marker and CRC check out, else null (damage).</summary>
    private static FrameHead? ReadFrameHeader(ArchiveFile handle, long offset)
    {
        Span<byte> fh = stackalloc byte[Osa.FrameHeaderSize];
        if (offset < 0 || !TryRead(handle, offset, fh) || !fh[..4].SequenceEqual(Osa.FrameMagic)
            || Osa.Crc(fh[..24]) != BinaryPrimitives.ReadUInt32LittleEndian(fh[24..])) return null;
        return new FrameHead(BinaryPrimitives.ReadUInt32LittleEndian(fh[4..]), BinaryPrimitives.ReadUInt64LittleEndian(fh[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(fh[16..]));
    }

    /// <summary>
    /// In an encrypted archive, the frame header of every block the authenticated index lists: one that checks out must
    /// have the block's number, the encrypted flag, not the index flag, no flag this version does not know, and the
    /// index's length; otherwise the archive was made that way (<see cref="ModifiedArchiveException"/>), before anything
    /// is listed or written. A frame header that does not check out is damage: that block fails when it is read, and the
    /// others are not affected.
    /// </summary>
    private static void CheckBlockFrameHeaders(ArchiveFile handle, ArchiveIndex index)
    {
        foreach (var b in index.Blocks)
        {
            if (ReadFrameHeader(handle, b.Offset) is not { } h) continue;
            if (h.Number != (ulong)b.Frame || (h.Flags & Osa.FrameFlagEncrypted) == 0 || (h.Flags & Osa.FrameFlagIndex) != 0
                || UnknownFrameFlags(h.Flags) != 0 || h.BodyLength != b.FrameLength - Osa.FrameHeaderSize)
                throw new ModifiedArchiveException();
        }
    }

    /// <summary>
    /// Frame flags this version does not know. Frame headers are not authenticated: in an unencrypted archive such a flag
    /// was set by a newer version (its CRC checks out), but in an encrypted archive it cannot say so, as anyone can set
    /// it. A newer version that adds a frame flag to encrypted archives announces it in the header (FORMAT §2.1).
    /// </summary>
    internal static uint UnknownFrameFlags(uint flags) => flags & ~(Osa.FrameFlagEncrypted | Osa.FrameFlagIndex);

    /// <summary>An index frame longer than <see cref="Osa.MaxIndexFrameBytes"/> is refused before it is allocated or read
    /// (the footer that says so checks out, so this is not damage).</summary>
    private static void CheckIndexLength(ArchiveFooter footer)
    {
        if ((ulong)footer.IndexLength > Osa.MaxIndexFrameBytes) throw new CorruptDataException(IndexTooLarge);
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
    private static ArchiveIndex? IndexFromCopy(ArchiveFile handle, ArchiveFooter footer, ArchiveHeader header, ArchiveKey? key, MethodRegistry registry,
        IndexLimits limits)
    {
        var candidates = new List<long>();
        if (ReadTrailer(handle, footer.IndexOffset) is { Newer: false } t && footer.IndexOffset - t.CopyLength - footer.IndexLength is var b and >= 0)
            candidates.Add(b);
        candidates.AddRange(IndexFrameHeadersBefore(handle, footer, header).Where(c => !candidates.Contains(c)));
        foreach (long at in candidates)
        {
            try
            {
                var index = ReadIndex(handle, at, footer.IndexLength, at, header, key, registry, limits);
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
        ArchiveKey? key, MethodRegistry registry, IndexLimits limits)
    {
        var body = ReadFrame(handle, offset, frameLength, Osa.IndexFrameNumber, header, key, registry, isIndex: true);
        var raw = PipelineExecutor.Decode(body.Descriptor, body.Payload, CodecContext.None);
        if (BlockBody.Hash128(raw) != body.RawHash) throw new CorruptDataException("Archive index is corrupt (hash mismatch).");
        var index = ArchiveIndex.Deserialize(raw, header.Length, registry, limits);
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

    private bool SameBytes(long a, long b, long count) => SameBytesAt(_handle, a, b, count);

    private bool Zeros(long offset, long count) => ZerosAt(_handle, offset, count);

    private static BlockBody ReadFrame(ArchiveFile h, long offset, long frameLength, ulong expectedFrame,
        ArchiveHeader header, ArchiveKey? key, MethodRegistry reg, bool isIndex)
    {
        if (frameLength < Osa.FrameHeaderSize || frameLength - Osa.FrameHeaderSize > (isIndex ? Osa.MaxIndexFrameBytes : Osa.MaxBlockBodyBytes))
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
        // A frame that is not encrypted in an archive that says it is: made that way (its header CRC checks out).
        if (header.Encrypted && (flags & Osa.FrameFlagEncrypted) == 0) throw new ModifiedArchiveException();
        if (((flags & Osa.FrameFlagEncrypted) != 0) != header.Encrypted) throw new CorruptDataException("Frame encryption flag mismatch.");
        if (((flags & Osa.FrameFlagIndex) != 0) != isIndex) throw new CorruptDataException("Frame type mismatch.");
        // The frame header CRC has been checked: in an unencrypted archive a flag this version does not know was set by a
        // newer one; in an encrypted archive the frame header is not authenticated, so the archive was changed.
        if (UnknownFrameFlags(flags) is var unknown and not 0)
            throw header.Encrypted ? new ModifiedArchiveException() : new NewerVersionException($"frame flag 0x{unknown:X}", location);
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
        try
        {
            return BlockBody.Parse(plain, reg, location, PaddingAllowed(header, isIndex));
        }
        catch (NewerVersionException) when (!isIndex)
        {
            // A block is read only when the index gives it a pipeline this version knows (ReadBody), so a block whose own
            // copy of its pipeline needs a newer version is not what the index says it is: damage, never a newer version.
            throw new CorruptDataException(PipelineDiffers(expectedFrame));
        }
    }

    private static string PipelineDiffers(ulong frame) => $"Block {frame} does not match the file list (its pipeline differs).";

    /// <summary>Zero padding after the payload (FORMAT §5): in the index frame of an Argon2id archive, and in its block
    /// frames when its header has flag bit 3 ("Hide exact sizes", which the header allows only in Argon2id archives).</summary>
    internal static bool PaddingAllowed(ArchiveHeader header, bool isIndex) =>
        header.Encrypted && header.Encryption?.Kdf.Algorithm == KdfAlgorithm.Argon2id && (isIndex || header.PaddedBlocks);

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
            throw new CorruptDataException(PipelineDiffers((ulong)frame));
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
