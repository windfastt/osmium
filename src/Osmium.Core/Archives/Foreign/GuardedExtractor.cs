using System.Runtime.InteropServices;
using Osmium.Core.Container;
using Osmium.Core.IO;
using Osmium.Core.Text;

namespace Osmium.Core.Archives.Foreign;

/// <summary>
/// The only way an entry of a foreign archive reaches the disk. For each entry, in archive order: normalise and
/// refuse unsafe names, map the characters Windows does not allow, never create or follow a link (a TAR hard link
/// to a file this run wrote becomes a copy), refuse devices and other special entries, never write through a link
/// that is already in the destination, rename collisions inside the run (case-insensitively, also against the paths
/// of an earlier pass) and handle existing files by the overwrite mode, write to a part file through the limit
/// counters and the format's checks, then give it the Mark-of-the-Web, move it into place and set its time and only
/// its ReadOnly and Hidden attributes. A cancellation or failure removes this run's part files and the empty folders
/// it created; finished files stay.
/// </summary>
internal sealed class GuardedExtractor : IEntrySink
{
    private const int CopyBuffer = 1 << 18;
    private const long FreeSpaceInterval = 64L << 20;

    private readonly string _root;
    private readonly ArchiveExtractRequest _request;
    private readonly ImportLimits _limits;
    private readonly LimitGuard _guard;
    private readonly ProgressReporter _progress;
    private readonly string? _zone;
    private readonly Func<string, string, string?> _writeZone;
    private readonly CancellationToken _ct;

    // Paths this run (or the earlier pass) wrote, full and compared case-insensitively on every system: true = folder.
    private readonly Dictionary<string, bool> _written = new(StringComparer.OrdinalIgnoreCase);
    // Every selected file's own target, so a free name never takes another entry's name.
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    // Folder prefixes renamed because a file of the same name was there: original relative path → new one.
    private readonly Dictionary<string, string> _dirRenames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _throughLink = new(StringComparer.Ordinal);
    private readonly List<string> _createdDirs = [];
    private readonly HashSet<string> _createdDirSet = new(StringComparer.Ordinal);
    private readonly List<(ForeignEntry Entry, string Path)> _dirEntries = [];
    private readonly Dictionary<string, string> _filesByArchivePath = new(StringComparer.Ordinal);
    private readonly List<string> _errors = [], _warnings = [];
    private readonly List<ExtractedFile> _out = [];
    private string? _currentPart;
    private long _sinceFreeCheck;
    private int _ok, _failed, _existing, _unsafe, _links, _unsupported, _renamed, _unverified;

