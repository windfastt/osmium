using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Runtime.ExceptionServices;
using Osmium.Core.Crypto;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Container;

/// <summary>
/// <c>osmium salvage</c>: recovers everything that can be verified from a damaged archive and reports what was lost.
/// Frames are found by scanning for their headers (marker + CRC), never through the footer or the index's offsets, so
/// garbage, truncation and missing or duplicated regions are tolerated. A block is used only when its header, its body
/// (CRC or GCM tag, payload hash), every decode stage and its content hash check out; a file is written only when all
/// of its blocks did and its own hash matches. A block made by a newer version of Osmium is reported as such, never as
/// damage. With the index intact, files are restored with their names, folders and times; without it, each verified
/// block is written as <c>block-&lt;n&gt;.bin</c> next to a <see cref="ReportFileName"/>.
/// </summary>
public static class Salvage
{
    public const string ReportFileName = "salvage-report.txt";

    /// <param name="password">Asked once, and only for an encrypted archive whose header can supply the key.</param>
    /// <exception cref="PasswordRequiredException">The archive is encrypted and no password was given.</exception>
    /// <exception cref="WrongPasswordException">The header is intact and the password does not match it.</exception>
    /// <exception cref="NewerVersionException">The header is intact but uses a format feature of a newer version.</exception>
    /// <exception cref="CorruptDataException">The file holds no Osmium archive data at all.</exception>
    /// <exception cref="WeakProtectionException">The archive is protected more weakly than any Osmium preset and
    /// <see cref="SalvageOptions.Protection"/> does not allow it (before the password is asked for).</exception>
    /// <exception cref="KdfMemoryLimitException">Its key derivation needs more than <see cref="SalvageOptions.MemoryBudget"/>.</exception>
    /// <exception cref="KdfCostException">Its key derivation takes more work than the strongest preset, and
    /// <see cref="SalvageOptions.AllowSlowKdf"/> is not set.</exception>
    /// <exception cref="ModifiedArchiveException">The archive says it is encrypted, but frames where its authenticated
    /// index places blocks are not, or none of its blocks authenticates. Nothing is written.</exception>
    public static SalvageResult Run(string archivePath, string destination, Func<string?>? password = null,
        SalvageOptions? options = null, CancellationToken ct = default)
    {
        using var run = new SalvageRun(Path.GetFullPath(archivePath), Path.GetFullPath(destination), options ?? new SalvageOptions(), ct);
        return run.Execute(password);
    }
}

internal sealed class SalvageRun : IDisposable
{
    private readonly string _archive, _dest;
    private readonly SalvageOptions _o;
    private readonly CancellationToken _ct;
    private readonly SalvageSource _src;
    private readonly FrameScanner _scanner;
    private readonly List<string> _notes = [];
    private HeaderProbe? _probe; // what was found where the header should be: a header copy must be a copy of it
    private readonly ConcurrentQueue<SalvageLoss> _lost = new(), _newer = new();
    private readonly ConcurrentQueue<SalvageRename> _renamed = new();
    private readonly ConcurrentQueue<string> _warnings = new();
    private readonly ConcurrentQueue<string> _written = new();
    private long _bytesRecovered;
    private int _recovered, _skipped, _filesTotal;
    private string? _zone, _refused;

    public SalvageRun(string archive, string dest, SalvageOptions o, CancellationToken ct)
    {
        _archive = archive;
        _dest = dest;
        _o = o;
        _ct = ct;
        _src = new SalvageSource(archive);
        _scanner = new FrameScanner(_src, o.Registry, o.Progress, ct);
    }

    public void Dispose()
    {
        _scanner.Key?.Key.Dispose(); // the key is zeroed when salvage ends
        _src.Dispose();
    }

    private string? _archiveIdentity;
    private bool _archiveIdentityRead;

    /// <summary>The archive's <see cref="TargetNames.FileIdentity(string)"/>, read once: a file in the destination with
    /// it is the archive, whatever path reaches it.</summary>
    private string? ArchiveIdentity
    {
        get
        {
            if (!_archiveIdentityRead) (_archiveIdentity, _archiveIdentityRead) = (TargetNames.FileIdentity(_archive), true);
            return _archiveIdentity;
        }
    }

    public SalvageResult Execute(Func<string?>? password)
    {
        // 1. Header: an unencrypted archive does not need it; an encrypted one needs its salt and archive id.
        bool anyFrame = _scanner.NextFrameHeader(0, out var first);
        var probe = _probe = HeaderProbe.Find(_src, anyFrame ? first.Offset : _src.Length);
        if (probe.Header is null && !anyFrame)
            throw new CorruptDataException($"No Osmium archive data was found in '{_archive}': it has no intact header and no frame.");
        var (encrypted, encryptedBecause) = Encryption(probe, anyFrame);
        _scanner.ArchiveEncrypted = encrypted;
        if (encrypted)
        {
            _scanner.Key = ResolveKey(probe, anyFrame ? first.Offset : _src.Length, Once(password), out string? keyProblem);
            _scanner.KeyProblem = keyProblem;
        }
        else if (probe.Header is null) _notes.Add("The archive header is damaged; an unencrypted archive does not need it.");

        // 2. Every frame from the start of the file, verified on the way: where a header was found does not decide where
        // this archive's frames are (an archive stored in a block has a header of its own).
        var frames = _scanner.ScanAll(0);
        if (encrypted && frames.Any(f => f.State == SalvageFrameState.Ignored && f.Problem == FrameScanner.PlaintextInEncrypted))
            _notes.Add($"The archive counts as encrypted ({encryptedBecause}), so frames that are not encrypted were not written: "
                + "they are not protected as Osmium protects encrypted archives. "
                + (encryptedBecause == PasswordGivenReason
                    ? "If the archive was not encrypted, salvage it again without a password."
                    : "If the archive was encrypted, salvage it with its password. If you know it was not, it was changed on purpose, "
                        + "and salvage cannot recover it safely."));

        // 3. The index, if one is intact.
        long headerLength = probe.Header?.Length ?? (anyFrame ? first.Offset : 0);
        var (index, indexFrame, indexProblem) = ChooseIndex(frames, probe, headerLength);

        // 4. Files (index intact) or block files and a report (index lost).
        List<SalvageBlock> blocks = [];
        if (index is not null)
        {
            long unpacked = index.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size), stored = index.Blocks.Sum(b => Math.Max(0, b.RawSize));
            if (stored == 0 && unpacked > 0) _refused = $"the archive's files hold {SalvageResult.Size(unpacked)}, but its blocks hold nothing";
            else if (stored > 0 && (double)unpacked / stored > _o.MaxExpansion)
                _refused = $"the archive expands {(double)unpacked / stored:F0}× (heavy deduplication or a decompression bomb)";
            else blocks = RecoverFiles(index, frames, probe.Header is not null ? probe.Offset : 0);
        }
        else blocks = DumpBlocks(frames);
        string? refused = _refused;

