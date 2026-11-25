using Osmium.Core.Container;

namespace Osmium.Core.Archives;

/// <summary>Finds out what kind of archive a file is and opens it as an <see cref="IArchiveSource"/>.</summary>
public static class ArchiveSources
{
    /// <summary>The file name extensions of the archives Osmium reads, lower case with the dot.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".osa"];

    /// <summary>The kind of archive the file holds, judged by its content; null when it is none Osmium reads.</summary>
    /// <exception cref="IOException">The file cannot be read (also <see cref="UnauthorizedAccessException"/>).</exception>
    public static ArchiveKind? Detect(string path)
    {
        Span<byte> head = stackalloc byte[Osa.Magic.Length];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return n == head.Length && head.SequenceEqual(Osa.Magic) ? ArchiveKind.Osa : null;
    }

    /// <summary>Opens an archive for listing, testing and extracting.</summary>
    /// <exception cref="NotSupportedException">The file is not an archive Osmium can read.</exception>
    /// <exception cref="PasswordRequiredException">The archive needs a password and none was given.</exception>
    /// <exception cref="WrongPasswordException">The password is wrong.</exception>
    /// <exception cref="EntryLimitException">The archive passes <see cref="ImportLimits.MaxEntries"/> or <see cref="ImportLimits.MaxPathDepth"/>.</exception>
    public static IArchiveSource Open(string path, ArchiveOpenOptions? options = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Detect(path) switch
        {
            ArchiveKind.Osa => OsaArchiveSource.Open(path, options ?? new ArchiveOpenOptions(), ct),
            _ => throw new NotSupportedException("This file is not an archive Osmium can read."),
        };
    }

    /// <summary>
    /// The name of the folder an archive extracts to by default: its file name without the extension ("photos.osa" →
    /// "photos"). When that is not a safe folder name (empty, "." or "..", a device name such as CON, ending in a dot or
    /// space, characters Windows does not allow), it is "&lt;file name&gt; files" ("CON.osa" → "_CON.osa files"), made
    /// safe in the same way. The result always passes <see cref="PathSafety.Validate"/>.
    /// </summary>
    public static string StemOf(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        string stem = System.IO.Path.GetFileNameWithoutExtension(name);
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
