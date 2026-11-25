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
    /// <summary>The Mark-of-the-Web to give extracted files instead of the archive's own (tests).</summary>
    internal string? Zone { get; init; }
    /// <summary>Writes the Mark-of-the-Web instead of <see cref="Extractor.WriteZoneIdentifier"/> (tests).</summary>
    internal Func<string, string, string?>? ZoneWriter { get; init; }
}

public sealed record ExtractProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, string? Current);

public sealed record ExtractResult(int FilesOk, int FilesFailed, int FilesSkipped, IReadOnlyList<string> Errors)
{
    /// <summary>How many of <see cref="FilesFailed"/> are in blocks that need a newer version of Osmium (not damaged).</summary>
    public int FilesNeedingNewerVersion { get; init; }
    /// <summary>Problems that did not stop a file from being extracted, e.g. a Mark-of-the-Web that could not be written.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

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
        public int Remaining = entry.Segments.Count;
        public long[] SegmentFileOffsets { get; } = Offsets(entry);
        public bool Failed;
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

    /// <summary>Fully decodes everything and verifies every block and file hash without writing files.</summary>
    public static ExtractResult Test(OsaReader reader, IProgress<ExtractProgress>? progress, CancellationToken ct) =>
        Run(reader, null, null, new ExtractOptions(), progress, ct, test: true);

    private static ExtractResult Run(OsaReader reader, string? destination, Func<ArchiveEntry, bool>? selection,
        ExtractOptions options, IProgress<ExtractProgress>? progress, CancellationToken ct, bool test)
    {
        var errors = new ConcurrentQueue<string>();
        var entries = reader.Entries.Where(e => selection?.Invoke(e) ?? true).ToList();
        var files = new List<FileState>();
        int skipped = 0;
        if (!test) Directory.CreateDirectory(destination!);

        // Directories first (also implicit parents), then file targets.
        foreach (var e in entries.Where(e => e.IsDirectory))
        {
            if (test) continue;
            try { Directory.CreateDirectory(PathSafety.Resolve(destination!, e.Path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Enqueue($"{e.Path}: {ex.Message}"); }
        }
        foreach (var e in entries.Where(e => !e.IsDirectory))
        {
            string? target = null;
            if (!test)
            {
                try
                {
                    target = PathSafety.Resolve(destination!, e.Path);
                    if (File.Exists(target))
                    {
                        switch (options.Overwrite)
                        {
                            case OverwriteMode.Skip: skipped++; continue;
                            case OverwriteMode.Fail: throw new IOException("File exists.");
                            case OverwriteMode.Rename: target = UniqueName(target); break;
                        }
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
                {
                    errors.Enqueue($"{e.Path}: {ex.Message}");
                    continue;
                }
            }
            files.Add(new FileState(e, target));
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

        // Empty files complete immediately.
        foreach (var f in files.Where(f => f.Entry.Segments.Count == 0))
        {
            if (Complete(f, null)) ok++; else failed++;
            filesDone++;
        }

        var budget = new MemoryGate(options.MemoryBudget);
        var fileLocks = new ConcurrentDictionary<FileState, object>();
        var newerFrames = new ConcurrentDictionary<int, bool>();
        Parallel.ForEach(byFrame.Keys.OrderBy(k => k),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelBlocks), CancellationToken = ct },
            frame =>
            {
                var info = reader.Blocks[frame];
                long need = Math.Min(options.MemoryBudget, info.DecodeMemory + info.RawSize * 2);
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
                        else if (!f.Failed) WriteSegment(f, segIdx, raw.AsSpan((int)seg.Offset, (int)seg.Length), test, errors);
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

        if (!test) ApplyDirectoryMetadata(entries.Where(e => e.IsDirectory), destination!, options);
        progress?.Report(new ExtractProgress(total, total, filesDone, files.Count, null));
        return new ExtractResult(ok, failed, skipped, errors.ToList())
        {
            FilesNeedingNewerVersion = newerFrames.IsEmpty ? 0 : files.Count(f => f.Entry.Segments.Any(s => newerFrames.ContainsKey(s.Frame))),
            Warnings = [.. warnings],
        };

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
                    if (f.PartPath is null)
                    {
                        f.PartPath = PartName(f.Target!);
                        using (File.Create(f.PartPath)) { }
                    }
                    if (f.Failed) { Cleanup(f); return false; }
                    hash = HashFile(f.PartPath);
                }
                if (hash != f.Entry.Hash)
                {
                    errors.Enqueue($"{f.Entry.Path}: content hash mismatch");
                    if (!test) Cleanup(f);
                    return false;
                }
                if (!test)
                {
                    // Mark-of-the-Web goes on the part file: it moves with the rename, so the file never exists without
                    // it, and it is in place before a ReadOnly attribute would make the stream unwritable.
                    if (zone is not null && writeZone(f.PartPath!, zone) is { } why) warnings.Enqueue(ZoneWarning(f.Entry.Path, why));
                    Osmium.Core.IO.FileRetry.Move(f.PartPath!, f.Target!, overwrite: options.Overwrite == OverwriteMode.Overwrite);
                    ApplyFileMetadata(f.Target!, f.Entry, options);
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Enqueue($"{f.Entry.Path}: {ex.Message}");
                if (!test) Cleanup(f);
                return false;
            }
        }

        void Cleanup(FileState f)
        {
            if (f.PartPath is null || options.KeepBroken) return;
            try { File.Delete(f.PartPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void WriteSegment(FileState f, int segIdx, ReadOnlySpan<byte> data, bool test, ConcurrentQueue<string> errors)
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
            f.PartPath ??= PartName(f.Target!);
            using var h = File.OpenHandle(f.PartPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            RandomAccess.Write(h, data, f.SegmentFileOffsets[segIdx]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            f.Failed = true;
            errors.Enqueue($"{f.Entry.Path}: {ex.Message}");
        }
    }

    internal static string PartName(string target) =>
        target + ".osmium-part-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    internal static UInt128 HashFile(string path)
    {
        var h = new XxHash128();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        h.Append(fs);
        return h.GetCurrentHashAsUInt128();
    }

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

    internal static void ApplyDirectoryMetadata(IEnumerable<ArchiveEntry> dirs, string root, ExtractOptions o)
    {
        foreach (var d in dirs.OrderByDescending(d => d.Path.Count(c => c == '/')))
        {
            try
            {
                var p = PathSafety.Resolve(root, d.Path);
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