        var (unrecognized, ranges) = Unrecognized(frames, probe, indexFrame);
        var result = new SalvageResult
        {
            ArchivePath = _archive,
            Destination = _dest,
            ArchiveLength = _src.Length,
            Header = probe.Status,
            HeaderIntact = probe.Header is not null,
            Creator = probe.Header?.Creator,
            Encrypted = encrypted,
            KeyProblem = encrypted && _scanner.Key is null ? _scanner.KeyProblem : null,
            Frames = frames,
            Blocks = blocks,
            IndexFrame = indexFrame,
            IndexProblem = indexProblem,
            FilesTotal = _filesTotal,
            FilesRecovered = _recovered,
            FilesSkipped = _skipped,
            Lost = [.. _lost.OrderBy(l => l.Path, StringComparer.Ordinal)],
            NeedNewerVersion = [.. _newer.OrderBy(l => l.Path, StringComparer.Ordinal)],
            Written = [.. _written.Order(StringComparer.Ordinal)],
            Renamed = [.. _renamed.OrderBy(r => r.Path, StringComparer.Ordinal)],
            BytesRecovered = _bytesRecovered,
            UnrecognizedBytes = unrecognized,
            UnrecognizedRanges = ranges,
            Notes = _notes,
            Warnings = [.. _warnings],
            Refused = refused,
        };
        if (index is null && refused is null) WriteReport(result);
        return result;
    }

    /// <summary>Refuses (writing nothing) when the destination lacks the space for <paramref name="bytes"/>.</summary>
    private bool NoRoom(long bytes)
    {
        if (!_o.CheckFreeSpace || (_o.FreeSpaceProbe ?? FreeSpace)(_dest) is not { } free || free >= bytes) return false;
        _refused = $"not enough free space: {SalvageResult.Size(bytes)} would be written, {SalvageResult.Size(free)} is free";
        return true;
    }

    /// <summary>Free bytes on the volume that holds <paramref name="path"/> (the mount with the longest matching root).</summary>
    private static long? FreeSpace(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            DriveInfo? best = null;
            foreach (var d in DriveInfo.GetDrives())
            {
                string root = d.RootDirectory.FullName;
                bool under = path.StartsWith(root, comparison)
                    && (root.EndsWith(Path.DirectorySeparatorChar) || path.Length == root.Length || path[root.Length] == Path.DirectorySeparatorChar);
                if (under && (best is null || root.Length > best.RootDirectory.FullName.Length)) best = d;
            }
            return best is { IsReady: true } ? best.AvailableFreeSpace : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Without the header, the index frame that the footer at the end of the file points to, whose frame header checks
    /// out: both are protected by a CRC, and an archive stored in a block cannot place its footer there.
    /// </summary>
    private (long Offset, long Length, bool Encrypted)? EndIndexFrame(HeaderProbe probe)
    {
        if (probe.EndFooter is not { } ft || ft.IndexLength < Osa.FrameHeaderSize) return null;
        // The index frame sits right before the footer, or where the footer says when nothing before it has moved.
        foreach (long at in new[] { _src.Length - Osa.FooterSize - ft.IndexLength, ft.IndexOffset })
        {
            if (_scanner.ReadFrameHeader(at) is { } fh && fh.Number == Osa.IndexFrameNumber && (fh.Flags & Osa.FrameFlagIndex) != 0
                && fh.BodyLength == ft.IndexLength - Osa.FrameHeaderSize)
                return (at, ft.IndexLength, (fh.Flags & Osa.FrameFlagEncrypted) != 0);
        }
        return null;
    }

    /// <summary>
    /// The bytes that the blocks of a plaintext index frame occupy, when its body checks out and decodes: they end where
    /// its spare copy B begins (when the index names one and H's trailer says where) or where it begins. Null when it
    /// does not decode.
    /// </summary>
    private List<(long Start, long End)>? PlaintextIndexBlocks((long Offset, long Length, bool Encrypted) end)
    {
        if (end.Encrypted || end.Length > Osa.MaxIndexFrameBytes) return null;
        try
        {
            var body = _src.Read(end.Offset + Osa.FrameHeaderSize, (int)(end.Length - Osa.FrameHeaderSize));
            var parsed = BlockBody.Parse(body, _o.Registry, "the index");
            var raw = PipelineExecutor.Decode(parsed.Descriptor, parsed.Payload, new CodecContext { Token = _ct });
            if (BlockBody.Hash128(raw) != parsed.RawHash) return null;
            var index = ArchiveIndex.Deserialize(raw, 0, _o.Registry);
            long blocksEnd = end.Offset;
            if (index.Metadata.ContainsKey(ArchiveIndex.SparesKey) && end.Offset >= SpareCopies.TrailerSize
                && SpareTrailer.Read(_src.Read(end.Offset - SpareCopies.TrailerSize, SpareCopies.TrailerSize)) is { Newer: false } t
                && end.Offset - t.CopyLength - end.Length is var spare and >= 0)
                blocksEnd = spare;
            long span = index.Blocks.Count == 0 ? 0 : index.Blocks[^1].Offset + index.Blocks[^1].FrameLength;
            long origin = blocksEnd - span;
            return [.. index.Blocks.Select(b => (origin + b.Offset, origin + b.Offset + b.FrameLength))];
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException)) { return null; }
    }

    /// <summary>The encryption flag of a damaged header whose marker survived.</summary>
    private bool? DamagedHeaderEncrypted()
    {
        if (_src.Length < 16) return null;
        var b = _src.Read(0, 16);
        return b.AsSpan(0, 8).SequenceEqual(Osa.Magic) ? (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)) & Osa.FlagEncrypted) != 0 : null;
    }

    /// <summary>
    /// Whether the archive is encrypted, and what says so. An intact header decides. Without one, the archive counts as
    /// encrypted when anything says so: an intact copy of its header (FORMAT §4.1), the index frame the footer at the
    /// end of the file points to, a damaged header whose marker survived, a password given before salvage started, or
    /// an encrypted frame. An encrypted frame inside the bytes of a block that a plaintext index frame at the end lists
    /// does not count (an unencrypted archive whose damage uncovers an encrypted archive stored in one of its blocks is
    /// still salvaged); one outside them does, so a plaintext file list put beside an encrypted archive's frames cannot
    /// make salvage write what it lists. No frame that is not encrypted is written from an encrypted archive (FORMAT §8).
    /// </summary>
    private const string PasswordGivenReason = "a password was given";

    private (bool Encrypted, string? Because) Encryption(HeaderProbe probe, bool anyFrame)
    {
        if (probe.Header is { } h) return (h.Encrypted, h.Encrypted ? "its header says so" : null);
        if (IntactHeaderCopies(probe).Any(c => c.Encrypted)) return (true, "a copy of its header says so");
        var end = EndIndexFrame(probe);
        if (end is { Encrypted: true }) return (true, "the file list at its end is encrypted");
        if (DamagedHeaderEncrypted() == true) return (true, "its damaged header says so");
        if (_o.PasswordGiven) return (true, PasswordGivenReason);
        var listed = end is { } e ? PlaintextIndexBlocks(e) : null;
        if (!anyFrame || FirstEncryptedFrame(listed) is not { } at) return (false, null);
        return (true, listed is null ? $"an encrypted frame was found at offset {at:N0}"
            : $"an encrypted frame was found at offset {at:N0}, outside the blocks of the file list at its end");
    }

    /// <summary>The first frame header that checks out and says encrypted, outside the bytes of the blocks in
    /// <paramref name="listed"/>; each claimed body is skipped, so that the frames of an archive stored in an intact block
    /// are not seen.</summary>
    private long? FirstEncryptedFrame(List<(long Start, long End)>? listed)
    {
        for (long pos = 0; _scanner.NextFrameHeader(pos, out var fh);)
        {
            if ((fh.Flags & Osa.FrameFlagEncrypted) != 0 && !(listed is not null && Inside(listed, fh.Offset))) return fh.Offset;
            long end = fh.Offset + Osa.FrameHeaderSize + fh.BodyLength;
            pos = fh.BodyLength >= 0 && end > fh.Offset && end <= _src.Length ? end : fh.Offset + 1;
        }
        return null;

        // The blocks of an index follow one another in the file: the last one that starts at or before the offset.
        static bool Inside(List<(long Start, long End)> blocks, long at)
        {
            int lo = 0, hi = blocks.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (blocks[mid].Start <= at) lo = mid + 1;
                else hi = mid - 1;
            }
            return hi >= 0 && at < blocks[hi].End;
        }
    }

    private List<ArchiveHeader>? _headerCopies;

    /// <summary>
    /// The header copies H (FORMAT §4.1) that check out as headers, beside index frames found by their frame headers
    /// (bodies skipped, so that an archive stored in a block has no say while its frame header is intact), whose archive
    /// id agrees with the footer at the end of the file when there is one. Read once.
    /// </summary>
    private List<ArchiveHeader> IntactHeaderCopies(HeaderProbe probe)
    {
        if (_headerCopies is not null) return _headerCopies;
        var copies = new List<ArchiveHeader>();
        for (long pos = 0; _scanner.NextFrameHeader(pos, out var fh);)
        {
            long end = fh.Offset + Osa.FrameHeaderSize + fh.BodyLength;
            bool fits = fh.BodyLength >= 0 && end > fh.Offset && end <= _src.Length;
            if (fits && fh.Number == Osa.IndexFrameNumber && (fh.Flags & Osa.FrameFlagIndex) != 0)
                foreach (var (start, trailer) in HeaderCopiesBeside(fh.Offset, end - fh.Offset))
                {
                    ArchiveHeader h;
                    try { h = ArchiveHeader.Read(new MemoryStream(_src.Read(start, (int)trailer.HeaderLength), writable: false)); }
                    catch (Exception e) when (e is IOException or InvalidDataException) { continue; } // a newer version's, too
                    if (copies.Any(c => c.Raw.AsSpan().SequenceEqual(h.Raw))) continue;
                    if (probe.EndFooter is { } ft && BinaryPrimitives.ReadUInt64LittleEndian(h.ArchiveId) != ft.IdPrefix) continue;
                    copies.Add(h);
                }
            pos = fits ? end : fh.Offset + 1;
        }
        return _headerCopies = copies;
    }

    // ------------------------------------------------------------------ key

    /// <summary>A way to derive this archive's key: the settings, the archive id, and the key fields read for them.</summary>
    /// <param name="Settled">The kdf id is known (an intact or repaired header, or a kdf byte that reads 1 or 2): a refusal
    /// of its settings ends salvage. A reading of a damaged kdf byte that would be refused only counts as one that does
    /// not authenticate.</param>
    private sealed record KeyCandidate(EncryptionInfo Info, byte[] ArchiveId, KeyFields? Fields, bool Settled);

    private KeyMaterial? ResolveKey(HeaderProbe probe, long firstFrame, Func<string?>? password, out string? problem)
    {
        problem = null;
        var run = new KdfRun { MemoryLimitBytes = _o.MemoryBudget, Policy = _o.Protection, AllowSlow = _o.AllowSlowKdf, Cancellation = _ct };
        if (probe.Header is { Encryption: { } enc } h)
        {
            // Every check of the settings comes before the password is asked for.
            PasswordKdf.Admit(enc.Kdf, run);
            string pw = Ask(password);
            PasswordKdf.CheckPasswordText(pw, creating: false);
            var key = ArchiveKey.Derive(pw, enc, run);
            if (!key.Matches(enc.Verifier))
            {
                key.Dispose();
                throw new WrongPasswordException();
            }
            return new KeyMaterial(key, h.ArchiveId, h.Raw, PadsIndex: enc.Kdf.Algorithm == KdfAlgorithm.Argon2id, PadsBlocks: h.PaddedBlocks);
        }
        // Container 1.1 keeps an intact copy of the header beside the copies of the index (FORMAT §4.1).
        string? copyProblem = null;
        if (probe.Header is null && KeyFromHeaderCopy(probe, password, run, out copyProblem) is { } fromCopy) return fromCopy;
        // A damaged header may still hold the key fields intact: they are used only if they authenticate frames (GCM),
        // or the password verifier confirms them. They are read from the header with its CRC recomputed when that
        // parses, else from their fixed places; when the kdf byte itself is damaged, in both layouts.
        var fields = probe.Header is not null ? [] : KeyFields.Read(_src);
        var candidates = new List<KeyCandidate>();
        if (probe.Header is null && probe.Repaired?.Encryption is { } repairedInfo)
            candidates.Add(new(repairedInfo, probe.Repaired.ArchiveId, fields.FirstOrDefault(f => f.Encryption.Kdf.Algorithm == repairedInfo.Kdf.Algorithm), Settled: true));
        else if (probe.Header is null)
            candidates.AddRange(fields.Select(f => new KeyCandidate(f.Encryption, f.ArchiveId, f, f.KdfByteIntact)));
        if (candidates.Count == 0)
        {
            problem = copyProblem ?? "the archive header, which holds the key-derivation salt and the archive id, is damaged or missing, "
                + "and no intact spare copy of it was found (archives of Osmium 0.1 and small archives have none): "
                + "encrypted salvage needs an intact header";
            return null;
        }

        // Every candidate is admitted before the password is asked for.
        var admitted = new List<KeyCandidate>();
        Exception? firstRefusal = null;
        foreach (var c in candidates)
        {
            try
            {
                PasswordKdf.Admit(c.Info.Kdf, run);
                admitted.Add(c);
            }
            catch (Exception e) when (!c.Settled && e is KdfMemoryLimitException or KdfCostException or WeakProtectionException)
            {
                firstRefusal ??= e;
            }
        }
        if (admitted.Count == 0) ExceptionDispatchInfo.Throw(firstRefusal!);

        string password0 = Ask(password);
        PasswordKdf.CheckPasswordText(password0, creating: false);
        bool anyVerifierMatched = false;
        foreach (var c in admitted)
        {
            var key = ArchiveKey.Derive(password0, c.Info, run);
            bool verifierMatches = key.Matches(c.Info.Verifier);
            anyVerifierMatched |= verifierMatches;
            // The footer at the end of the file keeps the first 8 bytes of the archive id too.
            var ids = new List<byte[]> { c.ArchiveId };
            if (probe.EndFooter is { } ft && BinaryPrimitives.ReadUInt64LittleEndian(c.ArchiveId) != ft.IdPrefix)
            {
                var patched = (byte[])c.ArchiveId.Clone();
                BinaryPrimitives.WriteUInt64LittleEndian(patched, ft.IdPrefix);
                ids.Add(patched);
            }
            foreach (var id in ids)
            {
                bool? authenticates = _scanner.Authenticates(key, id);
                if (authenticates != true && !(authenticates is null && verifierMatches)) continue;
                _notes.Add("The archive header is damaged, but the key-derivation data and archive id read from it authenticate the frames.");
                // The index binds the exact header bytes: try the header as found and as a writer would have made it.
                var rebuilt = new List<byte[]>();
                if (probe.Repaired is { } repaired) rebuilt.Add(repaired.Raw);
                if (c.Fields is { } f) rebuilt.AddRange(f.Rebuilds(id, firstFrame));
                // The header's flags are not known: in an Argon2id archive, blocks that authenticate may be padded.
                bool argon2id = c.Info.Kdf.Algorithm == KdfAlgorithm.Argon2id;
                return new KeyMaterial(key, id, rebuilt.FirstOrDefault() ?? [], rebuilt.Skip(1).ToList(), argon2id, PadsBlocks: argon2id);
            }
            key.Dispose();
            if (verifierMatches) break; // the password and these settings belong together: another reading cannot be them
        }
        problem = anyVerifierMatched
            ? "the archive header is damaged: the password matches it, but the archive id needed to authenticate the frames does not"
            : "the archive header is damaged, and the password could not be confirmed against it (a wrong password, or damaged key-derivation data)";
        return null;

        static string Ask(Func<string?>? pw) => pw?.Invoke() ?? throw new PasswordRequiredException();
    }

    /// <summary>
    /// The key from a header copy H, when the header itself is damaged. H sits just before the index frame and just
    /// after its spare copy, and frame headers are not encrypted, so index frames are found by their frame headers
    /// (bodies skipped, so that an archive stored in a block has no say while its frame header is intact). A copy is
    /// used only when its archive id agrees with the footer at the end of the file, when there is one; when that footer
    /// vouches for it, a password it does not accept is wrong. It is used once frames authenticate under it.
    /// </summary>
    private KeyMaterial? KeyFromHeaderCopy(HeaderProbe probe, Func<string?>? password, KdfRun run, out string? problem)
    {
        problem = null;
        var copies = IntactHeaderCopies(probe).Where(h => h.Encryption is not null).ToList();
        if (copies.Count == 0) return null;
        // A copy checks out as a header, so its settings are settled: a refusal of them ends salvage, before the password.
        foreach (var h in copies) PasswordKdf.Admit(h.Encryption!.Kdf, run);
        string pw = password?.Invoke() ?? throw new PasswordRequiredException();
        PasswordKdf.CheckPasswordText(pw, creating: false);
        bool refused = false;
        foreach (var h in copies.AsEnumerable().Reverse()) // the last one first: the copy beside the index at the end
        {
            var key = ArchiveKey.Derive(pw, h.Encryption!, run);
            if (!key.Matches(h.Encryption!.Verifier))
            {
                key.Dispose();
                refused |= probe.EndFooter is not null;
                continue;
            }
            if (_scanner.Authenticates(key, h.ArchiveId) == false)
            {
                key.Dispose();
                continue;
            }
            _notes.Add("The archive header is damaged; its spare copy beside the file list is intact and was used.");
            return new KeyMaterial(key, h.ArchiveId, h.Raw, PadsIndex: h.Encryption.Kdf.Algorithm == KdfAlgorithm.Argon2id, PadsBlocks: h.PaddedBlocks);
        }
        if (refused) throw new WrongPasswordException();
        problem = "the archive header is damaged, and the spare copy of it that was found does not accept the password, "
            + "or does not authenticate the frames (a wrong password, or the copy of another archive)";
        return null;
    }

    /// <summary>Asks for the password at most once, however many places need it.</summary>
    private static Func<string?>? Once(Func<string?>? password)
    {
        if (password is null) return null;
        bool asked = false;
        string? answer = null;
        return () =>
        {
            if (!asked) (answer, asked) = (password(), true);
            return answer;
        };
    }

    // ------------------------------------------------------------------ index

    /// <summary>An intact, readable index frame, and what ties it to this archive.</summary>
    /// <param name="AtEnd">The footer at the end of the file directly follows it.</param>
    /// <param name="Vouched">A footer naming this archive directly follows it, or it authenticated under this archive's key.</param>
    /// <param name="Span">How many bytes its blocks occupy, ending at <paramref name="BlocksEnd"/>.</param>
    /// <param name="BlocksEnd">Where its blocks end: where the index frame starts, or, with spare copies (container 1.1),
    /// where the spare copy of the index frame starts.</param>
    private sealed record IndexCandidate(SalvageFrame Frame, ArchiveIndex Index, bool AtEnd, bool Vouched, long Span, long BlocksEnd);

    /// <summary>Where the blocks of the index in <paramref name="f"/> end: an index frame with a header copy's trailer
    /// just before it has its blocks end before its spare copy and the header copy (FORMAT §4.1).</summary>
    private long BlocksEnd(SalvageFrame f, ArchiveIndex index)
    {
        if (!index.Metadata.ContainsKey(ArchiveIndex.SparesKey) || f.Offset < SpareCopies.TrailerSize) return f.Offset;
        return SpareTrailer.Read(_src.Read(f.Offset - SpareCopies.TrailerSize, SpareCopies.TrailerSize)) is { Newer: false } t
            && f.Offset - t.CopyLength - f.FrameLength is var start and >= 0
            ? start : f.Offset;
    }

    /// <summary>
    /// Picks this archive's index among the intact index frames. Set aside, unless a footer naming this archive follows
    /// it: an index whose footer names another archive; one inside a damaged frame, or within the bytes that another
    /// index's blocks occupy (both may be an archive stored in a block); and one followed by intact frames it does not
    /// list. Of the rest, the one at the end of the file wins, then one a footer vouches for, then the one that accounts
    /// for the most frames found outside damaged frames, then the last in the file.
    /// </summary>
    private (ArchiveIndex? Index, SalvageFrame? Frame, string? Problem) ChooseIndex(List<SalvageFrame> frames, HeaderProbe probe, long headerLength)
    {
        var all = frames.Where(f => f.IsIndex).ToList();
        ulong? idPrefix = probe.IdPrefix;
        var candidates = new List<IndexCandidate>();
        // The frames that another archive's index lists are that archive's: they never stand for this archive's blocks.
        void Disown(IndexCandidate c)
        {
            foreach (var b in frames)
                if (b.Intact && b.ForeignIndexOffset is null && b.Offset >= c.BlocksEnd - c.Span && b.Offset < c.BlocksEnd && Lists(c.Index, b))
                    b.ForeignIndexOffset = c.Frame.Offset;
        }
        var foreignIndexes = new List<IndexCandidate>();
        foreach (var f in all.Where(f => f.State == SalvageFrameState.Verified))
        {
            var footer = FooterInfo.At(_src, f.Offset + f.FrameLength);
            if (footer is { } ft && ft.IndexLength != f.FrameLength) footer = null;
            bool foreign = footer is { } named && idPrefix is { } id && named.IdPrefix != id;
            var raw = DecodeOrReject(f);
            if (raw is null) continue;
            ArchiveIndex index;
            try
            {
                index = ArchiveIndex.Deserialize(raw, headerLength, _o.Registry);
            }
            catch (NewerVersionException e)
            {
                f.NeedsNewerVersion(e);
                continue;
            }
            catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
            {
                // Authentic but malformed (a writer bug or a crafted archive): fall back to block files.
                f.Reject(SalvageFrameState.Damaged, "the index is unreadable: " + e.Message.TrimEnd('.'));
                continue;
            }
            long span = index.Blocks.Count == 0 ? 0 : index.Blocks[^1].Offset + index.Blocks[^1].FrameLength - headerLength;
            bool atEnd = footer is not null && f.Offset + f.FrameLength + Osa.FooterSize == _src.Length;
            // An encrypted index that authenticated is this archive's: its tag binds the exact header bytes.
            bool vouched = (footer is not null && idPrefix is not null) || f.Encrypted;
            long blocksEnd = BlocksEnd(f, index);
            var candidate = new IndexCandidate(f, index, atEnd, vouched, Math.Clamp(span, 0, blocksEnd), blocksEnd);
            if (foreign)
            {
                f.Reject(SalvageFrameState.Ignored, "the footer after it names another archive");
                Disown(candidate);
                foreignIndexes.Add(candidate);
            }
            else candidates.Add(candidate);
        }
        // A copy of an index frame (a spare copy, container 1.1) belongs to the archive the index frame belongs to.
        foreach (var c in candidates.Where(c => foreignIndexes.Any(o => c.Frame.SameContent(o.Frame))).ToList())
        {
            var of = foreignIndexes.First(o => c.Frame.SameContent(o.Frame));
            c.Frame.Reject(SalvageFrameState.Ignored, $"a copy of the index at offset {of.Frame.Offset:N0}, whose footer names another archive");
            Disown(c);
            candidates.Remove(c);
        }

        // Copies of one index count once; the best placed copy stands for them. The copy an index names as its spare
        // (container 1.1) is no sign of damage.
        var kept = new List<IndexCandidate>();
        var copies = new Dictionary<IndexCandidate, List<IndexCandidate>>();
        foreach (var c in candidates.OrderByDescending(c => c.AtEnd).ThenByDescending(c => c.Vouched)
                     .ThenBy(c => c.Frame.EnclosingOffset is not null).ThenByDescending(c => c.Frame.Offset))
        {
            if (kept.FirstOrDefault(k => c.Frame.SameContent(k.Frame)) is { } same)
            {
                c.Frame.IsSpareCopy = same.Index.Metadata.TryGetValue(ArchiveIndex.SparesKey, out var v) && v.Length == 8
                    && BinaryPrimitives.ReadUInt64LittleEndian(v) == (ulong)c.Frame.Offset;
                c.Frame.Reject(SalvageFrameState.Duplicate, c.Frame.IsSpareCopy
                    ? $"the spare copy of the index at offset {same.Frame.Offset:N0}" : $"a copy of the index at offset {same.Frame.Offset:N0}");
                (copies.TryGetValue(same, out var list) ? list : copies[same] = []).Add(c);
            }
            else kept.Add(c);
        }

        var outside = frames.Where(f => !f.IsIndex && f.Intact && f.EnclosingOffset is null
            && f.State is SalvageFrameState.Verified or SalvageFrameState.NeedsNewerVersion).ToList();
        // Why an index cannot be this archive's, and whether that makes it another archive's.
        (string? Why, bool Foreign) Objection(IndexCandidate c)
        {
            if (c.AtEnd || c.Vouched) return (null, false);
            if (c.Frame.EnclosingOffset is { } enc)
                return ($"it lies inside the damaged frame at offset {enc:N0}, so it may belong to an archive stored in that frame", true);
            if (kept.FirstOrDefault(o => o != c && c.Frame.Offset >= o.BlocksEnd - o.Span && c.Frame.Offset < o.BlocksEnd) is { } outer)
                return ($"it lies within the blocks of the index at offset {outer.Frame.Offset:N0}, so it belongs to an archive stored in one of them", true);
            // An archive has one index, after all of its frames (the format has no appends).
            if (outside.FirstOrDefault(b => b.Offset > c.Frame.Offset && !Lists(c.Index, b)) is { } later)
                return ($"the intact {later.Name} at offset {later.Offset:N0} comes after it but is not in it", true);
            return (null, false);
        }
        var eligible = new List<IndexCandidate>();
        foreach (var c in kept)
        {
            var (why, foreign) = Objection(c);
            if (why is null) { eligible.Add(c); continue; }
            c.Frame.Reject(SalvageFrameState.Ignored, why);
            if (!foreign) continue;
            Disown(c);
            foreach (var copy in copies.GetValueOrDefault(c, []))
            {
                copy.Frame.IsSpareCopy = false;
                copy.Frame.Reject(SalvageFrameState.Ignored, $"a copy of the index at offset {c.Frame.Offset:N0}, which belongs to another archive");
                Disown(copy);
            }
        }
        var best = eligible.OrderByDescending(c => c.AtEnd).ThenByDescending(c => c.Vouched)
            .ThenByDescending(c => outside.Count(b => Lists(c.Index, b))).ThenByDescending(c => c.Frame.Offset).FirstOrDefault();
        if (best is not null)
        {
            foreach (var c in eligible.Where(c => c != best)) c.Frame.Reject(SalvageFrameState.Ignored, $"the index at offset {best.Frame.Offset:N0} was used");
            return (best.Index, best.Frame, null);
        }

        if (all.Count == 0)
            return (null, null, FooterAt(_src.Length - Osa.FooterSize) ? "no index frame was found" : "no index frame was found (the end of the archive is missing)");
        var telling = all.OrderBy(f => f.State == SalvageFrameState.Ignored).ThenBy(f => f.State == SalvageFrameState.Duplicate)
            .ThenByDescending(f => f.Offset).First();
        string problem = telling.State switch
        {
            SalvageFrameState.NeedsNewerVersion => $"the index needs a newer version of Osmium ({telling.Problem})",
            SalvageFrameState.NotDecrypted => "the index could not be decrypted",
            _ => $"the index frame at offset {telling.Offset:N0} is {SalvageResult.StateText(telling.State)} ({telling.Problem})",
        };
        if (telling.State == SalvageFrameState.Damaged && telling.Encrypted && probe.Header is null)
            problem += "; the index is authenticated together with the archive header, which is damaged";
        return (null, null, problem);
    }

    /// <summary>Whether a block frame is the block this index lists under its number: same frame length, raw size and pipeline.</summary>
    private static bool Lists(ArchiveIndex index, SalvageFrame f) =>
        !f.IsIndex && f.Number < (ulong)index.Blocks.Count && Matches(index.Blocks[(int)f.Number], f);

    private static bool Matches(BlockInfo info, SalvageFrame f) =>
        f.FrameLength == info.FrameLength && f.RawSize == info.RawSize
        && f.Descriptor is { } d && d.Serialize().AsSpan().SequenceEqual(info.Descriptor.Serialize());

    private bool FooterAt(long at) => FooterInfo.At(_src, at) is not null;

    /// <summary>
    /// Memory to reserve while a block decodes. Each stage's output is as large as the input size its descriptor records,
    /// and a crafted descriptor can make an inner stage far larger than the block, so the largest stage counts. A
    /// reservation above the budget makes the block decode alone.
    /// </summary>
    internal static long DecodeCharge(BlockDescriptor d, long declared = 0)
    {
        long largest = Math.Max(0, d.InputSizes.Max());
        return Math.Max(declared, d.Spec.DecodeMemory(largest) + 2 * largest);
    }

    /// <summary>Decodes and verifies a frame; on failure marks it damaged and returns null.</summary>
    private byte[]? DecodeOrReject(SalvageFrame f)
    {
        try
        {
            var raw = _scanner.Decode(f, _ct);
            f.Decoded = true;
            return raw;
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            f.Reject(SalvageFrameState.Damaged, "it could not be decoded: " + e.Message.TrimEnd('.'));
            return null;
        }
    }

    // ------------------------------------------------------------------ index intact: files

    private sealed class FileJob(ArchiveEntry entry, string target)
    {
        public ArchiveEntry Entry { get; } = entry;
        public string Target { get; } = target;
        public string? PartPath;
        public Microsoft.Win32.SafeHandles.SafeFileHandle? Part; // open from its creation until it is verified
        public int Remaining = entry.Segments.Count;
        public readonly long[] FileOffsets = Offsets(entry);
        public bool Failed, Finished, Renamed, Replaces;
        public string? Error;

        private static long[] Offsets(ArchiveEntry e)
        {
            var o = new long[e.Segments.Count];
            long acc = 0;
            for (int i = 0; i < o.Length; i++) { o[i] = acc; acc += e.Segments[i].Length; }
            return o;
        }
    }

    private List<SalvageBlock> RecoverFiles(ArchiveIndex index, List<SalvageFrame> frames, long baseOffset)
    {
        int n = index.Blocks.Count;
        var byNumber = frames.Where(f => !f.IsIndex && f.Number < (ulong)n).ToLookup(f => (int)f.Number);
        var chosen = new SalvageFrame?[n];
        for (int k = 0; k < n; k++) chosen[k] = Match(index.Blocks[k], byNumber[k], baseOffset);
        if (_scanner.ArchiveEncrypted) RefuseUnprotectedBlocks(index, frames, chosen, baseOffset);
        foreach (var f in frames.Where(f => !f.IsIndex && f.Number >= (ulong)n && f.State is SalvageFrameState.Verified or SalvageFrameState.NeedsNewerVersion))
            f.Reject(SalvageFrameState.Ignored, "the index lists no block with this number");

        SalvageBlock Status(int k) => chosen[k] switch
        {
            { State: SalvageFrameState.NeedsNewerVersion } c => new(k, SalvageBlockState.NeedsNewerVersion, c.Problem, c),
            { Decoded: true } c => new(k, SalvageBlockState.Verified, null, c),
            { } c => new(k, SalvageBlockState.Damaged, c.Problem, c),
            null => Unavailable(k, byNumber[k]),
        };
        string Needs(IEnumerable<int> ks) => "needs " + string.Join("; ", ks.Distinct().Order().Select(k => Describe(Status(k))));

        // Plan everything before writing anything, so that a refusal leaves the destination untouched, with the rules
        // of extraction (FORMAT §7): every entry's own name is reserved first, so that a renamed file never takes the
        // name of another entry (the archive may hold both report.txt and "report (2).txt") and two entries never share
        // a name on a case-insensitive file system; nothing is written below a link that is already in the destination;
        // only a file planned to be replaced is replaced.
        var names = _o.NameComparer ?? TargetNames.DefaultComparer;
        var own = new Dictionary<ArchiveEntry, string>();
        foreach (var e in index.Entries)
        {
            try { own[e] = PathSafety.Resolve(_dest, e.Path); }
            catch (CorruptDataException ex) { _lost.Enqueue(new(e.IsDirectory ? e.Path + "/" : e.Path, ex.Message)); }
        }
        // The archive being read, under whatever path the destination reaches it, gets a free name and is never replaced.
        var planner = new TargetNames(_dest, names, own.Values, null, ArchiveIdentity);
        var planned = new List<FileJob>();
        var folders = new List<(ArchiveEntry Entry, string Path)>();
        foreach (var e in index.Entries) // index order: parents before children
        {
            if (!e.IsDirectory) _filesTotal++;
            if (!own.TryGetValue(e, out var path)) continue;
            if (!e.IsDirectory)
            {
                // Damage anywhere loses the file; if its only obstacle is a newer version, a newer Osmium can recover it.
                var blocked = e.Segments.Select(s => s.Frame).Distinct()
                    .Where(k => chosen[k] is not { State: SalvageFrameState.Verified }).ToList();
                if (blocked.Any(k => Status(k).State != SalvageBlockState.NeedsNewerVersion)) { _lost.Enqueue(new(e.Path, Needs(blocked))); continue; }
                if (blocked.Count > 0) { _newer.Enqueue(new(e.Path, Needs(blocked))); continue; }
            }
            var t = planner.Plan(path, e.IsDirectory, _o.Overwrite, TargetNames.SpelledOtherwise(_dest, e.Path, path));
            switch (t.Outcome)
            {
                case TargetNames.Outcome.Skipped: _skipped++; break;
                case TargetNames.Outcome.Unsafe: _lost.Enqueue(new(e.IsDirectory ? e.Path + "/" : e.Path, t.Reason!)); break;
                case TargetNames.Outcome.Failed:
                    _lost.Enqueue(new(e.Path, t.Reason == "File exists." ? "a file of this name is already in the destination" : t.Reason!.TrimEnd('.')));
                    break;
                default:
                    if (e.IsDirectory) folders.Add((e, t.Path!));
                    else planned.Add(new FileJob(e, t.Path!) { Renamed = t.Renamed, Replaces = t.Replaces });
                    break;
            }
        }
        if (NoRoom(planned.Sum(j => j.Entry.Size))) return [];

        var created = new List<string>(); // folders this run created
        Extractor.CreateFolder(_dest, created);
        _zone = _o.Zone ?? Extractor.ReadZoneIdentifier(_archive);
        var eo = new ExtractOptions { Overwrite = _o.Overwrite, RestoreTimes = _o.RestoreTimes, RestoreAttributes = _o.RestoreAttributes };
        foreach (var (e, path) in folders)
        {
            try { Extractor.CreateFolder(path, created); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _lost.Enqueue(new(e.Path + "/", ex.Message)); }
        }
        var jobs = new List<FileJob>();
        foreach (var j in planned)
        {
            try { Extractor.CreateFolder(Path.GetDirectoryName(j.Target)!, created); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _lost.Enqueue(new(j.Entry.Path, ex.Message)); continue; }
            jobs.Add(j);
        }

        var users = new Dictionary<int, List<(FileJob Job, int Segment)>>();
        foreach (var j in jobs)
            for (int i = 0; i < j.Entry.Segments.Count; i++)
            {
                int k = j.Entry.Segments[i].Frame;
                if (!users.TryGetValue(k, out var list)) users[k] = list = [];
                list.Add((j, i));
            }

        // Every block of the index is decoded, needed or not, so that the report says which ones verify.
        var toDecode = Enumerable.Range(0, n).Where(k => chosen[k] is { State: SalvageFrameState.Verified }).ToList();
        long total = toDecode.Sum(k => index.Blocks[k].RawSize), done = 0;
        var gate = new MemoryGate(_o.MemoryBudget);
        bool finished = false;
        try
        {
            foreach (var j in jobs.Where(j => j.Entry.Segments.Count == 0)) Complete(j);
            Parallel.ForEach(toDecode, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _o.MaxParallelBlocks), CancellationToken = _ct }, k =>
            {
                var info = index.Blocks[k];
                byte[]? raw;
                using (gate.Acquire(Math.Min(_o.MemoryBudget, DecodeCharge(chosen[k]!.Descriptor!, info.DecodeMemory + info.RawSize * 2)), _ct))
                {
                    raw = DecodeOrReject(chosen[k]!);
                    if (users.TryGetValue(k, out var list))
                        foreach (var (j, seg) in list)
                            lock (j)
                            {
                                if (raw is null) j.Failed = true;
                                else if (!j.Failed) WriteSegment(j, seg, raw, _dest);
                                if (--j.Remaining == 0) Complete(j);
                            }
                }
                _scanner.Report("Recovering", Interlocked.Add(ref done, info.RawSize), total);
            });
            finished = true;
        }
        finally
        {
            foreach (var j in jobs.Where(j => !j.Finished)) DeletePart(j); // cancelled: leave no partial files behind
            if (!finished) Extractor.RemoveEmptyFolders(created);
        }
        // Times and attributes only on folders this run created, never System or ReadOnly (as extraction does).
        Extractor.ApplyCreatedFolderMetadata(folders, created, names, eo);
        return [.. Enumerable.Range(0, n).Select(Status)];

        void Complete(FileJob j)
        {
            j.Finished = true;
            if (j.Failed)
            {
                DeletePart(j);
                _lost.Enqueue(new(j.Entry.Path, j.Error ?? Needs(j.Entry.Segments.Select(s => s.Frame).Where(k => chosen[k] is not { Decoded: true }))));
                return;
            }
            try
            {
                if (j.Part is null) (j.PartPath, j.Part) = Extractor.CreatePart(_dest, j.Target); // an empty file
                bool intact = Extractor.HashFile(j.Part) == j.Entry.Hash;
                j.Part.Dispose(); // verified: closed before the mark and the rename
                j.Part = null;
                if (!intact)
                {
                    DeletePart(j);
                    _lost.Enqueue(new(j.Entry.Path, "its content does not match its hash, although every block it uses verified"));
                    return;
                }
                // Mark-of-the-Web goes on the part file: it moves with the rename, so the file never exists without
                // it, and it is in place before a ReadOnly attribute would make the stream unwritable.
                MarkAsDownloaded(j.PartPath!, j.Entry.Path);
                // The plan checked the folders for links; checked again now, as they may have changed since.
                if (new FileInfo(j.PartPath!).LinkTarget is not null) throw new IOException("its part file was replaced by a link while it was written");
                if (TargetNames.LinkOnTheWay(_dest, j.Target) is { } link) throw new IOException(link);
                FileRetry.Move(j.PartPath!, j.Target, overwrite: j.Replaces);
                j.PartPath = null;
                Extractor.ApplyFileMetadata(j.Target, j.Entry, eo);
                Interlocked.Increment(ref _recovered);
                Interlocked.Add(ref _bytesRecovered, j.Entry.Size);
                _written.Enqueue(Relative(j.Target));
                if (j.Renamed) _renamed.Enqueue(new(j.Entry.Path, Relative(j.Target)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DeletePart(j);
                _lost.Enqueue(new(j.Entry.Path, ex.Message));
            }
        }
    }

    /// <summary>
    /// As the reader does when it opens an encrypted archive (FORMAT §5): where the authenticated index places a block,
    /// a frame header that checks out, with the block's number and length, but without the encrypted flag (or with the
    /// index flag), was made that way; and when no block authenticates while some failed authentication, the archive
    /// only claims to be encrypted. Either ends salvage before anything is written.
    /// </summary>
    private static void RefuseUnprotectedBlocks(ArchiveIndex index, List<SalvageFrame> frames, SalvageFrame?[] chosen, long baseOffset)
    {
        var at = frames.Where(f => f.EnclosingOffset is null).ToLookup(f => f.Offset);
        foreach (var b in index.Blocks)
            if (at[baseOffset + b.Offset].Any(f => f.Number == (ulong)b.Frame && f.FrameLength == b.FrameLength && (!f.Encrypted || f.IsIndex)))
                throw new ModifiedArchiveException();
        if (index.Blocks.Count > 0 && chosen.All(c => c is null) && frames.Any(f => !f.IsIndex && f.FailedAuthentication))
            throw new ModifiedArchiveException();
    }

    /// <summary>
    /// The intact frame for one block of the index: same number, frame length, raw size and pipeline as the index says;
    /// preferring the expected offset, then frames outside damaged ones, then the first. The others are set aside.
    /// </summary>
    private static SalvageFrame? Match(BlockInfo info, IEnumerable<SalvageFrame> candidates, long baseOffset)
    {
        long expected = baseOffset + info.Offset;
        var usable = candidates.Where(f => f.Intact && f.State is SalvageFrameState.Verified or SalvageFrameState.NeedsNewerVersion).ToList();
        var matching = usable.Where(f => Matches(info, f))
            .OrderBy(f => f.Offset != expected).ThenBy(f => f.Doubtful).ThenBy(f => f.Offset)
            .ToList();
        foreach (var f in usable.Except(matching)) f.Reject(SalvageFrameState.Ignored, $"it does not match block {info.Frame} in the index");
        if (matching.Count == 0) return null;
        var best = matching[0];
        foreach (var f in matching.Skip(1))
        {
            bool same = f.SameContent(best);
            f.Reject(same ? SalvageFrameState.Duplicate : SalvageFrameState.Ignored,
                same ? $"a copy of the frame at offset {best.Offset:N0}" : $"the frame at offset {best.Offset:N0} was used for this block");
        }
        return best;
    }

    /// <summary>The state of a block for which no usable frame was found, from the most telling frame that has its number.</summary>
    private static SalvageBlock Unavailable(int k, IEnumerable<SalvageFrame> candidates)
    {
        var f = candidates.Where(f => f.State is not (SalvageFrameState.Verified or SalvageFrameState.Duplicate))
            .OrderBy(f => f.State == SalvageFrameState.Ignored).ThenBy(f => f.Doubtful).ThenBy(f => f.Offset)
            .FirstOrDefault();
        return f?.State switch
        {
            null => new(k, SalvageBlockState.Missing, null, null),
            SalvageFrameState.NeedsNewerVersion => new(k, SalvageBlockState.NeedsNewerVersion, f.Problem, f),
            SalvageFrameState.NotDecrypted => new(k, SalvageBlockState.NotDecrypted, f.Problem, f),
            SalvageFrameState.Ignored => new(k, SalvageBlockState.Missing, $"the frame at offset {f.Offset:N0} is not it: {f.Problem}", null),
            _ => new(k, SalvageBlockState.Damaged, f.Problem, f),
        };
    }

    private static string Describe(SalvageBlock b) => b.State switch
    {
        SalvageBlockState.Missing => $"block {b.Number}, which was not found" + (b.Problem is null ? "" : $" ({b.Problem})"),
        SalvageBlockState.NeedsNewerVersion => $"block {b.Number}, which needs a newer version of Osmium ({b.Problem})",
        SalvageBlockState.NotDecrypted => $"block {b.Number}, which could not be decrypted",
        _ => $"block {b.Number}, which is damaged ({b.Problem})",
    };

    private static void WriteSegment(FileJob j, int seg, byte[] raw, string dest)
    {
        var s = j.Entry.Segments[seg];
        try
        {
            // The first segment creates the part file (never one that is there already) and keeps it open.
            if (j.Part is null) (j.PartPath, j.Part) = Extractor.CreatePart(dest, j.Target);
            RandomAccess.Write(j.Part, raw.AsSpan((int)s.Offset, (int)s.Length), j.FileOffsets[seg]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            j.Failed = true;
            j.Error = ex.Message;
        }
    }

    /// <summary>Gives a written file the archive's Mark-of-the-Web, and warns when that fails instead of staying silent.</summary>
    private void MarkAsDownloaded(string path, string shownAs)
    {
        if (_zone is not null && (_o.ZoneWriter ?? Extractor.WriteZoneIdentifier)(path, _zone) is { } why)
            _warnings.Enqueue(Extractor.ZoneWarning(shownAs, why));
    }

    private static void DeletePart(FileJob j)
    {
        j.Part?.Dispose();
        j.Part = null;
        if (j.PartPath is null) return;
        TryDelete(j.PartPath);
        j.PartPath = null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Relative(string path) => Path.GetRelativePath(_dest, path).Replace(Path.DirectorySeparatorChar, '/');

    // ------------------------------------------------------------------ index lost: block files

    /// <summary>Writes each verified block as block-&lt;n&gt;.bin (an intact second copy with other content as
    /// block-&lt;n&gt;-at-&lt;offset&gt;.bin); identical copies are written once.</summary>
    private List<SalvageBlock> DumpBlocks(List<SalvageFrame> frames)
    {
        var plan = new List<SalvageFrame>();
        var seen = new Dictionary<(ulong, long, UInt128?, ulong?, ulong), SalvageFrame>();
        int extra = 0;
        ulong? previous = null;
        foreach (var f in frames.Where(f => !f.IsIndex && f.State == SalvageFrameState.Verified)
                     .OrderBy(f => f.Number).ThenBy(f => f.Doubtful).ThenBy(f => f.Offset))
        {
            var key = (f.Number, f.BodyLength, f.RawHash, f.PayloadHash, XxHash3.HashToUInt64(f.DescriptorBytes ?? []));
            if (seen.TryGetValue(key, out var same) && f.SameContent(same))
            {
                f.Reject(SalvageFrameState.Duplicate, $"a copy of the frame at offset {same.Offset:N0}");
                continue;
            }
            seen.TryAdd(key, f);
            // Every frame but the first of a number becomes one more file; a crafted file could ask for millions.
            if ((previous == f.Number || f.Doubtful) && ++extra > _o.MaxExtraBlockFiles)
            {
                f.Reject(SalvageFrameState.Ignored, $"not written: there are more than {_o.MaxExtraBlockFiles:N0} other intact frames");
                continue;
            }
            previous = f.Number;
            plan.Add(f);
        }
        // A block of 1 GiB of zeros takes a few kilobytes: without an index nothing else bounds what this writes.
        long total = plan.Sum(f => Math.Max(0, f.RawSize));
        double ratio = (double)total / Math.Max(1, _src.Length);
        if (ratio > _o.MaxExpansion)
        {
            _refused = $"the block files would be {ratio:F0}× the size of the archive (highly compressible data, or a decompression bomb)";
            return [];
        }
        if (NoRoom(total)) return [];

        Directory.CreateDirectory(_dest);
        _zone = _o.Zone ?? Extractor.ReadZoneIdentifier(_archive);
        var parts = new ConcurrentDictionary<SalvageFrame, string>();
        var gate = new MemoryGate(_o.MemoryBudget);
        long done = 0;
        try
        {
            Parallel.ForEach(plan, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _o.MaxParallelBlocks), CancellationToken = _ct }, f =>
            {
                using (gate.Acquire(Math.Min(_o.MemoryBudget, DecodeCharge(f.Descriptor!)), _ct))
                {
                    var raw = DecodeOrReject(f);
                    if (raw is null) return;
                    // Unique per frame: frames of one number decode at the same time.
                    string part = Extractor.PartName(Path.Combine(_dest, $"block-{f.Number}-at-{f.Offset}.bin"));
                    try
                    {
                        File.WriteAllBytes(part, raw);
                        MarkAsDownloaded(part, $"block-{f.Number}.bin"); // the stream moves with the rename
                        parts[f] = part;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        TryDelete(part);
                        f.NotWritten = ex.Message;
                        _lost.Enqueue(new($"block-{f.Number}.bin", ex.Message));
                    }
                }
                _scanner.Report("Recovering", Interlocked.Add(ref done, f.RawSize), total);
            });

            // Names in a fixed order: the first verified copy of block n is block-n.bin, unless it lies inside a damaged
            // frame (it may belong to an archive stored there), which never takes that name.
            foreach (var g in plan.GroupBy(f => f.Number))
            {
                bool primaryTaken = false;
                foreach (var f in g)
                {
                    if (!parts.TryRemove(f, out var part)) continue;
                    bool primary = !primaryTaken && !f.Doubtful;
                    primaryTaken |= primary;
                    string target = Path.Combine(_dest, primary ? $"block-{f.Number}.bin" : $"block-{f.Number}-at-{f.Offset}.bin");
                    try
                    {
                        // The archive being read, under whatever path, is never replaced: its name is taken in every mode.
                        if (File.Exists(target) && ArchiveIdentity is { } id && TargetNames.FileIdentity(target) == id)
                            target = Extractor.UniqueName(target);
                        if (File.Exists(target))
                        {
                            if (_o.Overwrite == OverwriteMode.Skip)
                            {
                                TryDelete(part);
                                _skipped++;
                                f.NotWritten = $"{Path.GetFileName(target)} is already in the destination";
                                continue;
                            }
                            if (_o.Overwrite == OverwriteMode.Fail) throw new IOException($"'{Path.GetFileName(target)}' is already in the destination.");
                            if (_o.Overwrite == OverwriteMode.Rename) target = Extractor.UniqueName(target);
                        }
                        FileRetry.Move(part, target, overwrite: _o.Overwrite == OverwriteMode.Overwrite);
                        f.Output = Relative(target);
                        _written.Enqueue(f.Output);
                        _bytesRecovered += f.RawSize;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        TryDelete(part);
                        f.NotWritten = ex.Message;
                        _lost.Enqueue(new(Path.GetFileName(target), ex.Message));
                    }
                }
            }
        }
        finally
        {
            foreach (var part in parts.Values) TryDelete(part);
        }

        // Which block numbers this archive has, judged from frames outside damaged ones when there are any.
        var numbered = frames.Where(f => !f.IsIndex && f.Number < FrameScanner.MaxBlockFrames && f.State != SalvageFrameState.Ignored).ToList();
        if (numbered.Any(f => !f.Doubtful)) numbered = [.. numbered.Where(f => !f.Doubtful)];
        if (numbered.Count == 0) return [];
        int max = (int)numbered.Max(f => f.Number);
        var byNumber = frames.Where(f => !f.IsIndex && f.Number <= (ulong)max).ToLookup(f => (int)f.Number);
        var blocks = new List<SalvageBlock>();
        for (int k = 0; k <= max; k++)
        {
            var withNumber = byNumber[k].OrderBy(f => f.Offset).ToList();
            if (withNumber.FirstOrDefault(f => !f.Doubtful && f.Output is not null) is { } written)
                blocks.Add(new(k, SalvageBlockState.Verified, null, written));
            else if (withNumber.FirstOrDefault(f => !f.Doubtful && f.Decoded && f.NotWritten is not null) is { } held)
                blocks.Add(new(k, SalvageBlockState.NotWritten, held.NotWritten, held));
            else if (withNumber.FirstOrDefault(f => f.Doubtful && f.Output is not null) is { } other)
                blocks.Add(new(k, SalvageBlockState.Missing, $"the only intact frame with this number, written as {other.Output}, "
                    + (other.EnclosingOffset is { } enc
                        ? $"lies inside the damaged frame at offset {enc:N0} and may belong to an archive stored there"
                        : $"is listed by the index of another archive at offset {other.ForeignIndexOffset:N0}"), null));
            else blocks.Add(Unavailable(k, withNumber));
        }
        if (!FooterAt(_src.Length - Osa.FooterSize))
            _notes.Add($"The end of the archive is missing, so blocks after block {max} may be lost as well.");
        // An unencrypted frame does not name its archive: past a point where the frames stop following one another,
        // intact frames may come from other data (a file pieced together from parts of others).
        if (!_scanner.ArchiveEncrypted && ChainBreak(frames) is { } at && plan.Any(f => f.Offset >= at && f.Output is not null && !f.Doubtful))
            _notes.Add($"At offset {at:N0} the frames stop following one another. An unencrypted frame does not name its archive, "
                + "so a block file written from a frame after that point may hold other data, if this file was pieced together from parts of others.");
        return blocks;
    }

    /// <summary>The offset of the first block frame that does not start where the one before it ends, as block number + 1.</summary>
    private static long? ChainBreak(List<SalvageFrame> frames)
    {
        SalvageFrame? previous = null;
        foreach (var f in frames.Where(f => !f.IsIndex && !f.Doubtful && f.State != SalvageFrameState.Ignored))
        {
            if (previous is not null && (f.Offset != previous.Offset + previous.FrameLength || f.Number != previous.Number + 1)) return f.Offset;
            previous = f;
        }
        return null;
    }

    private void WriteReport(SalvageResult result)
    {
        string path = Path.Combine(_dest, Salvage.ReportFileName);
        try
        {
            Directory.CreateDirectory(_dest);
            if (File.Exists(path) && _o.Overwrite != OverwriteMode.Overwrite) path = Extractor.UniqueName(path);
            File.WriteAllText(path, result.ReportText());
            result.ReportPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notes.Add($"The salvage report could not be written: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ coverage

    /// <summary>Bytes outside the header, the intact frames and the footer, and where they are.</summary>
    private (long Bytes, List<(long Start, long End)> Ranges) Unrecognized(List<SalvageFrame> frames, HeaderProbe probe, SalvageFrame? indexFrame)
    {
        var spans = new List<(long Start, long End)>();
        if (probe.Header is { } h) spans.Add((probe.Offset, probe.Offset + h.Length));
        spans.AddRange(frames.Where(f => f.Intact).Select(f => (f.Offset, f.Offset + f.FrameLength)));
        spans.AddRange(HeaderCopies(frames));
        if (FooterAt(_src.Length - Osa.FooterSize)) spans.Add((_src.Length - Osa.FooterSize, _src.Length));
        if (indexFrame is not null && FooterAt(indexFrame.Offset + indexFrame.FrameLength))
            spans.Add((indexFrame.Offset + indexFrame.FrameLength, indexFrame.Offset + indexFrame.FrameLength + Osa.FooterSize));
        spans.Sort();
        var gaps = new List<(long Start, long End)>();
        long pos = 0;
        foreach (var (s, e) in spans)
        {
            if (s > pos) gaps.Add((pos, s));
            pos = Math.Max(pos, e);
        }
        if (pos < _src.Length) gaps.Add((pos, _src.Length));
        return (gaps.Sum(g => g.End - g.Start), gaps);
    }

    /// <summary>
    /// The intact header copies of container 1.1 (FORMAT §4.1): the header bytes, zero padding and a trailer whose
    /// magic and CRC check out, with header bytes that check out as a header. A header copy sits between the spare
    /// copy of the index frame and the index frame, so it is looked for on both sides of every intact index frame.
    /// </summary>
    private List<(long Start, long End)> HeaderCopies(List<SalvageFrame> frames) =>
        [.. frames.Where(f => f.IsIndex && f.Intact).SelectMany(f => HeaderCopiesBeside(f.Offset, f.FrameLength))
            .Select(c => (c.Start, c.Start + c.Trailer.CopyLength))];

    /// <summary>The intact header copies on either side of the index frame at <paramref name="offset"/>: where each
    /// starts, and its trailer.</summary>
    private List<(long Start, SpareTrailer Trailer)> HeaderCopiesBeside(long offset, long frameLength)
    {
        var found = new List<(long Start, SpareTrailer Trailer)>();
        // Before it: the trailer ends where the index frame starts.
        if (offset >= SpareCopies.TrailerSize && SpareTrailer.Read(_src.Read(offset - SpareCopies.TrailerSize, SpareCopies.TrailerSize)) is { Newer: false } t
            && IsHeaderCopy(offset - t.CopyLength, t))
            found.Add((offset - t.CopyLength, t));
        // After it: a header, then the padding a writer puts after one of that length, then the trailer.
        long start = offset + frameLength;
        if (start + 40 > _src.Length) return found;
        long headerLength = 40 + BinaryPrimitives.ReadUInt32LittleEndian(_src.Read(start + 32, 4));
        long length = Math.Max(SpareCopies.MinHeaderCopyLength, headerLength + SpareCopies.TrailerSize);
        if (headerLength > SpareCopies.MaxHeaderLength || start + length > _src.Length) return found;
        if (SpareTrailer.Read(_src.Read(start + length - SpareCopies.TrailerSize, SpareCopies.TrailerSize)) is { Newer: false } after
            && after.HeaderLength == headerLength && after.CopyLength == length && IsHeaderCopy(start, after))
            found.Add((start, after));
        return found;
    }

    /// <summary>Whether the bytes at <paramref name="start"/> are a header copy of this archive: header bytes that check
    /// out as a header, with this archive's id (from the header, else the footer at the end of the file) and, when the
    /// header is intact, the header's very bytes; then zero padding.</summary>
    private bool IsHeaderCopy(long start, SpareTrailer t)
    {
        if (start < 0 || start + t.CopyLength > _src.Length) return false;
        var raw = _src.Read(start, (int)t.HeaderLength);
        try
        {
            if (ArchiveHeader.Read(new MemoryStream(raw, writable: false)).Length != t.HeaderLength) return false;
        }
        catch (NewerVersionException) { } // an intact header of a newer version is still a header
        catch (Exception e) when (e is IOException or InvalidDataException) { return false; }
        if (_probe?.IdPrefix is { } id && BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(16)) != id) return false;
        if (_probe?.Header is { } header && !raw.AsSpan().SequenceEqual(header.Raw)) return false;
        var padding = _src.Read(start + t.HeaderLength, (int)t.PaddingLength);
        return !padding.AsSpan().ContainsAnyExcept((byte)0);
    }
}
