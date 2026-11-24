using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Planning;

public sealed record CompressedBlock(BlockDescriptor Descriptor, byte[] Payload, string? Node = null);

/// <summary>Compresses one block with one pipeline. Local by default; the cluster layer adds remote nodes.</summary>
public interface IBlockCompressor
{
    Task<CompressedBlock> CompressAsync(PipelineSpec spec, byte[] raw, CodecContext ctx);
}

public sealed class LocalBlockCompressor : IBlockCompressor
{
    public static readonly LocalBlockCompressor Instance = new();

    public Task<CompressedBlock> CompressAsync(PipelineSpec spec, byte[] raw, CodecContext ctx)
    {
        var (d, payload) = PipelineExecutor.Encode(spec, raw, ctx);
        return Task.FromResult(new CompressedBlock(d, payload, "local"));
    }
}

public sealed record BuildOptions
{
    public required string OutputPath { get; init; }
    public string? Password { get; init; }
    public int KdfIterations { get; init; } = ArchiveKey.DefaultIterations;
    /// <summary>Decode every block and compare before writing it (strongly recommended).</summary>
    public bool VerifyBlocks { get; init; } = true;
    public bool Overwrite { get; init; }
    public int Threads { get; init; } = Environment.ProcessorCount;
    public long MemoryBudget { get; init; } = MemoryBudgets.Default();
    public IBlockCompressor Compressor { get; init; } = LocalBlockCompressor.Instance;
    /// <summary>Store the planner's explain log (archive-relative paths only) in the archive metadata.</summary>
    public bool StorePlanLog { get; init; } = true;
    public PauseGate? Gate { get; init; }
    public Action<BuildProgress>? Progress { get; init; }
}

public sealed record BuildProgress(string Phase, long BytesIn, long BytesInTotal, long BytesOut, int BlocksDone, int BlocksTotal, string? Detail);

public sealed record BuildResult(string Path, long InputBytes, long OutputBytes, int Files, int Blocks, TimeSpan Elapsed,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> PlanLog, double EstimatedDecodeSeconds, long MaxDecodeMemory)
{
    public double Ratio => InputBytes == 0 ? 1 : (double)OutputBytes / InputBytes;
}

