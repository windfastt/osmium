using System.Runtime.InteropServices;

namespace Osmium.Core.Container;

/// <summary>
/// Plans where every entry of one extraction goes, before anything is written (FORMAT §7): every entry's own name is
/// reserved first; a name another entry of this run already took, or a file where this run puts a folder (or the
/// other way round), gets a free name "name (2).ext", "name (3).ext"… in every overwrite mode, so an entry never
/// replaces another entry of the same archive; a name that exists in the destination follows the overwrite mode. A free
/// name is on no disk entry, is no entry's own name and was not chosen before. Folders that already exist below the
/// destination are checked for links: nothing is written through a symbolic link or junction already there.
/// </summary>
internal sealed class TargetNames
{
    /// <summary>The name comparison of the file system: case-insensitive on Windows and macOS.</summary>
    public static StringComparer DefaultComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public const string LinkRefused = "a link in the destination points elsewhere";

    public enum Outcome { Planned, Skipped, Failed, Unsafe }

    /// <param name="Path">The full path to write to (null unless planned).</param>
    /// <param name="Replaces">An existing file at <see cref="Path"/> is to be replaced (OverwriteMode.Overwrite).</param>
    /// <param name="Renamed">The path is not the entry's own resolved name.</param>
    /// <param name="Reason">Why the entry is not planned (Failed, Unsafe).</param>
    public readonly record struct Target(Outcome Outcome, string? Path, bool Replaces, bool Renamed, string? Reason);

    private enum OnDiskKind { None, File, Directory, Alias }

    private readonly string _root;
    private readonly StringComparer _names;
    private readonly HashSet<string> _reserved, _files, _dirs;
    private readonly Dictionary<string, string> _dirRenames;
    private readonly Dictionary<string, Dictionary<string, bool>?> _listings;
    private readonly Dictionary<string, int> _nextFree;
    private readonly Dictionary<string, string?> _links;

