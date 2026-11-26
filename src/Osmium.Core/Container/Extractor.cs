using System.Collections.Concurrent;
using System.IO.Hashing;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Container;

public static class PathSafety
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3", "CONIN$", "CONOUT$", "CLOCK$",
    };

    /// <summary>Validates an archive path (FORMAT §7). Returns an error message or null.</summary>
    public static string? Validate(string path)
    {
        if (string.IsNullOrEmpty(path)) return "empty path";
        if (path.Length > 32767) return "path too long";
        if (path.StartsWith('/')) return "absolute path";
        foreach (char c in path)
        {
            if (c < 32 || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                return $"invalid character U+{(int)c:X4}";
        }
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0) return "empty path component";
            if (part is "." or "..") return "relative component '" + part + "'";
            if (part.EndsWith('.') || part.EndsWith(' ')) return "component ends with dot or space";
            string stem = part.Contains('.') ? part[..part.IndexOf('.')] : part;
            if (Reserved.Contains(stem.TrimEnd(' '))) return "reserved device name '" + stem + "'";
        }
        return null;
    }

    /// <summary>Resolves an archive path under <paramref name="root"/>, guaranteeing containment.</summary>
    public static string Resolve(string root, string archivePath)
    {
        var err = Validate(archivePath);
        if (err is not null) throw new CorruptDataException($"Unsafe path '{archivePath}': {err}.");
        string fullRoot = Path.GetFullPath(root);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar)) fullRoot += Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(fullRoot, archivePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new CorruptDataException($"Path '{archivePath}' escapes the destination.");
        return full;
    }
}

public enum OverwriteMode { Overwrite, Skip, Rename, Fail }

public sealed record ExtractOptions
{
    public OverwriteMode Overwrite { get; init; } = OverwriteMode.Rename;
    public bool RestoreTimes { get; init; } = true;
    public bool RestoreAttributes { get; init; } = true;
    public bool KeepBroken { get; init; }
    public int MaxParallelBlocks { get; init; } = Math.Max(1, Environment.ProcessorCount);
    public long MemoryBudget { get; init; } = MemoryBudgets.Default();
    /// <summary>
    /// Maps an entry's archive path to its output path relative to the destination ("docs/a.txt" → "a.txt"); null
    /// leaves the entry out, and it is not counted anywhere. The result still goes through <see cref="PathSafety.Resolve"/>,
    /// so it cannot lead outside the destination. Null: every entry keeps its archive path.
    /// </summary>
    public Func<string, string?>? MapPath { get; init; }
    /// <summary>
    /// <see cref="OverwriteMode.Overwrite"/> only: called with the full path of an existing file once its replacement
    /// has been written and verified, just before it replaces the file. An exception from it fails that one file,
    /// which is then not replaced. May be called from several threads at once.
    /// </summary>
    public Action<string>? BeforeReplace { get; init; }
    /// <summary>The Mark-of-the-Web to give extracted files instead of the archive's own (tests).</summary>
    internal string? Zone { get; init; }
    /// <summary>Writes the Mark-of-the-Web instead of <see cref="Extractor.WriteZoneIdentifier"/> (tests).</summary>
    internal Func<string, string, string?>? ZoneWriter { get; init; }
    /// <summary>How names are compared when planning targets; null: as the file system does (case-insensitive on
    /// Windows and macOS). Tests set the case-insensitive rule on Linux.</summary>
    internal StringComparer? NameComparer { get; init; }
    /// <summary>Full paths an earlier pass of the same operation wrote (the second pass after "Extract anyway"): they
    /// count as written by this run, so a target equal to one of them gets a free name in every overwrite mode and is
    /// never replaced or skipped as existing.</summary>
    internal IReadOnlyCollection<string>? WrittenEarlier { get; init; }
    /// <summary>Called with a block's frame number and the memory reserved for decoding it (tests).</summary>
    internal Action<int, long>? OnReserve { get; init; }
}

