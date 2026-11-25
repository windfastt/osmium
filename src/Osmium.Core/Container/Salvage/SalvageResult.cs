using System.Text;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Container;

/// <summary>Options for <see cref="Salvage.Run"/>.</summary>
public sealed record SalvageOptions
{
    /// <summary>What to do when a recovered file's name already exists in the destination.</summary>
    public OverwriteMode Overwrite { get; init; } = OverwriteMode.Rename;
    public bool RestoreTimes { get; init; } = true;
    public bool RestoreAttributes { get; init; } = true;
    public int MaxParallelBlocks { get; init; } = Math.Max(1, Environment.ProcessorCount);
    public long MemoryBudget { get; init; } = MemoryBudgets.Default();
    /// <summary>
    /// When the recovered index describes more than this many times the data its blocks hold (heavy deduplication or
    /// a decompression bomb), or without an index the block files would be this many times the archive's size, nothing
    /// is written and <see cref="SalvageResult.Refused"/> says why.
    /// </summary>
    public double MaxExpansion { get; init; } = double.PositiveInfinity;
    /// <summary>Write nothing when what would be written does not fit in the destination's free space.</summary>
    public bool CheckFreeSpace { get; init; }
    /// <summary>Free bytes on the volume of a path, or null when unknown (replaced in tests).</summary>
    internal Func<string, long?>? FreeSpaceProbe { get; init; }
    public MethodRegistry Registry { get; init; } = MethodRegistry.Default;
    /// <summary>Called from worker threads, at most every 100 ms.</summary>
    public Action<SalvageProgress>? Progress { get; init; }
    /// <summary>Without the index, at most this many block files besides one per block number.</summary>
    internal int MaxExtraBlockFiles { get; init; } = 10_000;
    /// <summary>The Mark-of-the-Web to give written files instead of the archive's own (tests).</summary>
    internal string? Zone { get; init; }
    /// <summary>Writes the Mark-of-the-Web instead of <see cref="Extractor.WriteZoneIdentifier"/> (tests).</summary>
    internal Func<string, string, string?>? ZoneWriter { get; init; }
}

public sealed record SalvageProgress(string Phase, long Done, long Total);

public enum SalvageFrameState
{
    /// <summary>Header and body check out; once decoded, so does the content hash.</summary>
    Verified,
    /// <summary>An intact copy of a frame that was already found (a duplicated region).</summary>
    Duplicate,
    /// <summary>The frame header checks out but the frame does not (or the file ends inside it).</summary>
    Damaged,
    /// <summary>Intact, but a newer version of Osmium made it: this version cannot decode it. Not damage.</summary>
    NeedsNewerVersion,
    /// <summary>Encrypted, and no key could be derived (damaged header) or confirmed.</summary>
    NotDecrypted,
    /// <summary>Not part of this archive: it does not match the index, or it is of the wrong kind.</summary>
    Ignored,
}

/// <summary>A frame header found by the scan, and what became of the frame.</summary>
public sealed class SalvageFrame
{
    public long Offset { get; init; }
    public ulong Number { get; init; }
    public bool IsIndex { get; init; }
    public bool Encrypted { get; init; }
    public long BodyLength { get; init; }
    public long FrameLength => Osa.FrameHeaderSize + BodyLength;
    /// <summary>Set when the frame lies in the bytes claimed by a damaged frame at this offset (it may belong to an
    /// archive stored in that frame, or follow a region that is missing from the file).</summary>
    public long? EnclosingOffset { get; init; }
    /// <summary>Set when the index of another archive, at this offset, lists the frame (an archive stored in a block).</summary>
    public long? ForeignIndexOffset { get; internal set; }
    /// <summary>The frame cannot be this archive's: it lies in a damaged frame's bytes or another archive lists it.</summary>
    internal bool Doubtful => EnclosingOffset is not null || ForeignIndexOffset is not null;

    public SalvageFrameState State { get; internal set; }
    /// <summary>Why the frame is not usable, e.g. "payload hash mismatch" or, for a newer version, "it uses method id 8".</summary>
    public string? Problem { get; internal set; }
    public NewerVersionException? NewerVersion { get; private set; }
    public string? Pipeline { get; internal set; }
    public long RawSize { get; internal set; } = -1;
    public long PayloadLength { get; internal set; } = -1;
    public UInt128? RawHash { get; internal set; }
    public ulong? PayloadHash { get; internal set; }
    /// <summary>The file written for this block when the index was lost, relative to the destination.</summary>
    public string? Output { get; internal set; }
    /// <summary>Why the block file of this verified frame was not written, when it was not.</summary>
    public string? NotWritten { get; internal set; }

