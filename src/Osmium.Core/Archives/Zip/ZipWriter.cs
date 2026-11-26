using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using Osmium.Core.IO;
using Osmium.Core.Planning;

namespace Osmium.Core.Archives.Zip;

/// <summary>Test seams of <see cref="ZipWriter"/>; never set outside tests.</summary>
internal sealed record ZipTestHooks
{
    /// <summary>Wraps the decompressed stream of each file entry (by its name in the ZIP) while the written file is
    /// checked, so that a test can change a byte the check must catch.</summary>
    public Func<string, Stream, Stream>? VerifyStreamFilter { get; init; }
    /// <summary>Called with a source file's full path once its bytes are copied, before it is checked for changes.</summary>
    public Action<string>? AfterRead { get; init; }
}

/// <summary>
/// The body of <see cref="ZipCreator.CreateAsync"/>: writes a ZIP to a partial file next to the output, reads it back
/// and checks every entry, and only then moves it into place. A source that changes while it is read is left out and
/// the ZIP is written again without it, so the ZIP never holds a copy that matches no version of its source.
/// </summary>
internal static class ZipWriter
{
    private const int ChunkSize = 1 << 20;
    private const int MaxRestarts = 2;

    /// <summary>Extensions (lower case, no dot) of files that are compressed already: stored as they are.</summary>
    internal static readonly IReadOnlySet<string> StoredExtensions = new HashSet<string>(StringComparer.Ordinal)
    {
        "jpg", "jpeg", "png", "gif", "webp", "heic", "heif", "avif", "jxl", "mp3", "m4a", "aac", "ogg", "opus", "flac",
        "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "zip", "7z", "rar", "gz", "tgz", "bz2", "xz", "zst", "lz",
        "lzma", "cab", "jar", "apk", "docx", "xlsx", "pptx", "odt", "ods", "odp", "epub", "woff", "woff2",
    };

    /// <summary>The earliest and latest times a ZIP entry can carry (the DOS date range, local time).</summary>
    internal static readonly DateTime EarliestTime = new(1980, 1, 1, 0, 0, 0), LatestTime = new(2107, 12, 31, 23, 59, 58);

    private static bool IgnoreCase => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    private static StringComparison PathComparison => IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>One entry of the ZIP: a folder ("dir/") or a file, in the order they are written.</summary>
    private sealed record Item(string Name, SourceFile? File, SourceDir? Dir);

    /// <summary>A file entry as written: what the check after writing compares it with.</summary>
    private sealed record WrittenFile(string Name, long Length, uint Crc, bool Stored);

    /// <summary>A file whose entry was started but must not stay: it changed (<c>Reason</c> null) or failed while read.</summary>
    private sealed record Failure(string Path, string? Reason);

