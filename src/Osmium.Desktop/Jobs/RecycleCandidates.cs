using System.Security;
using Osmium.Core.Container;
using Osmium.Core.Planning;

namespace Osmium.Desktop.Jobs;

/// <summary>What a compress job knows once its archive is written, for deciding which originals may move.</summary>
public sealed record RecycleFacts
{
    /// <summary>The files and folders the job compressed, as they were given.</summary>
    public required IReadOnlyList<string> Inputs { get; init; }
    /// <summary>How the job ended. Only <see cref="JobState.Completed"/> lets anything move.</summary>
    public required JobState Outcome { get; init; }
    /// <summary>Every block was decoded and compared with its input before it was written.</summary>
    public required bool VerifiedBlocks { get; init; }
    /// <summary>The archive builder's result, or null when no archive was completed.</summary>
    public BuildResult? Build { get; init; }
    /// <summary>The scan's records (path, size, last-write time) of the files the archive holds.</summary>
    public IReadOnlyList<SourceFile> ArchivedFiles { get; init; } = [];
    /// <summary>The scan's records of the folders the archive holds.</summary>
    public IReadOnlyList<SourceDir> ArchivedDirs { get; init; } = [];
}

/// <summary>One input and whether it may move: <see cref="Reason"/> says why it stays, and is null when it may move.</summary>
public sealed record InputVerdict(string Path, string? Reason)
{
    public bool Moves => Reason is null;
}

/// <summary>The verdict for every input of a job, in the order the inputs were given.</summary>
public sealed record RecycleDecision(IReadOnlyList<InputVerdict> Inputs)
{
    /// <summary>The inputs that may move to the Recycle Bin, as full paths.</summary>
    public IReadOnlyList<string> Move => [.. Inputs.Where(i => i.Moves).Select(i => i.Path)];

    /// <summary>The inputs that stay where they are, each with its reason.</summary>
    public IReadOnlyList<InputVerdict> Keep => [.. Inputs.Where(i => !i.Moves)];

    /// <summary>Every input stays, for the same reason.</summary>
    public static RecycleDecision KeepAll(IEnumerable<string> inputs, string reason) =>
        new([.. inputs.Select(i => new InputVerdict(RecycleCandidates.Display(i), reason))]);
}

