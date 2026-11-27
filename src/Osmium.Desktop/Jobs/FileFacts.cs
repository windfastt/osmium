using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Osmium.Desktop.Jobs;

/// <summary>
/// Which file a handle is open on, and when it last changed in any way. On Windows: the volume, the file id and the
/// change time, which every write and every change of the file's times, attributes or name sets (setting the last-write
/// time back sets it too). On Linux: the device, the inode and the inode's change time (ctime).
/// </summary>
internal readonly record struct FileStamp(ulong Volume, UInt128 Id, long Changed);

/// <summary>What the rules for moving originals ask the file system beyond its listings; tests replace parts of it.</summary>
internal sealed record FileSystemQueries
{
    /// <summary>What this system answers.</summary>
    public static FileSystemQueries System { get; } = new();

    /// <summary>The names of the named data streams of a file or folder.</summary>
    public Func<string, IReadOnlyList<string>> Streams { get; init; } = FileFacts.NamedStreams;

    /// <summary>
    /// File names ignore case here (Windows, macOS), as extraction does when it writes them: two names in one folder
    /// that differ only in case cannot both come back as they are.
    /// </summary>
    public bool NamesIgnoreCase { get; init; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
}

/// <summary>What the file system knows about a file or folder beyond its listing, for deciding whether it may move.</summary>
internal static class FileFacts
{
    /// <summary>The name of the stream Windows keeps the mark of the web in (where a file was downloaded from).</summary>
    public const string ZoneIdentifier = "Zone.Identifier";

    /// <summary>The stamp of the file <paramref name="handle"/> is open on. Throws when the system cannot tell.</summary>
    public static FileStamp StampOf(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            ulong volume;
            UInt128 id;
            var idInfo = new byte[24]; // FILE_ID_INFO: the volume serial number and the 128-bit file id
            if (GetFileInformationByHandleEx(handle, FileIdInfo, idInfo, idInfo.Length))
            {
                volume = BitConverter.ToUInt64(idInfo, 0);
                id = new UInt128(BitConverter.ToUInt64(idInfo, 16), BitConverter.ToUInt64(idInfo, 8));
            }
            else if (GetFileInformationByHandle(handle, out var info))
            {
                volume = info.VolumeSerialNumber;
                id = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            }
            else throw LastError("which file it is could not be told");
            var basic = new byte[40]; // FILE_BASIC_INFO: creation, last-access, last-write and change times, attributes
            if (!GetFileInformationByHandleEx(handle, FileBasicInfo, basic, basic.Length)) throw LastError("when it last changed could not be told");
            return new FileStamp(volume, id, BitConverter.ToInt64(basic, 24));
        }
        if (OperatingSystem.IsLinux())
        {
            var stat = new byte[256]; // struct statx, the same on every Linux
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                if (statx((int)handle.DangerousGetHandle(), "", AtEmptyPath, StatxIno | StatxCtime, stat) != 0) throw LastError("when it last changed could not be told");
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new IOException("this system does not say when a file last changed", e);
            }
            finally
            {
                if (added) handle.DangerousRelease();
            }
            if ((BitConverter.ToUInt32(stat, 0) & (StatxIno | StatxCtime)) != (StatxIno | StatxCtime))
                throw new IOException("its file system does not say when it last changed");
            ulong device = ((ulong)BitConverter.ToUInt32(stat, 136) << 32) | BitConverter.ToUInt32(stat, 140);
            long changed = BitConverter.ToInt64(stat, 96) * 1_000_000_000 + BitConverter.ToUInt32(stat, 104);
            return new FileStamp(device, BitConverter.ToUInt64(stat, 32), changed);
        }
        throw new IOException("this system does not say when a file last changed");
    }

    private static IOException LastError(string what) => new($"{what}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    /// <summary>
    /// The names of the named data streams of a file or folder (such as "Zone.Identifier"), never its main contents.
    /// Only Windows file systems keep them; elsewhere the list is empty. Throws when they cannot be listed on a volume
    /// that keeps them.
    /// </summary>
    public static IReadOnlyList<string> NamedStreams(string path)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var names = new List<string>();
        var data = new byte[StreamDataSize];
        nint find = FindFirstStreamW(LongPath(path), 0, data, 0);
        if (find == InvalidHandle)
        {
            int error = Marshal.GetLastPInvokeError();
            // No stream at all (a folder without named streams), or a volume that keeps none (FAT, exFAT).
            if (error == ErrorHandleEof || !VolumeKeepsStreams(path)) return names;
            throw new IOException($"its streams could not be listed: {new Win32Exception(error).Message}");
        }
        try
        {
            do
            {
                if (NamedStream(data) is { } name) names.Add(name);
            }
            while (FindNextStreamW(find, data));
            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorHandleEof) throw new IOException($"its streams could not be listed: {new Win32Exception(error).Message}");
        }
        finally { FindClose(find); }
        return names;
    }

    /// <summary>The name in a WIN32_FIND_STREAM_DATA (":name:$DATA"), or null for the main contents ("::$DATA").</summary>
    private static string? NamedStream(byte[] data)
    {
        string raw = Encoding.Unicode.GetString(data, 8, data.Length - 8);
        int end = raw.IndexOf('\0');
        if (end >= 0) raw = raw[..end];
        const string Type = ":$DATA";
        if (raw.EndsWith(Type, StringComparison.OrdinalIgnoreCase)) raw = raw[..^Type.Length];
        if (raw.StartsWith(':')) raw = raw[1..];
        return raw.Length == 0 ? null : raw;
    }

    /// <summary>Whether the volume that holds <paramref name="path"/> keeps named streams; true when it cannot be told.</summary>
    private static bool VolumeKeepsStreams(string path)
    {
        var root = new char[Math.Max(path.Length + 2, 261)];
        if (!GetVolumePathNameW(path, root, (uint)root.Length)) return true;
        string volume = new string(root).TrimEnd('\0');
        if (!GetVolumeInformationW(volume, 0, 0, out _, out _, out uint flags, 0, 0)) return true;
        return (flags & FileNamedStreams) != 0;
    }

    /// <summary>The long-path form of a full path, so that a file deep in a long path is checked rather than refused.</summary>
    private static string LongPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;

    private const int FileBasicInfo = 0, FileIdInfo = 18;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxCtime = 0x80, StatxIno = 0x100;

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
    private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);

    private const int ErrorHandleEof = 38;
    private const uint FileNamedStreams = 0x0004_0000;
    private const int StreamDataSize = 8 + (260 + 36) * 2; // WIN32_FIND_STREAM_DATA: the size, then the name
    private static readonly nint InvalidHandle = -1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindFirstStreamW(string fileName, int infoLevel, [Out] byte[] findStreamData, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindNextStreamW(nint findStream, [Out] byte[] findStreamData);

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(nint findFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string fileName, [Out] char[] volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string rootPathName, nint volumeNameBuffer, uint volumeNameSize, out uint volumeSerialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, nint fileSystemNameBuffer, uint fileSystemNameSize);
}
