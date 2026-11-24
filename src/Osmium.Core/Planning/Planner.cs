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

public sealed class LocalTrialRunner(int threads, MemoryGate gate) : ITrialRunner
{
    public Task<long[]> RunAsync(IReadOnlyList<PipelineSpec> specs, byte[] sample, CancellationToken ct)
    {
        var sizes = new long[specs.Count];
        Parallel.For(0, specs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct }, i =>
        {
            long mem = specs[i].EncodeMemory(sample.Length);
            using var lease = gate.Acquire(mem, ct);
            try { sizes[i] = PipelineExecutor.TrialSize(specs[i], sample, new CodecContext { Token = ct }); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { sizes[i] = -1; }
        });
        return Task.FromResult(sizes);
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
                if (p.Level != ProfileLevel.Store && f.Src.Size > 0) f.Analysis = Sniffer.Analyze(f.Src.FullPath, p.SampleWindows);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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
            long cap = o.BlockCap ?? p.BlockCap;
            if (f.Class is CompatClass.Audio or CompatClass.Image && f.Src.Size > cap) f.Class = CompatClass.Structured;
            plan.Files.Add(f);
        }

        // 2. dedup → units
        var dedup = o.Dedup ?? p.Dedup;
        Dedupe(plan, dedup, ct);

        // 3. groups and layout
        BuildGroups(plan);
        Layout(plan, o.BlockCap ?? p.BlockCap);