/// <summary>
/// Progress of an extraction or test. When every selected file's size is known, BytesTotal is their total and BytesDone
/// counts the output bytes written or checked; .osa archives always know their sizes, so for them BytesTotal is the
/// selection's total and the last report has BytesDone equal to it. When a source does not know the total, BytesTotal
/// and FilesTotal are 0 and BytesDone counts archive bytes read so far. An empty selection reports (0, 0) once.
/// </summary>
public sealed record ExtractProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, string? Current);

/// <param name="FilesFailed">Files not written because of damage, an I/O error, a replacement that was refused or a
/// name that was taken in <see cref="OverwriteMode.Fail"/>; unsafe paths are not among them (<see cref="SkippedUnsafe"/>).</param>
public sealed record ExtractResult(int FilesOk, int FilesFailed, int FilesSkipped, IReadOnlyList<string> Errors)
{
    /// <summary>Files written under another name than their own, because it was taken (by a file in the destination
    /// in <see cref="OverwriteMode.Rename"/>, or by another entry of this run in any mode); each is in <see cref="Renames"/>.</summary>
    public int Renamed { get; init; }
    /// <summary>Each renamed file: its archive path and the full path it was written to.</summary>
    public IReadOnlyList<ExtractRename> Renames { get; init; } = [];
    /// <summary>How many of <see cref="FilesFailed"/> are in blocks that need a newer version of Osmium (not damaged).</summary>
    public int FilesNeedingNewerVersion { get; init; }
    /// <summary>Problems that did not stop a file from being extracted: first what the reader found when opening the
    /// archive (<see cref="OsaReader.Warnings"/>, such as a spare copy that stood in for the file list; Test adds the
    /// comparison of the spare copies), then problems of single files, such as a Mark-of-the-Web that could not be written.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Entries (files and folders) not extracted because their path is unsafe (<see cref="PathSafety"/>);
    /// each is also in <see cref="Errors"/>, and none is in <see cref="FilesFailed"/>.</summary>
    public int SkippedUnsafe { get; init; }
    /// <summary>The files written and kept, in archive order, with the path each was written to (empty when testing).</summary>
    public IReadOnlyList<WrittenFile> Written { get; init; } = [];
}

/// <summary>A file extraction wrote: its entry and the full path it was written to (renamed when the name was taken).</summary>
public sealed record WrittenFile(ArchiveEntry Entry, string Path);

/// <summary>A file written under another name because its own was taken: "'<paramref name="ArchivePath"/>' was saved as
/// '<paramref name="WrittenPath"/>'".</summary>
public sealed record ExtractRename(string ArchivePath, string WrittenPath);

public static class MemoryBudgets
{
    public static long Default()
    {
        long avail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (avail <= 0) avail = 8L << 30;
        return Math.Max(512L << 20, (long)(avail * 0.7) - (1L << 30));
    }
}

/// <summary>Extracts or tests archive contents. Blocks are decoded in parallel within a memory budget.</summary>
public static class Extractor
{
    private sealed class FileState(ArchiveEntry entry, string? target)
    {
        public ArchiveEntry Entry { get; } = entry;
        public string? Target { get; } = target;
        public string? PartPath { get; set; }
        /// <summary>The part file, open from its creation until it is verified: every write and the hash go through it.</summary>
        public Microsoft.Win32.SafeHandles.SafeFileHandle? Part;
        public int Remaining = entry.Segments.Count;
        public long[] SegmentFileOffsets { get; } = Offsets(entry);
        public bool Failed;
        public bool Written;                               // verified and in place at Target
        public bool Done;                                  // completed: written, or failed and cleaned up
        public bool Replaces;                              // planned to replace a file that is in the destination
        public bool Renamed;                               // Target is not the entry's own name
        public XxHash128? Running;                       // test mode: incremental hash in file order
        public int NextSeg;
        public Dictionary<int, byte[]>? Pending;           // test mode: segments that arrived early

        private static long[] Offsets(ArchiveEntry e)
        {
            var o = new long[e.Segments.Count];
            long acc = 0;
            for (int i = 0; i < o.Length; i++) { o[i] = acc; acc += e.Segments[i].Length; }
            return o;
        }
    }

