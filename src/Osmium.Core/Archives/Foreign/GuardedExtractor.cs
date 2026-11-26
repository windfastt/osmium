using System.Globalization;
using Osmium.Core.Container;
using Osmium.Core.IO;
using Osmium.Core.Text;

namespace Osmium.Core.Archives.Foreign;

/// <summary>
/// The only way an entry of a foreign archive reaches the disk. For each entry, in archive order: normalise and
/// refuse unsafe names, map the characters Windows does not allow, never create or follow a link (a TAR hard link
/// to a file this run wrote becomes a copy), refuse devices and other special entries, then plan the target with the
/// same rules as .osa extraction (<see cref="TargetNames"/>, here always case-insensitive): never through a link already
/// in the destination, a free name that nothing else uses for a collision inside the run, with an earlier pass, or with
/// a file the system finds under another spelling (a short 8.3 name), and the overwrite mode for files that were
/// already there; at most <see cref="MaxCopiesOfOneName"/> files of one name. The link check runs again just before
/// the part file is made and before it is moved. Write to a part file through the limit counters and the format's
/// checks, then give it the Mark-of-the-Web, move it into place and set its time and only
/// its ReadOnly and Hidden attributes. A cancellation or failure removes this run's part files and the empty folders
/// it created; finished files stay.
/// </summary>
internal sealed class GuardedExtractor : IEntrySink
{
    private const int CopyBuffer = 1 << 18;
    /// <summary>How many file entries of one run may have the same name (each further one gets the next free name);
    /// more are refused, so that a small archive of one name repeated cannot fill a folder with numbered copies.</summary>
    public const int MaxCopiesOfOneName = 1000;
    private const long FreeSpaceInterval = 64L << 20;

    private readonly string _root;
    private readonly ArchiveExtractRequest _request;
    private readonly ImportLimits _limits;
    private readonly LimitGuard _guard;
    private readonly ProgressReporter _progress;
    private readonly string? _zone;
    private readonly Func<string, string, string?> _writeZone;
    private readonly CancellationToken _ct;

    // Where each entry goes: names compared case-insensitively on every system (foreign archives come from anywhere).
    private readonly TargetNames _plan;
    private readonly List<string> _createdDirs = [];
    private readonly HashSet<string> _createdDirSet = new(StringComparer.Ordinal);
    private readonly List<(ForeignEntry Entry, string Path)> _dirEntries = [];
    private readonly Dictionary<string, string> _filesByArchivePath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _copies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _errors = [], _warnings = [];
    private readonly List<ExtractedFile> _out = [];
    private string? _currentPart;
    private long _sinceFreeCheck;
    private int _ok, _failed, _existing, _unsafe, _links, _unsupported, _renamed, _unverified;