        // 4. choose a pipeline for each group
        var gate = new MemoryGate(o.MemoryBudget);
        var trials = o.Trials ?? new LocalTrialRunner(o.Threads, gate);
        int done = 0;
        foreach (var g in plan.Groups.OrderByDescending(g => g.Size))
        {
            ct.ThrowIfCancellationRequested();
            await ChooseAsync(plan, g, trials, ct);
            o.Progress?.Invoke("Planning", (double)++done / plan.Groups.Count);
        }
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
                    (chunks, var h) = DedupScanner.Chunk(f.Src.FullPath, ct);
                    f.Hash = h;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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
                    try { sha = Convert.ToHexString(DedupScanner.Sha256File(files[i].Src.FullPath, ct)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
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

    private void BuildGroups(ArchivePlan plan)
    {
        var byKey = new Dictionary<string, PlanGroup>();
        var order = Enumerable.Range(0, plan.Files.Count)
            .OrderBy(i => Path.GetExtension(plan.Files[i].Src.RelPath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => Path.GetFileName(plan.Files[i].Src.RelPath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => plan.Files[i].Src.RelPath, StringComparer.Ordinal);
        foreach (int i in order)
        {
            var f = plan.Files[i];
            var own = f.Units.Where(u => plan.Units[u].File == i).ToList();
            if (own.Count == 0) continue;
            string solid = f.Rules.Solid;
            string key = f.Rules.Pipeline is not null ? $"F:{f.Rules.Pipeline}|{solid}|{f.Rules.Key}"
                : solid == "own" || f.Class is CompatClass.Audio or CompatClass.Image ? $"own:{i}"
                : solid.StartsWith("group:", StringComparison.OrdinalIgnoreCase) ? $"G:{solid}|{f.Rules.Key}"
                : $"C:{f.Class}|{(f.Class == CompatClass.Structured ? f.Analysis?.Stats.Stride : 0)}|{f.Rules.Key}";
            if (!byKey.TryGetValue(key, out var g))
            {
                g = new PlanGroup { Id = plan.Groups.Count, Key = key, Class = f.Class, Rep = f.Analysis, Rules = f.Rules, Forced = f.Rules.Pipeline is not null };
                byKey[key] = g;
                plan.Groups.Add(g);
            }
            if (f.Analysis is not null && (g.Rep is null || f.Src.Size > (g.Rep.Stats.SampledBytes))) g.Rep ??= f.Analysis;
            foreach (var u in own) { g.Units.Add(u); g.Size += plan.Units[u].Length; }
            f.Group = g.Id;
        }
        foreach (var g in plan.Groups)
        {
            g.Alignment = g.Class switch
            {
                CompatClass.CodeArm64 => 4,
                CompatClass.Utf16Text => 2,
                CompatClass.Structured => Math.Max(1, g.Rep?.Stats.Stride ?? 1),
                _ => 1,
            };
        }
    }

    private void Layout(ArchivePlan plan, long profileCap)
    {
        // Keep enough headroom for raw + filtered + compressed copies and verification.
        long memCap = Math.Max(16L << 20, o.MemoryBudget / 6);
        long cap = Math.Clamp(Math.Min(profileCap, memCap), 1L << 16, BlockLimits.MaxRawSize);
        foreach (var g in plan.Groups)
        {
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
        if (s.Codec.Method.Id == MethodIds.Cm && !o.Profile.AllowCm) return false;
        return s.EncodeMemory(blockSize) <= o.MemoryBudget && s.DecodeMemory(blockSize) <= o.MemoryBudget;
    }

    private async Task ChooseAsync(ArchivePlan plan, PlanGroup g, ITrialRunner trials, CancellationToken ct)
    {
        var p = o.Profile;
        long biggestBlock = plan.Blocks.Where(b => b.Group == g.Id).Select(b => b.RawSize).DefaultIfEmpty(0).Max();
        g.Explain.Add($"group {g.Id}: {g.Class}, {g.Units.Count} unit(s), {g.Size:N0} bytes{(g.Rep is null ? "" : ", e.g. " + g.Rep.Format)}");
        foreach (var pr in g.Rules.Provenance.Distinct()) g.Explain.Add("  rule: " + pr);

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
            g.Spec = allowed.FirstOrDefault(s => s.Codec.Method.Id != MethodIds.Cm) ?? allowed[0];
            g.Explain.Add($"  chose {g.Spec} ({(g.Size < 4096 ? "tiny group" : "profile default")})");
            return;
        }

        var alive = allowed.Take(p.MaxCandidates).ToList();
        var scores = new Dictionary<PipelineSpec, double>();
        byte[] sample = [];
        foreach (long rung in p.FullDataTrials ? [Math.Min(g.Size, BlockLimits.MaxRawSize)] : p.Rungs)
        {
            sample = BuildSample(plan, g, rung);
            var sizes = await trials.RunAsync(alive, sample, ct);
            var ranked = alive.Select((s, i) => (s, size: sizes[i])).Where(t => t.size >= 0)
                .OrderBy(t => Objective(t.s, t.size, sample.Length)).ThenBy(t => t.s.ToString(), StringComparer.Ordinal).ToList();
            g.Explain.Add($"  rung {sample.Length:N0} B: " + string.Join(", ", ranked.Select(t => $"{t.s} = {t.size:N0}")));
            if (ranked.Count == 0) break;
            foreach (var (s, size) in ranked) scores[s] = Objective(s, size, sample.Length);
            int keep = Math.Max(2, (int)Math.Ceiling(ranked.Count / (double)p.Eta));
            alive = ranked.Take(keep).Select(t => t.s).ToList();
            if (sample.Length >= g.Size) break;
        }
        if (alive.Count == 0)
        {
            g.Spec = allowed[0];
            g.Explain.Add($"  all trials failed; using {g.Spec}");
            return;
        }

        var best = alive[0];
        // Incompressible data: anything but Store must win by at least 1 %.
        if (g.Class == CompatClass.Incompressible && best.Codec.Method.Id != MethodIds.Store)
        {
            var store = alive.FirstOrDefault(s => s.IsStore);
            if (store is not null && scores[best] > scores[store] * 0.99) best = store;
        }

        if (p.SearchEvals > 0 && !best.IsStore)
            best = await HillClimbAsync(g, best, scores[best], sample, trials, ct);

        g.Spec = best;
        if (p.RaceTopTwo)
        {
            var second = alive.FirstOrDefault(s => !s.Equals(best));
            if (second is not null) g.Alternates.Add(second);
        }
        g.Explain.Add($"  chose {g.Spec}{(g.Alternates.Count > 0 ? $" (race vs {g.Alternates[0]})" : "")}");
    }

    private async Task<PipelineSpec> HillClimbAsync(PlanGroup g, PipelineSpec best, double bestScore, byte[] sample, ITrialRunner trials, CancellationToken ct)
    {
        int budget = o.Profile.SearchEvals;
        var tried = new HashSet<PipelineSpec> { best };
        while (budget > 0)
        {
            var neighbours = Neighbours(g, best).Where(n => tried.Add(n) && Allowed(g, n, sample.Length)).Take(budget).ToList();
            if (neighbours.Count == 0) break;
            budget -= neighbours.Count;
            var sizes = await trials.RunAsync(neighbours, sample, ct);
            var top = neighbours.Select((s, i) => (s, score: sizes[i] < 0 ? double.MaxValue : Objective(s, sizes[i], sample.Length)))
                .OrderBy(t => t.score).ThenBy(t => t.s.ToString(), StringComparer.Ordinal).First();
            if (top.score < bestScore * (1 - 1e-4))
            {
                g.Explain.Add($"  search: {top.s} improves {bestScore:N0} → {top.score:N0}");
                best = top.s;
                bestScore = top.score;
            }
            else break;
        }
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
            CompatClass.CodeX86 => "x86",
            CompatClass.CodeArm64 => "arm64",
            CompatClass.Text => "text",
            CompatClass.Structured when g.Rep is { Stats.Stride: > 1 } => $"delta(stride={g.Rep.Stats.Stride})",
            _ => null,
        };
        if (natural is not null && filters.Count < 3 && !filters.Any(f => natural.StartsWith(f.Method.Name, StringComparison.Ordinal)))
        {
            var add = PipelineSpec.Parse(natural + "+store", _reg).Stages[0];
            yield return spec.WithFilters(filters.Prepend(add));
        }
    }

    // ------------------------------------------------------------------ samples

    /// <summary>
    /// Deterministic sample of a group: the whole group when it fits, a prefix for region-filtered classes
    /// (audio/image parameters are absolute offsets), otherwise 16 aligned slices spread across the group.
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
                var raw = BlockAssembler.Assemble(plan, b, (int)Math.Min(b.RawSize, len - pos));
                raw.CopyTo(whole.AsSpan((int)pos));
                pos += raw.Length;
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
            // locate block containing 'start'
            long acc = 0;
            int written = 0;
            foreach (var b in blocks)
            {
                if (start + written >= acc + b.RawSize) { acc += b.RawSize; continue; }
                var raw = BlockAssembler.Assemble(plan, b, (int)b.RawSize);
                int from = (int)(start + written - acc);
                int n = Math.Min(sliceLen - written, raw.Length - from);
                raw.AsSpan(from, n).CopyTo(result.AsSpan(s * sliceLen + written));
                written += n;
                acc += b.RawSize;
                if (written >= sliceLen) break;
            }
        }
        return result;
    }
}
