using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Osmium.Core.Archives.Zip;

namespace Osmium.Core.Planning;

/// <summary>
/// Which file the scan saw, and opening it again: without following a link at its name or in a folder on the way, and
/// only while it is still that file.
/// </summary>
internal static class SourceIdentity
{
    /// <summary>
    /// What the scan records about the file at <paramref name="path"/> besides its size and times: what it is when it
    /// is not a regular file ("a pipe", "a device", ...), null when it is one or when that cannot be told without
    /// opening it; and its file id (device and inode) where the system gives one. Linux tells both; elsewhere both are
    /// null.
    /// </summary>
    public static (string? NotRegular, UInt128? Id) Look(string path) =>
        Linux.TryStat(path, out var st) ? (Kind(st.Mode), st.Id) : (null, null);

    /// <summary>
    /// Opens <paramref name="src"/> for reading if it is still the file the scan saw: not a link now, nor below a folder
    /// that became one, a regular file of the size and modification time the scan saw, and the same file by its id where
    /// the scan recorded one (by its creation time where the system gives no id). Throws
    /// <see cref="SourceChangedException"/> when it is not, and as <see cref="FileStream"/> does when it cannot be opened.
    /// </summary>
    public static FileStream Open(SourceFile src)
    {
        var fs = ZipSources.Open(src.FullPath, src.RelPath, out string? refusal);
        if (fs is null)
            throw new SourceChangedException(src.FullPath, refusal switch
            {
                ZipSources.IsLink => "became a link after it was scanned (links are not followed)",
                ZipSources.FolderIsLink => "is below a folder that became a link after it was scanned (links are not followed)",
                _ => "is not a regular file now",
            });
        try
        {
            if (Changed(fs.SafeFileHandle, src) is { } why) throw new SourceChangedException(src.FullPath, why);
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private static string? Changed(SafeFileHandle h, SourceFile src)
    {
        const string Replaced = "was replaced by another file after it was scanned";
        if (src.FileId is { } id && Linux.TryStat(h, out var st) && (st.Id != id || Kind(st.Mode) is not null)) return Replaced;
        if (RandomAccess.GetLength(h) != src.Size || File.GetLastWriteTimeUtc(h) != src.ModifiedUtc) return "changed after it was scanned";
        // Linux gives no stable creation time; Windows and macOS keep one per file.
        if (src.FileId is null && !OperatingSystem.IsLinux() && File.GetCreationTimeUtc(h) != src.CreatedUtc) return Replaced;
        return null;
    }

    private static string? Kind(uint mode) => (mode & 0xF000) switch
    {
        0x8000 => null,
        0x1000 => "a pipe",
        0x2000 or 0x6000 => "a device",
        0xC000 => "a socket",
        0xA000 => "a link",
        0x4000 => "a folder",
        _ => "a special file",
    };

    /// <summary>statx(2), whose layout is the same on every Linux architecture.</summary>
    private static class Linux
    {
        private const int AtFdCwd = -100, AtSymlinkNoFollow = 0x100, AtEmptyPath = 0x1000;
        private const uint StatxType = 0x1, StatxIno = 0x100;
        private static bool missing;

        public readonly record struct Stat(uint Mode, UInt128? Id);

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct Statx
        {
            [FieldOffset(0)] public uint Mask;
            [FieldOffset(28)] public ushort Mode;
            [FieldOffset(32)] public ulong Ino;
            [FieldOffset(136)] public uint DevMajor;
            [FieldOffset(140)] public uint DevMinor;
        }

        /// <summary>The file at <paramref name="path"/> itself, not what a link there points to.</summary>
        public static bool TryStat(string path, out Stat st) => TryStat(AtFdCwd, path, AtSymlinkNoFollow, out st);

        public static bool TryStat(SafeFileHandle h, out Stat st)
        {
            bool added = false;
            try
            {
                h.DangerousAddRef(ref added);
                return TryStat((int)h.DangerousGetHandle(), "", AtEmptyPath, out st);
            }
            finally
            {
                if (added) h.DangerousRelease();
            }
        }

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
}