    /// <param name="reservedTargets">Relative paths (as the archive names them, after the prefix) of every selected
    /// file, when the listing is complete.</param>
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
        foreach (var p in request.WrittenEarlier ?? [])
            _written[Path.GetFullPath(p)] = false;
        if (reservedTargets is not null)
            foreach (var rel in reservedTargets)
                if (EntryNames.Normalize(rel, int.MaxValue, out var n) is null && n is not null)
                {
                    EntryNames.MapForWindows(n, out var mapped);
                    if (PathSafety.Validate(mapped) is null) _reserved.Add(Full(mapped));
                }
        CreateRoot();
    }

    private static readonly AsyncLocal<string?> ZoneSeam = new();
    private static readonly AsyncLocal<Func<string, string, string?>?> ZoneWriterSeam = new();
    private static readonly AsyncLocal<long?> FreeSpaceIntervalSeam = new();

    /// <summary>For tests (this async flow only): the Mark-of-the-Web to give files instead of the archive's own.</summary>
    internal static string? ZoneOverride { get => ZoneSeam.Value; set => ZoneSeam.Value = value; }
    /// <summary>For tests: writes the Mark-of-the-Web instead of <see cref="Extractor.WriteZoneIdentifier"/>.</summary>
    internal static Func<string, string, string?>? ZoneWriterOverride { get => ZoneWriterSeam.Value; set => ZoneWriterSeam.Value = value; }
    /// <summary>For tests: how many bytes are written between free-space checks (64 MiB).</summary>
    internal static long? FreeSpaceIntervalOverride { get => FreeSpaceIntervalSeam.Value; set => FreeSpaceIntervalSeam.Value = value; }

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
            if (!PrepareParents(e, ref mapped, ref renamed)) return;
            if (e.Type == EntryType.Directory) { MakeDirectory(e, mapped, renamed); return; }
            if (!ChooseTarget(e, mapped, ref renamed, out string target, out bool replace)) return;
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

    /// <summary>Rule 7 and the folder half of rule 8: every folder between the root and the entry exists, is not a
    /// link, and is not a file (a prefix that names a file is renamed, for this and every later entry below it).</summary>
    private bool PrepareParents(ForeignEntry e, ref string mapped, ref bool renamed)
    {
        mapped = ApplyDirRenames(mapped, ref renamed);
        int folders = mapped.Split('/').Length - (e.Type == EntryType.Directory ? 0 : 1);
        for (int i = 0; i < folders;)
        {
            string rel = string.Join('/', mapped.Split('/'), 0, i + 1);
            string full = Full(rel);
            bool fileThere = _written.TryGetValue(full, out bool isDir) ? !isDir : IsFileOrFileLink(full);
            if (fileThere)
            {
                string free = Relative(UniqueName(full));
                _dirRenames[rel] = free;
                _warnings.Add($"'{Esc(rel)}' is also the name of a file; the folder was saved as '{Esc(free)}'");
                mapped = free + mapped[rel.Length..];
                renamed = true;
                continue; // the same level again, under its new name
            }
            if (Directory.Exists(full))
            {
                if (ThroughLink(full))
                {
                    Refuse(ref _unsafe, e, "it would be written through a link that is already in the destination");
                    return false;
                }
                _written.TryAdd(full, true);
            }
            else
            {
                CreateDir(full);
                _written[full] = true;
            }
            i++;
        }
        return true;
    }

    /// <summary>Applies the folder renames of earlier entries (repeatedly, for renamed folders inside renamed ones).</summary>
    private string ApplyDirRenames(string mapped, ref bool renamed)
    {
        for (int round = 0; round < 1024 && _dirRenames.Count > 0; round++)
        {
            var parts = mapped.Split('/');
            string? hit = null;
            for (int i = parts.Length; i >= 1 && hit is null; i--)
            {
                string prefix = string.Join('/', parts, 0, i);
                if (_dirRenames.TryGetValue(prefix, out var to) && !string.Equals(to, prefix, StringComparison.OrdinalIgnoreCase))
                    hit = to + mapped[prefix.Length..];
            }
            if (hit is null) break;
            mapped = hit;
            renamed = true;
        }
        return mapped;
    }

    private void MakeDirectory(ForeignEntry e, string mapped, bool renamed)
    {
        string full = Full(mapped);
        if (renamed) _warnings.Add($"'{Esc(e.Path)}' was saved as '{Esc(mapped)}'");
        if (_createdDirSet.Contains(full)) _dirEntries.Add((e, full));
    }

    /// <summary>Rules 8 and 9 for a file: the target, renamed when this run or the earlier pass wrote that name (in
    /// every mode), else by the overwrite mode when a file or folder was already there. False: nothing to write.</summary>
    private bool ChooseTarget(ForeignEntry e, string mapped, ref bool renamed, out string target, out bool replace)
    {
        target = Full(mapped);
        replace = false;
        if (_written.ContainsKey(target))
        {
            target = UniqueName(target);
            renamed = true;
            return true;
        }
        bool isFile = IsFileOrFileLink(target), isDir = !isFile && Directory.Exists(target);
        if (!isFile && !isDir) return true;
        switch (_request.Overwrite)
        {
            case OverwriteMode.Skip:
                _existing++;
                return false;
            case OverwriteMode.Fail:
                _failed++;
                _errors.Add($"{Esc(e.Path)}: a {(isDir ? "folder" : "file")} of this name already exists");
                return false;
            case OverwriteMode.Overwrite when isFile:
                replace = true;
                return true;
            default:
                target = UniqueName(target);
                renamed = true;
                return true;
        }
    }

    private void Write(ForeignEntry e, string target, bool replace, bool renamed, EntryContent content)
    {
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
            FileRetry.Move(part, target, overwrite: replace);
            _currentPart = null;
            SetFileMetadata(target, e);
            _written[target] = false;
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

    /// <summary>A free "name (n).ext" next to <paramref name="full"/>: not on disk, not written in this run, and not
    /// another selected entry's own name.</summary>
    private string UniqueName(string full)
    {
        string dir = Path.GetDirectoryName(full)!, stem = Path.GetFileNameWithoutExtension(full), ext = Path.GetExtension(full);
        for (int i = 2; i < 10_000_000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!_written.ContainsKey(candidate) && !_reserved.Contains(candidate) && !Exists(candidate)) return candidate;
        }
        throw new IOException("No free name was found.");
    }

    private static bool Exists(string full) => IsFileOrFileLink(full) || Directory.Exists(full);

    /// <summary>A file, or a link of any kind that is not a folder we may write into (dangling links included).</summary>
    private static bool IsFileOrFileLink(string full)
    {
        var fi = new FileInfo(full);
        if (fi.Exists) return true;
        try { return fi.LinkTarget is not null && !Directory.Exists(full); }
        catch (IOException) { return false; }
    }

    /// <summary>Rule 7: a folder that is a symbolic link, junction or mount point (a name-surrogate reparse point);
    /// cloud placeholders and other reparse points are allowed.</summary>
    private bool ThroughLink(string dir)
    {
        if (_throughLink.TryGetValue(dir, out bool cached)) return cached;
        bool link;
        try
        {
            var info = new DirectoryInfo(dir);
            link = info.LinkTarget is not null
                || (OperatingSystem.IsWindows() && info.Attributes.HasFlag(FileAttributes.ReparsePoint) && (ReparseTag(dir) & 0x20000000) != 0);
        }
        catch (IOException) { link = true; }
        catch (UnauthorizedAccessException) { link = true; }
        _throughLink[dir] = link;
        return link;
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

    private static uint ReparseTag(string path)
    {
        var h = FindFirstFileW(path, out var data);
        if (h == -1) return 0;
        FindClose(h);
        return (data.dwFileAttributes & 0x400) != 0 ? data.dwReserved0 : 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint dwFileAttributes;
        public uint ftCreationLow, ftCreationHigh, ftLastAccessLow, ftLastAccessHigh, ftLastWriteLow, ftLastWriteHigh;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindFirstFileW(string fileName, out Win32FindData data);

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(nint handle);
}
