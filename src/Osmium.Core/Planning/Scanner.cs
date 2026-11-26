namespace Osmium.Core.Planning;

public sealed record SourceFile(string FullPath, string RelPath, long Size, DateTime ModifiedUtc, DateTime CreatedUtc, FileAttributes Attributes)
{
    /// <summary>The file's id (device and inode) when the scan could learn it without opening the file, as on Linux: a
    /// later read checks that it opened this same file.</summary>
    internal UInt128? FileId { get; init; }
}
public sealed record SourceDir(string FullPath, string RelPath, DateTime ModifiedUtc, DateTime CreatedUtc, FileAttributes Attributes);

public sealed record ScanOptions
{
    public bool IncludeHidden { get; init; } = true;
    public bool IncludeSystem { get; init; }
    /// <summary>Cloud placeholders (OneDrive "online-only") would be downloaded in full if read.</summary>
    public bool IncludeCloudPlaceholders { get; init; }
    /// <summary>Paths to exclude (archive-relative globs).</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];
}

public sealed record ScanResult(List<SourceFile> Files, List<SourceDir> Dirs, List<string> Skipped);

/// <summary>Enumerates inputs into archive-relative paths. Never follows reparse points (junction cycles).</summary>
public static class Scanner
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    public static ScanResult Scan(IEnumerable<string> inputs, ScanOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ScanOptions();
        var files = new List<SourceFile>();
        var dirs = new List<SourceDir>();
        var skipped = new List<string>();
        var usedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludes = options.Excludes.Select(Glob.ToRegex).ToList();

        foreach (var input in inputs)
        {
            var full = Path.GetFullPath(input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string rootName = Path.GetFileName(full);
            if (string.IsNullOrEmpty(rootName)) rootName = "root";
            string unique = rootName;
            for (int i = 2; !usedRoots.Add(unique); i++) unique = $"{rootName} ({i})";

            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                if (Accept(fi, unique, out var id)) files.Add(ToSource(fi, unique, id));
            }
            else if (Directory.Exists(full))
            {
                var di = new DirectoryInfo(full);
                dirs.Add(new SourceDir(di.FullName, unique, di.LastWriteTimeUtc, di.CreationTimeUtc, di.Attributes));
                Walk(di, unique);
            }
            else skipped.Add($"{input}: not found");
        }

        files.Sort((a, b) => string.CompareOrdinal(a.RelPath, b.RelPath));
        dirs.Sort((a, b) => string.CompareOrdinal(a.RelPath, b.RelPath));
        return new ScanResult(files, dirs, skipped);

        void Walk(DirectoryInfo dir, string rel)
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> children;
            try { children = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 }).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped.Add($"{dir.FullName}: {e.Message}"); return; }
            foreach (var c in children)
            {
                string childRel = rel + "/" + c.Name;
                if (c is DirectoryInfo d)
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) { skipped.Add($"{d.FullName}: junction/symlink not followed"); continue; }
                    if (!AcceptAttrs(d.Attributes, childRel, d.FullName)) continue;
                    dirs.Add(new SourceDir(d.FullName, childRel, d.LastWriteTimeUtc, d.CreationTimeUtc, d.Attributes));
                    Walk(d, childRel);
                }
                else if (c is FileInfo f && Accept(f, childRel, out var id)) files.Add(ToSource(f, childRel, id));
            }
        }

        bool Accept(FileInfo f, string rel, out UInt128? id)
        {
            id = null;
            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) { skipped.Add($"{f.FullName}: symbolic link skipped"); return false; }
            if (!AcceptAttrs(f.Attributes, rel, f.FullName)) return false;
            // A pipe could keep a read waiting for ever, and a device could feed it without end.
            (string? notRegular, id) = SourceIdentity.Look(f.FullName);
            if (notRegular is null) return true;
            skipped.Add($"{f.FullName}: not a regular file ({notRegular}), left out");
            return false;
        }

        bool AcceptAttrs(FileAttributes a, string rel, string fullName)
        {
            if (!options.IncludeHidden && (a & FileAttributes.Hidden) != 0) return false;
            if (!options.IncludeSystem && (a & FileAttributes.System) != 0 && OperatingSystem.IsWindows()) return false;
            if (!options.IncludeCloudPlaceholders && (a & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
            {
                skipped.Add($"{fullName}: cloud placeholder (not downloaded)");
                return false;
            }
            foreach (var rx in excludes)
                if (rx.IsMatch(rel) || rx.IsMatch(Path.GetFileName(rel))) return false;
            return true;
        }
    }

    private static SourceFile ToSource(FileInfo f, string rel, UInt128? id) =>
        new(f.FullName, rel, f.Length, f.LastWriteTimeUtc, f.CreationTimeUtc, f.Attributes) { FileId = id };
}
