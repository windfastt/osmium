using Osmium.Core.Container; // OverwriteMode, ExtractProgress, WrongPasswordException, PasswordRequiredException

namespace Osmium.Core.Archives;

/// <summary>The container format of an archive. A compressed TAR (.tar.gz, .tgz…) is <see cref="Tar"/>; a single
/// compressed file that is not a TAR is <see cref="GZip"/>, <see cref="BZip2"/>, <see cref="Xz"/> or <see cref="Zstd"/>.</summary>
public enum ArchiveKind { Osa, Zip, SevenZip, Rar, Tar, GZip, BZip2, Xz, Zstd }

/// <summary>One entry of an archive. <see cref="Index"/> is its position in <see cref="IArchiveSource.Items"/> and
/// identifies it for the life of the source (two entries may map to the same name; the index never does).</summary>
/// <param name="Index">Position in <see cref="IArchiveSource.Items"/>; use it (never the path) to name an entry in
/// <see cref="ArchiveExtractRequest.Indices"/> and to match <see cref="ExtractedFile.Index"/>.</param>
/// <param name="Path">The name inside the archive, '/'-separated, without a trailing '/' for folders, as the archive
/// stores it: it is not checked and may be unsafe (absolute, "..", device names, control characters). Never use it
/// as a file name without <c>PathSafety</c>, and escape it with <c>DisplayText.Escape</c> before showing it.</param>
/// <param name="Size">Unpacked size in bytes; null for folders and when the format does not say.</param>
/// <param name="PackedSize">Stored size in bytes; null when the format does not keep one per entry (solid blocks).</param>
/// <param name="ModifiedUtc">Last write time in UTC (<see cref="DateTimeKind.Utc"/>); null when not stored.</param>
/// <param name="Encrypted">The entry's data is encrypted (for archives with an encrypted file list, every entry).</param>
/// <param name="Method">How the data is stored, for display ("Deflate", "LZMA2", an .osa pipeline); null when unknown
/// or for entries without data.</param>
public sealed record ArchiveItem(int Index, string Path, bool IsDirectory, long? Size, long? PackedSize,
    DateTime? ModifiedUtc, bool Encrypted, string? Method)
{
    /// <summary>A symbolic or hard link, junction or redirection: never followed or created.</summary>
    public bool IsLink { get; init; }
    /// <summary>Where a link points, for display only (unchecked, like <see cref="Path"/>).</summary>
    public string? LinkTarget { get; init; }
    /// <summary>A device, FIFO, sparse or multi-volume part: never extracted.</summary>
    public bool IsSpecial { get; init; }
}

/// <summary>One line of an archive's properties ("Format", ".osa 1.0"). Values that come from the archive (creator,
/// comment) are as stored: escape them with <c>DisplayText.Escape</c> before showing them.</summary>
public sealed record ArchiveDetail(string Label, string Value);

public sealed record ArchiveOpenOptions
{
    /// <summary>Called at most once in the life of the source, on the thread that needs it (callers marshal to the UI).
    /// Returning null throws PasswordRequiredException.</summary>
    /// <remarks>Formats that encrypt the file list (.osa with an encrypted index, 7z with encrypted headers) call it in
    /// Open. Formats that encrypt only entries call it lazily: in the first Extract or Test whose selection holds an
    /// encrypted entry, never in Open and never for a selection without encrypted entries. The answer is kept for the
    /// life of the source. A null answer throws PasswordRequiredException from Open, or from that Extract or Test
    /// before anything is written. Not called for archives without encryption. To try another password, dispose the
    /// source and open it again (rule 5 of <see cref="IArchiveSource"/>).</remarks>
    public Func<string?>? Password { get; init; }
    /// <summary>Limits for listing and, unless the request gives its own, for Extract and Test.</summary>
    public ImportLimits Limits { get; init; } = ImportLimits.Default;
    /// <summary>Archive bytes read while listing (stream formats read the whole file to list it).</summary>
    /// <remarks>Formats that list from an index (.osa, ZIP, 7z) may not report anything.</remarks>
    public IProgress<long>? ListingProgress { get; init; }
    /// <summary>False: a stream format (TAR family, single compressed file) may skip the full listing in Open;
    /// <see cref="IArchiveSource.ItemsComplete"/> is then false and Extract or Test lists as it goes.</summary>
    public bool ListEagerly { get; init; } = true;
}