    /// <param name="root">The destination's full path.</param>
    /// <param name="own">Every entry's own resolved full path (files and folders).</param>
    /// <param name="writtenEarlier">Paths an earlier pass of the same operation wrote: taken, never replaced or skipped.</param>
    public TargetNames(string root, StringComparer names, IEnumerable<string> own, IEnumerable<string>? writtenEarlier)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _names = names;
        _reserved = new HashSet<string>(names);
        _files = new HashSet<string>(names);
        _dirs = new HashSet<string>(names);
        _dirRenames = new Dictionary<string, string>(names);
        _listings = new Dictionary<string, Dictionary<string, bool>?>(names);
        _nextFree = new Dictionary<string, int>(names);
        _links = new Dictionary<string, string?>(names);
        foreach (var p in own)
            for (string? a = p; a is not null && !_names.Equals(a, _root) && _reserved.Add(a); a = Path.GetDirectoryName(a)) { }
        foreach (var w in writtenEarlier ?? []) _files.Add(Path.GetFullPath(w));
    }

    /// <summary>Plans one entry; call in index order (parents before children).</summary>
    public Target Plan(string own, bool isDirectory, OverwriteMode mode)
    {
        string path = own;
        // Folders above it, outermost first: one that a file of this run took moves to a free name, with everything
        // below it; one that exists already must not be a link.
        var parts = Path.GetRelativePath(_root, own).Split(Path.DirectorySeparatorChar);
        string current = _root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string next = Path.Combine(current, parts[i]);
            if (_dirRenames.TryGetValue(next, out var moved)) next = moved;
            else if (_files.Contains(next)) next = _dirRenames[next] = FreeName(next, folder: true);
            _dirs.Add(next);
            if (LinkAt(next) is { } why) return new(Outcome.Unsafe, null, false, false, why);
            current = next;
        }
        path = Path.Combine(current, parts[^1]);

        if (isDirectory)
        {
            if (_dirRenames.TryGetValue(path, out var moved)) path = moved;
            else if (_files.Contains(path)) path = _dirRenames[path] = FreeName(path, folder: true);
            if (LinkAt(path) is { } why) return new(Outcome.Unsafe, null, false, false, why);
            _dirs.Add(path);
            return new(Outcome.Planned, path, false, !_names.Equals(path, own), null);
        }

        bool replaces = false;
        if (_files.Contains(path) || _dirs.Contains(path))
            path = FreeName(path, folder: false); // another entry of this run has this name: never replace it
        else
        {
            var kind = OnDisk(path);
            if (kind != OnDiskKind.None)
            {
                switch (mode)
                {
                    case OverwriteMode.Skip: return new(Outcome.Skipped, null, false, false, null);
                    case OverwriteMode.Fail: return new(Outcome.Failed, null, false, false, kind == OnDiskKind.Directory ? "A folder of this name exists." : "File exists.");
                    case OverwriteMode.Overwrite when kind == OnDiskKind.File: replaces = true; break;
                    case OverwriteMode.Overwrite when kind == OnDiskKind.Directory:
                        return new(Outcome.Failed, null, false, false, "A folder of this name is in the destination; it is not replaced by a file.");
                    default: path = FreeName(path, folder: false); break; // Rename, or a name known only by another spelling
                }
            }
        }
        _files.Add(path);
        return new(Outcome.Planned, path, replaces, !_names.Equals(path, own), null);
    }

    /// <summary>"name (2).ext", "name (3).ext"… (folders: "name (2)"): the first that nothing has. The search for one
    /// name resumes where it stopped, so many entries of one name cost one pass.</summary>
    private string FreeName(string path, bool folder)
    {
        string dir = Path.GetDirectoryName(path)!, file = Path.GetFileName(path);
        string stem = folder ? file : Path.GetFileNameWithoutExtension(file), ext = folder ? "" : Path.GetExtension(file);
        string key = Path.Combine(dir, stem) + "\0" + ext;
        for (int i = _nextFree.GetValueOrDefault(key, 2); ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (_reserved.Contains(candidate) || _files.Contains(candidate) || _dirs.Contains(candidate) || OnDisk(candidate) != OnDiskKind.None) continue;
            _nextFree[key] = i + 1;
            return candidate;
        }
    }

    /// <summary>
    /// What is at <paramref name="path"/> before the run, by the name comparison of this run (a folder listing, so
    /// that the comparison also holds where the file system would differ). <see cref="OnDiskKind.Alias"/>: the system
    /// finds something under another spelling (a short 8.3 name): never replaced, only avoided.
    /// </summary>
    private OnDiskKind OnDisk(string path)
    {
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileName(path);
        var listing = Listing(dir);
        if (listing is not null && listing.TryGetValue(name, out bool isDir)) return isDir ? OnDiskKind.Directory : OnDiskKind.File;
        if (File.Exists(path)) return listing is null ? OnDiskKind.File : OnDiskKind.Alias;
        if (Directory.Exists(path)) return listing is null ? OnDiskKind.Directory : OnDiskKind.Alias;
        return OnDiskKind.None;
    }

    /// <summary>The names in a folder (true: a folder), read once; empty when it does not exist, null when it cannot
    /// be listed (then the system's own checks decide).</summary>
    private Dictionary<string, bool>? Listing(string dir)
    {
        if (_listings.TryGetValue(dir, out var known)) return known;
        Dictionary<string, bool>? listing = new(_names);
        try
        {
            if (Directory.Exists(dir))
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
                    listing.TryAdd(e.Name, (e.Attributes & FileAttributes.Directory) != 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            listing = null;
        }
        _listings[dir] = listing;
        return listing;
    }

    /// <summary>
    /// Why nothing may be written below <paramref name="dir"/> when it already exists as a link (a symbolic link, a
    /// junction, or on Windows any reparse point whose tag is a name surrogate), or when it cannot be checked; null
    /// when it is an ordinary folder or does not exist yet. Other reparse points (OneDrive and other cloud
    /// placeholders) are ordinary folders. The destination itself is the user's choice and is never checked.
    /// </summary>
    private string? LinkAt(string dir)
    {
        if (_names.Equals(dir, _root)) return null;
        if (_links.TryGetValue(dir, out var known)) return known;
        string? why;
        try
        {
            var info = new DirectoryInfo(dir);
            if (info.LinkTarget is not null) why = LinkRefused;
            else if (!info.Exists && !File.Exists(dir)) why = null;
            else if (OperatingSystem.IsWindows() && (info.Attributes & FileAttributes.ReparsePoint) != 0 && IsNameSurrogate(dir)) why = LinkRefused;
            else why = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = "a folder in the destination could not be checked for links";
        }
        _links[dir] = why;
        return why;
    }

    private const uint NameSurrogateBit = 0x20000000;

    /// <summary>Windows: the reparse tag of <paramref name="path"/> marks a name surrogate (fails closed: true when the
    /// tag cannot be read).</summary>
    private static bool IsNameSurrogate(string path)
    {
        // The long-path form, so that a folder deep in a long path is checked rather than refused.
        string longPath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
        var handle = FindFirstFileW(longPath, out var data);
        if (handle == new IntPtr(-1)) return true;
        FindClose(handle);
        return (data.dwFileAttributes & (uint)FileAttributes.ReparsePoint) != 0 && (data.dwReserved0 & NameSurrogateBit) != 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow;
        public uint dwReserved0; // the reparse tag when FILE_ATTRIBUTE_REPARSE_POINT is set
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string fileName, out Win32FindData data);

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr handle);
}