    public string Name => IsIndex ? "index" : $"block {Number}";

    /// <summary>The frame's bytes check out (header, then CRC or tag and payload hash).</summary>
    internal bool Intact { get; set; }
    /// <summary>Decoded, with every stage size and the content hash verified.</summary>
    internal bool Decoded { get; set; }
    internal byte[]? DescriptorBytes { get; set; }
    internal BlockDescriptor? Descriptor { get; set; }

    internal void Reject(SalvageFrameState state, string? problem)
    {
        State = state;
        Problem = problem;
    }

    internal void NeedsNewerVersion(NewerVersionException e)
    {
        State = SalvageFrameState.NeedsNewerVersion;
        NewerVersion = e;
        Problem = "it uses " + e.Feature;
    }

    /// <summary>Two intact frames with the same number, length, pipeline and hashes hold the same block.</summary>
    internal bool SameContent(SalvageFrame other) =>
        Intact && other.Intact && IsIndex == other.IsIndex && Number == other.Number && BodyLength == other.BodyLength
        && RawHash == other.RawHash && PayloadHash == other.PayloadHash
        && DescriptorBytes.AsSpan().SequenceEqual(other.DescriptorBytes);
}

public enum SalvageBlockState
{
    Verified,
    Damaged,
    Missing,
    NeedsNewerVersion,
    NotDecrypted,
    /// <summary>Verified, but its block file was not written (one was already there, or writing failed).</summary>
    NotWritten,
}

/// <summary>The outcome for one block number (all of the index's blocks, or every number seen when it was lost).</summary>
public sealed record SalvageBlock(int Number, SalvageBlockState State, string? Problem, SalvageFrame? Frame);

/// <summary>An entry that was not recovered, and why.</summary>
public sealed record SalvageLoss(string Path, string Reason);

/// <summary>An entry written under another name, because its own name was taken in the destination.</summary>
public sealed record SalvageRename(string Path, string WrittenAs);

