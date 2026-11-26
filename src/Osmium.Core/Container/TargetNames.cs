using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    private readonly string? _protected;

    /// <param name="root">The destination's full path.</param>
    /// <param name="own">Every entry's own resolved full path (files and folders).</param>
    /// <param name="writtenEarlier">Paths an earlier pass of the same operation wrote: taken, never replaced or skipped.</param>
    /// <param name="protectedFile">The <see cref="FileIdentity(string)"/> of the archive being read: a file in the
    /// destination that is the archive, under whatever path, gets a free name in every mode and is never replaced.</param>
    public TargetNames(string root, StringComparer names, IEnumerable<string> own, IEnumerable<string>? writtenEarlier, string? protectedFile = null)
    {
        _protected = protectedFile;
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

    /// <summary>
    /// Whether the system turned <paramref name="relative"/> into another spelling when it resolved it to
    /// <paramref name="resolved"/>: on Windows a short 8.3 name of an existing file or folder ("LONGFO~1") becomes its
    /// long name. Such an entry may name a file the user knows under its long name, so it never replaces one.
    /// </summary>
    internal static bool SpelledOtherwise(string root, string relative, string resolved)
    {
        string full = Path.GetFullPath(root);
        if (!full.EndsWith(Path.DirectorySeparatorChar)) full += Path.DirectorySeparatorChar;
        return !string.Equals(full + relative.Replace('/', Path.DirectorySeparatorChar), resolved, StringComparison.Ordinal);
    }

    /// <summary>Plans one entry; call in index order (parents before children).</summary>
    /// <param name="otherSpelling">The entry reached its own path under another spelling (<see cref="SpelledOtherwise"/>):
    /// a file there is never replaced, it gets a free name instead.</param>
    public Target Plan(string own, bool isDirectory, OverwriteMode mode, bool otherSpelling = false)
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
            if (kind == OnDiskKind.File && _protected is not null && FileIdentity(path) == _protected)
                path = FreeName(path, folder: false); // the archive being read, reached by another spelling of its path
            else if (kind != OnDiskKind.None)
            {
                switch (mode)
                {
                    case OverwriteMode.Skip: return new(Outcome.Skipped, null, false, false, null);
                    case OverwriteMode.Fail: return new(Outcome.Failed, null, false, false, kind == OnDiskKind.Directory ? "A folder of this name exists." : "File exists.");
                    case OverwriteMode.Overwrite when kind == OnDiskKind.File && !otherSpelling: replaces = true; break;
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
    /// name resumes where it stopped, so many entries of one name cost one pass. A name so long that the number would
    /// take it past 255 characters has its stem cut short (whole characters, the extension kept); names that are the
    /// same once cut share one search.</summary>
    private string FreeName(string path, bool folder)
    {
        string dir = Path.GetDirectoryName(path)!, file = Path.GetFileName(path);
        string stem = folder ? file : Path.GetFileNameWithoutExtension(file), ext = folder ? "" : Path.GetExtension(file);
        if (stem.Length + ext.Length + MaxNumber.Length > MaxName)
        {
            if (ext.Length + MaxNumber.Length >= MaxName) (stem, ext) = (file, ""); // an extension that long is no extension
            int keep = MaxName - ext.Length - MaxNumber.Length;
            if (char.IsHighSurrogate(stem[keep - 1])) keep--;
            stem = stem[..keep];
        }
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
        return _links[dir] = LinkReason(dir);
    }

    /// <summary>
    /// Checked again, without the plan's answers, just before a part file is created and just before it is renamed to
    /// its file's name: why no folder between <paramref name="root"/> and <paramref name="path"/> may be written
    /// through (one became a link while the files were written), or null.
    /// </summary>
    internal static string? LinkOnTheWay(string root, string path)
    {
        string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (string? d = Path.GetDirectoryName(path); d is not null && d.Length > r.Length; d = Path.GetDirectoryName(d))
            if (LinkReason(d) is { } why) return why;
        return null;
    }

    private static string? LinkReason(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (info.LinkTarget is not null) return LinkRefused;
            if (!info.Exists && !File.Exists(dir)) return null;
            if (OperatingSystem.IsWindows() && (info.Attributes & FileAttributes.ReparsePoint) != 0 && IsNameSurrogate(dir)) return LinkRefused;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "a folder in the destination could not be checked for links";
        }
    }

    /// <summary>
    /// What identifies a file whatever path reaches it (a link or junction on the way, a mapped drive, a short name, a
    /// hard link): the volume and file id on Windows, the device and inode on Linux. Null when it cannot be read, or
    /// on other systems.
    /// </summary>
    internal static string? FileIdentity(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FileIdentity(handle);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <inheritdoc cref="FileIdentity(string)"/>
    internal static string? FileIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            var id = new byte[24]; // FILE_ID_INFO: the volume serial number and the 128-bit file id
            if (GetFileInformationByHandleEx(handle, FileIdInfo, id, id.Length)) return "id:" + Convert.ToHexString(id);
            return GetFileInformationByHandle(handle, out var info)
                ? $"index:{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}" : null;
        }
        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
        {
            // struct stat begins with st_dev and st_ino, 8 bytes each, on every 64-bit Linux.
            var stat = new byte[256];
            try { return fstat((int)handle.DangerousGetHandle(), stat) == 0 ? "dev-ino:" + Convert.ToHexString(stat, 0, 16) : null; }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
        }
        return null;
    }

    private const int FileIdInfo = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, [Out] byte[] info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int fstat(int fd, [Out] byte[] stat);

    /// <summary>The longest file name the systems take, and the widest number a free name gets (" (1000000)").</summary>
    private const int MaxName = 255;
    private const string MaxNumber = " (1000000)";

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
