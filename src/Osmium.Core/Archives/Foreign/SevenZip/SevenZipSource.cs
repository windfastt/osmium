using Microsoft.Win32.SafeHandles;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;
using Osmium.Core.Text;

namespace Osmium.Core.Archives.Foreign.SevenZip;

/// <summary>
/// A 7z archive, read with Osmium's own header reader and folder decoder (SharpCompress 1.0.0 decodes a solid folder
/// again from its start for every file in it, so a solid archive of many files took quadratic time). Each folder is
/// decoded once, in order, and the files nobody selected are read through and discarded (their bytes still count
/// against the limits). Every file is checked against its stored CRC-32; one without a stored CRC counts as unverified.
/// Encrypted file lists ask for the password in Open; encrypted data asks for it on the first operation that needs it.
/// </summary>
internal sealed class SevenZipSource : ForeignSource
{
    /// <summary>The SHA-256 rounds of key derivation one archive may cost in all: 256 keys at the 2^19 rounds 7z
    /// writers use, 8 at the most Osmium accepts for one key. Each salt needs its own key, so without this a small
    /// archive of many folders, each with its own salt, could keep the processor busy for days.</summary>
    public const long MaxKeyRounds = 1L << 27;

    internal const string KeyBudgetRefusal = "this archive asks for more password key derivation than Osmium allows for one archive";

    private static readonly AsyncLocal<Func<int, bool>?> DropChecksums = new();
    private static readonly AsyncLocal<long?> KeyRoundsSeam = new();
    private readonly SafeFileHandle _handle;
    private SzDatabase _db = new();
    private bool _encryptedHeaders;
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private CancellationToken _ct;

    /// <summary>For tests (this async flow only): forget the stored CRCs of the folders this accepts when the archive is
    /// read, as an archive written without checksums would be.</summary>
    internal static Func<int, bool>? DropChecksumsForTests { get => DropChecksums.Value; set => DropChecksums.Value = value; }

    /// <summary>For tests: the key derivation budget instead of <see cref="MaxKeyRounds"/>.</summary>
    internal static long? MaxKeyRoundsOverride { get => KeyRoundsSeam.Value; set => KeyRoundsSeam.Value = value; }

    private static long KeyBudget => MaxKeyRoundsOverride ?? MaxKeyRounds;

