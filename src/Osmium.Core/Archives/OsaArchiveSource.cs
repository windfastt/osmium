using Osmium.Core.Container;
using Osmium.Core.Crypto;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Archives;

/// <summary>
/// An .osa archive through the common interface: <see cref="OsaReader"/> lists it and <see cref="Extractor"/> tests and
/// extracts it, verifying every file against its stored hash. The file list of an encrypted archive is encrypted, so
/// the password is checked when it opens, before anything is written.
/// </summary>
public sealed class OsaArchiveSource : IArchiveSource
{
    private readonly ImportLimits _limits;
    private readonly Dictionary<ArchiveEntry, int> _indexOf = [];

    private OsaArchiveSource(OsaReader reader, ImportLimits limits)
    {
        Reader = reader;
        _limits = limits;
        var items = new List<ArchiveItem>(reader.Entries.Count);
        for (int i = 0; i < reader.Entries.Count; i++)
        {
            var e = reader.Entries[i];
            _indexOf[e] = i;
            string? method = e.Segments.Count > 0 ? reader.Blocks[e.Segments[0].Frame].Descriptor.Spec.ToString() : null;
            items.Add(new ArchiveItem(i, e.Path, e.IsDirectory, e.IsDirectory ? null : e.Size, null, e.ModifiedUtc, reader.Header.Encrypted, method));
        }
        Items = items;
        Details = Describe(reader);
    }

    /// <summary>The archive's reader, for what only .osa has (its blocks, the planner log).</summary>
    public OsaReader Reader { get; }

    public string Path => Reader.Path;
    public ArchiveKind Kind => ArchiveKind.Osa;
    /// <summary>The archive's own container version, ".osa 1.0" or ".osa 1.1".</summary>
    public string FormatName => $".osa {Osa.Major}.{Reader.Header.Minor}";
    public bool IsEncrypted => Reader.Header.Encrypted;
    public long FileLength => Reader.FileLength;
    public long? TotalSize => Reader.TotalUnpacked;
    public bool ItemsComplete => true;
    public IReadOnlyList<ArchiveItem> Items { get; }
    public IReadOnlyList<ArchiveDetail> Details { get; }
    public IReadOnlyList<string> Warnings => Reader.Warnings;

    /// <summary>
    /// Opens an .osa archive (<see cref="ArchiveSources.Open"/> does this for .osa files). The key derivation of an
    /// encrypted archive may use the memory limit of <see cref="ArchiveOpenOptions.Limits"/>; above it, or above the work
    /// of the strongest preset, it throws <see cref="KdfMemoryLimitException"/> or <see cref="KdfCostException"/>
    /// before the password is asked for (the caller may ask the user and open again with a higher memory limit).
    /// </summary>
    public static OsaArchiveSource Open(string path, ArchiveOpenOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var reader = OsaReader.Open(path, new OsaOpenOptions
        {
            Password = options.Password,
            KdfMemoryLimit = options.Limits.MemoryBudgetBytes,
            Protection = options.AllowWeakProtection ? ProtectionPolicy.AllowWeak : ProtectionPolicy.Default,
            Cancellation = ct,
            // Checked while the file list is decoded, before anything is allocated for an entry over the limits.
            IndexLimits = new IndexLimits { MaxEntries = options.Limits.MaxEntries, MaxPathDepth = options.Limits.MaxPathDepth },
        });
        try
        {
            return new OsaArchiveSource(reader, options.Limits);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    public ArchiveExtractResult Extract(string destination, ArchiveExtractRequest request, IProgress<ExtractProgress>? progress, CancellationToken ct)
    {
        var limits = request.Limits ?? _limits;
        var selection = Selection(request.Indices, request.Select);
        var map = PrefixMap(request.StripPrefix);
        var chosen = Reader.Entries.Where(e => !e.IsDirectory && (selection?.Invoke(e) ?? true) && (map is null || map(e.Path) is not null)).ToList();
        long output = chosen.Sum(e => e.Size);
        CheckExpansion(output, limits);
        CheckFreeSpace(destination, output, limits);
        var result = Extractor.Extract(Reader, destination, selection, new ExtractOptions
        {
            Overwrite = request.Overwrite,
            RestoreTimes = request.RestoreTimes,
            RestoreAttributes = request.RestoreAttributes,
            KeepBroken = request.KeepBroken,
            MemoryBudget = limits.MemoryBudgetBytes,
            MapPath = map,
            BeforeReplace = request.BeforeReplace,
            WrittenEarlier = request.WrittenEarlier,
        }, progress, ct);
        return Map(result);
    }

    public ArchiveExtractResult Test(Func<ArchiveItem, bool>? select, IProgress<ExtractProgress>? progress, CancellationToken ct)
    {
        var selection = Selection(null, select);
        var chosen = Reader.Entries.Where(e => !e.IsDirectory && (selection?.Invoke(e) ?? true)).ToList();
        CheckExpansion(chosen.Sum(e => e.Size), _limits);
        var result = Extractor.Test(Reader, selection, new ExtractOptions { MemoryBudget = _limits.MemoryBudgetBytes }, progress, ct);
        return Map(result);
    }

    public void Dispose() => Reader.Dispose();

    private ArchiveExtractResult Map(ExtractResult r)
    {
        int newer = r.FilesNeedingNewerVersion;
        return new ArchiveExtractResult
        {
            FilesOk = r.FilesOk,
            // Damaged, an I/O error, a refused replacement: every failed file that is not waiting for a newer Osmium.
            FilesFailed = Math.Max(0, r.FilesFailed - newer),
            SkippedExisting = r.FilesSkipped,
            SkippedUnsafe = r.SkippedUnsafe,
            Renamed = r.Renamed,
            NeedNewerVersion = newer,
            Errors = r.Errors,
            Warnings = r.Warnings,
            Written = [.. r.Written.Select(w => new ExtractedFile(_indexOf[w.Entry], w.Path))],
        };
    }

    private Func<ArchiveEntry, bool>? Selection(IReadOnlySet<int>? indices, Func<ArchiveItem, bool>? select) =>
        indices is not null ? e => indices.Contains(_indexOf[e])
        : select is not null ? e => select(Items[_indexOf[e]])
        : null;

    /// <summary>"docs/img": "docs/img/a.png" → "a.png"; entries outside the folder, and the folder itself, map to null.</summary>
    private static Func<string, string?>? PrefixMap(string? prefix)
    {
        prefix = prefix?.TrimEnd('/');
        if (string.IsNullOrEmpty(prefix)) return null;
        string folder = prefix + "/";
        return p => p.Length > folder.Length && p.StartsWith(folder, StringComparison.Ordinal) ? p[folder.Length..] : null;
    }

    private void CheckExpansion(long output, ImportLimits limits)
    {
        if (!limits.AllowLargeExpansion && output > limits.ExpansionFloorBytes && output > limits.MaxExpansionRatio * FileLength)
            throw new ExpansionLimitException(limits.MaxExpansionRatio, output);
    }

    private static void CheckFreeSpace(string destination, long output, ImportLimits limits)
    {
        if (output <= 0) return;
        long free, size;
        try
        {
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(destination))!);
            if (!drive.IsReady) return;
            free = drive.AvailableFreeSpace;
            size = drive.TotalSize;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return; } // a volume that does not say
        double reserve = Math.Max(limits.FreeSpaceReserveBytes, limits.FreeSpaceReserveFraction * size);
        if ((double)free - output < reserve)
            throw new FreeSpaceException($"Not enough free space: the files need {Bytes(output)} and {Bytes(reserve >= long.MaxValue ? long.MaxValue : (long)reserve)} must stay free, but the drive has {Bytes(free)} free.");
    }