/// <summary>
/// Decides which originals of a finished compress job may move to the Recycle Bin. It only reads the file system and
/// never moves or deletes anything. An input moves only when every rule below holds; whenever something is unclear it
/// stays where it is, and the verdict says why.
/// <list type="number">
/// <item>The job completed: no error, not canceled.</item>
/// <item>Every block was verified before it was written.</item>
/// <item>The archive builder reported no warnings (unreadable, skipped or changed files), and the archive exists with
/// the size the builder reported.</item>
/// <item>A file is in the archive, with the size and last-write time the scan recorded. A folder is in the archive,
/// and a fresh listing that includes hidden, system and excluded files finds exactly the files the archive holds
/// below it (same relative paths, sizes and last-write times) and otherwise only folders: no links, junctions or
/// cloud placeholders, and not the archive itself. A folder then moves as one item. Either way, Osmium's extractor
/// must accept every name involved (<see cref="PathSafety.Validate"/>): an entry it refuses would not come back.</item>
/// <item>It is on a fixed local drive. Removable and network drives have no Recycle Bin, so their files would be
/// deleted for good; the drive type comes from the caller, so these rules run anywhere.</item>
/// </list>
/// </summary>
public static class RecycleCandidates
{
    /// <summary>Offline, recall-on-open and recall-on-data-access: a cloud file whose contents are not on this PC.</summary>
    private const FileAttributes CloudPlaceholder = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    /// <summary>File names on Windows and macOS ignore case: two inputs that differ only in case are the same item.</summary>
    private static bool IgnoresCase => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    private static StringComparison PathComparison => IgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer PathComparer => IgnoresCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Why no input of this job may move (rules 1 to 3: the job, block verification, builder warnings and the archive
    /// file), or null when each input is judged on its own.
    /// </summary>
    public static string? JobBlocker(RecycleFacts facts)
    {
        if (facts.Outcome == JobState.Canceled) return "the job was canceled";
        if (facts.Outcome != JobState.Completed) return "the job did not complete";
        if (!facts.VerifiedBlocks) return "block verification was off for this job (Settings → Compression → Verify every block)";
        if (facts.Build is not { } build) return "the archive was not completed";
        if (build.Warnings.Count > 0)
            return $"the archive builder reported {build.Warnings.Count} warning{(build.Warnings.Count == 1 ? "" : "s")} (unreadable, skipped or changed files)";
        var archive = new FileInfo(build.Path);
        if (!archive.Exists) return "the archive is missing";
        if (archive.Length != build.OutputBytes)
            return $"the archive is {archive.Length:N0} bytes, not the {build.OutputBytes:N0} bytes the builder reported";
        return null;
    }

    /// <summary>
    /// The verdict for each input. <paramref name="driveType"/> tells the type of the drive that holds a path; only
    /// <see cref="DriveType.Fixed"/> lets an input move.
    /// </summary>
    public static RecycleDecision Decide(RecycleFacts facts, Func<string, DriveType> driveType)
    {
        if (JobBlocker(facts) is { } blocker) return RecycleDecision.KeepAll(facts.Inputs, blocker);
        var archived = new Archived(facts.ArchivedFiles, facts.ArchivedDirs);
        string archive = Path.GetFullPath(facts.Build!.Path);
        var inputs = facts.Inputs.Select(i => (Given: i, Normal: Normalize(i))).ToList();
        var overlaps = new Overlaps(inputs.Where(i => i.Normal.Full is not null).Select(i => i.Normal.Full!));
        var verdicts = new List<InputVerdict>(inputs.Count);
        foreach (var (given, (full, problem)) in inputs)
        {
            if (full is null)
            {
                verdicts.Add(new InputVerdict(given, problem));
                continue;
            }
            string? reason;
            try { reason = overlaps.Of(full) ?? Judge(full, archive, archived, driveType); }
            catch (Exception e) { reason = $"it could not be checked ({e.Message})"; } // whatever went wrong, the input stays
            verdicts.Add(new InputVerdict(full, reason));
        }
        return new RecycleDecision(verdicts);
    }

    /// <summary>
    /// Judges one input again with the same facts, for the moment just before it moves: null when it may still move,
    /// else the reason it now stays (the archive changed, or the input no longer matches what the archive holds).
    /// </summary>
    public static Func<string, string?> Recheck(RecycleFacts facts, Func<string, DriveType> driveType)
    {
        if (JobBlocker(facts) is { } blocker) return _ => blocker;
        var archived = new Archived(facts.ArchivedFiles, facts.ArchivedDirs);
        string archive = Path.GetFullPath(facts.Build!.Path);
        return input =>
        {
            try
            {
                if (JobBlocker(facts) is { } now) return now;
                var (full, problem) = Normalize(input);
                return full is null ? problem : Judge(full, archive, archived, driveType);
            }
            catch (Exception e) { return $"it could not be checked again ({e.Message})"; }
        };
    }

    /// <summary>
    /// The planned files and folders that the archive's own index lists: files with the same archive path, size and
    /// last-write time, folders with the same archive path. Only these count as being in the archive.
    /// </summary>
    public static (List<SourceFile> Files, List<SourceDir> Dirs) InArchive(IEnumerable<SourceFile> files, IEnumerable<SourceDir> dirs, IEnumerable<ArchiveEntry> index)
    {
        var entries = new Dictionary<string, ArchiveEntry>(StringComparer.Ordinal);
        foreach (var e in index) entries.TryAdd(e.Path, e);
        return (
            [.. files.Where(f => entries.TryGetValue(f.RelPath, out var e) && !e.IsDirectory && e.Size == f.Size && e.ModifiedUtc == f.ModifiedUtc)],
            [.. dirs.Where(d => entries.TryGetValue(d.RelPath, out var e) && e.IsDirectory)]);
    }

    /// <summary>The full path of an input as the scan sees it, or the input as given when it is not a usable path.</summary>
    internal static string Display(string input) => Normalize(input).Full ?? input;

    /// <summary>
    /// The full path the scan used for an input (trailing separators removed). Relative and drive-relative paths and
    /// the roots of drives and shares are refused: they never move.
    /// </summary>
    private static (string? Full, string? Problem) Normalize(string input)
    {
        string trimmed = input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0 || !Path.IsPathFullyQualified(trimmed)) return (null, "it is not a full path");
        string full;
        try { full = Path.GetFullPath(trimmed); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return (null, $"it is not a valid path ({e.Message})"); }
        if (string.Equals(Path.GetPathRoot(full), full, PathComparison)) return (null, "it is the root of a drive or share");
        return (full, null);
    }

