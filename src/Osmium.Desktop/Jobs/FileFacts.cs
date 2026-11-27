using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Osmium.Desktop.Jobs;

/// <summary>What the file system knows about a file or folder beyond its listing, for deciding whether it may move.</summary>
internal static class FileFacts
{
    /// <summary>The name of the stream Windows keeps the mark of the web in (where a file was downloaded from).</summary>
    public const string ZoneIdentifier = "Zone.Identifier";

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