/// <summary>What to extract and how. Every filter applies together: an entry is extracted when it is selected (by
/// <see cref="Indices"/>, else by <see cref="Select"/>, else every entry) and lies inside <see cref="StripPrefix"/>.
/// Entries left out by these filters are not counted anywhere in the result.</summary>
public sealed record ArchiveExtractRequest
{
    /// <summary>Exact entries by <see cref="ArchiveItem.Index"/>; when set, <see cref="Select"/> is ignored.</summary>
    /// <remarks>A folder's index selects the folder itself, not its contents: add the indices of the entries below
    /// it (or use <see cref="Select"/>). Indices that name no item are ignored.</remarks>
    public IReadOnlySet<int>? Indices { get; init; }
    /// <summary>Null and no <see cref="Indices"/>: everything.</summary>
    public Func<ArchiveItem, bool>? Select { get; init; }
    /// <summary>An archive folder ("docs/img") whose contents go to the destination root; entries outside it are not
    /// extracted. Used by "Extract selected" from a subfolder and by drag-out.</summary>
    /// <remarks>'/'-separated, compared ordinally with the stored paths, a trailing '/' ignored; null or empty: no
    /// prefix. The folder itself is not created again. The remaining relative paths still pass every path rule, so a
    /// prefix can never lead outside the destination.</remarks>
    public string? StripPrefix { get; init; }
    public OverwriteMode Overwrite { get; init; } = OverwriteMode.Rename;
    /// <summary>Called with the full path of an existing file just before it is replaced (the app moves it to the
    /// Recycle Bin). An exception from it fails that one file, which is then not replaced.</summary>
    /// <remarks>Only in <see cref="OverwriteMode.Overwrite"/>, once the new content has been written and verified;
    /// the existing file is replaced only when the call returns, and it may then have been moved away already. May be
    /// called from several threads at once.</remarks>
    public Action<string>? BeforeReplace { get; init; }
    /// <summary>Keep the partial file of an entry that fails verification instead of deleting it.</summary>
    public bool KeepBroken { get; init; }
    public bool RestoreTimes { get; init; } = true;
    public bool RestoreAttributes { get; init; } = true;
    /// <summary>Null: the limits given to Open.</summary>
    public ImportLimits? Limits { get; init; }
    /// <summary>Full paths an earlier pass of the same operation wrote into this destination (the second pass after
    /// "Extract anyway"). They count as written by this run: a target equal to one of them, by the source's name
    /// comparison, gets a free name in every overwrite mode and is never replaced or skipped as existing.</summary>
    /// <remarks>Null or empty: no earlier pass. A source that cannot honour a non-empty set throws
    /// NotSupportedException before writing anything; it never ignores it.</remarks>
    public IReadOnlyCollection<string>? WrittenEarlier { get; init; }
}

/// <summary>A file written by Extract: the entry's <see cref="ArchiveItem.Index"/> and the full path on disk (which
/// differs from the archive name when it was renamed).</summary>
public sealed record ExtractedFile(int Index, string Path);

/// <summary>What Extract or Test did. <see cref="FilesOk"/>, <see cref="FilesFailed"/>, <see cref="SkippedExisting"/>,
/// <see cref="Renamed"/>, <see cref="Unverified"/> and <see cref="NeedNewerVersion"/> count files; the other
/// Skipped counts count every entry refused (files, folders and links alike).</summary>
public sealed record ArchiveExtractResult
{
    /// <summary>Written (Extract) or read back (Test) and verified, or, when the format stores no checksum, written
    /// without error (also counted in <see cref="Unverified"/>).</summary>
    public int FilesOk { get; init; }
    /// <summary>Damaged, wrong checksum, I/O error. Does not include <see cref="NeedNewerVersion"/>.</summary>
    public int FilesFailed { get; init; }
    /// <summary><see cref="OverwriteMode.Skip"/>: the user's choice, not "left out".</summary>
    public int SkippedExisting { get; init; }
    /// <summary>Would leave the destination, device names, absolute paths…</summary>
    public int SkippedUnsafe { get; init; }
    public int SkippedLinks { get; init; }
    /// <summary>Sparse TAR entries, devices, methods Osmium cannot read.</summary>
    public int SkippedUnsupported { get; init; }
    /// <summary>Name collisions and names Windows does not allow: written under another name (see <see cref="Written"/>)
    /// and also counted in <see cref="FilesOk"/>.</summary>
    public int Renamed { get; init; }
    /// <summary>Written, but the format stores no checksum for them.</summary>
    public int Unverified { get; init; }
    /// <summary>Blocks from a newer Osmium.</summary>
    public int NeedNewerVersion { get; init; }
    /// <summary>One line per problem, naming the entry as stored (escape before showing).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
    /// <summary>Problems that did not stop a file from being written (a Mark-of-the-Web that could not be set…).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Every file Extract wrote and kept, in archive order (empty for Test).</summary>
    public IReadOnlyList<ExtractedFile> Written { get; init; } = [];
    /// <summary>Entries the user asked for that are not on disk afterwards for a reason other than their choice.</summary>
    public int LeftOut => FilesFailed + SkippedUnsafe + SkippedLinks + SkippedUnsupported + NeedNewerVersion;
}