public sealed class SalvageResult
{
    public required string ArchivePath { get; init; }
    public required string Destination { get; init; }
    public long ArchiveLength { get; init; }
    /// <summary>"intact", "damaged (CRC mismatch)", …</summary>
    public required string Header { get; init; }
    public bool HeaderIntact { get; init; }
    public string? Creator { get; init; }
    public bool Encrypted { get; init; }
    /// <summary>Why encrypted frames could not be decrypted, when they could not.</summary>
    public string? KeyProblem { get; init; }
    /// <summary>Every frame header found, in file order.</summary>
    public required IReadOnlyList<SalvageFrame> Frames { get; init; }
    public required IReadOnlyList<SalvageBlock> Blocks { get; init; }
    public bool IndexRecovered => IndexFrame is not null;
    public SalvageFrame? IndexFrame { get; init; }
    public string? IndexProblem { get; init; }
    /// <summary>File entries in the recovered index.</summary>
    public int FilesTotal { get; init; }
    public int FilesRecovered { get; init; }
    /// <summary>Not written because a file of that name was already there (<see cref="OverwriteMode.Skip"/>).</summary>
    public int FilesSkipped { get; init; }
    /// <summary>Entries lost to damage or missing data, unsafe paths or write errors. Directories end with '/'.</summary>
    public required IReadOnlyList<SalvageLoss> Lost { get; init; }
    /// <summary>Files whose blocks are intact but need a newer version of Osmium to decode: not damaged, not recovered.</summary>
    public required IReadOnlyList<SalvageLoss> NeedNewerVersion { get; init; }
    /// <summary>Everything written, relative to the destination with '/' separators (files or block files).</summary>
    public required IReadOnlyList<string> Written { get; init; }
    /// <summary>Files written under another name (relative to the destination) because theirs was taken.</summary>
    public required IReadOnlyList<SalvageRename> Renamed { get; init; }
    public long BytesRecovered { get; init; }
    /// <summary>Bytes that are not part of the header, an intact frame or the footer: damage, garbage, or both.</summary>
    public long UnrecognizedBytes { get; init; }
    public required IReadOnlyList<(long Start, long End)> UnrecognizedRanges { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    /// <summary>Problems that did not stop a file from being written, e.g. a Mark-of-the-Web that could not be written.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
    /// <summary>The salvage report written when the index was lost.</summary>
    public string? ReportPath { get; internal set; }
    /// <summary>Set when nothing was written on purpose (see <see cref="SalvageOptions.MaxExpansion"/>).</summary>
    public string? Refused { get; init; }

    public int BlocksVerified => Blocks.Count(b => b.State == SalvageBlockState.Verified);
    /// <summary>Intact frames that were set aside: not this archive's (another archive's index lists them, or this
    /// archive's index does not), or not written (too many extra block files).</summary>
    public int FramesSetAside => Frames.Count(f => f.Intact && f.State == SalvageFrameState.Ignored);
    /// <summary>Every file of the archive was recovered and every intact frame found is accounted for (damage that cost
    /// no data does not count).</summary>
    public bool Complete => Refused is null && IndexRecovered && Lost.Count == 0 && NeedNewerVersion.Count == 0 && FramesSetAside == 0;
    /// <summary>0 everything recovered, 2 something could not be recovered, 1 refused.</summary>
    public int ExitCode => Refused is not null ? 1 : Complete ? 0 : 2;

    public IEnumerable<string> Summary()
    {
        yield return $"Archive: {ArchivePath} ({Size(ArchiveLength)})";
        yield return $"Header:  {Header}{(Creator is null ? "" : $", created by {Creator}")}{(Encrypted ? ", encrypted" : "")}";
        if (KeyProblem is not null) yield return $"         Encrypted frames cannot be decrypted: {KeyProblem}.";
        yield return $"Frames:  {Frames.Count:N0} frame header(s) found{FrameBreakdown()}";
        yield return $"Blocks:  {BlocksVerified:N0} of {Blocks.Count:N0} verified";
        foreach (var line in BlockProblems()) yield return "  " + line;
        foreach (var f in Frames.Where(f => f.State is SalvageFrameState.Ignored or SalvageFrameState.Duplicate).Take(10))
            yield return $"  {f.Name} at offset {f.Offset:N0}: {StateText(f.State)} ({f.Problem})";
        yield return IndexRecovered
            ? $"Index:   intact (frame at offset {IndexFrame!.Offset:N0})"
            : $"Index:   not recovered: {IndexProblem}. File names, folders and times are lost.";
        if (Refused is not null)
        {
            yield return $"Stopped: {Refused}; nothing was written.";
            yield break;
        }
        if (IndexRecovered)
        {
            yield return $"Files:   {FilesRecovered:N0} of {FilesTotal:N0} recovered ({Size(BytesRecovered)}) to {Destination}"
                + (FilesSkipped > 0 ? $"; {FilesSkipped:N0} skipped (already there)" : "");
            foreach (var r in Renamed.Take(20)) yield return $"  renamed: {r.Path} → {r.WrittenAs} (the name was taken)";
            if (Renamed.Count > 20) yield return $"  … and {Renamed.Count - 20:N0} more renamed";
            foreach (var l in NeedNewerVersion) yield return $"  needs a newer Osmium: {l.Path} — {l.Reason}";
            foreach (var l in Lost) yield return $"  lost: {l.Path} — {l.Reason}";
        }
        else
        {
            yield return (Written.Count > 0
                    ? $"Output:  {Written.Count:N0} block file(s) (block-<n>.bin, {Size(BytesRecovered)}) in {Destination}"
                    : "Output:  no block file was written")
                + (FilesSkipped > 0 ? $"; {FilesSkipped:N0} already there were left as they were" : "");
            foreach (var l in Lost) yield return $"  not written: {l.Path} — {l.Reason}";
            if (ReportPath is not null) yield return $"Report:  {ReportPath}";
        }
        if (UnrecognizedBytes > 0)
            yield return $"Skipped: {Size(UnrecognizedBytes)} that did not verify as part of the header, a frame or the footer, at {Ranges(5)}";
        foreach (var n in Notes) yield return "Note:    " + n;
        foreach (var w in Warnings) yield return "Warning: " + w;
        yield return Verdict();
    }

    private string Verdict()
    {
        string? skipped = FilesSkipped == 0 ? null
            : $"{FilesSkipped:N0} {(IndexRecovered ? "file(s)" : "block file(s)")} already in the destination were left as they were";
        if (Complete)
        {
            string done = Frames.Any(f => f.State != SalvageFrameState.Verified) || UnrecognizedBytes > 0 || !HeaderIntact
                ? "Everything was recovered: the archive is damaged, but no data was lost"
                : "Everything was recovered";
            return skipped is null ? done + "." : $"{done}; {skipped}.";
        }
        var parts = new List<string>();
        if (!IndexRecovered && Written.Count > 0) parts.Add($"file names were lost; {Written.Count:N0} intact block(s) were written as block files");
        if (skipped is not null) parts.Add(skipped);
        int folders = Lost.Count(l => l.Path.EndsWith('/')), files = Lost.Count - folders;
        if (Lost.Count > 0)
            parts.Add(string.Join(" and ", new[] { files > 0 ? $"{files:N0} file(s)" : null, folders > 0 ? $"{folders:N0} folder(s)" : null }.OfType<string>())
                + " could not be recovered");
        if (NeedNewerVersion.Count > 0) parts.Add($"{NeedNewerVersion.Count:N0} file(s) need a newer version of Osmium");
        if (FramesSetAside > 0) parts.Add($"{FramesSetAside:N0} intact frame(s) were set aside");
        int newerBlocks = Blocks.Count(b => b.State == SalvageBlockState.NeedsNewerVersion);
        if (!IndexRecovered && newerBlocks > 0) parts.Add($"{newerBlocks:N0} block(s) need a newer version of Osmium");
        if (Written.Count == 0 && skipped is null)
            return parts.Count == 0 ? "Nothing could be recovered." : $"Nothing could be recovered: {string.Join("; ", parts)}.";
        return $"Not everything was recovered: {string.Join("; ", parts)}.";
    }

    private string FrameBreakdown()
    {
        var counts = Frames.Where(f => f.State is SalvageFrameState.Duplicate or SalvageFrameState.Ignored)
            .GroupBy(f => f.State).OrderBy(g => g.Key).Select(g => $"{g.Count():N0} {StateText(g.Key)}").ToList();
        return counts.Count == 0 ? "" : $" ({string.Join(", ", counts)})";
    }

    /// <summary>One line per block that did not verify; runs of blocks with the same problem share a line.</summary>
    private IEnumerable<string> BlockProblems()
    {
        var bad = Blocks.Where(b => b.State != SalvageBlockState.Verified).ToList();
        for (int i = 0; i < bad.Count;)
        {
            int j = i;
            while (j + 1 < bad.Count && bad[j + 1].Number == bad[j].Number + 1 && bad[j + 1].State == bad[i].State && bad[j + 1].Problem == bad[i].Problem) j++;
            var b = bad[i];
            string which = j > i ? $"blocks {b.Number}–{bad[j].Number}" : b.Frame is { } f ? $"block {b.Number} at offset {f.Offset:N0}" : $"block {b.Number}";
            yield return $"{which}: {StateText(b.State)}{(b.Problem is null ? "" : $" ({b.Problem})")}";
            i = j + 1;
        }
    }

    private string Ranges(int max)
    {
        var shown = UnrecognizedRanges.Take(max).Select(r => $"{r.Start:N0}–{r.End:N0}").ToList();
        if (UnrecognizedRanges.Count > max) shown.Add($"{UnrecognizedRanges.Count - max:N0} more");
        return string.Join(", ", shown);
    }

    internal static string StateText(SalvageBlockState s) => s switch
    {
        SalvageBlockState.Verified => "verified",
        SalvageBlockState.Damaged => "damaged",
        SalvageBlockState.Missing => "not found",
        SalvageBlockState.NeedsNewerVersion => "needs a newer version of Osmium",
        SalvageBlockState.NotWritten => "verified, not written",
        _ => "not decrypted",
    };

    internal static string StateText(SalvageFrameState s) => s switch
    {
        SalvageFrameState.Verified => "verified",
        SalvageFrameState.Duplicate => "duplicate",
        SalvageFrameState.Damaged => "damaged",
        SalvageFrameState.NeedsNewerVersion => "needs a newer version of Osmium",
        SalvageFrameState.NotDecrypted => "not decrypted",
        _ => "ignored",
    };

    internal static string Size(long b) => b switch
    {
        < 1024 => $"{b:N0} bytes",
        < 1 << 20 => $"{b / 1024.0:F1} KiB",
        < 1 << 30 => $"{b / 1048576.0:F1} MiB",
        _ => $"{b / 1073741824.0:F2} GiB",
    };

    /// <summary>The report written next to the block files when the index was lost.</summary>
    internal string ReportText()
    {
        var sb = new StringBuilder();
        string version = typeof(Salvage).Assembly.GetName().Version?.ToString(3) ?? "?";
        sb.AppendLine("Osmium salvage report");
        sb.AppendLine("=====================");
        sb.AppendLine();
        sb.AppendLine($"Archive:      {ArchivePath} ({ArchiveLength:N0} bytes)");
        sb.AppendLine($"Destination:  {Destination}");
        sb.AppendLine($"Salvaged by:  Osmium {version}, {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine($"The archive index could not be recovered: {IndexProblem}.");
        sb.AppendLine("File names, folders and timestamps were lost with it. The raw content of every block that verified");
        sb.AppendLine("(frame header, body, and content hash after decoding) was written as block-<frame number>.bin. A block");
        sb.AppendLine("holds the contents of one or more files back to back, in archive order; a large file continues in the");
        sb.AppendLine("next block. Nothing that failed verification was written. Blocks that need a newer version of Osmium");
        sb.AppendLine("are intact: salvage the archive again with a newer version to recover them.");
        sb.AppendLine();
        sb.AppendLine($"Header:       {Header}{(Creator is null ? "" : $", created by {Creator}")}");
        sb.AppendLine($"Encrypted:    {(Encrypted ? "yes" : "no")}{(KeyProblem is null ? "" : $"; frames cannot be decrypted: {KeyProblem}")}");
        sb.AppendLine($"Frames found: {Frames.Count:N0}");
        sb.AppendLine($"Blocks:       {BlocksVerified:N0} of {Blocks.Count:N0} verified; {Written.Count:N0} file(s), {BytesRecovered:N0} bytes written");
        if (FilesSkipped > 0) sb.AppendLine($"Skipped:      {FilesSkipped:N0} block file(s) already in the destination were left as they were");
        foreach (var l in Lost) sb.AppendLine($"Not written:  {l.Path}: {l.Reason}");
        foreach (var n in Notes) sb.AppendLine($"Note:         {n}");
        foreach (var w in Warnings) sb.AppendLine($"Warning:      {w}");
        sb.AppendLine();
        sb.AppendLine("Blocks");
        sb.AppendLine("------");
        foreach (var b in Blocks)
        {
            sb.AppendLine($"block {b.Number}: {StateText(b.State)}{(b.Problem is null ? "" : $" ({b.Problem})")}");
            if (b.Frame is { } f) AppendFrame(sb, f);
        }
        var listed = Blocks.Where(b => b.Frame is not null).Select(b => b.Frame!).ToHashSet();
        var others = Frames.Where(f => !listed.Contains(f)).ToList();
        if (others.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Other frames");
            sb.AppendLine("------------");
            foreach (var f in others)
            {
                sb.AppendLine($"{f.Name} at offset {f.Offset:N0}: {StateText(f.State)}{(f.Problem is null ? "" : $" ({f.Problem})")}");
                if (f.Output is not null) AppendFrame(sb, f);
            }
        }
        if (UnrecognizedRanges.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Unrecognized data");
            sb.AppendLine("-----------------");
            sb.AppendLine($"{UnrecognizedBytes:N0} bytes are not part of the header, an intact frame or the footer:");
            foreach (var (s, e) in UnrecognizedRanges) sb.AppendLine($"  offsets {s:N0} to {e:N0} ({e - s:N0} bytes)");
        }
        return sb.ToString();
    }

    private static void AppendFrame(StringBuilder sb, SalvageFrame f)
    {
        sb.AppendLine($"  offset         {f.Offset:N0}");
        if (f.Pipeline is not null) sb.AppendLine($"  pipeline       {f.Pipeline}");
        if (f.RawSize >= 0) sb.AppendLine($"  raw size       {f.RawSize:N0} bytes");
        sb.AppendLine(f.PayloadLength >= 0
            ? $"  stored size    {f.PayloadLength:N0} bytes (frame {f.FrameLength:N0} bytes)"
            : $"  frame length   {f.FrameLength:N0} bytes");
        if (f.RawHash is { } rh) sb.AppendLine($"  raw XXH128     {rh:x32}");
        if (f.PayloadHash is { } ph) sb.AppendLine($"  payload XXH3   {ph:x16}");
        if (f.EnclosingOffset is { } enc)
            sb.AppendLine($"  found inside   the damaged frame at offset {enc:N0} (it may belong to an archive stored in that frame)");
        if (f.ForeignIndexOffset is { } owner)
            sb.AppendLine($"  listed by      the index at offset {owner:N0}, which belongs to another archive (one stored in a block)");
        if (f.Output is not null) sb.AppendLine($"  written to     {f.Output}");
    }
}
