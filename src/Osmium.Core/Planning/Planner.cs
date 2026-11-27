using System.Collections.Concurrent;
using Osmium.Core.Analysis;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Planning;

/// <summary>Evaluates pipelines on sample data. Implementations must be deterministic (same sizes everywhere).</summary>
public interface ITrialRunner
{
    /// <returns>Compressed size per spec, or -1 when a spec failed.</returns>
    Task<long[]> RunAsync(IReadOnlyList<PipelineSpec> specs, byte[] sample, CancellationToken ct);
}

/// <summary>
/// Runs trials on this machine. All concurrent <see cref="RunAsync"/> calls share one pool of <c>threads</c> slots,
/// so the planner can work on several groups at once without oversubscribing the CPU.
/// </summary>
public sealed class LocalTrialRunner(int threads, MemoryGate gate) : ITrialRunner
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, threads));

    public async Task<long[]> RunAsync(IReadOnlyList<PipelineSpec> specs, byte[] sample, CancellationToken ct)
    {
        var sizes = new long[specs.Count];
        await Task.WhenAll(specs.Select((spec, i) => Task.Run(async () =>
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var lease = gate.Acquire(spec.EncodeMemory(sample.Length), ct);
                try { sizes[i] = PipelineExecutor.TrialSize(spec, sample, new CodecContext { Token = ct }); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { sizes[i] = -1; }
            }
            finally { _slots.Release(); }
        }, ct))).ConfigureAwait(false);
        return sizes;
    }
}

public sealed record PlanOptions
{
    public required Profile Profile { get; init; }
    public RuleSet Rules { get; init; } = RuleSet.BuiltIn();
    public int Threads { get; init; } = Environment.ProcessorCount;
    public long MemoryBudget { get; init; } = MemoryBudgets.Default();
    public DedupMode? Dedup { get; init; }
    public long? BlockCap { get; init; }
    /// <summary>0 = ignore decode speed, 1 = light penalty, 2 = strong penalty (avoids CM).</summary>
    public int DecodeSpeedWeight { get; init; }
    public bool AllowExperimental { get; init; }
    /// <summary>Pipeline forced for every file (CLI --pipeline); rules can still override per file.</summary>
    public string? ForcedPipeline { get; init; }
    public MethodRegistry Registry { get; init; } = MethodRegistry.Default;
    public ITrialRunner? Trials { get; init; }
    public Action<string>? Log { get; init; }
    public Action<string, double>? Progress { get; init; }
}

public sealed class PlanUnit
{
    public int Id { get; init; }
    public int File { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    /// <summary>Known content hash (chunk dedup) used to detect files changing during compression.</summary>
    public UInt128? Xxh { get; init; }
}

public sealed class PlanFile
{
    public required SourceFile Src { get; init; }
    public FileAnalysis? Analysis { get; set; }
    public EffectiveRules Rules { get; set; } = new();
    public CompatClass Class { get; set; } = CompatClass.Binary;
    public List<int> Units { get; } = [];
    public int DuplicateOf { get; set; } = -1;
    public UInt128? Hash { get; set; }
    public int Group { get; set; } = -1;
}

public readonly record struct Placement(int Unit, long UnitOffset, long Length, long BlockOffset);

public sealed class PlanGroup
{
    public int Id { get; init; }
    public required string Key { get; init; }
    public CompatClass Class { get; set; }
    public FileAnalysis? Rep { get; set; }
    public int Alignment { get; set; } = 1;
    public List<int> Units { get; } = [];
    public long Size { get; set; }
    public PipelineSpec? Spec { get; set; }
    public List<PipelineSpec> Alternates { get; } = [];
    public EffectiveRules Rules { get; set; } = new();
    public List<string> Explain { get; } = [];
    public bool Forced { get; set; }
    /// <summary>How the files are ordered when not by extension, name and path, for the explain log.</summary>
    public string? Ordering { get; set; }
}

public sealed class PlanBlock
{
    public int Id { get; init; }
    public int Group { get; init; }
    public List<Placement> Parts { get; } = [];
    public long RawSize { get; set; }
}

public sealed class ArchivePlan
{
    public required Profile Profile { get; init; }
    public List<PlanFile> Files { get; } = [];
    public List<PlanUnit> Units { get; } = [];
    public List<PlanGroup> Groups { get; } = [];
    public List<PlanBlock> Blocks { get; } = [];
    public List<SourceDir> Dirs { get; init; } = [];
    public List<string> Warnings { get; } = [];
    public long TotalInput => Files.Sum(f => f.Src.Size);
    public long StoredInput => Units.Sum(u => u.Length);
    public bool UsesExperimental => Profile.Experimental || Groups.Any(g => g.Spec?.Experimental == true);
}

/// <summary>Builds an <see cref="ArchivePlan"/>: analysis → rules → dedup → groups → layout → pipeline choice.</summary>
public sealed class Planner(PlanOptions o)
{
    private readonly MethodRegistry _reg = o.Registry;
    private void Log(string s) => o.Log?.Invoke(s);

