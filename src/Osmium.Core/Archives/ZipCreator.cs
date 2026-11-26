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
    /// <remarks>
    /// <para>The ZIP is written to <c>&lt;OutputPath&gt;.partial-&lt;random&gt;</c> in the output folder, read back
    /// and checked entry by entry (<see cref="ZipOptions.Verify"/>), and only then moved into place; a cancellation or
    /// any failure deletes the partial file and touches nothing else. Every folder of the scan is an entry ("dir/"), so
    /// empty folders survive. ZIP files are never encrypted.</para>
    /// <para>A file that cannot be read is left out (<see cref="ZipResult.LeftOutUnreadable"/>). A file whose size or
    /// last-write time changes while it is read is left out too (<see cref="ZipResult.LeftOutChanged"/>): the ZIP is
    /// written again without it, never with a torn copy. The third time files change, the operation fails with
    /// <see cref="Osmium.Core.Planning.SourceChangedException"/>.</para>
    /// <para>Progress: while files are added, <see cref="ZipProgress.Current"/> names the file being added and
    /// BytesDone counts the source bytes read; while the finished ZIP is checked, Current is null, FilesDone equals
    /// FilesTotal and BytesDone counts the checked bytes from 0 again. Reports come at most every 100 ms, and once at
    /// the end. Writing starts again from 0 after a changed file.</para>
    /// </remarks>
    /// <exception cref="IOException">The output is inside a folder being compressed, or exists and
    /// <see cref="ZipOptions.Overwrite"/> is false (nothing is written then).</exception>
    /// <exception cref="InvalidDataException">The written ZIP did not verify (nothing is left behind).</exception>
    public static Task<ZipResult> CreateAsync(Osmium.Core.Planning.ScanResult scan, ZipOptions options, CancellationToken ct)
        => CreateAsync(scan, options, null, ct);

    internal static Task<ZipResult> CreateAsync(Osmium.Core.Planning.ScanResult scan, ZipOptions options, Zip.ZipTestHooks? hooks, CancellationToken ct)
        => Task.Run(() => Zip.ZipWriter.Write(scan, options, hooks, ct), ct);
}
