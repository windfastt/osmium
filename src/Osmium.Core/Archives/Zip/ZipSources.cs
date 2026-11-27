using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Osmium.Core.IO;

namespace Osmium.Core.Archives.Zip;

/// <summary>
/// What may go into a ZIP file: names that every system extracts where they say, and only the regular files the scan
/// found, opened without following a link that has taken a file's place since.
/// </summary>
internal static class ZipSources
{
    /// <summary>A name that cannot go into a ZIP file: why, and how many of its characters reach the part at fault
    /// (the whole name, or a folder above it).</summary>
    internal readonly record struct NameProblem(string Reason, int Length);

    /// <summary>
    /// Why <paramref name="name"/> (parts separated by '/', no final '/') cannot be an entry name: a part that is empty,
    /// "." or "..", or that holds a control character, '\' or ':' (which extract outside the destination, or to a
    /// drive or a stream, on Windows), text that is not valid Unicode, or a name too long for a ZIP header. Null when
    /// it can be one.
    /// </summary>
    public static NameProblem? CheckName(string name, bool folder)
    {
        int start = 0, bytes = 0;
        while (true)
        {
            int end = name.IndexOf('/', start);
            if (end < 0) end = name.Length;
            var part = name.AsSpan(start, end - start);
            if (PartProblem(part) is { } why) return new NameProblem(why, end);
            // A ZIP header holds at most 65,535 bytes of name; a folder's entry name ends with '/'.
            bytes += (start > 0 ? 1 : 0) + System.Text.Encoding.UTF8.GetByteCount(part);
            if (bytes + (end < name.Length || folder ? 1 : 0) > ushort.MaxValue) return new NameProblem("it is too long for a ZIP file", end);
            if (end == name.Length) return null;
            start = end + 1;
        }
    }

    private static string? PartProblem(ReadOnlySpan<char> part)
    {
        if (part.IsEmpty) return "a part of it is empty";
        if (part is "." or "..") return $"a part of it is '{part}'";
        for (int i = 0; i < part.Length; i++)
        {
            char c = part[i];
            if (c < 32 || c == 127) return "it contains a control character";
            if (c == '\\') return @"it contains '\'";
            if (c == ':') return "it contains ':'";
            if (char.IsHighSurrogate(c) && i + 1 < part.Length && char.IsLowSurrogate(part[i + 1])) i++;
            else if (char.IsSurrogate(c)) return "it is not valid Unicode";
        }
        return null;
    }

    internal const string IsLink = "it is a link now, so it was left out (links are not followed)";
    internal const string FolderIsLink = "a folder above it is a link now, so it was left out (links are not followed)";
    internal const string NotRegular = "it is not a regular file, so it was left out";

    /// <summary>
    /// Opens the file at <paramref name="path"/> (<paramref name="name"/> in the ZIP) for reading without following a
    /// link at its name or in a folder between it and the folder that was scanned. Returns null with the reason in
    /// <paramref name="refusal"/> when it, or such a folder, is a link, or when it is not a regular file (a folder, a
    /// pipe, a device); throws as <see cref="FileStream"/> does when it cannot be opened.
    /// </summary>
    public static FileStream? Open(string path, string name, out string? refusal)
    {
        // Before the file is opened, and again once it is: a folder that became a link in between is found too.
        if (FolderOnTheWayIsLink(path, name)) { refusal = FolderIsLink; return null; }
        var fs = OpenFile(path, out refusal);
        if (fs is not null && FolderOnTheWayIsLink(path, name))
        {
            fs.Dispose();
            refusal = FolderIsLink;
            return null;
        }
        return fs;
    }

    /// <summary>Whether a folder between <paramref name="path"/> and the folder that was scanned (the first part of
    /// <paramref name="name"/>, which may be a link the user chose) is a link or another reparse point; the scan
    /// followed none.</summary>
    internal static bool FolderOnTheWayIsLink(string path, string name)
    {
        int inside = name.Count(c => c == '/') - 1;
        string? folder = Path.GetDirectoryName(path);
        for (int i = 0; i < inside && folder is not null; i++, folder = Path.GetDirectoryName(folder))
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static FileStream? OpenFile(string path, out string? refusal)
    {
        refusal = null;
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows()) handle = OpenWindows(path);
        else if (UnixFlags() is { } flags)
        {
            int fd = open(path, flags);
            if (fd < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == (OperatingSystem.IsMacOS() ? 62 : 40)) { refusal = IsLink; return null; } // ELOOP
                string message = Marshal.GetPInvokeErrorMessage(errno);
                throw errno switch
                {
                    1 or 13 => new UnauthorizedAccessException(message), // EPERM, EACCES
                    2 => new FileNotFoundException(message, path),
                    _ => new IOException(message),
                };
            }
            handle = new SafeFileHandle(fd, ownsHandle: true);
        }
        else
        {
            if (new FileInfo(path).LinkTarget is not null) { refusal = IsLink; return null; }
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        }
        try
        {
            // What the system says the open file is: a device can seek like a file and read without end.
            if (FileStat.IsRegular(handle) is false) refusal = NotRegular;
            else
            {
                var attributes = File.GetAttributes(handle);
                if ((attributes & FileAttributes.ReparsePoint) != 0) refusal = IsLink;
                else if ((attributes & FileAttributes.Directory) != 0) refusal = NotRegular;
                else
                {
                    var fs = new FileStream(handle, FileAccess.Read, 1 << 16);
                    if (fs.CanSeek) return fs;
                    fs.Dispose();
                    refusal = NotRegular;
                    return null;
                }
            }
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        handle.Dispose();
        return null;
    }

    /// <summary>open(2) flags for reading without following a link at the name and without waiting on a pipe, where
    /// their values are known; null elsewhere.</summary>
    private static int? UnixFlags()
    {
        if (OperatingSystem.IsMacOS()) return 0x100 | 0x4 | 0x1000000; // O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC
        if (!OperatingSystem.IsLinux()) return null;
        int noFollow = FileStat.LinuxNoFollow();
        return noFollow == 0 ? null : noFollow | 0x800 | 0x80000; // O_NONBLOCK | O_CLOEXEC
    }

    private static SafeFileHandle OpenWindows(string path)
    {
        const uint GenericRead = 0x80000000, OpenExisting = 3;
        const uint OpenReparsePoint = 0x00200000, SequentialScan = 0x08000000;
        // The long-path form, so that a file deep in a long path opens as FileStream would open it.
        string longPath = path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
        var handle = CreateFileW(longPath, GenericRead, (uint)(FileShare.ReadWrite | FileShare.Delete), IntPtr.Zero, OpenExisting,
            OpenReparsePoint | SequentialScan, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw Marshal.GetExceptionForHR(unchecked((int)0x80070000) | error) ?? new IOException(Marshal.GetPInvokeErrorMessage(error));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);
}