/// <summary>
/// An archive opened for reading. Rules every implementation follows (tests check them):
/// 1. The password is checked before any file or folder is written. Formats that encrypt the file list check it in
///    Open; formats that encrypt only entries check it in Extract and Test on the first selected encrypted entry and
///    throw WrongPasswordException before creating anything. The check streams the entry through decryption (and,
///    where the format needs it, decompression) into a discarding sink, under the same limit counters, cancellation
///    and progress as extraction; nothing is buffered in memory. ZIP with ZipCrypto: the one-byte check, then the
///    whole entry decoded and its CRC-32 compared (one extra decode of that entry). AES ZIP: the 2-byte verifier,
///    then the authentication code over the entry's encrypted bytes (one extra read, no decompression). 7z: no
///    verifier exists, so the first encrypted folder is decoded until the end of its first file with a stored CRC
///    (at worst the whole folder; a solid archive is often one folder, so at worst one extra pass over the archive,
///    reported as progress). Encrypted 7z entries in a folder with no stored CRC are extracted only when the password
///    was verified on another folder of the same archive (counted Unverified); otherwise they are refused
///    (SkippedUnsupported, "encrypted without a stored checksum, so the password cannot be checked").
/// 2. Nothing is written outside <c>destination</c>; links are never created or followed.
/// 3. Every limit in <see cref="ImportLimits"/> applies to listing, Test and Extract alike. Test uses the limits given
///    to Open; Extract uses <see cref="ArchiveExtractRequest.Limits"/> when set.
/// 4. Cancellation removes only the partial files of this run.
/// 5. Every exception from Extract or Test (wrong password, a limit, damage, cancellation) leaves the source usable:
///    Items and Details stay valid and later operations run. A password that failed (WrongPasswordException) or was
///    refused (PasswordRequiredException) stays spent: later encrypted operations throw the same exception without
///    calling the callback again. Retrying with another password, or retrying Test with AllowLargeExpansion, means
///    disposing the source and opening it again with new options; Extract can retry on the same source with
///    <see cref="ArchiveExtractRequest.Limits"/>.
/// 6. Progress (<see cref="ExtractProgress"/>): when every selected file's size is known, BytesTotal is their total
///    and BytesDone counts output bytes written or checked. When the total is unknown (a size is null, or the source
///    lists as it goes), BytesTotal is 0, BytesDone counts archive bytes read so far and FilesTotal is 0; callers then
///    show an indeterminate meter and "N MB read so far". An empty selection reports (0, 0) once at the end.
/// </summary>
/// <remarks>
/// Open through <see cref="ArchiveSources.Open"/>. One operation at a time per source; the source keeps the archive
/// file open until it is disposed. Extract creates <c>destination</c> when it is missing; files completed before a
/// cancellation (OperationCanceledException) or a limit exception stay where they are. Limits are checked against
/// declared sizes before anything is written where the format declares them, and again while writing.
/// </remarks>
public interface IArchiveSource : IDisposable
{
    /// <summary>The archive's full path.</summary>
    string Path { get; }
    ArchiveKind Kind { get; }
    /// <summary>For display: "ZIP", "7z", "TAR + gzip", ".osa 1.0".</summary>
    string FormatName { get; }
    /// <summary>The file list or at least one entry is encrypted.</summary>
    bool IsEncrypted { get; }
    /// <summary>The archive file's length in bytes.</summary>
    long FileLength { get; }
    /// <summary>Unpacked size of every file; null when the format does not say.</summary>
    long? TotalSize { get; }
    /// <summary>False when Open skipped the full listing (<see cref="ArchiveOpenOptions.ListEagerly"/>): <see cref="Items"/>
    /// is then empty or partial.</summary>
    bool ItemsComplete { get; }
    /// <summary>Every entry in archive order; <c>Items[i].Index == i</c>.</summary>
    IReadOnlyList<ArchiveItem> Items { get; }
    IReadOnlyList<ArchiveDetail> Details { get; }
    /// <summary>Problems found while opening that did not stop it (a damaged copy that another one replaced…).</summary>
    IReadOnlyList<string> Warnings { get; }
    /// <summary>Extracts the selected entries under <paramref name="destination"/> (a full path).</summary>
    ArchiveExtractResult Extract(string destination, ArchiveExtractRequest request, IProgress<ExtractProgress>? progress, CancellationToken ct);
    /// <summary>Reads and verifies the selected entries (<paramref name="select"/> null: every entry) without writing
    /// anything.</summary>
    ArchiveExtractResult Test(Func<ArchiveItem, bool>? select, IProgress<ExtractProgress>? progress, CancellationToken ct);
}

/// <summary>Raised when output would pass both limits; the caller may ask the user and retry with
/// <see cref="ImportLimits.AllowLargeExpansion"/>. Path and link rules are never lifted.</summary>
public sealed class ExpansionLimitException(double factor, long outputBytes)
    : IOException($"This archive expands to more than {factor:N0} times its size.")
{
    /// <summary>The ratio that was passed (<see cref="ImportLimits.MaxExpansionRatio"/>).</summary>
    public double Factor { get; } = factor;
    /// <summary>The output size that passed it: declared before writing, or written so far.</summary>
    public long OutputBytes { get; } = outputBytes;
}

/// <summary>The destination's volume would keep less free space than <see cref="ImportLimits"/> asks for.</summary>
public sealed class FreeSpaceException(string message) : IOException(message);

/// <summary>More entries than <see cref="ImportLimits.MaxEntries"/>, or a path deeper than
/// <see cref="ImportLimits.MaxPathDepth"/>.</summary>
public sealed class EntryLimitException(string message) : IOException(message);
