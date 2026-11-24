using System.IO.Hashing;
using System.Text;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests;

/// <summary>
/// A deterministic <see cref="ITrialRunner"/>: every size comes from a function of the spec and the sample, and every
/// call is recorded, so tests can follow successive halving and the hill-climb without compressing anything.
/// </summary>
internal sealed class ScriptedTrials(Func<PipelineSpec, byte[], long> size) : ITrialRunner
{
    public sealed record Call(string[] Specs, long[] Sizes, int SampleLength);

    private readonly List<Call> _calls = [];

    /// <summary>Optional delay per call, to make concurrently planned groups finish in a different order.</summary>
    public Func<int, TimeSpan>? Delay { get; init; }

    public IReadOnlyList<Call> Calls { get { lock (_calls) return [.. _calls]; } }

    public async Task<long[]> RunAsync(IReadOnlyList<PipelineSpec> specs, byte[] sample, CancellationToken ct)
    {
        var sizes = specs.Select(s => size(s, sample)).ToArray();
        int n;
        lock (_calls)
        {
            _calls.Add(new Call([.. specs.Select(s => s.ToString())], sizes, sample.Length));
            n = _calls.Count;
        }
        if (Delay is not null) await Task.Delay(Delay(n), ct);
        return sizes;
    }

    /// <summary>A stable hash (unlike string.GetHashCode, which changes from run to run).</summary>
    public static ulong StableHash(string s) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(s));
}

internal static class PlannerFixtures
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static PlanOptions Options(Profile profile, ITrialRunner? trials, long memory = 2L << 30, int threads = 2) => new()
    {
        Profile = profile,
        MemoryBudget = memory,
        Threads = threads,
        Trials = trials,
    };

    public static async Task<ArchivePlan> PlanFile(TempDir t, string name, byte[] data, PlanOptions options)
    {
        var path = t.Sub(name);
        File.WriteAllBytes(path, data);
        return await new Planner(options).BuildAsync(Scanner.Scan([path]), Ct);
    }

    /// <summary>Everything the planner decided, as text: two plans are the same plan when these are equal.</summary>
    public static string Fingerprint(ArchivePlan p)
    {
        var sb = new StringBuilder();
        foreach (var f in p.Files)
            sb.AppendLine($"F {f.Src.RelPath} {f.Class} group={f.Group} dup={f.DuplicateOf} units=[{string.Join(",", f.Units)}]");
        foreach (var u in p.Units)
            sb.AppendLine($"U{u.Id} file={u.File} {u.Offset}+{u.Length}");
        foreach (var g in p.Groups)
        {
            sb.AppendLine($"G{g.Id} {g.Key} {g.Class} align={g.Alignment} size={g.Size} units=[{string.Join(",", g.Units)}]");
            sb.AppendLine($"  spec={g.Spec} alternates=[{string.Join("; ", g.Alternates)}]");
            foreach (var line in g.Explain) sb.AppendLine("  " + line);
        }
        foreach (var b in p.Blocks)
            sb.AppendLine($"B{b.Id} group={b.Group} raw={b.RawSize} " + string.Join(" ", b.Parts.Select(x => $"{x.Unit}:{x.UnitOffset}+{x.Length}@{x.BlockOffset}")));
        return sb.ToString();
    }
}
