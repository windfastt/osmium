using System.Buffers;
using System.IO.Hashing;
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
    /// <summary>
    /// The archive's last-write time when its index was read back, or null before then. Once it is set, an archive
    /// written to since, or one that has become a link or a cloud placeholder, lets nothing more move.
    /// </summary>
    public DateTime? ArchiveModifiedUtc { get; init; }
    /// <summary>The files the archive holds, each with the scan's record of it and the hash its index entry stores.</summary>
    public IReadOnlyList<ArchivedFile> ArchivedFiles { get; init; } = [];
    /// <summary>The scan's records of the folders the archive holds.</summary>
    public IReadOnlyList<SourceDir> ArchivedDirs { get; init; } = [];
}

/// <summary>
/// A file the archive holds: the scan's record of it (path, size, last-write time) and the XxHash128 of its contents
/// that the archive's index stores, which extraction checks the file it writes against.
/// </summary>
public sealed record ArchivedFile(SourceFile Scanned, UInt128 Hash);

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
/// must accept every name involved (<see cref="PathSafety.Validate"/>): an entry it refuses would not come back, nor
/// would one of two names in a folder that differ only in case where names ignore case (a folder Windows keeps
/// case-sensitive can hold both). And neither the item nor anything in it holds data in a named stream (Windows'
/// alternate data streams), which the archive does not keep; only the mark of the web (Zone.Identifier) may be left
/// behind.</item>
/// <item>It is on a fixed local drive. Removable and network drives have no Recycle Bin, so their files would be
/// deleted for good; the drive type comes from the caller, so these rules run anywhere.</item>
/// </list>
/// Just before an input moves (<see cref="Recheck"/>), the same rules run once more, every file it holds is read
/// again and must hash to what the archive's index stores for it, and then the rules run a last time, with every file
/// still the one that was read and unchanged since (same file id and change time). Sizes and times alone would miss an
/// edit that keeps both, and the bytes of a file stored once for several identical files are read only once while the
/// archive is built. What changes after that last look, while the system moves the item, is not seen.
/// </summary>
public static class RecycleCandidates
{
    /// <summary>Offline, recall-on-open and recall-on-data-access: a cloud file whose contents are not on this PC.</summary>
    private const FileAttributes CloudPlaceholder = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    /// <summary>Why an input stays when its job is canceled while it is checked.</summary>
    internal const string Canceled = "the job was canceled";

