using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Osmium.Core.IO;

/// <summary>
/// What kind of file a name or an open handle is, and its file id, as the system tells it: statx(2) on Linux (one
/// layout on every architecture), fstat(2) on macOS, GetFileType on Windows.
/// </summary>
internal static class FileStat
{
    private const uint TypeMask = 0xF000, RegularType = 0x8000;

    /// <summary>A file's type and permission bits (st_mode), and its id (device and inode) when known.</summary>
    public readonly record struct Stat(uint Mode, UInt128? Id);

    /// <summary>The file at <paramref name="path"/> itself, not what a link there points to. Linux only: false
    /// elsewhere, and when the system cannot tell.</summary>
    public static bool TryStat(string path, out Stat st) => Linux.TryStat(path, out st);

    /// <summary>The file open as <paramref name="h"/>, on Linux and macOS; false elsewhere, and when the system cannot
    /// tell.</summary>
    public static bool TryStat(SafeFileHandle h, out Stat st)
    {
        st = default;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;
        bool added = false;
        try
        {
            h.DangerousAddRef(ref added);
            int fd = (int)h.DangerousGetHandle();
            return OperatingSystem.IsLinux() ? Linux.TryStat(fd, out st) : Mac.TryStat(fd, out st);
        }
        finally
        {
            if (added) h.DangerousRelease();
        }
    }

    /// <summary>
    /// Whether the open file is a regular file: on Linux and macOS by its type (S_ISREG), on Windows by
    /// GetFileType (a disk file, not a pipe or a character device; folders are told by their attributes). Null when the
    /// system cannot tell.
    /// </summary>
    public static bool? IsRegular(SafeFileHandle h)
    {
        if (OperatingSystem.IsWindows()) return GetFileType(h) == FileTypeDisk;
        if (!TryStat(h, out var st)) return null;
        return Kind(st.Mode) switch { null => true, "a special file" => null, _ => false };
    }

    /// <summary>What a file of <paramref name="mode"/> is when it is not a regular file ("a pipe", "a device", ...); null
    /// for a regular file.</summary>
    public static string? Kind(uint mode) => (mode & TypeMask) switch
    {
        RegularType => null,
        0x1000 => "a pipe",
        0x2000 or 0x6000 => "a device",
        0xC000 => "a socket",
        0xA000 => "a link",
        0x4000 => "a folder",
        _ => "a special file",
    };

    /// <summary>Whether <paramref name="mode"/> is a symbolic link's.</summary>
    public static bool IsLink(uint mode) => (mode & TypeMask) == 0xA000;

    /// <summary>What <see cref="OpenIfSame"/> found at the name.</summary>
    public enum Reopen
    {
        /// <summary>The regular file with the id asked for, now open for reading.</summary>
        Opened,
        /// <summary>A link, pipe, device, socket or folder, which was not opened for reading.</summary>
        NotRegular,
        /// <summary>A regular file with another id.</summary>
        Different,
        /// <summary>No path-only open or no /proc here, or the name could not be opened at all.</summary>
        Unavailable,
    }

    /// <summary>
    /// Opens the file at <paramref name="path"/> for reading only if it is the regular file with id <paramref name="id"/>,
    /// and never opens anything else for reading (Linux). The name is first opened as a path only (O_PATH: nothing is
    /// read, no pipe is waited on, no device is started), its type and id are checked on that, and only then is that very
    /// file opened for reading through /proc/self/fd and its id checked again, so nothing can change between the two.
    /// <paramref name="seen"/> is what the path-only open found.
    /// </summary>
    /// <remarks>The open through /proc/self/fd cannot take O_NOFOLLOW: the name there is the kernel's own link to the
    /// open file, which leads to that file and never to another path.</remarks>
    public static Reopen OpenIfSame(string path, UInt128 id, out SafeFileHandle? handle, out Stat seen)
    {
        handle = null;
        seen = default;
        if (!OperatingSystem.IsLinux() || LinuxNoFollow() == 0) return Reopen.Unavailable;
        int pathFd = Linux.Open(path, Linux.PathOnly | LinuxNoFollow() | Linux.CloseOnExec);
        if (pathFd < 0) return Reopen.Unavailable;
        try
        {
            if (!Linux.TryStat(pathFd, out seen)) return Reopen.Unavailable;
            if (Kind(seen.Mode) is not null) return Reopen.NotRegular;
            if (seen.Id != id) return Reopen.Different;
            int fd = Linux.Open("/proc/self/fd/" + pathFd.ToString(System.Globalization.CultureInfo.InvariantCulture), Linux.CloseOnExec);
            if (fd < 0) return Reopen.Unavailable;
            var opened = new SafeFileHandle(fd, ownsHandle: true);
            if (!Linux.TryStat(fd, out var now) || now.Id != id || Kind(now.Mode) is not null)
            {
                opened.Dispose();
                return Reopen.Different;
            }
            handle = opened;
            return Reopen.Opened;
        }
        finally
        {
            Linux.Close(pathFd);
        }
    }