/// <summary>Executes an <see cref="ArchivePlan"/> and writes a verified archive.</summary>
public static class ArchiveBuilder
{
    public static async Task<BuildResult> BuildAsync(ArchivePlan plan, BuildOptions o, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (File.Exists(o.OutputPath) && !o.Overwrite)
            throw new IOException($"'{o.OutputPath}' already exists (use overwrite to replace it).");

        using var writer = new OsaWriter(o.OutputPath, new OsaWriterOptions
        {
            Password = o.Password,
            KdfIterations = o.KdfIterations,
            Experimental = plan.UsesExperimental,
            IndexPipeline = plan.Profile.Level >= ProfileLevel.Ultra ? "lzma(dict=16M)" : "zstd(level=19)",
        });

        var gate = new MemoryGate(o.MemoryBudget);
        var throttle = new SemaphoreSlim(Math.Max(1, o.Threads));
        var results = new Dictionary<int, (CompressedBlock Block, UInt128 Hash, long DecodeMem)>();
        var resultsLock = new object();
        var writeLock = new object();
        using var failCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var jobCt = failCts.Token;
        int nextToWrite = 0, blocksDone = 0;
        long bytesIn = 0, bytesOut = 0;
        long totalRaw = plan.Blocks.Sum(b => b.RawSize);
        var fileHashes = new Dictionary<int, XxHash128>();
        var unitHashes = new Dictionary<int, XxHash128>();
        var computedFileHash = new Dictionary<int, UInt128>();
        var tasks = new List<Task>();
        var warnings = new List<string>(plan.Warnings);

        void Report(string phase, string? detail = null) =>
            o.Progress?.Invoke(new BuildProgress(phase, Interlocked.Read(ref bytesIn), totalRaw, Interlocked.Read(ref bytesOut), blocksDone, plan.Blocks.Count, detail));

        // Frames are written strictly in plan order (frame number == block id), so the archive layout does not
        // depend on which thread finished first. Popping and writing happen under one lock.
        void DrainWrites()
        {
            lock (writeLock)
            {
                while (true)
                {
                    (CompressedBlock Block, UInt128 Hash, long DecodeMem) r;
                    lock (resultsLock)
                    {
                        if (!results.Remove(nextToWrite, out r)) return;
                    }
                    int frame = writer.WriteBlock(r.Block.Descriptor, r.Hash, r.Block.Payload, r.DecodeMem);
                    if (frame != nextToWrite) throw new InvalidOperationException("Internal error: frame order.");
                    nextToWrite++;
                    Interlocked.Add(ref bytesOut, Osa.FrameHeaderSize + r.Block.Payload.Length);
                }
            }
        }

        foreach (var block in plan.Blocks)
        {
            jobCt.ThrowIfCancellationRequested();
            var g = plan.Groups[block.Group];
            var specs = new List<PipelineSpec> { g.Spec! };
            specs.AddRange(g.Alternates);
            long mem = specs.Max(s => Math.Max(s.EncodeMemory(block.RawSize), s.DecodeMemory(block.RawSize))) + block.RawSize * 2;
            var lease = gate.Acquire(Math.Min(mem, o.MemoryBudget), jobCt);
            await throttle.WaitAsync(jobCt);
            byte[] raw;
            try
            {
                Report("Reading", $"block {block.Id + 1}/{plan.Blocks.Count}");
                raw = BlockAssembler.Assemble(plan, block, (int)block.RawSize);
                HashParts(plan, block, raw, fileHashes, unitHashes, computedFileHash);
            }
            catch
            {
                lease.Dispose();
                throttle.Release();
                throw;
            }

            var b = block;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var ctx = new CodecContext
                    {
                        Token = jobCt,
                        Gate = o.Gate,
                        Progress = done => Report("Compressing", $"block {b.Id + 1}/{plan.Blocks.Count}: {specs[0].Codec.Method.DisplayName}"),
                    };
                    var rawHash = BlockBody.Hash128(raw);
                    CompressedBlock? best = null;
                    foreach (var spec in specs)
                    {
                        CompressedBlock c;
                        try { c = await o.Compressor.CompressAsync(spec, raw, ctx); }
                        catch (Exception e) when (e is not OperationCanceledException && specs.Count > 1)
                        {
                            lock (warnings) warnings.Add($"block {b.Id}: {spec} failed ({e.Message})");
                            continue;
                        }
                        if (best is null || c.Payload.Length < best.Payload.Length) best = c;
                    }
                    // Never expand: store when compression did not help.
                    if (best is null || best.Payload.Length >= raw.Length)
                        best = await LocalBlockCompressor.Instance.CompressAsync(PipelineSpec.Parse("store", MethodRegistry.Default), raw, ctx);
                    if (o.VerifyBlocks)
                    {
                        Report("Verifying", $"block {b.Id + 1}/{plan.Blocks.Count}");
                        var back = PipelineExecutor.Decode(best.Descriptor, best.Payload, new CodecContext { Token = jobCt, Gate = o.Gate });
                        if (back.Length != raw.Length || BlockBody.Hash128(back) != rawHash)
                            throw new InvalidOperationException($"Verification failed for block {b.Id} ({best.Descriptor.Spec}). The archive was not written. Please report this.");
                    }
                    long decodeMem = best.Descriptor.Spec.DecodeMemory(raw.Length);
                    lock (resultsLock) results[b.Id] = (best, rawHash, decodeMem);
                    Interlocked.Add(ref bytesIn, raw.Length);
                    Interlocked.Increment(ref blocksDone);
                    DrainWrites();
                    Report("Compressing");
                }
                catch
                {
                    failCts.Cancel(); // stop the other blocks promptly
                    throw;
                }
                finally
                {
                    lease.Dispose();
                    throttle.Release();
                }
            }, jobCt));

            // surface failures early
            var faulted = tasks.FirstOrDefault(t => t.IsFaulted);
            if (faulted is not null) await faulted;
        }
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // a sibling failed; rethrow the real error
            var real = tasks.FirstOrDefault(t => t.IsFaulted)?.Exception?.InnerException;
            if (real is not null) throw real;
            throw;
        }
        DrainWrites();
        if (nextToWrite != plan.Blocks.Count) throw new InvalidOperationException("Internal error: not all blocks were written.");

        // Source change detection (size/mtime) — the archive must describe one consistent snapshot.
        foreach (var f in plan.Files)
        {
            var fi = new FileInfo(f.Src.FullPath);
            if (!fi.Exists || fi.Length != f.Src.Size || fi.LastWriteTimeUtc != f.Src.ModifiedUtc)
                throw new SourceChangedException(f.Src.FullPath, "changed while it was being archived");
        }

        // Index entries
        var placements = new Dictionary<int, List<(int Block, long BlockOffset, long UnitOffset, long Length)>>();
        foreach (var b in plan.Blocks)
            foreach (var part in b.Parts)
            {
                if (!placements.TryGetValue(part.Unit, out var l)) placements[part.Unit] = l = [];
                l.Add((b.Id, part.BlockOffset, part.UnitOffset, part.Length));
            }
        var entries = new List<ArchiveEntry>();
        foreach (var d in plan.Dirs)
            entries.Add(new ArchiveEntry { Path = d.RelPath, Kind = EntryKind.Directory, ModifiedUtc = d.ModifiedUtc, CreatedUtc = d.CreatedUtc, Attributes = d.Attributes & ArchiveIndex.StoredAttributes });
        for (int i = 0; i < plan.Files.Count; i++)
        {
            var f = plan.Files[i];
            var segs = new List<Segment>();
            foreach (int u in f.Units)
                foreach (var pl in placements[u].OrderBy(x => x.UnitOffset))
                {
                    if (segs.Count > 0 && segs[^1].Frame == pl.Block && segs[^1].Offset + segs[^1].Length == pl.BlockOffset)
                        segs[^1] = segs[^1] with { Length = segs[^1].Length + pl.Length };
                    else segs.Add(new Segment(pl.Block, pl.BlockOffset, pl.Length));
                }
            int hashOwner = f.DuplicateOf >= 0 ? f.DuplicateOf : i;
            UInt128 hash = f.Src.Size == 0 ? XxHash128.HashToUInt128([])
                : f.Hash ?? (computedFileHash.TryGetValue(hashOwner, out var h) ? h
                    : throw new InvalidOperationException($"Internal error: no hash for {f.Src.RelPath}"));
            entries.Add(new ArchiveEntry
            {
                Path = f.Src.RelPath, Kind = EntryKind.File, ModifiedUtc = f.Src.ModifiedUtc, CreatedUtc = f.Src.CreatedUtc,
                Attributes = f.Src.Attributes & ArchiveIndex.StoredAttributes, Size = f.Src.Size, Segments = segs, Hash = hash,
            });
        }

        var planLog = plan.Groups.SelectMany(g => g.Explain).ToList();
        var meta = new Dictionary<string, byte[]>
        {
            ["profile"] = Encoding.UTF8.GetBytes(plan.Profile.Name),
            ["created"] = Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O")),
        };
        if (o.StorePlanLog) meta["plan"] = JsonSerializer.SerializeToUtf8Bytes(planLog);
        Report("Writing index");
        writer.Finish(entries, meta);

        // Structural re-read of the written file (CRCs, payload hashes, index) before it replaces anything.
        Report("Checking archive");
        double decodeSec;
        long maxMem;
        using (var check = OsaReader.Open(writer.TempPath, () => o.Password))
        {
            for (int i = 0; i < check.Blocks.Count; i++) check.ReadBody(i);
            if (check.Entries.Count != entries.Count) throw new InvalidOperationException("Index re-read mismatch.");
            decodeSec = check.EstimatedDecodeSeconds;
            maxMem = check.MaxBlockDecodeMemory;
        }
        writer.Commit(o.Overwrite);
        long outLen = new FileInfo(o.OutputPath).Length;
        Report("Done");
        return new BuildResult(o.OutputPath, plan.TotalInput, outLen, plan.Files.Count, plan.Blocks.Count, sw.Elapsed, warnings, planLog, decodeSec, maxMem);
    }

    /// <summary>Hashes file content from the exact bytes placed in blocks (files are laid out in order).</summary>
    private static void HashParts(ArchivePlan plan, PlanBlock block, byte[] raw,
        Dictionary<int, XxHash128> fileHashes, Dictionary<int, XxHash128> unitHashes, Dictionary<int, UInt128> done)
    {
        foreach (var part in block.Parts)
        {
            var unit = plan.Units[part.Unit];
            var data = raw.AsSpan((int)part.BlockOffset, (int)part.Length);
            if (unit.Xxh is { } expect)
            {
                if (!unitHashes.TryGetValue(unit.Id, out var uh)) unitHashes[unit.Id] = uh = new XxHash128();
                uh.Append(data);
                if (part.UnitOffset + part.Length == unit.Length)
                {
                    if (uh.GetCurrentHashAsUInt128() != expect)
                        throw new SourceChangedException(plan.Files[unit.File].Src.FullPath, "changed while it was being archived");
                    unitHashes.Remove(unit.Id);
                }
                continue;
            }
            // whole-file unit
            if (!fileHashes.TryGetValue(unit.File, out var fh)) fileHashes[unit.File] = fh = new XxHash128();
            fh.Append(data);
            if (part.UnitOffset + part.Length == unit.Length)
            {
                done[unit.File] = fh.GetCurrentHashAsUInt128();
                fileHashes.Remove(unit.File);
            }
        }
    }
}