    /// <summary>The bytes read at a time when a file is hashed again; one buffer at a time, from the shared pool.</summary>
    private const int HashBuffer = 1 << 20;

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
        try
        {
            var archive = new FileInfo(build.Path);
            if (!archive.Exists) return "the archive is missing";
            // Once its index was read back, the archive must stay as it was then: not written to, and no link or cloud
            // placeholder in its place.
            if (facts.ArchiveModifiedUtc is { } checkedAt
                && (archive.LastWriteTimeUtc != checkedAt || (archive.Attributes & (FileAttributes.ReparsePoint | CloudPlaceholder)) != 0))
                return "the archive changed after it was checked";
            if (archive.Length != build.OutputBytes)
                return $"the archive is {archive.Length:N0} bytes, not the {build.OutputBytes:N0} bytes the builder reported";
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return $"the archive could not be checked ({e.Message})";
        }
    }

    /// <summary>
    /// The verdict for each input. <paramref name="driveType"/> tells the type of the drive that holds a path; only
    /// <see cref="DriveType.Fixed"/> lets an input move.
    /// </summary>
    /// <param name="ct">The job's cancellation: once it is canceled, every input stays, because the job was canceled.</param>
    public static RecycleDecision Decide(RecycleFacts facts, Func<string, DriveType> driveType, CancellationToken ct = default) =>
        Decide(facts, driveType, FileSystemQueries.System, ct);

    /// <inheritdoc cref="Decide(RecycleFacts, Func{string, DriveType}, CancellationToken)"/>
    /// <param name="queries">What the rules ask the file system beyond its listings.</param>
    internal static RecycleDecision Decide(RecycleFacts facts, Func<string, DriveType> driveType, FileSystemQueries queries, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return RecycleDecision.KeepAll(facts.Inputs, Canceled);
        if (JobBlocker(facts) is { } blocker) return RecycleDecision.KeepAll(facts.Inputs, blocker);
        var rules = new Rules(facts, driveType, queries, ct);
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
            try { reason = ct.IsCancellationRequested ? Canceled : overlaps.Of(full) ?? Judge(full, rules, contents: null); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { reason = Canceled; }
            catch (Exception e) { reason = $"it could not be checked ({e.Message})"; } // whatever went wrong, the input stays
            verdicts.Add(new InputVerdict(full, reason));
        }
        return new RecycleDecision(verdicts);
    }

    /// <summary>
    /// Judges one input again with the same facts, for the moment just before it moves: null when it may still move,
    /// else the reason it now stays (the archive changed, or the input no longer matches what the archive holds). On
    /// top of the rules of <see cref="Decide"/>, it reads every file the input holds and compares its hash with the one
    /// the archive's index stores for it. Then it applies the rules once more and requires every file to be the same
    /// file, with the same change time, as when it was opened to be read: a file that appeared, went, was replaced or
    /// was written to meanwhile keeps the input, even with its size and last-write time put back. Files are read one at
    /// a time through one 1 MiB buffer.
    /// </summary>
    /// <param name="ct">Stops the reading: the input then stays, because the job was canceled.</param>
    public static Func<string, string?> Recheck(RecycleFacts facts, Func<string, DriveType> driveType, CancellationToken ct = default) =>
        Recheck(facts, driveType, FileSystemQueries.System, ct);

    /// <inheritdoc cref="Recheck(RecycleFacts, Func{string, DriveType}, CancellationToken)"/>
    /// <param name="queries">What the rules ask the file system beyond its listings.</param>
    internal static Func<string, string?> Recheck(RecycleFacts facts, Func<string, DriveType> driveType, FileSystemQueries queries, CancellationToken ct = default)
    {
        if (JobBlocker(facts) is { } blocker) return _ => blocker;
        var rules = new Rules(facts, driveType, queries, ct);
        return input =>
        {
            try
            {
                if (ct.IsCancellationRequested) return Canceled;
                if (JobBlocker(facts) is { } now) return now;
                var (full, problem) = Normalize(input);
                if (full is null) return problem;
                var contents = new List<Content>();
                if (Judge(full, rules, contents) is { } reason) return reason;
                var stamps = new FileStamp[contents.Count];
                if (CompareContents(contents, stamps, ct) is { } differs) return differs;
                // The last look, after the reading: the same rules once more (the listing with its sizes and last-write
                // times, links, placeholders, streams), and each file read must still be the same file, not changed in
                // any way since it was opened to be read (same file id and change time).
                return Judge(full, rules, contents: null) ?? CompareStamps(contents, stamps);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Canceled; }
            catch (Exception e) { return $"it could not be checked again ({e.Message})"; }
        };
    }

    /// <summary>
    /// The planned files and folders that the archive's own index lists: files with the same archive path, size and
    /// last-write time, each with the hash its entry stores; folders with the same archive path. Only these count as
    /// being in the archive.
    /// </summary>
    public static (List<ArchivedFile> Files, List<SourceDir> Dirs) InArchive(IEnumerable<SourceFile> files, IEnumerable<SourceDir> dirs, IEnumerable<ArchiveEntry> index)
    {
        var entries = new Dictionary<string, ArchiveEntry>(StringComparer.Ordinal);
        foreach (var e in index) entries.TryAdd(e.Path, e);
        var held = new List<ArchivedFile>();
        foreach (var f in files)
            if (entries.TryGetValue(f.RelPath, out var e) && !e.IsDirectory && e.Size == f.Size && e.ModifiedUtc == f.ModifiedUtc)
                held.Add(new ArchivedFile(f, e.Hash));
        return (held, [.. dirs.Where(d => entries.TryGetValue(d.RelPath, out var e) && e.IsDirectory)]);
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

    /// <summary>
    /// Applies the rules to one input. When it passes and <paramref name="contents"/> is given, that list gets every file
    /// the input holds, each with the hash the archive's index stores for it.
    /// </summary>
    private static string? Judge(string full, Rules rules, List<Content>? contents)
    {
        FileSystemInfo item = File.Exists(full) ? new FileInfo(full) : new DirectoryInfo(full);
        if (!item.Exists) return "it is no longer there";
        var drive = rules.DriveOf(full);
        if (drive != DriveType.Fixed) return $"it is on {DriveName(drive)}, which has no Recycle Bin";
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return "it is a link (a junction, symbolic link or cloud file)";
        if ((item.Attributes & CloudPlaceholder) != 0) return "it is a cloud placeholder whose contents are not on this PC";
        if (StreamProblem(full, item.Name, rules) is { } streams) return streams;
        return item is FileInfo file ? JudgeFile(full, file, rules, contents) : JudgeFolder(full, (DirectoryInfo)item, rules, contents);
    }

    /// <summary>
    /// Why an item stays because of its named data streams: one holds data the archive does not keep (only the mark of
    /// the web may be left behind: extraction gives every file the archive's own), or they cannot be listed.
    /// </summary>
    /// <param name="name">The item as the reason names it: its name, or its path below the input.</param>
    private static string? StreamProblem(string path, string name, Rules rules)
    {
        IReadOnlyList<string> streams;
        try { streams = rules.Streams(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return $"the alternate streams of {name} could not be listed ({e.Message})";
        }
        foreach (var stream in streams)
            if (!string.Equals(stream, FileFacts.ZoneIdentifier, StringComparison.OrdinalIgnoreCase))
                return $"it holds data in an alternate stream that the archive does not keep: {name}:{stream}";
        return null;
    }

    private static string? JudgeFile(string full, FileInfo file, Rules rules, List<Content>? contents)
    {
        var archived = rules.Archived;
        if (string.Equals(full, rules.Archive, PathComparison)) return "it is the archive";
        if (!archived.Files.TryGetValue(full, out var held)) return "it is not in the archive" + Describe(file.Attributes);
        if (held is null || file.Length != held.Scanned.Size || file.LastWriteTimeUtc != held.Scanned.ModifiedUtc) return "it changed after it was read";
        if (PathSafety.Validate(held.Scanned.RelPath) is { } unsafeName) return $"Osmium cannot extract it under its name ({unsafeName})";
        contents?.Add(new Content(full, null, held.Hash));
        return null;
    }

    private static string? JudgeFolder(string full, DirectoryInfo dir, Rules rules, List<Content>? contents)
    {
        var archived = rules.Archived;
        if (!archived.Dirs.TryGetValue(full, out var root)) return "it is not in the archive";
        if (PathSafety.Validate(root.RelPath) is { } unsafeRoot) return $"Osmium cannot extract it under its name ({unsafeRoot})";
        if (IsInside(rules.Archive, full)) return "the archive is inside it";
        var found = new List<(string Rel, FileInfo Info)>();
        var folders = new List<string>();
        if (Walk(dir, rules, found, folders) is { } problem) return problem;
        string prefix = full + Path.DirectorySeparatorChar;
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var held = new List<Content>(found.Count);
        foreach (var (rel, info) in found.OrderBy(f => f.Rel, StringComparer.Ordinal))
        {
            string path = prefix + rel;
            listed.Add(path);
            if (!archived.Files.TryGetValue(path, out var file)) return $"it holds a file that is not in the archive: {rel}{Describe(info.Attributes)}";
            if (file is null || info.Length != file.Scanned.Size || info.LastWriteTimeUtc != file.Scanned.ModifiedUtc) return $"{rel} changed after it was read";
            if (PathSafety.Validate(file.Scanned.RelPath) is { } unsafeName) return $"Osmium cannot extract {rel} under its name ({unsafeName})";
            held.Add(new Content(path, rel, file.Hash));
        }
        foreach (var path in archived.Under(prefix))
            if (!listed.Contains(path)) return $"{path[prefix.Length..]} is no longer there";
        if (rules.NamesIgnoreCase && CaseClash(found.Select(f => f.Rel).Concat(folders)) is var (first, second))
            return $"it holds names that differ only in case ({first}, {second}), which extraction cannot restore as they are";
        // Folders need not be in the archive, but a folder with a name extraction refuses would not come back.
        foreach (var rel in folders.Order(StringComparer.Ordinal))
            if (PathSafety.Validate(root.RelPath + "/" + rel.Replace(Path.DirectorySeparatorChar, '/')) is { } unsafeFolder)
                return $"Osmium cannot extract the folder {rel} under its name ({unsafeFolder})";
        contents?.AddRange(held);
        return null;
    }

    /// <summary>
    /// Two of the files and folders below an input (their paths below it) that lie in one folder and have names that are
    /// equal when case is ignored, the first in ordinal order first; null when there are none. A folder that keeps case
    /// (on Windows, one marked case-sensitive) can hold both, but extraction there would give one of them another name.
    /// </summary>
    private static (string First, string Second)? CaseClash(IEnumerable<string> rels)
    {
        var byFolder = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var rel in rels.Order(StringComparer.Ordinal))
        {
            string folder = Path.GetDirectoryName(rel) ?? "", name = Path.GetFileName(rel);
            if (!byFolder.TryGetValue(folder, out var names)) byFolder[folder] = names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!names.TryAdd(name, rel)) return (names[name], rel);
        }
        return null;
    }

    /// <summary>
    /// Reads each file of <paramref name="contents"/> as it is now and compares its XxHash128 with the hash the
    /// archive's index stores for it, which is what extraction checks the file it writes against. Every file is read on
    /// its own, also one the archive stores once for several identical files. <paramref name="stamps"/> gets each
    /// file's stamp, taken before its first byte is read, so that any change from then on shows. Returns why the input
    /// stays when a file differs or cannot be read, null when every file matches.
    /// </summary>
    private static string? CompareContents(List<Content> contents, FileStamp[] stamps, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(HashBuffer);
        try
        {
            for (int i = 0; i < contents.Count; i++)
            {
                var c = contents[i];
                ct.ThrowIfCancellationRequested();
                UInt128 now;
                try { (now, stamps[i]) = HashOf(c.Path, buffer, ct); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
                {
                    return Unreadable(c, e);
                }
                if (now != c.Hash)
                    return c.Rel is null ? "its contents no longer match the archive" : $"the contents of {c.Rel} no longer match the archive";
            }
            return null;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>
    /// Why the input stays when a file it holds is no longer the file that was read, or has changed in any way since
    /// (another file id, or another change time: a write, or its times or attributes set, even back to what they were).
    /// </summary>
    private static string? CompareStamps(List<Content> contents, FileStamp[] stamps)
    {
        for (int i = 0; i < contents.Count; i++)
        {
            var c = contents[i];
            FileStamp now;
            try
            {
                using var handle = File.OpenHandle(c.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                now = FileFacts.StampOf(handle);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
            {
                return Unreadable(c, e);
            }
            if (now != stamps[i]) return c.Rel is null ? "it changed after it was read" : $"{c.Rel} changed after it was read";
        }
        return null;
    }

    private static string Unreadable(Content c, Exception e) => c.Rel is null
        ? $"it could not be read to compare it with the archive ({e.Message})"
        : $"{c.Rel} could not be read to compare it with the archive ({e.Message})";

    /// <summary>
    /// The XxHash128 of a file's whole contents, read from start to end, and the file's stamp, taken from the same
    /// handle before the first byte is read.
    /// </summary>
    private static (UInt128 Hash, FileStamp Stamp) HashOf(string path, byte[] buffer, CancellationToken ct)
    {
        // Shared with readers only: on Windows a file that another program has open for writing cannot be opened so.
        // It may be changing, so it stays.
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        var stamp = FileFacts.StampOf(handle);
        var hash = new XxHash128();
        int n;
        for (long at = 0; (n = RandomAccess.Read(handle, buffer, at)) > 0; at += n)
        {
            ct.ThrowIfCancellationRequested();
            hash.Append(buffer.AsSpan(0, n));
        }
        return (hash.GetCurrentHashAsUInt128(), stamp);
    }

    /// <summary>
    /// Lists every file and folder below <paramref name="root"/>, hidden and system ones included, without following
    /// anything. Returns why the folder must stay when it holds a link, a cloud placeholder or data in an alternate
    /// stream, or cannot be read completely.
    /// </summary>
    private static string? Walk(DirectoryInfo root, Rules rules, List<(string Rel, FileInfo Info)> found, List<string> folders)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var pending = new Stack<(DirectoryInfo Dir, string Rel)>();
        pending.Push((root, ""));
        while (pending.TryPop(out var next))
        {
            rules.Ct.ThrowIfCancellationRequested();
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
                if (StreamProblem(child.FullName, rel, rules) is { } streams) return streams;
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

    /// <summary>A file an input holds, by its full path, and the hash the archive's index stores for it.</summary>
    /// <param name="Rel">Its path below the input; null when it is the input itself.</param>
    private readonly record struct Content(string Path, string? Rel, UInt128 Hash);

    /// <summary>What every input of a job is judged against.</summary>
    private sealed class Rules(RecycleFacts facts, Func<string, DriveType> driveType, FileSystemQueries queries, CancellationToken ct)
    {
        /// <summary>The job's cancellation, checked once per folder listed.</summary>
        public CancellationToken Ct { get; } = ct;
        /// <summary>The archive's full path.</summary>
        public string Archive { get; } = Path.GetFullPath(facts.Build!.Path);
        public Archived Archived { get; } = new(facts.ArchivedFiles, facts.ArchivedDirs);
        public Func<string, DriveType> DriveOf { get; } = driveType;
        /// <summary>The names of the named data streams of a file or folder.</summary>
        public Func<string, IReadOnlyList<string>> Streams { get; } = queries.Streams;
        /// <inheritdoc cref="FileSystemQueries.NamesIgnoreCase"/>
        public bool NamesIgnoreCase { get; } = queries.NamesIgnoreCase;
    }

    /// <summary>The scan's records of what the archive holds, by full path.</summary>
    private sealed class Archived
    {
        /// <summary>Files by full path; null when the archive holds one file twice with different sizes, times or hashes.</summary>
        public Dictionary<string, ArchivedFile?> Files { get; } = new(StringComparer.Ordinal);
        /// <summary>Folders by full path (the first record when the scan recorded one twice).</summary>
        public Dictionary<string, SourceDir> Dirs { get; } = new(StringComparer.Ordinal);
        private readonly string[] _sorted;

        public Archived(IEnumerable<ArchivedFile> files, IEnumerable<SourceDir> dirs)
        {
            foreach (var f in files)
            {
                string path = f.Scanned.FullPath;
                if (!Files.TryAdd(path, f) && Files[path] is { } first
                    && (first.Scanned.Size != f.Scanned.Size || first.Scanned.ModifiedUtc != f.Scanned.ModifiedUtc || first.Hash != f.Hash))
                    Files[path] = null;
            }
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