    private static string? Judge(string full, string archive, Archived archived, Func<string, DriveType> driveType)
    {
        FileSystemInfo item = File.Exists(full) ? new FileInfo(full) : new DirectoryInfo(full);
        if (!item.Exists) return "it is no longer there";
        var drive = driveType(full);
        if (drive != DriveType.Fixed) return $"it is on {DriveName(drive)}, which has no Recycle Bin";
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return "it is a link (a junction, symbolic link or cloud file)";
        if ((item.Attributes & CloudPlaceholder) != 0) return "it is a cloud placeholder whose contents are not on this PC";
        return item is FileInfo file ? JudgeFile(full, file, archive, archived) : JudgeFolder(full, (DirectoryInfo)item, archive, archived);
    }

    private static string? JudgeFile(string full, FileInfo file, string archive, Archived archived)
    {
        if (string.Equals(full, archive, PathComparison)) return "it is the archive";
        if (!archived.Files.TryGetValue(full, out var scanned)) return "it is not in the archive" + Describe(file.Attributes);
        if (scanned is null || file.Length != scanned.Size || file.LastWriteTimeUtc != scanned.ModifiedUtc) return "it changed after it was read";
        if (PathSafety.Validate(scanned.RelPath) is { } unsafeName) return $"Osmium cannot extract it under its name ({unsafeName})";
        return null;
    }