    /// <summary>Extracts <paramref name="selection"/> (all entries when null) under <paramref name="destination"/>.</summary>
    public static ExtractResult Extract(OsaReader reader, string destination, Func<ArchiveEntry, bool>? selection,
        ExtractOptions? options, IProgress<ExtractProgress>? progress, CancellationToken ct) =>
        Run(reader, destination, selection, options ?? new(), progress, ct, test: false);

    /// <summary>Fully decodes everything and verifies every block and file hash without writing files. Damaged spare
    /// copies of the file list or header (<see cref="OsaReader.VerifySpareCopies"/>) are reported in Warnings.</summary>
    public static ExtractResult Test(OsaReader reader, IProgress<ExtractProgress>? progress, CancellationToken ct) =>
        Run(reader, null, null, new ExtractOptions(), progress, ct, test: true);

    /// <summary>Decodes and verifies the entries <paramref name="selection"/> accepts (all when null) without writing files.</summary>
    public static ExtractResult Test(OsaReader reader, Func<ArchiveEntry, bool>? selection, IProgress<ExtractProgress>? progress, CancellationToken ct) =>
        Test(reader, selection, new ExtractOptions(), progress, ct);

    /// <summary>As the public overload, decoding within <paramref name="options"/>' memory budget and parallelism.</summary>
    internal static ExtractResult Test(OsaReader reader, Func<ArchiveEntry, bool>? selection, ExtractOptions options,
        IProgress<ExtractProgress>? progress, CancellationToken ct) =>
        Run(reader, null, selection, options, progress, ct, test: true);

