using System.Text;
using Osmium.Core.Archives.Foreign;
using Osmium.Core.Container;

namespace Osmium.Core.Archives;

/// <summary>Finds out what kind of archive a file is and opens it as an <see cref="IArchiveSource"/>.</summary>
public static class ArchiveSources
{
    // ZIP names without the UTF-8 flag are in an OEM code page (437, 850, 932…), which .NET has only with this provider.
    static ArchiveSources() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Makes the OEM code pages available (ZIP names); safe to call more than once.</summary>
    internal static void EnsureCodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The file name extensions of the archives Osmium reads, lower case with the dot.</summary>
    public static IReadOnlyList<string> Extensions { get; } =
    [
        ".osa", ".zip", ".7z", ".tar",
        ".tgz", ".tar.gz", ".gz",
        ".tbz", ".tbz2", ".tar.bz2", ".bz2",
        ".txz", ".tar.xz", ".xz",
        ".tzst", ".tar.zst", ".zst",
    ];

    /// <summary>Double extensions whose whole length is the archive type ("photos.tar.gz" → "photos").</summary>
    private static readonly string[] DoubleExtensions = [".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst"];

    /// <summary>The kind of archive the file holds, judged by its content; null when it is none Osmium reads. A TAR
    /// inside gzip, bzip2, xz or Zstandard is <see cref="ArchiveKind.Tar"/>; an .osa with a damaged header is still an
    /// .osa when its footer is valid; the name only breaks one tie (an old-style TAR without the "ustar" mark).</summary>
    /// <exception cref="IOException">The file cannot be read (also <see cref="UnauthorizedAccessException"/>).</exception>
    /// <exception cref="NotSupportedException">A RAR or multi-volume ZIP archive, which this version cannot read.</exception>
    public static ArchiveKind? Detect(string path) => FormatSniffer.Sniff(path, ImportLimits.Default)?.Kind;

    /// <summary>Opens an archive for listing, testing and extracting.</summary>
    /// <exception cref="NotSupportedException">The file is not an archive Osmium can read.</exception>
    /// <exception cref="PasswordRequiredException">The archive needs a password and none was given.</exception>
    /// <exception cref="WrongPasswordException">The password is wrong.</exception>
    /// <exception cref="EntryLimitException">The archive passes <see cref="ImportLimits.MaxEntries"/> or <see cref="ImportLimits.MaxPathDepth"/>.</exception>
    public static IArchiveSource Open(string path, ArchiveOpenOptions? options = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        options ??= new ArchiveOpenOptions();
        var sniff = FormatSniffer.Sniff(path, options.Limits);
        try { return OpenAs(sniff, path, options, ct); }
        catch (OutOfMemoryException)
        {
            // A last resort only: every source checks its counts and memory before it allocates.
            throw new EntryLimitException("Opening this archive needs more memory than Osmium accepts.");
        }
    }

    private static IArchiveSource OpenAs(SniffResult? sniff, string path, ArchiveOpenOptions options, CancellationToken ct)
    {
        return sniff?.Kind switch
        {
            ArchiveKind.Osa => OsaArchiveSource.Open(path, options, ct),
            ArchiveKind.Zip => Foreign.Zip.ZipSource.Open(path, options, ct),
            ArchiveKind.SevenZip => Foreign.SevenZip.SevenZipSource.Open(path, options, ct),
            ArchiveKind.Tar => Foreign.Tar.TarSource.Open(path, sniff.Value.Compression, options, ct),
            ArchiveKind.GZip or ArchiveKind.BZip2 or ArchiveKind.Xz or ArchiveKind.Zstd => SingleStreamSource.Open(path, sniff.Value.Compression, options, ct),
            _ => throw new NotSupportedException("This file is not an archive Osmium can read."),
        };
    }

    /// <summary>
    /// The name of the folder an archive extracts to by default: its file name without the extension ("photos.osa" →
    /// "photos", "photos.tar.gz" → "photos", "x.tgz" → "x"). When that is not a safe folder name (empty, "." or "..", a
    /// device name such as CON, ending in a dot or space, characters Windows does not allow), it is "&lt;file name&gt;
    /// files" ("CON.osa" → "_CON.osa files"), made safe in the same way. The result always passes
    /// <see cref="PathSafety.Validate"/>.
    /// </summary>
    public static string StemOf(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        string? stem = null;
        foreach (var ext in DoubleExtensions)
            if (name.Length > ext.Length && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) stem = name[..^ext.Length];
        stem ??= System.IO.Path.GetFileNameWithoutExtension(name);
        if (IsSafeName(stem)) return stem;
        return MakeSafe(name.Length == 0 ? "files" : name + " files");
    }

    /// <summary>One path component that <see cref="PathSafety.Validate"/> accepts.</summary>
    private static bool IsSafeName(string name) => name is not ("" or "." or "..") && !name.Contains('/') && PathSafety.Validate(name) is null;

    /// <summary>Replaces what <see cref="PathSafety"/> refuses in one component: characters Windows does not allow
    /// become '_', trailing dots and spaces go, and a name that starts with a device name gets a leading '_'.</summary>
    private static string MakeSafe(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] < 32 || chars[i] is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') chars[i] = '_';
        string safe = new string(chars).TrimEnd('.', ' ');
        if (safe.Length == 0) safe = "files";
        if (!IsSafeName(safe)) safe = "_" + safe;
        return IsSafeName(safe) ? safe : "files";
    }
}
