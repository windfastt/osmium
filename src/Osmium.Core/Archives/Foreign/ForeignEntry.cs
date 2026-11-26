using Osmium.Core.Container;

namespace Osmium.Core.Archives.Foreign;

internal enum EntryType { File, Directory, SymbolicLink, HardLink, Special, Anti }

/// <summary>One entry of a foreign archive, with what the guarded writer needs beyond <see cref="ArchiveItem"/>.</summary>
internal sealed class ForeignEntry
{
    public required int Index { get; init; }
    /// <summary>As the archive stores it, '/'-separated, no trailing '/' for folders; unchecked.</summary>
    public required string Path { get; init; }
    public EntryType Type { get; init; }
    public long? Size { get; init; }
    public long? PackedSize { get; init; }
    public DateTime? ModifiedUtc { get; init; }
    public bool Encrypted { get; init; }
    public string? Method { get; init; }
    /// <summary>A link's target as stored (display; for TAR hard links, the archive path of the file it names).</summary>
    public string? LinkTarget { get; init; }
    public bool ReadOnly { get; init; }
    public bool Hidden { get; init; }
    /// <summary>Why this entry cannot be extracted (sparse, device, a method Osmium cannot read…): SkippedUnsupported.</summary>
    public string? Unsupported { get; set; }
    /// <summary>Why this entry is refused before its name is even looked at (ZIP entries that overlap): SkippedUnsafe.</summary>
    public string? Unsafe { get; set; }

    public bool HasData => Type == EntryType.File;

    public ArchiveItem ToItem() => new(Index, Path, Type == EntryType.Directory, Type == EntryType.Directory ? null : Size,
        PackedSize, ModifiedUtc, Encrypted, Method)
    {
        IsLink = Type is EntryType.SymbolicLink or EntryType.HardLink,
        LinkTarget = LinkTarget,
        IsSpecial = Type == EntryType.Special,
    };
}

/// <summary>A file's data as the format decodes it. Reading <see cref="Stream"/> to its end also checks it: a file
/// that does not verify throws there, so the writer never sees a clean end of a wrong file.</summary>
/// <param name="Verified">False when the format stores no checksum for it (TAR, zstd frames without a checksum…).</param>
internal sealed record EntryContent(Stream Stream, bool Verified);

/// <summary>Reading an entry's data failed (damage, wrong password): the entry is already counted and reported; the
/// format decides whether the archive can go on (ZIP: next entry; 7z: next folder; streams: stop).</summary>
internal sealed class EntryDataException(Exception inner) : Exception(inner.Message, inner);

/// <summary>What Extract (the guarded writer) or Test does with each selected entry, in archive order.</summary>
internal interface IEntrySink
{
    /// <param name="relativePath">The entry's path below the destination (after <c>StripPrefix</c>), unchecked.</param>
    /// <param name="open">Opens the entry's data; valid only during the call. Not called for entries that are
    /// refused or skipped, whose data the format then skips (and still counts).</param>
    void Accept(ForeignEntry entry, string relativePath, Func<EntryContent>? open);
    /// <summary>The entry could not be read at all (an earlier entry of a solid block was damaged…).</summary>
    void Fail(ForeignEntry entry, string message);
    /// <summary>The archive is damaged where no single entry can be named (a TAR header, the end of a compressed
    /// stream): reported, and counted as one failed file so the result never reads as complete.</summary>
    void Damaged(string message);
    /// <summary>A note for the result (Test: "these files have no stored checksum").</summary>
    void Warn(string message);
    ArchiveExtractResult Result();
}

/// <summary>Progress by the convention of <see cref="IArchiveSource"/> (rule 6): output bytes against the total when
/// every selected size is known, else archive bytes read with totals of 0.</summary>
internal sealed class ProgressReporter(IProgress<ExtractProgress>? progress, long bytesTotal, int filesTotal, Func<long> archiveBytesRead)
{
    private long _output, _lastTick;
    private int _files;
    private string? _current;

    public bool KnownTotal => bytesTotal > 0 || filesTotal > 0;

    public void Output(long bytes)
    {
        _output += bytes;
        Maybe();
    }

    public void Current(string? name)
    {
        _current = name;
        Maybe(force: true);
    }

    public void FileDone()
    {
        _files++;
        Maybe();
    }

    public void Final()
    {
        if (progress is null) return;
        progress.Report(KnownTotal
            ? new ExtractProgress(bytesTotal, bytesTotal, filesTotal, filesTotal, null)
            : new ExtractProgress(archiveBytesRead(), 0, _files, 0, null));
    }

    private void Maybe(bool force = false)
    {
        if (progress is null) return;
        long now = Environment.TickCount64;
        if (!force && now - _lastTick < 100) return;
        _lastTick = now;
        progress.Report(KnownTotal
            ? new ExtractProgress(Math.Min(_output, bytesTotal), bytesTotal, Math.Min(_files, filesTotal), filesTotal, _current)
            : new ExtractProgress(archiveBytesRead(), 0, _files, 0, _current));
    }
}