    public async Task<ArchivePlan> BuildAsync(ScanResult scan, CancellationToken ct)
    {
        var p = o.Profile;
        var plan = new ArchivePlan { Profile = p, Dirs = scan.Dirs };
        plan.Warnings.AddRange(scan.Skipped);
        var forced = o.ForcedPipeline is null ? null : PipelineSpec.Parse(o.ForcedPipeline, _reg);

        // 1. rules pre-pass + analysis
        var candidates = new List<PlanFile>();
        foreach (var f in scan.Files)
        {
            var pre = o.Rules.Resolve(f.RelPath, f.Size, null, _reg);
            if (pre.Exclude) { Log($"excluded {f.RelPath} ({string.Join("; ", pre.Provenance)})"); continue; }
            candidates.Add(new PlanFile { Src = f });
        }
        int analyzed = 0;
        var failed = new ConcurrentBag<PlanFile>();
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(o.Threads, 1, 8), CancellationToken = ct }, (f, token) =>
        {
            try
            {
                if (p.Level != ProfileLevel.Store && f.Src.Size > 0)
                {
                    using var fs = SourceIdentity.Open(f.Src);
                    f.Analysis = Sniffer.Analyze(fs.SafeFileHandle, f.Src.Size, p.SampleWindows);
                }
            }
            catch (Exception e) when (e is (IOException or UnauthorizedAccessException) and not SourceChangedException)
            {
                failed.Add(f);
                lock (plan.Warnings) plan.Warnings.Add($"{f.Src.RelPath}: cannot read ({e.Message})");
            }
            o.Progress?.Invoke("Analyzing", (double)Interlocked.Increment(ref analyzed) / Math.Max(1, candidates.Count));
            return ValueTask.CompletedTask;
        });
        foreach (var f in candidates)
        {
            if (failed.Contains(f)) continue;
            f.Rules = o.Rules.Resolve(f.Src.RelPath, f.Src.Size, f.Analysis, _reg);
            if (f.Rules.Exclude) { Log($"excluded {f.Src.RelPath} ({string.Join("; ", f.Rules.Provenance)})"); continue; }
            if (forced is not null && f.Rules.Pipeline is null) f.Rules.Pipeline = forced;
            f.Class = f.Analysis?.Class ?? CompatClass.Binary;
            // Audio and image filters work on one file in one block; a file too large for that is treated as records.
            if (f.Class is CompatClass.Audio or CompatClass.Image && f.Src.Size > MediaBlockCap(o.BlockCap ?? p.BlockCap)) f.Class = CompatClass.Structured;
            plan.Files.Add(f);
        }

        // 2. dedup → units
        var dedup = o.Dedup ?? p.Dedup;
        Dedupe(plan, dedup, ct);

        // 3. groups and layout
        BuildGroups(plan, ct);
        Layout(plan, o.BlockCap ?? p.BlockCap);