    private static ExtractResult Run(OsaReader reader, string? destination, Func<ArchiveEntry, bool>? selection,
        ExtractOptions options, IProgress<ExtractProgress>? progress, CancellationToken ct, bool test)
    {
        var errors = new ConcurrentQueue<string>();
        var entries = reader.Entries.Where(e => selection?.Invoke(e) ?? true).ToList();
        // Output paths relative to the destination: the archive's own, or what MapPath makes of them (null: left out).
        var mapped = new Dictionary<ArchiveEntry, string>();
        if (options.MapPath is { } map)
        {
            var kept = new List<ArchiveEntry>(entries.Count);
            foreach (var e in entries)
            {
                if (map(e.Path) is not { } p) continue;
                mapped[e] = p;
                kept.Add(e);
            }
            entries = kept;
        }
        var files = new List<FileState>();
        int skipped = 0, unsafePaths = 0, failedEarly = 0;
        var folders = new List<(ArchiveEntry Entry, string Path)>();
        var created = new List<string>(); // folders this run created, each after the folder it is in
        var names = options.NameComparer ?? TargetNames.DefaultComparer;
        string? root = test ? null : Path.GetFullPath(destination!);
        if (test) files.AddRange(entries.Where(e => !e.IsDirectory).Select(e => new FileState(e, null)));
        else
        {
            // Plan every target before writing anything (FORMAT §7): resolve every entry, reserve every entry's own
            // name, then take names in index order, parents before children.
            var own = new Dictionary<ArchiveEntry, string>();
            foreach (var e in entries)
            {
                try { own[e] = PathSafety.Resolve(root!, OutPath(e)); }
                catch (CorruptDataException ex)
                {
                    unsafePaths++;
                    errors.Enqueue($"{e.Path}: {ex.Message}");
                }
            }
            var planner = new TargetNames(root!, names, own.Values, options.WrittenEarlier, TargetNames.FileIdentity(reader.Path));
            foreach (var e in entries)
            {
                if (!own.TryGetValue(e, out var path)) continue;
                var t = planner.Plan(path, e.IsDirectory, options.Overwrite, TargetNames.SpelledOtherwise(root!, OutPath(e), path));
                switch (t.Outcome)
                {
                    case TargetNames.Outcome.Skipped: skipped++; break;
                    case TargetNames.Outcome.Unsafe:
                        unsafePaths++;
                        errors.Enqueue($"{e.Path}: {t.Reason}");
                        break;
                    case TargetNames.Outcome.Failed:
                        failedEarly++;
                        errors.Enqueue($"{e.Path}: {t.Reason}");
                        break;
                    default:
                        if (e.IsDirectory) folders.Add((e, t.Path!));
                        else files.Add(new FileState(e, t.Path) { Replaces = t.Replaces, Renamed = t.Renamed });
                        break;
                }
            }
            // Nothing is written before this point. Folders: the destination, the folder entries, the files' folders.
            CreateFolder(root!, created);
            foreach (var (e, path) in folders)
            {
                try { CreateFolder(path, created); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Enqueue($"{e.Path}: {ex.Message}"); }
            }
            files.RemoveAll(f =>
            {
                try { CreateFolder(Path.GetDirectoryName(f.Target!)!, created); return false; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failedEarly++;
                    errors.Enqueue($"{f.Entry.Path}: {ex.Message}");
                    return true;
                }
            });
        }

        string? zone = test ? null : options.Zone ?? ReadZoneIdentifier(reader.Path);
        var writeZone = options.ZoneWriter ?? WriteZoneIdentifier;
        var warnings = new ConcurrentQueue<string>();
        long total = files.Sum(f => f.Entry.Size);
        long done = 0;
        int filesDone = 0, ok = 0, failed = 0;
        var byFrame = new Dictionary<int, List<(FileState F, int Seg)>>();
        foreach (var f in files)
        {
            for (int i = 0; i < f.Entry.Segments.Count; i++)
            {
                int fr = f.Entry.Segments[i].Frame;
                if (!byFrame.TryGetValue(fr, out var list)) byFrame[fr] = list = [];
                list.Add((f, i));
            }
        }

        var budget = new MemoryGate(options.MemoryBudget);
        var fileLocks = new ConcurrentDictionary<FileState, object>();
        var newerFrames = new ConcurrentDictionary<int, bool>();
        bool finished = false;
        try
        {
            // Empty files complete immediately.
            foreach (var f in files.Where(f => f.Entry.Segments.Count == 0))
            {
                if (Complete(f, null)) ok++; else failed++;
                filesDone++;
            }

            Parallel.ForEach(byFrame.Keys.OrderBy(k => k),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelBlocks), CancellationToken = ct },
                frame =>
                {
                    var info = reader.Blocks[frame];
                    // Every stage of the pipeline holds its whole output: reserve for the largest, which the descriptor
                    // records (a filter that expands, or a crafted descriptor, can make it larger than the block). More
                    // than the budget makes the block decode alone.
                    long need = Math.Min(options.MemoryBudget, SalvageRun.DecodeCharge(info.Descriptor, info.DecodeMemory + info.RawSize * 2));
                    options.OnReserve?.Invoke(frame, need);
                    using var lease = budget.Acquire(need, ct);
                    byte[]? raw = null;
                    try
                    {
                        raw = reader.ReadBlock(frame, new CodecContext { Token = ct });
                    }
                    catch (NewerVersionException ex)
                    {
                        errors.Enqueue(ex.Message); // names the block and what it needs; it is not damage
                        newerFrames[frame] = true;
                    }
                    catch (Exception ex) when (ex is CorruptDataException or InvalidDataException or IOException)
                    {
                        errors.Enqueue($"block {frame}: {ex.Message}");
                    }
                    foreach (var (f, segIdx) in byFrame[frame])
                    {
                        var seg = f.Entry.Segments[segIdx];
                        lock (fileLocks.GetOrAdd(f, _ => new object()))
                        {
                            if (raw is null) f.Failed = true;
                            else if (!f.Failed) WriteSegment(f, segIdx, raw.AsSpan((int)seg.Offset, (int)seg.Length), test, errors, root);
                            f.Remaining--;
                            Interlocked.Add(ref done, seg.Length);
                            if (f.Remaining == 0)
                            {
                                bool good = Complete(f, null) && !f.Failed;
                                if (good) Interlocked.Increment(ref ok); else Interlocked.Increment(ref failed);
                                Interlocked.Increment(ref filesDone);
                            }
                        }
                    }
                    progress?.Report(new ExtractProgress(Interlocked.Read(ref done), total, filesDone, files.Count, null));
                });
            finished = true;
        }
        finally
        {
            if (!test)
            {
                // Cancelled or failed: no partial file of this run stays, and no folder it created that is still empty.
                foreach (var f in files.Where(f => !f.Done)) Cleanup(f);
                if (!finished) RemoveEmptyFolders(created);
            }
        }

        if (!test) ApplyCreatedFolderMetadata(folders, created, names, options);
        progress?.Report(new ExtractProgress(total, total, filesDone, files.Count, null));
        var renamed = files.Where(f => f.Written && f.Renamed).ToList();
        return new ExtractResult(ok, failed + failedEarly, skipped, errors.ToList())
        {
            FilesNeedingNewerVersion = newerFrames.IsEmpty ? 0 : files.Count(f => f.Entry.Segments.Any(s => newerFrames.ContainsKey(s.Frame))),
            // What the reader found when opening (a spare copy that stood in for the file list…) comes first, so that
            // every caller showing the result shows it; Test adds the comparison of the spare copies.
            Warnings = [.. reader.Warnings.Concat(test ? reader.VerifySpareCopies() : []).Distinct(), .. warnings],
            SkippedUnsafe = unsafePaths,
            Written = [.. files.Where(f => f.Written).Select(f => new WrittenFile(f.Entry, f.Target!))],
            Renamed = renamed.Count,
            Renames = [.. renamed.Select(f => new ExtractRename(f.Entry.Path, f.Target!))],
        };

        string OutPath(ArchiveEntry e) => mapped.TryGetValue(e, out var p) ? p : e.Path;

        bool Complete(FileState f, byte[]? _)
        {
            try
            {
                UInt128 hash;
                if (test)
                {
                    if (f.Entry.Segments.Count == 0) hash = XxHash128.HashToUInt128([]);
                    else if (f.Failed) return false;
                    else if (f.Running is not null && f.NextSeg == f.Entry.Segments.Count) hash = f.Running.GetCurrentHashAsUInt128();
                    else { errors.Enqueue($"{f.Entry.Path}: internal error (incomplete reassembly)"); return false; }
                }
                else
                {
                    if (f.Failed) { Cleanup(f); return false; }
                    if (f.Part is null) (f.PartPath, f.Part) = CreatePart(root!, f.Target!); // an empty file
                    hash = HashFile(f.Part);
                    f.Part.Dispose(); // verified: closed before the mark and the rename
                    f.Part = null;
                }
                if (hash != f.Entry.Hash)
                {
                    errors.Enqueue($"{f.Entry.Path}: content hash mismatch");
                    if (!test) Cleanup(f);
                    return false;
                }
                if (!test)
                {
                    // Only a file planned to be replaced is: never another entry of this run, never a file of an
                    // earlier pass, and never a name that appeared while the files were written.
                    if (f.Replaces && options.BeforeReplace is { } beforeReplace && File.Exists(f.Target!))
                    {
                        try { beforeReplace(f.Target!); }
                        catch (Exception ex)
                        {
                            errors.Enqueue($"{f.Entry.Path}: {ex.Message}");
                            Cleanup(f);
                            return false;
                        }
                    }
                    // Mark-of-the-Web goes on the part file: it moves with the rename, so the file never exists without
                    // it, and it is in place before a ReadOnly attribute would make the stream unwritable.
                    if (zone is not null && writeZone(f.PartPath!, zone) is { } why) warnings.Enqueue(ZoneWarning(f.Entry.Path, why));
                    // The plan checked the folders for links; checked again now, as they may have changed since.
                    if (new FileInfo(f.PartPath!).LinkTarget is not null) throw new IOException("its part file was replaced by a link while it was written");
                    if (TargetNames.LinkOnTheWay(root!, f.Target!) is { } link) throw new IOException(link);
                    Osmium.Core.IO.FileRetry.Move(f.PartPath!, f.Target!, overwrite: f.Replaces);
                    f.PartPath = null;
                    ApplyFileMetadata(f.Target!, f.Entry, options);
                    f.Written = true;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Enqueue($"{f.Entry.Path}: {ex.Message}");
                if (!test) Cleanup(f);
                return false;
            }
            finally
            {
                f.Done = true;
            }
        }

        // Deletes the part file of an entry that did not complete; only ever one this run created.
        void Cleanup(FileState f)
        {
            f.Part?.Dispose();
            f.Part = null;
            if (f.PartPath is null || options.KeepBroken) return;
            try { File.Delete(f.PartPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            f.PartPath = null;
        }
    }

    /// <summary>
    /// Creates the part file of <paramref name="target"/> under a new random name and keeps it open: it never opens a
    /// file (or a link) that is already there, and refuses when a folder on the way from <paramref name="root"/> has
    /// become a link since the targets were planned. It is shared with nothing (never FileShare.Delete), so on Windows
    /// it cannot be deleted, renamed or replaced while it is open.
    /// </summary>
    internal static (string Path, Microsoft.Win32.SafeHandles.SafeFileHandle Handle) CreatePart(string root, string target)
    {
        if (TargetNames.LinkOnTheWay(root, target) is { } why) throw new IOException(why);
        string part = PartName(target);
        return (part, File.OpenHandle(part, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
    }

    /// <summary>XxHash128 of an open file's whole content, read through the handle.</summary>
    internal static UInt128 HashFile(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var h = new XxHash128();
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int n;
            for (long at = 0; (n = RandomAccess.Read(handle, buffer, at)) > 0; at += n) h.Append(buffer.AsSpan(0, n));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
        return h.GetCurrentHashAsUInt128();
    }

    /// <summary>Creates <paramref name="folder"/> and the folders above it that are missing, adding each one this call
    /// created to <paramref name="created"/>, outermost first.</summary>
    internal static void CreateFolder(string folder, List<string> created)
    {
        var missing = new List<string>();
        for (string? d = folder; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Add(d);
        if (missing.Count == 0) return;
        Directory.CreateDirectory(folder);
        for (int i = missing.Count - 1; i >= 0; i--) created.Add(missing[i]);
    }

    /// <summary>Deletes the folders this run created that are empty, deepest first.</summary>
    internal static void RemoveEmptyFolders(List<string> created)
    {
        foreach (var d in created.OrderByDescending(d => d.Length))
        {
            try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Times and attributes of folder entries, only on folders this run created (a folder that was already there keeps
    /// its own), deepest first, after their contents. Never System or ReadOnly: on Windows either makes Explorer act on
    /// a desktop.ini in the folder, which an archive should not be able to turn on.
    /// </summary>
    internal static void ApplyCreatedFolderMetadata(List<(ArchiveEntry Entry, string Path)> folders, List<string> created, StringComparer names, ExtractOptions o)
    {
        var ours = new HashSet<string>(created, names);
        foreach (var (e, path) in folders.Where(f => ours.Contains(f.Path)).OrderByDescending(f => f.Path.Length))
        {
            try
            {
                if (o.RestoreTimes && e.ModifiedUtc is { } m) Directory.SetLastWriteTimeUtc(path, m);
                if (o.RestoreAttributes && e.Attributes is { } a && OperatingSystem.IsWindows())
                    File.SetAttributes(path, (a & ~(FileAttributes.System | FileAttributes.ReadOnly)) | FileAttributes.Directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void WriteSegment(FileState f, int segIdx, ReadOnlySpan<byte> data, bool test, ConcurrentQueue<string> errors, string? root)
    {
        if (test)
        {
            // Blocks decode in parallel, so segments can arrive out of file order: hash in order and park
            // early arrivals until their predecessors have been hashed.
            f.Running ??= new XxHash128();
            if (segIdx != f.NextSeg)
            {
                (f.Pending ??= [])[segIdx] = data.ToArray();
                return;
            }
            f.Running.Append(data);
            f.NextSeg++;
            while (f.Pending is not null && f.Pending.Remove(f.NextSeg, out var early))
            {
                f.Running.Append(early);
                f.NextSeg++;
            }
            return;
        }
        try
        {
            // The first segment creates the part file (never one that is there already); the path is kept only then,
            // so that cleaning up never deletes a file this run did not create.
            if (f.Part is null) (f.PartPath, f.Part) = CreatePart(root!, f.Target!);
            RandomAccess.Write(f.Part, data, f.SegmentFileOffsets[segIdx]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            f.Failed = true;
            errors.Enqueue($"{f.Entry.Path}: {ex.Message}");
        }
    }

    internal static string PartName(string target) =>
        target + ".osmium-part-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();


    /// <summary>Mark-of-the-Web: files extracted from a downloaded archive inherit its zone (Windows only).</summary>
    internal static string? ReadZoneIdentifier(string archivePath)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return File.ReadAllText(archivePath + ":Zone.Identifier"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// Gives a file the Mark-of-the-Web. A read-only file refuses a new stream, so its ReadOnly attribute is cleared for
    /// the write and then restored. Returns why the mark could not be written, or null.
    /// </summary>
    internal static string? WriteZoneIdentifier(string path, string zone)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            bool readOnly = attributes.HasFlag(FileAttributes.ReadOnly);
            if (readOnly) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            try
            {
                File.WriteAllText(path + ":Zone.Identifier", zone);
            }
            finally
            {
                if (readOnly) File.SetAttributes(path, attributes);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    /// <summary>The warning for a file that came from a downloaded archive but could not be marked as such.</summary>
    internal static string ZoneWarning(string path, string why) =>
        $"{path}: the mark that it came from the internet (Mark-of-the-Web) could not be written ({why.TrimEnd('.')}); treat it as downloaded";

    internal static string UniqueName(string target)
    {
        string dir = Path.GetDirectoryName(target)!, stem = Path.GetFileNameWithoutExtension(target), ext = Path.GetExtension(target);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    internal static void ApplyFileMetadata(string path, ArchiveEntry e, ExtractOptions o)
    {
        try
        {
            if (o.RestoreTimes)
            {
                if (e.ModifiedUtc is { } m) File.SetLastWriteTimeUtc(path, m);
                if (e.CreatedUtc is { } c && OperatingSystem.IsWindows()) File.SetCreationTimeUtc(path, c);
            }
            if (o.RestoreAttributes && e.Attributes is { } a && OperatingSystem.IsWindows())
                File.SetAttributes(path, a == 0 ? FileAttributes.Normal : a);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <param name="outPath">The output path of each folder relative to <paramref name="root"/> (null: its archive path).</param>
    internal static void ApplyDirectoryMetadata(IEnumerable<ArchiveEntry> dirs, string root, ExtractOptions o, Func<ArchiveEntry, string>? outPath = null)
    {
        foreach (var d in dirs.OrderByDescending(d => d.Path.Count(c => c == '/')))
        {
            try
            {
                var p = PathSafety.Resolve(root, outPath?.Invoke(d) ?? d.Path);
                if (o.RestoreTimes && d.ModifiedUtc is { } m) Directory.SetLastWriteTimeUtc(p, m);
                if (o.RestoreAttributes && d.Attributes is { } a && OperatingSystem.IsWindows()) File.SetAttributes(p, a | FileAttributes.Directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException) { }
        }
    }
}

/// <summary>A byte-weighted semaphore. A single request larger than the capacity is admitted alone.</summary>
public sealed class MemoryGate(long capacity)
{
    private readonly object _lock = new();
    private long _used;

    public long Capacity => capacity;
    public long Used { get { lock (_lock) return _used; } }

    public Lease Acquire(long bytes, CancellationToken ct)
    {
        bytes = Math.Max(0, bytes);
        lock (_lock)
        {
            while (_used > 0 && _used + bytes > capacity)
            {
                ct.ThrowIfCancellationRequested();
                Monitor.Wait(_lock, 200);
            }
            _used += bytes;
        }
        return new Lease(this, bytes);
    }

    private void Release(long bytes)
    {
        lock (_lock)
        {
            _used -= bytes;
            Monitor.PulseAll(_lock);
        }
    }

    public readonly struct Lease(MemoryGate gate, long bytes) : IDisposable
    {
        public void Dispose() => gate?.Release(bytes);
    }
}