    /// <summary>O_NOFOLLOW on this Linux architecture; 0 where its value is not known.</summary>
    public static int LinuxNoFollow() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm or Architecture.Armv6 or Architecture.Arm64 or Architecture.Ppc64le => 0x8000,
        Architecture.X86 or Architecture.X64 or Architecture.S390x or Architecture.LoongArch64 or Architecture.RiscV64 => 0x20000,
        _ => 0,
    };

    private const uint FileTypeDisk = 0x0001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    private static class Linux
    {
        private const int AtFdCwd = -100, AtSymlinkNoFollow = 0x100, AtEmptyPath = 0x1000;
        private const uint StatxType = 0x1, StatxIno = 0x100;
        /// <summary>O_PATH and O_CLOEXEC, the same on every architecture above (O_RDONLY is 0).</summary>
        public const int PathOnly = 0x200000, CloseOnExec = 0x80000;
        private static bool missing;

        public static int Open(string path, int flags)
        {
            try
            {
                return open(path, flags);
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
                return -1;
            }
        }

        public static void Close(int fd) => close(fd);

        [DllImport("libc", SetLastError = true)]
        private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct Statx
        {
            [FieldOffset(0)] public uint Mask;
            [FieldOffset(28)] public ushort Mode;
            [FieldOffset(32)] public ulong Ino;
            [FieldOffset(136)] public uint DevMajor;
            [FieldOffset(140)] public uint DevMinor;
        }

        public static bool TryStat(string path, out Stat st) => TryStat(AtFdCwd, path, AtSymlinkNoFollow, out st);

        public static bool TryStat(int fd, out Stat st) => TryStat(fd, "", AtEmptyPath, out st);

        private static bool TryStat(int dirfd, string path, int flags, out Stat st)
        {
            st = default;
            if (!OperatingSystem.IsLinux() || missing) return false;
            Statx buf;
            try
            {
                if (statx(dirfd, path, flags, StatxType | StatxIno, out buf) != 0) return false;
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
                missing = true; // a C library without statx: no kinds and no ids, as on other systems
                return false;
            }
            if ((buf.Mask & StatxType) == 0) return false;
            UInt128? id = (buf.Mask & StatxIno) == 0 ? null : (UInt128)((ulong)buf.DevMajor << 32 | buf.DevMinor) << 64 | buf.Ino;
            st = new Stat(buf.Mode, id);
            return true;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx buf);
    }

    /// <summary>fstat(2) with 64-bit inodes, the only kind on Apple silicon and the "$INODE64" one on Intel: st_dev, then
    /// st_mode, then st_ino.</summary>
    private static class Mac
    {
        private static bool missing;

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct StatBuf
        {
            [FieldOffset(0)] public int Dev;
            [FieldOffset(4)] public ushort Mode;
            [FieldOffset(8)] public ulong Ino;
        }

        public static bool TryStat(int fd, out Stat st)
        {
            st = default;
            if (missing) return false;
            StatBuf buf;
            try
            {
                int r = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? fstat64(fd, out buf) : fstat(fd, out buf);
                if (r != 0) return false;
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
                missing = true;
                return false;
            }
            st = new Stat(buf.Mode, (UInt128)(uint)buf.Dev << 64 | buf.Ino);
            return true;
        }

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int fstat(int fd, out StatBuf buf);

        [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
        private static extern int fstat64(int fd, out StatBuf buf);
    }
}