        // 4. choose a pipeline for each group. Groups are independent, so they are planned concurrently; the trial
        //    runner bounds the total parallelism, and every group's choice depends only on its own trial results, so
        //    the plan is the same as planning them one after another (largest first, so they start first).
        var gate = new MemoryGate(o.MemoryBudget);
        var trials = o.Trials ?? new LocalTrialRunner(o.Threads, gate);
        int done = 0;
        ct.ThrowIfCancellationRequested();
        await Task.WhenAll(plan.Groups.OrderByDescending(g => g.Size).Select(async g =>
        {
            await ChooseAsync(plan, g, trials, ct).ConfigureAwait(false);
            o.Progress?.Invoke("Planning", (double)Interlocked.Increment(ref done) / plan.Groups.Count);
        })).ConfigureAwait(false);
        return plan;
    }

    // ------------------------------------------------------------------ dedup

    private void Dedupe(ArchivePlan plan, DedupMode mode, CancellationToken ct)
    {
        var files = plan.Files;
        if (mode == DedupMode.Chunks)
        {
            var seen = new Dictionary<(string, int), int>();
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                if (f.Src.Size == 0) continue;
                List<ChunkInfo> chunks;
                try
                {
                    (chunks, var h) = DedupScanner.Chunk(f.Src, ct);
                    f.Hash = h;
                }
                catch (Exception e) when (e is (IOException or UnauthorizedAccessException) and not SourceChangedException)
                {
                    plan.Warnings.Add($"{f.Src.RelPath}: cannot read ({e.Message})");
                    continue;
                }
                foreach (var c in chunks)
                {
                    var key = (Convert.ToHexString(c.Sha), c.Length);
                    if (!seen.TryGetValue(key, out int unit))
                    {
                        unit = plan.Units.Count;
                        plan.Units.Add(new PlanUnit { Id = unit, File = i, Offset = c.Offset, Length = c.Length, Xxh = c.Xxh });
                        seen[key] = unit;
                    }
                    f.Units.Add(unit);
                }
                o.Progress?.Invoke("Deduplicating", (double)(i + 1) / files.Count);
            }
            long saved = plan.TotalInput - plan.StoredInput;
            if (saved > 0) Log($"chunk dedup removed {saved:N0} duplicate bytes");
            return;
        }

        var duplicateOf = new int[files.Count];
        Array.Fill(duplicateOf, -1);
        if (mode == DedupMode.Files)
        {
            foreach (var bySize in Enumerable.Range(0, files.Count).Where(i => files[i].Src.Size > 0).GroupBy(i => files[i].Src.Size).Where(g => g.Count() > 1))
            {
                var bySha = new Dictionary<string, int>();
                foreach (int i in bySize)
                {
                    string sha;
                    try { sha = Convert.ToHexString(DedupScanner.Sha256File(files[i].Src, ct)); }
                    catch (Exception e) when (e is (IOException or UnauthorizedAccessException) and not SourceChangedException) { continue; }
                    if (bySha.TryGetValue(sha, out int first)) duplicateOf[i] = first;
                    else bySha[sha] = i;
                }
            }
        }
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            if (duplicateOf[i] >= 0) { f.DuplicateOf = duplicateOf[i]; continue; }
            if (f.Src.Size == 0) continue;
            int unit = plan.Units.Count;
            plan.Units.Add(new PlanUnit { Id = unit, File = i, Offset = 0, Length = f.Src.Size });
            f.Units.Add(unit);
        }
        for (int i = 0; i < files.Count; i++)
            if (files[i].DuplicateOf >= 0) files[i].Units.AddRange(files[files[i].DuplicateOf].Units);
        int dups = files.Count(f => f.DuplicateOf >= 0);
        if (dups > 0) Log($"whole-file dedup: {dups} duplicate file(s) stored once");
    }

    // ------------------------------------------------------------------ grouping

    private void BuildGroups(ArchivePlan plan, CancellationToken ct)
    {
        var byKey = new Dictionary<string, PlanGroup>();
        var members = new List<List<GroupMember>>(); // per group, its files in the order below
        var order = Enumerable.Range(0, plan.Files.Count)
            .OrderBy(i => Path.GetExtension(plan.Files[i].Src.RelPath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => Path.GetFileName(plan.Files[i].Src.RelPath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => plan.Files[i].Src.RelPath, StringComparer.Ordinal);
        foreach (int i in order)
        {
            var f = plan.Files[i];
            // A chunk can repeat inside a file (a run of zeros, say): its unit is placed once, at its first occurrence.
            var own = f.Units.Where(u => plan.Units[u].File == i).Distinct().ToList();
            if (own.Count == 0) continue;
            string solid = f.Rules.Solid;
            string key = f.Rules.Pipeline is not null ? $"F:{f.Rules.Pipeline}|{solid}|{f.Rules.Key}"
                : solid == "own" || f.Class is CompatClass.Audio or CompatClass.Image ? $"own:{i}"
                : solid.StartsWith("group:", StringComparison.OrdinalIgnoreCase) ? $"G:{solid}|{f.Rules.Key}"
                : $"C:{f.Class}|{(f.Class == CompatClass.Structured ? f.Analysis?.Stats.Stride : 0)}{(f.Class == CompatClass.CodeX86 ? $"|{f.Analysis?.Arch}" : "")}|{f.Rules.Key}";
            if (!byKey.TryGetValue(key, out var g))
            {
                g = new PlanGroup { Id = plan.Groups.Count, Key = key, Class = f.Class, Rep = f.Analysis, Rules = f.Rules, Forced = f.Rules.Pipeline is not null };
                byKey[key] = g;
                plan.Groups.Add(g);
                members.Add([]);
            }
            if (f.Analysis is not null && (g.Rep is null || f.Src.Size > (g.Rep.Stats.SampledBytes))) g.Rep ??= f.Analysis;
            members[g.Id].Add(new GroupMember(i, own));
            foreach (var u in own) g.Size += plan.Units[u].Length;
            f.Group = g.Id;
        }
        if (o.Profile.SimilarityOrder) OrderBySimilarity(plan, members, ct);
        foreach (var g in plan.Groups)
        {
            // A file's own units stay together and in their order (chunk dedup splits a file into several).
            foreach (var m in members[g.Id]) g.Units.AddRange(m.Units);
            g.Alignment = g.Class switch
            {
                CompatClass.CodeArm64 => 4,
                CompatClass.Utf16Text => 2,
                CompatClass.Structured => Math.Max(1, g.Rep?.Stats.Stride ?? 1),
                _ => 1,
            };
        }
    }

    private readonly record struct GroupMember(int File, List<int> Units);

    /// <summary>
    /// Reorders the files of every group of at least <see cref="SimilarityOrder.MinFiles"/> files whose classes qualify
    /// (<see cref="SimilarityOrder.Applies"/>) so that similar files are adjacent. The chain starts from the group's first
    /// file in name order and breaks ties by that order, so the result depends only on the files' contents and names.
    /// Groups forced to Store gain nothing and keep their order, as do groups with a file that cannot be read.
    /// </summary>
    private void OrderBySimilarity(ArchivePlan plan, List<List<GroupMember>> members, CancellationToken ct)
    {
        var eligible = plan.Groups.Where(g =>
            members[g.Id].Count is >= SimilarityOrder.MinFiles and <= SimilarityOrder.MaxFiles
            && !(g.Forced && g.Rules.Pipeline!.IsStore)
            && members[g.Id].All(m => SimilarityOrder.Applies(plan.Files[m.File].Class))).ToList();
        if (eligible.Count == 0) return;
        var files = eligible.SelectMany(g => members[g.Id]).Select(m => m.File).ToArray();
        var signatures = new ulong[files.Length];
        var unreadable = new bool[files.Length];
        int hashed = 0;
        SourceChangedException? changed = null;
        // Each signature depends only on its own file, so computing them in parallel does not change the order.
        Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(o.Threads, 1, 8), CancellationToken = ct }, k =>
        {
            try { signatures[k] = SimilarityOrder.Signature(plan.Files[files[k]].Src); }
            catch (SourceChangedException e) { Interlocked.CompareExchange(ref changed, e, null); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unreadable[k] = true; }
            o.Progress?.Invoke("Ordering", (double)Interlocked.Increment(ref hashed) / files.Length);
        });
        if (changed is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(changed);
        int start = 0;
        foreach (var g in eligible)
        {
            var list = members[g.Id];
            var range = start..(start + list.Count);
            start += list.Count;
            int bad = Array.IndexOf(unreadable, true, range.Start.Value, list.Count);
            if (bad >= 0)
            {
                g.Ordering = $"order: by extension, name and path ({plan.Files[files[bad]].Src.RelPath} could not be read)";
                continue;
            }
            members[g.Id] = [.. SimilarityOrder.Chain(signatures[range]).Select(k => list[k])];
            g.Ordering = $"order: similarity (SimHash), {list.Count:N0} files";
        }
    }

    /// <summary>The largest block, given the profile's (or the job's) block cap: it leaves enough headroom in the memory
    /// budget for raw, filtered and compressed copies and for verification.</summary>
    private long BlockCap(long profileCap) =>
        Math.Clamp(Math.Min(profileCap, Math.Max(16L << 20, o.MemoryBudget / 6)), 1L << 16, BlockLimits.MaxRawSize);

    /// <summary>The largest single audio or image file kept in one block for its own filter: at least 64 MiB, so that a
    /// profile with small blocks still gives a song or a large picture its audio or image pipeline, within the same
    /// memory headroom as any block.</summary>
    private long MediaBlockCap(long profileCap) => BlockCap(Math.Max(profileCap, MediaMinBlock));

    private const long MediaMinBlock = 64L << 20;

    private void Layout(ArchivePlan plan, long profileCap)
    {
        long blockCap = BlockCap(profileCap), mediaCap = MediaBlockCap(profileCap);
        foreach (var g in plan.Groups)
        {
            // Audio and image files are their own groups, each in one block (see the reclassification above).
            long cap = g.Class is CompatClass.Audio or CompatClass.Image && g.Key.StartsWith("own:", StringComparison.Ordinal) ? mediaCap : blockCap;
            PlanBlock? cur = null;
            foreach (int u in g.Units)
            {
                var unit = plan.Units[u];
                long uoff = 0;
                while (uoff < unit.Length)
                {
                    if (cur is null || cur.RawSize >= cap)
                    {
                        cur = new PlanBlock { Id = plan.Blocks.Count, Group = g.Id };
                        plan.Blocks.Add(cur);
                    }
                    // Align file starts so stride/alignment-sensitive filters see aligned records.
                    if (uoff == 0 && unit.Offset == 0 && g.Alignment > 1 && cur.RawSize % g.Alignment != 0)
                        cur.RawSize += g.Alignment - cur.RawSize % g.Alignment;
                    long take = Math.Min(unit.Length - uoff, cap - cur.RawSize);
                    if (take <= 0) { cur = null; continue; }
                    cur.Parts.Add(new Placement(u, uoff, take, cur.RawSize));
                    cur.RawSize += take;
                    uoff += take;
                }
            }
        }
    }

    // ------------------------------------------------------------------ pipeline choice

    private double Lambda => o.DecodeSpeedWeight switch { 1 => 2_000, 2 => 20_000, _ => 0 };

    private double Objective(PipelineSpec s, long size, long sampleLen) =>
        size + Lambda * s.EstimateDecodeSeconds(sampleLen);

    private bool Allowed(PlanGroup g, PipelineSpec s, long blockSize)
    {
        if (!g.Rules.Allows(s)) return false;
        if (s.Experimental && !o.AllowExperimental) return false;
        if (MethodIds.IsContextMixing(s.Codec.Method.Id) && !o.Profile.AllowCm) return false;
        return s.EncodeMemory(blockSize) <= o.MemoryBudget && s.DecodeMemory(blockSize) <= o.MemoryBudget;
    }

    private async Task ChooseAsync(ArchivePlan plan, PlanGroup g, ITrialRunner trials, CancellationToken ct)
    {
        var p = o.Profile;
        long biggestBlock = plan.Blocks.Where(b => b.Group == g.Id).Select(b => b.RawSize).DefaultIfEmpty(0).Max();
        g.Explain.Add($"group {g.Id}: {g.Class}, {g.Units.Count} unit(s), {g.Size:N0} bytes{(g.Rep is null ? "" : ", e.g. " + g.Rep.Format)}");
        foreach (var pr in g.Rules.Provenance.Distinct()) g.Explain.Add("  rule: " + pr);
        if (g.Ordering is not null) g.Explain.Add("  " + g.Ordering);

        if (g.Forced)
        {
            g.Spec = g.Rules.Apply(g.Rules.Pipeline!);
            g.Explain.Add($"  forced pipeline: {g.Spec}");
            return;
        }

        var all = Candidates.For(g.Class, g.Rep, p, _reg).Select(g.Rules.Apply).Distinct().ToList();
        var allowed = all.Where(s => Allowed(g, s, biggestBlock)).ToList();
        foreach (var s in all.Except(allowed)) g.Explain.Add($"  pruned {s} (rules, memory or profile)");
        if (allowed.Count == 0) allowed.Add(PipelineSpec.Parse("zstd(level=19)", _reg));

        if (p.Rungs.Length == 0 || g.Size < 4096)
        {
            g.Spec = allowed.FirstOrDefault(s => !MethodIds.IsContextMixing(s.Codec.Method.Id)) ?? allowed[0];
            g.Explain.Add($"  chose {g.Spec} ({(g.Size < 4096 ? "tiny group" : "profile default")})");
            return;
        }

        var alive = allowed.Take(p.MaxCandidates).ToList();
        // The candidates of the last rung that produced results, best first, with their objective on its sample; and,
        // when the search is screened, the earlier rungs it could still screen on, with the scores of the candidates
        // that took part in the rung after them.
        var ranked = new List<(PipelineSpec Spec, double Score)>();
        byte[] sample = [];
        var earlier = new List<(byte[] Sample, Dictionary<PipelineSpec, double> Scores)>();
        foreach (long rung in p.FullDataTrials ? [Math.Min(g.Size, BlockLimits.MaxRawSize)] : p.Rungs)
        {
            var next = BuildSample(plan, g, rung);
            var sizes = await trials.RunAsync(alive, next, ct);
            var results = alive.Select((s, i) => (s, size: sizes[i])).Where(t => t.size >= 0)
                .OrderBy(t => Objective(t.s, t.size, next.Length)).ThenBy(t => t.s.ToString(), StringComparer.Ordinal).ToList();
            g.Explain.Add($"  rung {next.Length:N0} B: " + string.Join(", ", results.Select(t => $"{t.s} = {t.size:N0}")));
            if (results.Count == 0) break;
            if (p.ScreenSearch && p.SearchEvals > 0 && ranked.Count > 0)
            {
                var tried = alive.ToHashSet();
                earlier.Add((sample, ranked.Where(t => tried.Contains(t.Spec)).ToDictionary(t => t.Spec, t => t.Score)));
            }
            sample = next;
            // A kept sample at most half the current one also qualifies for any later, larger last sample, so the
            // smaller ones before it can no longer be chosen.
            int usable = earlier.FindLastIndex(e => 2L * e.Sample.Length <= sample.Length);
            if (usable > 0) earlier.RemoveRange(0, usable);
            ranked = [.. results.Select(t => (t.s, Objective(t.s, t.size, next.Length)))];
            int keep = Math.Max(2, (int)Math.Ceiling(ranked.Count / (double)p.Eta));
            alive = ranked.Take(keep).Select(t => t.Spec).ToList();
            if (sample.Length >= g.Size) break;
        }
        if (ranked.Count == 0)
        {
            g.Spec = allowed[0];
            g.Explain.Add($"  all trials failed; using {g.Spec}");
            return;
        }
        var scores = ranked.ToDictionary(t => t.Spec, t => t.Score);

        var best = p.PreferFasterDecoder ? FasterOnANearTie(g, ranked) : ranked[0].Spec;
        // Incompressible data: anything but Store must win by at least 1 %.
        if (g.Class == CompatClass.Incompressible && best.Codec.Method.Id != MethodIds.Store)
        {
            var store = alive.FirstOrDefault(s => s.IsStore);
            if (store is not null && scores[best] > scores[store] * 0.99) best = store;
        }

        bool search = p.SearchEvals > 0 && !best.IsStore;
        Screen? screen = search && p.ScreenSearch ? ScreeningSample(earlier, sample.Length, best) : null;
        earlier.Clear(); // only the screening sample, if any, lives on during the search
        if (search) best = await HillClimbAsync(g, best, scores[best], sample, screen, biggestBlock, trials, ct);

        g.Spec = best;
        if (p.RaceTopTwo)
        {
            var second = alive.FirstOrDefault(s => !s.Equals(best));
            if (second is not null) g.Alternates.Add(second);
        }
        g.Explain.Add($"  chose {g.Spec}{(g.Alternates.Count > 0 ? $" (race vs {g.Alternates[0]})" : "")}");
    }

    /// <summary>Candidates whose objective is within this fraction of the best are a near tie.</summary>
    private const double TieMargin = 0.001;

    /// <summary>
    /// A near tie goes to the faster decoder: of the last rung's candidates whose objective is within
    /// <see cref="TieMargin"/> of the best, the one with the lowest estimated decode time for the whole group (then the
    /// lower objective, then the spec text). The size cost is at most 0.1 % of the group.
    /// </summary>
    private static PipelineSpec FasterOnANearTie(PlanGroup g, List<(PipelineSpec Spec, double Score)> ranked)
    {
        var (best, bestScore) = ranked[0];
        var (pick, pickScore) = ranked.Where(t => t.Score <= bestScore * (1 + TieMargin))
            .OrderBy(t => t.Spec.EstimateDecodeSeconds(g.Size)).ThenBy(t => t.Score).ThenBy(t => t.Spec.ToString(), StringComparer.Ordinal)
            .First();
        if (!pick.Equals(best))
            g.Explain.Add($"  tie: chose {pick} (decodes faster) over {best} ({(bestScore > 0 ? 100 * (pickScore - bestScore) / bestScore : 0):0.00} % smaller)");
        return pick;
    }

    /// <summary>A screened neighbour reaches the full sample only if it is within this fraction of the incumbent's score
    /// on the smaller sample.</summary>
    private const double ScreenMargin = 0.005;

    /// <summary>The smaller sample a search step is screened on, and the incumbent's objective on it.</summary>
    private readonly record struct Screen(byte[] Sample, double Score);

    /// <summary>
    /// The largest earlier rung's sample that is at most half the last one, so that screening a step with enough
    /// neighbours costs less than compressing all of them on the last (<see cref="ScreeningPays"/> decides per step): the
    /// previous rung when the group fills the profile's rungs (1 of 4 MiB for Ultra, 4 of 16 MiB for Insane), an earlier
    /// one or none for a group that ends between rungs. Every candidate of the last rung took part in every earlier rung,
    /// so its score there is known.
    /// </summary>
    private static Screen? ScreeningSample(List<(byte[] Sample, Dictionary<PipelineSpec, double> Scores)> earlier, int last, PipelineSpec best)
    {
        for (int k = earlier.Count - 1; k >= 0; k--)
            if (2L * earlier[k].Sample.Length <= last && earlier[k].Scores.TryGetValue(best, out double score))
                return new Screen(earlier[k].Sample, score);
        return null;
    }

    /// <summary>
    /// Whether screening a step of <paramref name="n"/> neighbours can cut its work: compressing all of them on the
    /// smaller sample and up to <paramref name="keep"/> on the full one must cost less than compressing all of them on the
    /// full sample. It cannot when there are few neighbours: with n ≤ 2, every promising neighbour is compressed twice.
    /// </summary>
    internal static bool ScreeningPays(int n, int keep, long small, long full) => n * small + (long)keep * full < n * full;

    /// <param name="screen">When set, each step compresses its neighbours on this smaller sample first and only the best
    /// max(2, ⌈n / η⌉) of them, and of those only the ones within <see cref="ScreenMargin"/> of the incumbent, also on
    /// <paramref name="sample"/>, unless that cannot cut the step's work (<see cref="ScreeningPays"/>): such a step tries
    /// its neighbours on <paramref name="sample"/> only, and if it moves, the rest of the search is not screened (the new
    /// incumbent has no score on the smaller sample). A move needs the same gain on <paramref name="sample"/> as without
    /// screening, and the budget still counts every neighbour generated.</param>
    /// <param name="biggestBlock">The largest block of the group: neighbours must fit the memory budget for it, the same
    /// check the candidates passed (the sample is usually much smaller than the blocks it stands for).</param>
    private async Task<PipelineSpec> HillClimbAsync(PlanGroup g, PipelineSpec best, double bestScore, byte[] sample, Screen? screen,
        long biggestBlock, ITrialRunner trials, CancellationToken ct)
    {
        int budget = o.Profile.SearchEvals;
        var tried = new HashSet<PipelineSpec> { best };
        int screened = 0, promoted = 0, screenLength = screen?.Sample.Length ?? 0;
        while (budget > 0)
        {
            var neighbours = Neighbours(g, best).Where(n => tried.Add(n) && Allowed(g, n, biggestBlock)).Take(budget).ToList();
            if (neighbours.Count == 0) break;
            budget -= neighbours.Count;
            var candidates = neighbours;
            Dictionary<PipelineSpec, double>? smallScores = null;
            int keep = Math.Max(2, (int)Math.Ceiling(neighbours.Count / (double)o.Profile.Eta));
            if (screen is { } sc && ScreeningPays(neighbours.Count, keep, sc.Sample.Length, sample.Length))
            {
                var small = await trials.RunAsync(neighbours, sc.Sample, ct);
                var promising = neighbours.Select((s, i) => (s, score: small[i] < 0 ? double.MaxValue : Objective(s, small[i], sc.Sample.Length)))
                    .OrderBy(t => t.score).ThenBy(t => t.s.ToString(), StringComparer.Ordinal)
                    .Take(keep).Where(t => t.score <= sc.Score * (1 + ScreenMargin)).ToList();
                screened += neighbours.Count;
                promoted += promising.Count;
                if (promising.Count == 0) break; // nothing promising: as if no neighbour were better
                smallScores = promising.ToDictionary(t => t.s, t => t.score);
                candidates = [.. promising.Select(t => t.s)];
            }
            var sizes = await trials.RunAsync(candidates, sample, ct);
            var top = candidates.Select((s, i) => (s, score: sizes[i] < 0 ? double.MaxValue : Objective(s, sizes[i], sample.Length)))
                .OrderBy(t => t.score).ThenBy(t => t.s.ToString(), StringComparer.Ordinal).First();
            if (top.score < bestScore * (1 - 1e-4))
            {
                g.Explain.Add($"  search: {top.s} improves {bestScore:N0} → {top.score:N0}");
                best = top.s;
                bestScore = top.score;
                screen = screen is { } moved && smallScores is not null ? moved with { Score = smallScores[best] } : null;
            }
            else break;
        }
        if (screened > 0)
            g.Explain.Add($"  screening: {screened} neighbour(s) on {screenLength:N0} B, {promoted} promoted to {sample.Length:N0} B");
        return best;
    }

    /// <summary>±1 step on every tunable parameter dimension, plus removing each filter / adding a natural one.</summary>
    private IEnumerable<PipelineSpec> Neighbours(PlanGroup g, PipelineSpec spec)
    {
        for (int i = 0; i < spec.Stages.Count; i++)
        {
            var st = spec.Stages[i];
            foreach (var d in st.Method.Params)
            {
                var dim = d.Dimension;
                if (dim.Count < 2) continue;
                long cur = st.Params.Get(d.Name, d.Default);
                int idx = 0;
                for (int k = 1; k < dim.Count; k++) if (Math.Abs(dim[k] - cur) < Math.Abs(dim[idx] - cur)) idx = k;
                foreach (int ni in new[] { idx - 1, idx + 1 })
                {
                    if (ni < 0 || ni >= dim.Count || dim[ni] == cur) continue;
                    var stages = spec.Stages.ToList();
                    stages[i] = new StageSpec(st.Method, st.Params.With(d.Name, dim[ni]));
                    PipelineSpec? n = null;
                    try { n = new PipelineSpec(stages); } catch (ArgumentException) { }
                    if (n is not null) yield return n;
                }
            }
        }
        var filters = spec.Filters.ToList();
        for (int i = 0; i < filters.Count; i++)
            yield return spec.WithFilters(filters.Where((_, k) => k != i));
        string? natural = g.Class switch
        {
            CompatClass.CodeX86 => Candidates.BranchFilter(g.Rep),
            CompatClass.CodeArm64 => "arm64",
            CompatClass.Text => "text",
            CompatClass.Structured when g.Rep is { Stats.Stride: > 1 } => $"delta(stride={g.Rep.Stats.Stride})",
            _ => null,
        };
        if (natural is not null && filters.Count < 3 && !Candidates.HasNaturalFilter(spec, natural))
        {
            var add = PipelineSpec.Parse(natural + "+store", _reg).Stages[0];
            yield return spec.WithFilters(filters.Prepend(add));
        }
    }

    // ------------------------------------------------------------------ samples

    /// <summary>
    /// Deterministic sample of a group: the whole group when it fits, a prefix for region-filtered classes
    /// (audio/image parameters are absolute offsets), otherwise 16 aligned slices spread across the group.
    /// Only the bytes that end up in the sample are read from the source files.
    /// </summary>
    internal static byte[] BuildSample(ArchivePlan plan, PlanGroup g, long rung)
    {
        var blocks = plan.Blocks.Where(b => b.Group == g.Id).ToList();
        long total = blocks.Sum(b => b.RawSize);
        if (total <= rung || g.Class is CompatClass.Audio or CompatClass.Image)
        {
            long len = Math.Min(total, rung);
            var whole = new byte[len];
            long pos = 0;
            foreach (var b in blocks)
            {
                if (pos >= len) break;
                int n = (int)Math.Min(b.RawSize, len - pos);
                BlockAssembler.AssembleRange(plan, b, 0, whole.AsSpan((int)pos, n));
                pos += n;
            }
            return whole;
        }
        const int slices = 16;
        int sliceLen = (int)(rung / slices);
        sliceLen -= sliceLen % Math.Max(1, g.Alignment);
        var result = new byte[sliceLen * slices];
        for (int s = 0; s < slices; s++)
        {
            long start = (total - sliceLen) * s / (slices - 1);
            start -= start % Math.Max(1, g.Alignment);
            // The slice is [start, start + sliceLen) of the group's blocks laid end to end; it can span blocks.
            long acc = 0;
            int written = 0;
            foreach (var b in blocks)
            {
                if (start + written >= acc + b.RawSize) { acc += b.RawSize; continue; }
                long from = start + written - acc;
                int n = (int)Math.Min(sliceLen - written, b.RawSize - from);
                BlockAssembler.AssembleRange(plan, b, from, result.AsSpan(s * sliceLen + written, n));
                written += n;
                acc += b.RawSize;
                if (written >= sliceLen) break;
            }
        }
        return result;
    }
}
