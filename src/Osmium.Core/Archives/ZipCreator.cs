namespace Osmium.Core.Archives;

/// <summary>How <see cref="ZipCreator"/> writes a ZIP file.</summary>
public sealed record ZipOptions
{
    public required string OutputPath { get; init; }
    /// <summary>Replace an existing file at <see cref="OutputPath"/> (only once the new one is complete).</summary>
    public bool Overwrite { get; init; }
    /// <summary>Read the written file back and check every entry before it takes its place.</summary>
    public bool Verify { get; init; } = true;
    /// <summary>Store files that are already compressed (images, video, archives) instead of deflating them.</summary>
    public bool StoreCompressedTypes { get; init; } = true;
    public System.IO.Compression.CompressionLevel Level { get; init; } = System.IO.Compression.CompressionLevel.Optimal;
    public Osmium.Core.Pipeline.PauseGate? Gate { get; init; }
    public Action<ZipProgress>? Progress { get; init; }
}

/// <summary>Progress of a ZIP being written; <see cref="Current"/> is the file being added, if any.</summary>
public sealed record ZipProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, string? Current);

/// <summary>What <see cref="ZipCreator.CreateAsync"/> wrote.</summary>
public sealed record ZipResult
{
    public string OutputPath { get; init; } = "";
    public int FilesWritten { get; init; }
    /// <summary>Of <see cref="FilesWritten"/>, stored without compression.</summary>
    public int FilesStored { get; init; }
    /// <summary>Of <see cref="FilesWritten"/>, compressed with Deflate.</summary>
    public int FilesDeflated { get; init; }
    /// <summary>Files that could not be read and are not in the ZIP.</summary>
    public int LeftOutUnreadable { get; init; }
    /// <summary>Files that changed while they were read and are not in the ZIP.</summary>
    public int LeftOutChanged { get; init; }
    public long BytesIn { get; init; }
    public long BytesOut { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public int LeftOut => LeftOutUnreadable + LeftOutChanged;
}

/// <summary>Writes ZIP files.</summary>
public static class ZipCreator
{
    /// <summary>Writes the scanned files and folders to <see cref="ZipOptions.OutputPath"/>.</summary>
    public static Task<ZipResult> CreateAsync(Osmium.Core.Planning.ScanResult scan, ZipOptions options, CancellationToken ct)
        => throw new NotSupportedException("Writing ZIP files is not available yet.");
}