    public static ZipResult Write(ScanResult scan, ZipOptions o, ZipTestHooks? hooks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(o);
        ct.ThrowIfCancellationRequested();
        string output = Path.GetFullPath(o.OutputPath);
        string folder = Path.GetDirectoryName(output) ?? throw new IOException($"'{output}' is not a file path.");
        foreach (var d in scan.Dirs)
            if (IsUnder(output, d.FullPath))
                throw new IOException("The ZIP file cannot be saved inside a folder that is being compressed.");
        if (Directory.Exists(output)) throw new IOException($"'{output}' is a folder.");
        if (!o.Overwrite && File.Exists(output)) throw new IOException($"'{output}' already exists.");

        string partialPrefix = output + ".partial-";
        var items = Items(scan, output, partialPrefix);
        var unreadable = new List<(string Path, string Reason)>();
        var changed = new List<string>();
        var leftOut = new HashSet<string>(IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var progress = new Reporter(o.Progress);
        var createdFolders = CreateFolder(folder);
        string partial = partialPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        bool committed = false;
        try
        {
            for (int pass = 0; ; pass++)
            {
                var kept = items.Where(i => i.File is null || !leftOut.Contains(i.File.FullPath)).ToList();
                using var fs = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
                var (written, failed) = WritePass(fs, kept, o, hooks, unreadable, progress, ct);
                foreach (var (path, _) in unreadable) leftOut.Add(path);
                if (failed.Count > 0)
                {
                    // An entry cannot be taken out of a ZIP being written: start again without these files.
                    fs.Dispose();
                    File.Delete(partial);
                    foreach (var f in failed)
                    {
                        leftOut.Add(f.Path);
                        if (f.Reason is null) changed.Add(f.Path);
                        else unreadable.Add((f.Path, f.Reason));
                    }
                    if (pass == MaxRestarts) throw Changed(failed);
                    continue;
                }
                fs.Flush(flushToDisk: true);
                if (o.Verify) Verify(fs, [.. kept.Where(i => i.File is null || !leftOut.Contains(i.File.FullPath))], written, o, hooks, progress, ct);
                long length = fs.Length;
                fs.Dispose();
                FileRetry.Move(partial, output, overwrite: o.Overwrite);
                committed = true;
                long bytes = written.Sum(w => w.Length);
                progress.Report(bytes, bytes, written.Count, written.Count, null, force: true);
                return new ZipResult
                {
                    OutputPath = output,
                    FilesWritten = written.Count,
                    FilesStored = written.Count(w => w.Stored),
                    FilesDeflated = written.Count(w => !w.Stored),
                    LeftOutUnreadable = unreadable.Count,
                    LeftOutChanged = changed.Count,
                    BytesIn = bytes,
                    BytesOut = length,
                    Warnings = [.. unreadable.Select(u => $"{u.Path}: {u.Reason.TrimEnd('.')}"),
                        .. changed.Select(c => $"{c}: it changed while it was being read, so it was left out")],
                };
            }
        }
        finally
        {
            if (!committed)
            {
                TryDelete(partial);
                for (int i = createdFolders.Count - 1; i >= 0; i--) TryDeleteEmptyFolder(createdFolders[i]);
            }
        }
    }

    /// <summary>Folders and files in name order ("dir/" before what it holds), without the output and partial files.</summary>
    private static List<Item> Items(ScanResult scan, string output, string partialPrefix)
    {
        var items = new List<Item>(scan.Files.Count + scan.Dirs.Count);
        foreach (var d in scan.Dirs) items.Add(new Item(d.RelPath.TrimEnd('/') + "/", null, d));
        foreach (var f in scan.Files)
        {
            string full = Path.GetFullPath(f.FullPath);
            if (string.Equals(full, output, PathComparison) || full.StartsWith(partialPrefix, PathComparison)) continue;
            items.Add(new Item(f.RelPath, f, null));
        }
        items.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return items;
    }

    /// <summary>
    /// Writes every item. Returns the file entries written and the files whose entry was started but that changed or
    /// failed while they were read; files that cannot be opened at all are left out and added to
    /// <paramref name="unreadable"/>.
    /// </summary>
    private static (List<WrittenFile> Written, List<Failure> Failed) WritePass(FileStream fs, List<Item> items, ZipOptions o,
        ZipTestHooks? hooks, List<(string Path, string Reason)> unreadable, Reporter progress, CancellationToken ct)
    {
        var written = new List<WrittenFile>();
        var failed = new List<Failure>();
        int filesTotal = items.Count(i => i.File is not null);
        long bytesTotal = items.Sum(i => i.File?.Size ?? 0), bytesDone = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var item in items)
            {
                o.Gate?.WaitIfPaused(ct);
                ct.ThrowIfCancellationRequested();
                if (item.Dir is { } dir)
                {
                    zip.CreateEntry(item.Name, CompressionLevel.NoCompression).LastWriteTime = ZipTime(dir.ModifiedUtc);
                    continue;
                }
                var src = item.File!;
                progress.Report(bytesDone, bytesTotal, written.Count, filesTotal, item.Name, force: false);
                FileStream input;
                long lengthBefore;
                DateTime timeBefore;
                try
                {
                    input = new FileStream(src.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    unreadable.Add((src.FullPath, e.Message));
                    continue;
                }
                using (input)
                {
                    try
                    {
                        lengthBefore = RandomAccess.GetLength(input.SafeFileHandle);
                        timeBefore = File.GetLastWriteTimeUtc(input.SafeFileHandle);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        unreadable.Add((src.FullPath, e.Message));
                        continue;
                    }
                    bool stored = o.StoreCompressedTypes && StoredExtensions.Contains(Path.GetExtension(src.RelPath).TrimStart('.').ToLowerInvariant());
                    var entry = zip.CreateEntry(item.Name, stored ? CompressionLevel.NoCompression : o.Level);
                    entry.LastWriteTime = ZipTime(timeBefore);
                    var crc = new Crc32();
                    long read = 0;
                    string? readError = null;
                    using (var es = entry.Open())
                    {
                        while (true)
                        {
                            int n;
                            try { n = input.Read(buffer, 0, ChunkSize); }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { readError = e.Message; break; }
                            if (n == 0) break;
                            crc.Append(buffer.AsSpan(0, n));
                            es.Write(buffer, 0, n);
                            read += n;
                            bytesDone += n;
                            o.Gate?.WaitIfPaused(ct);
                            ct.ThrowIfCancellationRequested();
                            progress.Report(bytesDone, bytesTotal, written.Count, filesTotal, item.Name, force: false);
                        }
                    }
                    if (readError is not null) { failed.Add(new Failure(src.FullPath, readError)); continue; }
                    hooks?.AfterRead?.Invoke(src.FullPath);
                    try
                    {
                        long lengthAfter = RandomAccess.GetLength(input.SafeFileHandle);
                        DateTime timeAfter = File.GetLastWriteTimeUtc(input.SafeFileHandle);
                        if (read != lengthBefore || lengthAfter != lengthBefore || timeAfter != timeBefore) { failed.Add(new Failure(src.FullPath, null)); continue; }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        failed.Add(new Failure(src.FullPath, e.Message));
                        continue;
                    }
                    written.Add(new WrittenFile(item.Name, read, crc.GetCurrentHashAsUInt32(), stored));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return (written, failed);
    }

    /// <summary>Reads the ZIP back: the same entries in the same order, and every file's content with the CRC-32 and
    /// length it had when read from its source, which the ZIP must also have stored.</summary>
    private static void Verify(FileStream fs, List<Item> items, List<WrittenFile> written, ZipOptions o, ZipTestHooks? hooks,
        Reporter progress, CancellationToken ct)
    {
        fs.Position = 0;
        var files = written.ToDictionary(w => w.Name, StringComparer.Ordinal);
        long total = written.Sum(w => w.Length), done = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        string current = "its file list";
        try
        {
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
            var entries = zip.Entries;
            if (entries.Count != items.Count) throw NotVerified(current);
            for (int i = 0; i < items.Count; i++)
            {
                var e = entries[i];
                current = items[i].Name;
                if (e.FullName != items[i].Name) throw NotVerified(current);
                if (items[i].File is null)
                {
                    if (e.Length != 0) throw NotVerified(current);
                    continue;
                }
                if (!files.TryGetValue(e.FullName, out var w) || e.Length != w.Length || e.Crc32 != w.Crc) throw NotVerified(current);
                var crc = new Crc32();
                long length = 0;
                using (var raw = e.Open())
                using (var s = hooks?.VerifyStreamFilter?.Invoke(e.FullName, raw) ?? raw)
                {
                    int n;
                    while ((n = s.Read(buffer, 0, ChunkSize)) > 0)
                    {
                        crc.Append(buffer.AsSpan(0, n));
                        length += n;
                        done += n;
                        o.Gate?.WaitIfPaused(ct);
                        ct.ThrowIfCancellationRequested();
                        progress.Report(done, total, written.Count, written.Count, null, force: false);
                    }
                }
                if (length != w.Length || crc.GetCurrentHashAsUInt32() != w.Crc) throw NotVerified(current);
            }
        }
        catch (InvalidDataException e) when (!e.Message.StartsWith(NotVerifiedText, StringComparison.Ordinal))
        {
            throw NotVerified(current, e);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private const string NotVerifiedText = "The ZIP file did not verify: ";

    private static InvalidDataException NotVerified(string entry, Exception? inner = null) => new($"{NotVerifiedText}{entry}.", inner);

    /// <summary>The third time files change while they are read, the ZIP is not written at all.</summary>
    private static SourceChangedException Changed(List<Failure> failed)
    {
        var paths = failed.Select(f => f.Path).ToList();
        string names = string.Join("', '", paths.Take(10)) + (paths.Count > 10 ? $"' and {paths.Count - 10:N0} more '" : "");
        string why = failed.Any(f => f.Reason is not null) ? "could not be read to the end or changed while the ZIP file was written"
            : paths.Count == 1 ? "changed while it was being read" : "changed while they were being read";
        return new SourceChangedException(names, why + ", three times in a row");
    }

    /// <summary>The entry time for a UTC file time: local time, as ZIP stores it, kept within the range ZIP can hold.</summary>
    internal static DateTimeOffset ZipTime(DateTime utc)
    {
        var local = utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        var clock = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (clock < EarliestTime) clock = EarliestTime;
        if (clock > LatestTime) clock = LatestTime;
        var offset = TimeZoneInfo.Local.GetUtcOffset(clock);
        return new DateTimeOffset(clock, TimeSpan.FromMinutes(Math.Round(offset.TotalMinutes)));
    }

    /// <summary>Calls the progress callback at most every 100 ms, and whenever forced.</summary>
    private sealed class Reporter(Action<ZipProgress>? callback)
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
        private long _last = long.MinValue;

        public void Report(long done, long total, int files, int filesTotal, string? current, bool force)
        {
            if (callback is null) return;
            long now = Stopwatch.GetTimestamp();
            if (!force && _last != long.MinValue && Stopwatch.GetElapsedTime(_last, now) < Interval) return;
            _last = now;
            callback(new ZipProgress(done, total, files, filesTotal, current));
        }
    }

    /// <summary>Creates <paramref name="folder"/> if needed; returns the folders it created, outermost first.</summary>
    private static List<string> CreateFolder(string folder)
    {
        var missing = new List<string>();
        for (string? f = folder; f is not null && !Directory.Exists(f); f = Path.GetDirectoryName(f)) missing.Insert(0, f);
        if (missing.Count > 0) Directory.CreateDirectory(folder);
        return missing;
    }

    private static bool IsUnder(string path, string folder)
    {
        string f = Path.GetFullPath(folder);
        if (!f.EndsWith(Path.DirectorySeparatorChar)) f += Path.DirectorySeparatorChar;
        return path.StartsWith(f, PathComparison);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteEmptyFolder(string folder)
    {
        try { if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
