using Microsoft.Win32.SafeHandles;
using Osmium.Core.Archives.Zip;
using Osmium.Core.IO;

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
        FileStat.TryStat(path, out var st) ? (FileStat.Kind(st.Mode), st.Id) : (null, null);

    /// <summary>
    /// Opens <paramref name="src"/> for reading if it is still the file the scan saw: not a link now, nor below a folder
    /// that became one, a regular file of the size and modification time the scan saw, and the same file by its id where
    /// the scan recorded one (by its creation time where the system gives no id). Throws
    /// <see cref="SourceChangedException"/> when it is not, and as <see cref="FileStream"/> does when it cannot be opened.
    /// </summary>
    /// <remarks>Where the scan recorded a file id (Linux), an open whose file has that id has reached the very file the
    /// scan saw, whatever the folders on the way are now, so they are checked only when the id differs or cannot be
    /// read; everywhere else every folder on the way is checked, before and after the open.</remarks>
    public static FileStream Open(SourceFile src)
    {
        if (src.FileId is { } id && OperatingSystem.IsLinux())
        {
            var fs = ZipSources.OpenFile(src.FullPath, out string? refusal) ?? throw Refused(src, refusal);
            if (FileStat.TryStat(fs.SafeFileHandle, out var st) && st.Id == id && FileStat.Kind(st.Mode) is null) return Checked(fs, src, sameId: true);
            // Another file, or one whose id cannot be read: say why as the full check does.
            fs.Dispose();
        }
        return Checked(ZipSources.Open(src.FullPath, src.RelPath, out string? why) ?? throw Refused(src, why), src);
    }

    private static SourceChangedException Refused(SourceFile src, string? refusal) => new(src.FullPath, refusal switch
    {
        ZipSources.IsLink => "became a link after it was scanned (links are not followed)",
        ZipSources.FolderIsLink => "is below a folder that became a link after it was scanned (links are not followed)",
        _ => "is not a regular file (it is a pipe or a device)",
    });

    /// <summary><paramref name="fs"/> when it is still the scanned file by <see cref="Changed"/>; otherwise closes it and
    /// throws.</summary>
    private static FileStream Checked(FileStream fs, SourceFile src, bool sameId = false)
    {
        try
        {
            if (Changed(fs.SafeFileHandle, src, sameId) is { } why) throw new SourceChangedException(src.FullPath, why);
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Checks, once everything is read, that <paramref name="src"/> is still the file the scan saw, by the rules
    /// of <see cref="Open"/>; a file that is gone has changed too.</summary>
    public static void Verify(SourceFile src)
    {
        try
        {
            using (Open(src)) { }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SourceChangedException(src.FullPath, "was removed while it was being archived");
        }
    }

    /// <summary>Why the open file is not the one the scan saw, or null; <paramref name="sameId"/> when its id has already
    /// been found to be the scanned one.</summary>
    private static string? Changed(SafeFileHandle h, SourceFile src, bool sameId)
    {
        const string Replaced = "was replaced by another file after it was scanned";
        if (!sameId && src.FileId is { } id && FileStat.TryStat(h, out var st) && (st.Id != id || FileStat.Kind(st.Mode) is not null)) return Replaced;
        if (RandomAccess.GetLength(h) != src.Size || File.GetLastWriteTimeUtc(h) != src.ModifiedUtc) return "changed after it was scanned";
        // Linux gives no stable creation time; Windows and macOS keep one per file.
        if (src.FileId is null && !OperatingSystem.IsLinux() && File.GetCreationTimeUtc(h) != src.CreatedUtc) return Replaced;
        return null;
    }
}