    /// <summary>The archive's properties, as the Archives page shows them. "Spare copy of the file list" is there for
    /// archives that have one and for archives of container 1.0, which never have one.</summary>
    private static List<ArchiveDetail> Describe(OsaReader r)
    {
        var details = Properties(r);
        string? spare = r.SpareCopyStatus switch
        {
            SpareCopyStatus.Present => "yes",
            SpareCopyStatus.Damaged => "damaged (the archive is complete; Test says more)",
            SpareCopyStatus.Newer => "made by a newer version of Osmium (this version does not use it)",
            _ => r.Header.Minor == 0 ? "no (archives of Osmium 0.1 have none)" : null,
        };
        if (spare is not null) details.Insert(1, new("Spare copy of the file list", spare));
        return details;
    }

    private static List<ArchiveDetail> Properties(OsaReader r) =>
    [
        new("Format", $".osa {Osa.Major}.{r.Header.Minor}"),
        new("Created by", r.Creator ?? "?"),
        new("Profile", r.Index.MetaString("profile") ?? "?"),
        new("Files", $"{r.Entries.Count(e => !e.IsDirectory):N0}"),
        new("Folders", $"{r.Entries.Count(e => e.IsDirectory):N0}"),
        new("Original size", Bytes(r.TotalUnpacked)),
        new("Archive size", Bytes(r.FileLength)),
        new("Ratio", $"{(double)r.FileLength / Math.Max(1, r.TotalUnpacked):P2}"),
        new("Extraction", $"~{Duration(TimeSpan.FromSeconds(r.EstimatedDecodeSeconds))} single-core"),
        new("Memory per block", $"up to {Bytes(r.MaxBlockDecodeMemory)}"),
        new("Encrypted", r.Header.Encrypted ? "yes" : "no"),
        // An open source has checked the password and authenticated the file list.
        .. r.Header.Encryption is { } e
            ? new ArchiveDetail[]
            {
                new("Encryption", "AES-256-GCM, key from " + e.Kdf.KeyText()), new("Password", "verified"),
                new("Exact sizes hidden", r.Header.PaddedBlocks ? "yes" : "no"),
            }
            : [],
        new("Experimental features", r.Header.Experimental ? "yes" : "no"),
    ];

    private static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1 << 20 => $"{b / 1024.0:F1} KB",
        < 1 << 30 => $"{b / 1048576.0:F1} MB",
        _ => $"{b / 1073741824.0:F2} GB",
    };

    private static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:F0}s" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m {t.Seconds:D2}s" : $"{(int)t.TotalHours}h {t.Minutes:D2}m";
}