    private static string? JudgeFolder(string full, DirectoryInfo dir, string archive, Archived archived)
    {
        if (!archived.Dirs.TryGetValue(full, out var root)) return "it is not in the archive";
        if (PathSafety.Validate(root.RelPath) is { } unsafeRoot) return $"Osmium cannot extract it under its name ({unsafeRoot})";
        if (IsInside(archive, full)) return "the archive is inside it";
        var found = new List<(string Rel, FileInfo Info)>();
        var folders = new List<string>();
        if (Walk(dir, found, folders) is { } problem) return problem;
        string prefix = full + Path.DirectorySeparatorChar;
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rel, info) in found.OrderBy(f => f.Rel, StringComparer.Ordinal))
        {
            string path = prefix + rel;
            listed.Add(path);
            if (!archived.Files.TryGetValue(path, out var scanned)) return $"it holds a file that is not in the archive: {rel}{Describe(info.Attributes)}";
            if (scanned is null || info.Length != scanned.Size || info.LastWriteTimeUtc != scanned.ModifiedUtc) return $"{rel} changed after it was read";
            if (PathSafety.Validate(scanned.RelPath) is { } unsafeName) return $"Osmium cannot extract {rel} under its name ({unsafeName})";
        }
        foreach (var path in archived.Under(prefix))
            if (!listed.Contains(path)) return $"{path[prefix.Length..]} is no longer there";
        // Folders need not be in the archive, but a folder with a name extraction refuses would not come back.
        foreach (var rel in folders.Order(StringComparer.Ordinal))
            if (PathSafety.Validate(root.RelPath + "/" + rel.Replace(Path.DirectorySeparatorChar, '/')) is { } unsafeFolder)
                return $"Osmium cannot extract the folder {rel} under its name ({unsafeFolder})";
        return null;
    }

    /// <summary>
    /// Lists every file and folder below <paramref name="root"/>, hidden and system ones included, without following
    /// anything. Returns why the folder must stay when it holds a link or cloud placeholder or cannot be read completely.
    /// </summary>
    private static string? Walk(DirectoryInfo root, List<(string Rel, FileInfo Info)> found, List<string> folders)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var pending = new Stack<(DirectoryInfo Dir, string Rel)>();
        pending.Push((root, ""));
        while (pending.TryPop(out var next))
        {
            List<FileSystemInfo> children;
            try { children = [.. next.Dir.EnumerateFileSystemInfos("*", options)]; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
            {
                return $"part of it could not be read ({(next.Rel.Length == 0 ? "the folder itself" : next.Rel)}: {e.Message})";
            }
            foreach (var child in children)
            {
                string rel = next.Rel.Length == 0 ? child.Name : next.Rel + Path.DirectorySeparatorChar + child.Name;
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) return $"it holds a link (a junction, symbolic link or cloud file): {rel}";
                if ((child.Attributes & CloudPlaceholder) != 0) return $"it holds a cloud placeholder whose contents are not on this PC: {rel}";
                if (child is DirectoryInfo sub)
                {
                    folders.Add(rel);
                    pending.Push((sub, rel));
                }
                else if (child is FileInfo file) found.Add((rel, file));
                else return $"it holds something that is neither a file nor a folder: {rel}";
            }
        }
        return null;
    }

    private static bool IsInside(string path, string folder) => path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison);

    private static string Describe(FileAttributes a) =>
        (a & FileAttributes.Hidden) != 0 ? " (a hidden file)" : (a & FileAttributes.System) != 0 ? " (a system file)" : "";

    private static string DriveName(DriveType type) => type switch
    {
        DriveType.Removable => "a removable drive",
        DriveType.Network => "a network drive",
        DriveType.CDRom => "a CD or DVD drive",
        DriveType.Ram => "a RAM disk",
        _ => "a drive of unknown type",
    };

    /// <summary>The scan's records of what the archive holds, by full path.</summary>
    private sealed class Archived
    {
        /// <summary>Files by full path; null when the scan recorded one file twice with different sizes or times.</summary>
        public Dictionary<string, SourceFile?> Files { get; } = new(StringComparer.Ordinal);
        /// <summary>Folders by full path (the first record when the scan recorded one twice).</summary>
        public Dictionary<string, SourceDir> Dirs { get; } = new(StringComparer.Ordinal);
        private readonly string[] _sorted;

        public Archived(IEnumerable<SourceFile> files, IEnumerable<SourceDir> dirs)
        {
            foreach (var f in files)
                if (!Files.TryAdd(f.FullPath, f) && Files[f.FullPath] is { } first && (first.Size != f.Size || first.ModifiedUtc != f.ModifiedUtc))
                    Files[f.FullPath] = null;
            foreach (var d in dirs) Dirs.TryAdd(d.FullPath, d);
            _sorted = [.. Files.Keys.Order(StringComparer.Ordinal)];
        }

        /// <summary>The full paths of the archived files that start with <paramref name="prefix"/> (a folder and its separator).</summary>
        public IEnumerable<string> Under(string prefix)
        {
            // Paths that share a prefix sort next to each other: they lie between the prefix and the prefix whose last
            // character is one higher.
            int start = LowerBound(prefix), end = LowerBound(prefix[..^1] + (char)(prefix[^1] + 1));
            for (int i = start; i < end; i++) yield return _sorted[i];
        }

        private int LowerBound(string key)
        {
            int i = Array.BinarySearch(_sorted, key, StringComparer.Ordinal);
            return i >= 0 ? i : ~i;
        }
    }

    /// <summary>
    /// Inputs given twice, or inside one another: the archive holds such an item twice and moving one would take the
    /// other along, so both stay.
    /// </summary>
    private sealed class Overlaps
    {
        private readonly Dictionary<string, int> _count = new(PathComparer);
        private readonly Dictionary<string, string> _holds = new(PathComparer); // a folder → an input inside it

        public Overlaps(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                _count[p] = _count.GetValueOrDefault(p) + 1;
                for (var parent = Path.GetDirectoryName(p); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                    _holds.TryAdd(parent, p);
            }
        }

        public string? Of(string path)
        {
            if (_count[path] > 1) return "it was given more than once";
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (_count.ContainsKey(parent)) return $"it is inside another input, {parent}";
            return _holds.TryGetValue(path, out var inner) ? $"another input is inside it: {inner}" : null;
        }
    }
}