    /// <param name="reservedTargets">Relative paths (as the archive names them, after the prefix) of every selected
    /// file and folder, when the listing is complete: their own names, which a free name never takes.</param>
    public GuardedExtractor(string destination, ArchiveExtractRequest request, ImportLimits limits, LimitGuard guard,
        ProgressReporter progress, string archivePath, IEnumerable<string>? reservedTargets, CancellationToken ct)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        _request = request;
        _limits = limits;
        _guard = guard;
        _progress = progress;
        _ct = ct;
        _zone = ZoneOverride ?? Extractor.ReadZoneIdentifier(archivePath);
        _writeZone = ZoneWriterOverride ?? Extractor.WriteZoneIdentifier;
        var own = new List<string>();
        foreach (var rel in reservedTargets ?? [])
            if (EntryNames.Normalize(rel, int.MaxValue, out var n) is null && n is not null)
            {
                EntryNames.MapForWindows(n, out var mapped);
                if (PathSafety.Validate(mapped) is null) own.Add(Full(mapped));
            }
        // The archive being read is taken like a file of an earlier pass: an entry of its name gets a free name in every
        // mode, so extracting next to the archive never replaces it.
        _plan = new TargetNames(_root, StringComparer.OrdinalIgnoreCase, own, [.. request.WrittenEarlier ?? [], Path.GetFullPath(archivePath)]);
        CreateRoot();
    }

    private static readonly AsyncLocal<string?> ZoneSeam = new();
    private static readonly AsyncLocal<Func<string, string, string?>?> ZoneWriterSeam = new();
    private static readonly AsyncLocal<long?> FreeSpaceIntervalSeam = new();
    private static readonly AsyncLocal<int?> MaxCopiesSeam = new();

    /// <summary>For tests (this async flow only): the Mark-of-the-Web to give files instead of the archive's own.</summary>
    internal static string? ZoneOverride { get => ZoneSeam.Value; set => ZoneSeam.Value = value; }
    /// <summary>For tests: writes the Mark-of-the-Web instead of <see cref="Extractor.WriteZoneIdentifier"/>.</summary>
    internal static Func<string, string, string?>? ZoneWriterOverride { get => ZoneWriterSeam.Value; set => ZoneWriterSeam.Value = value; }
    /// <summary>For tests: how many bytes are written between free-space checks (64 MiB).</summary>
    internal static long? FreeSpaceIntervalOverride { get => FreeSpaceIntervalSeam.Value; set => FreeSpaceIntervalSeam.Value = value; }
    /// <summary>For tests: how many file entries may have one name (<see cref="MaxCopiesOfOneName"/>).</summary>
    internal static int? MaxCopiesOverride { get => MaxCopiesSeam.Value; set => MaxCopiesSeam.Value = value; }

    private static string Esc(string s) => DisplayText.Escape(s);
    private string Full(string relative) => PathSafety.Resolve(_root, relative);
    private string Relative(string full) => Path.GetRelativePath(_root, full).Replace('\\', '/');

    private void CreateRoot()
    {
        string root = _root.TrimEnd(Path.DirectorySeparatorChar);
        if (Directory.Exists(root)) return;
        // Create the missing part of the destination one level at a time, so a cancel can remove what this run made.
        var missing = new Stack<string>();
        for (string? d = root; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Push(d);
        while (missing.TryPop(out var d)) CreateDir(d);
    }

    private void CreateDir(string full)
    {
        Directory.CreateDirectory(full);
        if (_createdDirSet.Add(full)) _createdDirs.Add(full);
    }

    /// <summary>Creates the missing folders from the destination down to <paramref name="dir"/>, one level at a time,
    /// recording each as made by this run.</summary>
    private void EnsureFolder(string dir)
    {
        var missing = new Stack<string>();
        string root = _root.TrimEnd(Path.DirectorySeparatorChar);
        for (string? d = dir; d is not null && d.Length > root.Length && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Push(d);
        while (missing.TryPop(out var d)) CreateDir(d);
    }

    public void Fail(ForeignEntry entry, string message)
    {
        if (!entry.HasData) return;
        _failed++;
        _errors.Add($"{Esc(entry.Path)}: {message}");
    }

    public void Damaged(string message)
    {
        _failed++;
        _errors.Add(message);
    }

    public void Warn(string message) => _warnings.Add(message);

    private void Refuse(ref int counter, ForeignEntry e, string why)
    {
        counter++;
        _errors.Add($"{Esc(e.Path)}: not extracted: {Esc(why)}");
    }

    public void Accept(ForeignEntry e, string relativePath, Func<EntryContent>? open)
    {
        _ct.ThrowIfCancellationRequested();
        if (e.Type == EntryType.Anti)
        {
            _warnings.Add($"{Esc(e.Path)}: a deletion marker of an update, not a file; skipped");
            return;
        }
        if (e.Unsafe is { } overlap) { Refuse(ref _unsafe, e, overlap); return; }
        if (EntryNames.Normalize(relativePath, _limits.MaxPathDepth, out var normalized) is { } why) { Refuse(ref _unsafe, e, why); return; }
        bool renamed = EntryNames.MapForWindows(normalized!, out var mapped);
        if (PathSafety.Validate(mapped) is { } invalid) { Refuse(ref _unsafe, e, invalid); return; }
        switch (e.Type)
        {
            case EntryType.SymbolicLink:
                Refuse(ref _links, e, e.LinkTarget is { } t ? $"a symbolic link (to '{t}'); links are never created" : "a symbolic link; links are never created");
                return;
            case EntryType.Special:
                Refuse(ref _unsupported, e, e.Unsupported ?? "not a regular file");
                return;
        }
        if (e.Unsupported is { } unsupported) { Refuse(ref _unsupported, e, unsupported); return; }
        string? hardLinkSource = null;
        if (e.Type == EntryType.HardLink)
        {
            string? key = e.LinkTarget is { } lt && EntryNames.Normalize(lt, int.MaxValue, out var ln) is null ? ln : null;
            if (key is null || !_filesByArchivePath.TryGetValue(key, out hardLinkSource))
            {
                Refuse(ref _links, e, $"a hard link to '{e.LinkTarget}', which is not a file this extraction wrote; links are never created");
                return;
            }
        }

        try
        {
            string full = Full(mapped);
            if (e.Type != EntryType.Directory)
            {
                int copies = _copies[full] = _copies.GetValueOrDefault(full) + 1, max = MaxCopiesOverride ?? MaxCopiesOfOneName;
                if (copies > max)
                {
                    Refuse(ref _unsafe, e, $"more than {max.ToString("N0", CultureInfo.InvariantCulture)} entries of this archive have this name");
                    return;
                }
            }
            var plan = _plan.Plan(full, e.Type == EntryType.Directory, _request.Overwrite, TargetNames.SpelledOtherwise(_root, mapped, full));
            switch (plan.Outcome)
            {
                case TargetNames.Outcome.Unsafe:
                    Refuse(ref _unsafe, e, plan.Reason ?? TargetNames.LinkRefused);
                    return;
                case TargetNames.Outcome.Skipped:
                    _existing++;
                    return;
                case TargetNames.Outcome.Failed:
                    _failed++;
                    _errors.Add($"{Esc(e.Path)}: {Esc(plan.Reason ?? "it could not be written")}");
                    return;
            }
            string target = plan.Path!;
            bool replace = plan.Replaces;
            renamed |= plan.Renamed;
            if (e.Type == EntryType.Directory)
            {
                EnsureFolder(target);
                if (renamed) _warnings.Add($"'{Esc(e.Path)}' was saved as '{Esc(Relative(target))}'");
                if (_createdDirSet.Contains(target)) _dirEntries.Add((e, target));
                return;
            }
            EnsureFolder(Path.GetDirectoryName(target)!);
            if (hardLinkSource is not null)
            {
                using var copy = new CountingStream(new FileStream(hardLinkSource, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBuffer), _guard);
                Write(e, target, replace, renamed, new EntryContent(copy, Verified: false));
            }
            else
            {
                EntryContent content;
                try { content = open!(); }
                catch (NotSupportedException ex)
                {
                    _unsupported++;
                    _errors.Add(ex.Message.StartsWith(Esc(e.Path), StringComparison.Ordinal) ? ex.Message : $"{Esc(e.Path)}: {Esc(ex.Message)}");
                    return;
                }
                catch (Exception ex) when (ForeignErrors.IsDecoderFailure(ex))
                {
                    // Damaged before the first byte (a wrong verifier, a damaged header of the data): the same as later.
                    _failed++;
                    _errors.Add($"{Esc(e.Path)}: {(e.Encrypted ? "wrong password or damaged file" : "damaged data")} ({Esc(ForeignErrors.Reason(ex))})");
                    throw new EntryDataException(ex);
                }
                Write(e, target, replace, renamed, content);
            }
            // A later hard link may name this file by its archive path.
            if (_out.Count > 0 && _out[^1].Index == e.Index && EntryNames.Normalize(e.Path, int.MaxValue, out var own) is null)
                _filesByArchivePath[own!] = _out[^1].Path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not (ExpansionLimitException or FreeSpaceException or EntryLimitException))
        {
            // A folder that could not be made, a file that could not be opened… (data errors are handled in Write).
            _failed += e.HasData ? 1 : 0;
            _errors.Add($"{Esc(e.Path)}: {Esc(ex.Message)}");
        }
    }

    private void Write(ForeignEntry e, string target, bool replace, bool renamed, EntryContent content)
    {
        // Checked again here, without the plan's answers: a folder on the way may have become a link since.
        if (TargetNames.LinkOnTheWay(_root, target) is { } link)
        {
            Refuse(ref _unsafe, e, link);
            return;
        }
        string part = Extractor.PartName(target);
        _currentPart = part;
        _progress.Current(e.Path);
        bool keep = false;
        try
        {
            using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buf = new byte[CopyBuffer];
                while (true)
                {
                    int n = ReadContent(content.Stream, buf);
                    if (n == 0) break;
                    fs.Write(buf, 0, n);
                    _progress.Output(n);
                    _sinceFreeCheck += n;
                    if (_sinceFreeCheck >= (FreeSpaceIntervalOverride ?? FreeSpaceInterval))
                    {
                        _sinceFreeCheck = 0;
                        FreeSpace.CheckWhileWriting(_root, _limits);
                    }
                }
            }
            // Verified (the stream throws at its end otherwise). The mark goes on the part file first, so the file
            // never exists without it and a ReadOnly attribute cannot keep it out.
            if (_zone is not null && _writeZone(part, _zone) is { } why) _warnings.Add(Extractor.ZoneWarning(Esc(e.Path), why));
            if (replace && _request.BeforeReplace is { } before)
            {
                try { before(target); }
                catch (Exception ex)
                {
                    _failed++;
                    _errors.Add($"{Esc(e.Path)}: the existing file was not replaced ({Esc(ex.Message)})");
                    return;
                }
            }
            if (TargetNames.LinkOnTheWay(_root, target) is { } moved)
            {
                Refuse(ref _unsafe, e, moved);
                return;
            }
            FileRetry.Move(part, target, overwrite: replace);
            _currentPart = null;
            SetFileMetadata(target, e);
            _ok++;
            if (!content.Verified) _unverified++;
            if (renamed)
            {
                _renamed++;
                _warnings.Add($"'{Esc(e.Path)}' was saved as '{Esc(Relative(target))}'");
            }
            _out.Add(new ExtractedFile(e.Index, target));
            _progress.FileDone();
        }
        catch (EntryDataException ex)
        {
            _failed++;
            _errors.Add($"{Esc(e.Path)}: {(e.Encrypted ? "wrong password or damaged file" : "damaged data")} ({Esc(ForeignErrors.Reason(ex.InnerException!))})");
            keep = _request.KeepBroken;
            // A broken file that is kept holds bytes the archive chose, as a finished one does: it gets the mark too.
            if (keep && _zone is not null && _writeZone(part, _zone) is { } why) _warnings.Add(Extractor.ZoneWarning(Esc(e.Path), why));
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not (ExpansionLimitException or FreeSpaceException or EntryLimitException))
        {
            _failed++;
            _errors.Add($"{Esc(e.Path)}: {Esc(ex.Message)}");
        }
        finally
        {
            if (_currentPart is not null)
            {
                if (!keep) TryDelete(_currentPart);
                _currentPart = null;
            }
        }
    }

    private static int ReadContent(Stream s, byte[] buf)
    {
        try { return s.Read(buf, 0, buf.Length); }
        catch (Exception ex) when (ForeignErrors.IsDecoderFailure(ex)) { throw new EntryDataException(ex); }
    }

    private void SetFileMetadata(string path, ForeignEntry e)
    {
        try
        {
            if (_request.RestoreTimes && e.ModifiedUtc is { } m) File.SetLastWriteTimeUtc(path, Clamp(m));
            if (_request.RestoreAttributes && OperatingSystem.IsWindows() && (e.ReadOnly || e.Hidden))
            {
                var a = File.GetAttributes(path);
                if (e.ReadOnly) a |= FileAttributes.ReadOnly;
                if (e.Hidden) a |= FileAttributes.Hidden;
                File.SetAttributes(path, a);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static DateTime Clamp(DateTime utc)
    {
        var min = new DateTime(1601, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(9999, 12, 30, 0, 0, 0, DateTimeKind.Utc);
        return utc < min ? min : utc > max ? max : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    }

    public ArchiveExtractResult Result()
    {
        // Folders' times and attributes at the end (writing files changes a folder's time), deepest first, and only
        // on folders this run created.
        foreach (var (e, path) in _dirEntries.OrderByDescending(d => d.Path.Length))
        {
            try
            {
                if (_request.RestoreTimes && e.ModifiedUtc is { } m) Directory.SetLastWriteTimeUtc(path, Clamp(m));
                if (_request.RestoreAttributes && OperatingSystem.IsWindows() && e.Hidden)
                    File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return new ArchiveExtractResult
        {
            FilesOk = _ok,
            FilesFailed = _failed,
            SkippedExisting = _existing,
            SkippedUnsafe = _unsafe,
            SkippedLinks = _links,
            SkippedUnsupported = _unsupported,
            Renamed = _renamed,
            Unverified = _unverified,
            Errors = [.. _errors],
            Warnings = [.. _warnings],
            Written = [.. _out],
        };
    }

    /// <summary>After a cancellation or a failure: this run's part file goes, then the folders this run created that
    /// are still empty, deepest first. Finished files stay; nothing else is touched.</summary>
    public void Abort()
    {
        if (_currentPart is not null) TryDelete(_currentPart);
        _currentPart = null;
        for (int i = _createdDirs.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Directory.Exists(_createdDirs[i]) && !Directory.EnumerateFileSystemEntries(_createdDirs[i]).Any())
                    Directory.Delete(_createdDirs[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
