namespace Osmium.Core.Planning;

public enum ProfileLevel { Store, Fast, Balanced, Max, Ultra, Insane, Exhaustive }

public enum DedupMode { Off, Files, Chunks }

/// <summary>A compression profile (BLUEPRINT §8.1). Every field can be overridden per job or by settings.</summary>
public sealed record Profile
{
    public required string Name { get; init; }
    public required ProfileLevel Level { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Stratified 64 KiB windows sampled per file for analysis.</summary>
    public int SampleWindows { get; init; } = 16;
    /// <summary>Successive-halving sample sizes (empty = no trials, use the seed table).</summary>
    public long[] Rungs { get; init; } = [];
    public int MaxCandidates { get; init; } = 1;
    public int Eta { get; init; } = 3;
    /// <summary>Hill-climb evaluations per group (0 = off).</summary>
    public int SearchEvals { get; init; }
    /// <summary>Insane+: compress each block with the top-2 pipelines and keep the smaller.</summary>
    public bool RaceTopTwo { get; init; }
    /// <summary>Exhaustive: evaluate every candidate on the full group, no sampling.</summary>
    public bool FullDataTrials { get; init; }
    public long BlockCap { get; init; } = 64L << 20;
    public bool AllowCm { get; init; }
    public int CmMem { get; init; } = 22;
    public long LzmaDict { get; init; } = 64L << 20;
    public int ZstdLevel { get; init; } = 19;
    public DedupMode Dedup { get; init; } = DedupMode.Files;
    public bool Experimental { get; init; }
    /// <summary>Experimental: every CM candidate uses the full model set (orders 5/8, all sparse models).</summary>
    public bool CmAllModels { get; init; }
    /// <summary>Order the files of a group so that similar files are adjacent (SimHash of sampled 4-grams, greedy
    /// nearest neighbour) instead of only by extension, name and path. Off in every profile: on the benchmark folders
    /// it made C headers 3 % larger (IMPROVISATION.md §5.4).</summary>
    public bool SimilarityOrder { get; init; }
    /// <summary>When candidates are within 0.1 % of the best after the last rung, choose the one that decodes fastest;
    /// the parameter search then starts from it. Off in every profile: no group of the benchmark folders has such a
    /// near tie, so it changes no archive there (IMPROVISATION.md §5.4).</summary>
    public bool PreferFasterDecoder { get; init; }
    /// <summary>Each parameter-search step tries its neighbours on an earlier rung's sample first (the largest that is at
    /// most half the last one) and compresses only the most promising on the last rung's sample. Groups without such a
    /// rung search as before.</summary>
    public bool ScreenSearch { get; init; }

    public static readonly Profile Store = new()
    {
        Name = "store", Level = ProfileLevel.Store, Description = "No compression; packaging only.",
        SampleWindows = 0, BlockCap = 64L << 20, Dedup = DedupMode.Off,
    };

    public static readonly Profile Fast = new()
    {
        Name = "fast", Level = ProfileLevel.Fast, Description = "zstd level 3; quick to make and to open.",
        SampleWindows = 1, BlockCap = 16L << 20, ZstdLevel = 3, Dedup = DedupMode.Off,
    };

    public static readonly Profile Balanced = new()
    {
        Name = "balanced", Level = ProfileLevel.Balanced, Description = "Smart per-type defaults (LZMA / PPMd / zstd with filters). Fast to decompress.",
        SampleWindows = 16, BlockCap = 32L << 20, LzmaDict = 32L << 20,
    };

    public static readonly Profile Max = new()
    {
        Name = "max", Level = ProfileLevel.Max, Description = "Trial-compresses a few candidates per group; no context mixing, so it still opens fast.",
        SampleWindows = 32, Rungs = [1L << 20], MaxCandidates = 4, BlockCap = 256L << 20, LzmaDict = 64L << 20,
    };

    public static readonly Profile Ultra = new()
    {
        Name = "ultra", Level = ProfileLevel.Ultra, Description = "Context mixing allowed, two sample rungs and parameter search.",
        SampleWindows = 64, Rungs = [1L << 20, 4L << 20], MaxCandidates = 10, SearchEvals = 24,
        BlockCap = 512L << 20, AllowCm = true, CmMem = 22, LzmaDict = 128L << 20, ScreenSearch = true,
    };

    public static readonly Profile Insane = new()
    {
        Name = "insane", Level = ProfileLevel.Insane, Description = "Everything: up to 40 candidates, 4 rungs, deep search, top-2 race on full blocks, chunk dedup. Very slow; CM archives also decompress slowly.",
        SampleWindows = 128, Rungs = [256L << 10, 1L << 20, 4L << 20, 16L << 20], MaxCandidates = 40, SearchEvals = 48,
        RaceTopTwo = true, BlockCap = 1L << 30, AllowCm = true, CmMem = 24, LzmaDict = 256L << 20, ZstdLevel = 22,
        Dedup = DedupMode.Chunks, ScreenSearch = true,
    };

    public static readonly Profile Exhaustive = new()
    {
        Name = "exhaustive", Level = ProfileLevel.Exhaustive, Experimental = true,
        Description = "EXPERIMENTAL: every candidate is compressed on the full data, then hill-climbed. Can take days.",
        SampleWindows = 128, Rungs = [16L << 20], MaxCandidates = 200, SearchEvals = 96, RaceTopTwo = true,
        FullDataTrials = true, BlockCap = 1L << 30, AllowCm = true, CmMem = 24, LzmaDict = 256L << 20, ZstdLevel = 22,
        Dedup = DedupMode.Chunks,
    };

    public static IReadOnlyList<Profile> All { get; } = [Store, Fast, Balanced, Max, Ultra, Insane, Exhaustive];

    public static Profile ByName(string name) =>
        All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown profile '{name}'. Known: {string.Join(", ", All.Select(p => p.Name))}.");
}