    private SevenZipSource(string path, ArchiveOpenOptions options) : base(path, options) =>
        _handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    public static SevenZipSource Open(string path, ArchiveOpenOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var s = new SevenZipSource(path, options);
        try
        {
            s._ct = ct;
            s.Load(ct);
            s._ct = default;
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    public override ArchiveKind Kind => ArchiveKind.SevenZip;
    public override string FormatName => "7z";
    public override bool IsEncrypted => _encryptedHeaders || base.IsEncrypted;
    private string Name => System.IO.Path.GetFileName(Path);

    private void Load(CancellationToken ct)
    {
        using var file = new FileSlice(_handle, 0, FileLength, 4096);
        // One guard for every level of packed headers, against the file's size and the limits the caller gave.
        var guard = new LimitGuard(Options.Limits, FileLength, ct);
        bool headerProof = true;
        try
        {
            _db = SevenZipHeader.Read(file, Options.Limits, ct, DecodeHeader);
        }
        catch (Exception e) when (_encryptedHeaders && (e is SevenZipHeader.HeaderCheckException || ForeignErrors.IsDecoderFailure(e)))
        {
            // Encrypted headers decode to nonsense under a wrong password.
            throw SpendPassword();
        }
        catch (Exception e) when (ForeignErrors.IsDecoderFailure(e)) { throw ForeignErrors.Damaged(Name, e); }
        catch (OutOfMemoryException) { throw SevenZipHeader.TooLarge(); } // a last resort: the limits above come first
        // The file list decoded and matched its CRC: that proves the password, when every AES coder decrypted on the way.
        if (_encryptedHeaders && headerProof) PasswordProven = true;
        if (DropChecksumsForTests is { } drop)
        {
            foreach (var f in _db.Files) if (f.Folder >= 0 && drop(f.Folder)) f.Crc = null;
            for (int i = 0; i < _db.Folders.Count; i++) if (drop(i)) _db.Folders[i].UnpackCrc = null;
        }
        var entries = new List<ForeignEntry>(_db.Files.Count);
        for (int i = 0; i < _db.Files.Count; i++) entries.Add(Make(_db.Files[i], i));
        SetEntries(entries, complete: true);

        byte[] DecodeHeader(SzDatabase packed, SzFolder folder, long size)
        {
            if (folder.IsEncrypted) _encryptedHeaders = true;
            var aes = new List<SevenZipAesStream>();
            using var s = new CountingStream(SevenZipDecoder.Open(_handle, packed, folder, Options.Limits, Name, folder.IsEncrypted ? Key : null, null, aes), guard);
            var bytes = new byte[size];
            s.ReadExactly(bytes);
            if (folder.IsEncrypted) headerProof &= folder.UnpackCrc is not null && size > 0 && aes.Count > 0 && aes.All(a => a.Delivered > 0);
            return bytes;
        }
    }

    /// <summary>The SHA-256 rounds of key derivation this source has run so far.</summary>
    internal long KeyRoundsSpent { get; private set; }

    /// <summary>The AES key for a coder's properties, derived once per salt and round count (7z writers give every
    /// folder its own IV, which the key does not depend on).</summary>
    private byte[] Key(byte[] props)
    {
        string password = ObtainPassword();
        string identity = SevenZipAes.KeyIdentity(props);
        lock (_keys)
        {
            if (_keys.TryGetValue(identity, out var known)) return known;
            long rounds = SevenZipAes.Rounds(props);
            if (KeyRoundsSpent + rounds > KeyBudget) throw new NotSupportedException(KeyBudgetRefusal);
            var key = SevenZipAes.Key(props, password, _ct);
            KeyRoundsSpent += rounds;
            return _keys[identity] = key;
        }
    }

    /// <summary>Whether the keys of the folder's AES coders are known or fit in what is left of the budget (damaged
    /// properties are reported when the folder is opened).</summary>
    private bool KeysWithinBudget(SzFolder f)
    {
        lock (_keys)
        {
            try
            {
                long needed = f.Coders.Where(c => c.Id == SevenZipHeader.AesId).DistinctBy(c => SevenZipAes.KeyIdentity(c.Props))
                    .Where(c => !_keys.ContainsKey(SevenZipAes.KeyIdentity(c.Props))).Sum(c => SevenZipAes.Rounds(c.Props));
                return KeyRoundsSpent + needed <= KeyBudget;
            }
            catch (CorruptDataException) { return true; }
        }
    }

    private ForeignEntry Make(SzFile f, int index)
    {
        uint attr = f.Attributes ?? 0;
        uint mode = (attr & 0x8000) != 0 ? attr >> 16 : 0;
        var folder = f.Folder >= 0 ? _db.Folders[f.Folder] : null;
        var type = f.IsAnti ? EntryType.Anti
            : (mode & 0xF000) == 0xA000 || (attr & 0x400) != 0 ? EntryType.SymbolicLink
            : (mode & 0xF000) is 0x1000 or 0x2000 or 0x6000 or 0xC000 ? EntryType.Special
            : f.IsDir ? EntryType.Directory
            : EntryType.File;
        return new ForeignEntry
        {
            Index = index,
            Path = f.Name.Replace('\\', '/').TrimEnd('/'),
            Type = type,
            Size = type == EntryType.File ? f.Size : null,
            PackedSize = null,
            ModifiedUtc = f.ModifiedUtc,
            Encrypted = type == EntryType.File && folder is { IsEncrypted: true },
            Method = type == EntryType.File && folder is not null ? SevenZipDecoder.Methods(folder) : null,
            ReadOnly = type == EntryType.File && ((attr & 1) != 0 || (mode != 0 && (mode & 0x80) == 0)),
            Hidden = (attr & 2) != 0,
            Unsupported = type == EntryType.Special ? "a device, FIFO or socket" : null,
        };
    }

    /// <summary>Files of each folder in folder order (their order in the archive).</summary>
    private Dictionary<int, List<int>> FilesByFolder()
    {
        var map = new Dictionary<int, List<int>>();
        for (int i = 0; i < _db.Files.Count; i++)
        {
            int f = _db.Files[i].Folder;
            if (f < 0) continue;
            if (!map.TryGetValue(f, out var list)) map[f] = list = [];
            list.Add(i);
        }
        return map;
    }

    /// <summary>The file whose CRC can prove the password in a folder: the first with a stored CRC and at least one
    /// byte (an empty file is the same under every password); -1 when only the folder's own CRC can, over at least one
    /// byte; null when nothing in the folder can.</summary>
    private int? ProofFile(int folder, Dictionary<int, List<int>> byFolder)
    {
        int first = byFolder[folder].FirstOrDefault(i => _db.Files[i].Crc is not null && _db.Files[i].Size > 0, -1);
        if (first >= 0) return first;
        var f = _db.Folders[folder];
        return f.UnpackCrc is not null && f.UnpackSize > 0 ? -1 : null;
    }

    /// <summary>Whether a folder can show that the password is right.</summary>
    private bool Checkable(int folder, Dictionary<int, List<int>> byFolder) => ProofFile(folder, byFolder) is not null;

    /// <summary>
    /// Rule 1 for 7z, which has no password verifier: the first selected encrypted folder that has a stored CRC is
    /// decoded from its start into a discarding sink until the end of its first file with a CRC and data (the whole
    /// folder when only the folder has one), and compared. It proves the password only when every AES coder of the
    /// folder decrypted data on the way (a coder whose output nothing reads proves nothing; the folder's structure
    /// already puts every coder on the way to its result). A data error or a mismatch is a wrong password or damage, which
    /// 7z cannot tell apart: the next such folder is tried, and when one proves the password the failed one is
    /// reported as damaged when it is read; when every one fails, the password is wrong. When no selected encrypted
    /// folder has any CRC, nothing can prove the password; those entries are then refused.
    /// </summary>
    protected override bool VerifyPassword(IReadOnlyList<ForeignEntry> encrypted, string password, Operation op)
    {
        var byFolder = FilesByFolder();
        bool failed = false;
        foreach (int folder in encrypted.Select(e => _db.Files[e.Index].Folder).Distinct())
        {
            if (ProofFile(folder, byFolder) is not { } stopAfter) continue;
            var f = _db.Folders[folder];
            if (!KeysWithinBudget(f)) continue;
            long total = stopAfter < 0 ? f.UnpackSize : byFolder[folder].TakeWhile(i => i != stopAfter).Sum(i => _db.Files[i].Size) + _db.Files[stopAfter].Size;
            var aes = new List<SevenZipAesStream>();
            try
            {
                _ct = op.Token;
                using var s = new CountingStream(SevenZipDecoder.Open(_handle, _db, f, op.Guard.Limits, Name, Key, op.Read, aes), op.Guard);
                var buf = new byte[1 << 16];
                if (stopAfter < 0)
                {
                    var crc = new System.IO.Hashing.Crc32();
                    long done = 0;
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) { crc.Append(buf.AsSpan(0, n)); done += n; op.Progress.Checking(done, total); }
                    if (crc.GetCurrentHashAsUInt32() != f.UnpackCrc) { failed = true; continue; }
                }
                else
                {
                    long skipped = 0;
                    foreach (int i in byFolder[folder])
                    {
                        var file = _db.Files[i];
                        using var part = new VerifyingStream(new ExactStream(new NonClosing(s), file.Size, "a file"), file.Size, i == stopAfter ? file.Crc : null, "the file");
                        int n;
                        while ((n = part.Read(buf, 0, buf.Length)) > 0) { skipped += n; op.Progress.Checking(skipped, total); }
                        if (i == stopAfter) break;
                    }
                }
                if (aes.Count > 0 && aes.All(a => a.Delivered > 0)) return true;
            }
            catch (Exception ex) when (ForeignErrors.IsDecoderFailure(ex)) { failed = true; }
            finally { _ct = default; }
        }
        if (failed) throw new WrongPasswordException();
        return false;
    }

    protected override void Run(Operation op)
    {
        _ct = op.Token;
        try { RunFolders(op); }
        finally { _ct = default; }
    }

    private void RunFolders(Operation op)
    {
        var byFolder = FilesByFolder();
        // Entries without data (folders, empty files, links, deletion markers), in archive order.
        for (int i = 0; i < _db.Files.Count; i++)
        {
            var file = _db.Files[i];
            if (file.Folder >= 0) continue;
            var e = Entries[i];
            if (op.Selected(e) is not { } rel) continue;
            try { op.Sink.Accept(e, rel, e.HasData ? () => new EntryContent(Stream.Null, Verified: true) : null); }
            catch (EntryDataException) { }
        }
        for (int folder = 0; folder < _db.Folders.Count; folder++)
        {
            op.Token.ThrowIfCancellationRequested();
            if (!byFolder.TryGetValue(folder, out var files)) continue;
            var wanted = files.Where(i => op.Selected(Entries[i]) is not null).ToList();
            if (wanted.Count == 0) continue;
            var f = _db.Folders[folder];
            bool unprovable = f.IsEncrypted && !Checkable(folder, byFolder);
            if (unprovable && !PasswordProven)
            {
                foreach (int i in wanted)
                    op.Sink.Accept(Entries[i].Refused("encrypted without a stored checksum, so Osmium cannot check the password"), op.Selected(Entries[i])!, null);
                continue;
            }
            if (f.IsEncrypted && !KeysWithinBudget(f))
            {
                foreach (int i in wanted) op.Sink.Accept(Entries[i].Refused(KeyBudgetRefusal), op.Selected(Entries[i])!, null);
                continue;
            }
            int last = wanted[^1];
            var accepted = new HashSet<int>();
            Stream? folderStream = null;
            try
            {
                folderStream = new CountingStream(SevenZipDecoder.Open(_handle, _db, f, op.Guard.Limits, Name, f.IsEncrypted ? Key : null, op.Read), op.Guard);
                var scratch = new byte[1 << 16];
                foreach (int i in files)
                {
                    var file = _db.Files[i];
                    var e = Entries[i];
                    string? rel = op.Selected(e);
                    using (var part = new ExactStream(new NonClosing(folderStream), file.Size, "a file"))
                    {
                        if (rel is not null)
                        {
                            accepted.Add(i);
                            bool verified = file.Crc is not null;
                            if (!verified && f.IsEncrypted) op.Sink.Warn($"{DisplayText.Escape(e.Path)}: no stored checksum; the contents were not checked");
                            op.Sink.Accept(e, rel, () => new EntryContent(new VerifyingStream(new NonClosing(part), file.Size, file.Crc, "the file"), verified));
                        }
                        // Whatever the sink did not read (an entry refused or skipped, or not selected) is read through.
                        while (part.Read(scratch, 0, scratch.Length) > 0) { }
                    }
                    if (i == last) break;
                }
            }
            catch (Exception ex) when (ex is EntryDataException || ForeignErrors.IsDecoderFailure(ex) || ex is NotSupportedException)
            {
                // The rest of this solid block cannot be read; other folders are independent.
                string message = ex switch
                {
                    EntryDataException => "not read: an earlier file of its solid block is damaged",
                    NotSupportedException => "not read: " + ex.Message,
                    _ => $"not read: damaged data ({DisplayText.Escape(ForeignErrors.Reason(ex))})",
                };
                foreach (int i in wanted)
                    if (!accepted.Contains(i)) op.Sink.Fail(Entries[i], message);
            }
            finally { folderStream?.Dispose(); }
        }
    }

    protected override IReadOnlyList<ArchiveDetail> Describe()
    {
        var files = Entries.Where(e => e.HasData).ToList();
        var byFolder = FilesByFolder();
        var d = new List<ArchiveDetail>
        {
            new("Format", FormatName),
            new("Files", Count(files.Count)),
            new("Folders", Count(Entries.Count(e => e.Type == EntryType.Directory))),
        };
        if (TotalSize is { } total) d.Add(new("Original size", Bytes(total)));
        d.Add(new("Archive size", Bytes(FileLength)));
        bool solid = byFolder.Values.Any(l => l.Count > 1);
        d.Add(new("Solid", solid ? $"yes ({Count(_db.Folders.Count)} block{(_db.Folders.Count == 1 ? "" : "s")})" : "no"));
        var methods = _db.Folders.Select(SevenZipDecoder.Methods).Distinct().ToList();
        if (methods.Count > 0) d.Add(new("Methods", string.Join("; ", methods)));
        d.Add(new("Encrypted", _encryptedHeaders ? "yes (AES-256, file names too)" : _db.Folders.Any(f => f.IsEncrypted) ? "yes (AES-256)" : "no"));
        int unchecked_ = _db.Files.Count(f => f.HasStream && f.Crc is null);
        if (unchecked_ > 0) d.Add(new("Without a checksum", $"{Count(unchecked_)} file(s)"));
        int links = Entries.Count(e => e.Type == EntryType.SymbolicLink);
        if (links > 0) d.Add(new("Links", $"{Count(links)} (not extracted)"));
        return d;
    }

    public override void Dispose() => _handle.Dispose();

    private sealed class NonClosing(Stream inner) : ReadOnlyStream
    {
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
    }
}
