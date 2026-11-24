using System.IO.Hashing;
using System.Text;
using Osmium.Core.IO;

namespace Osmium.Core.Pipeline;

public sealed record StageSpec(IMethod Method, ParamValues Params)
{
    public override string ToString()
    {
        if (Params.Count == 0) return Method.Name;
        var sizeNames = Method.Params.Where(p => p.Type == ParamType.Size).Select(p => p.Name).ToHashSet();
        return $"{Method.Name}({Params.Format(sizeNames.Contains)})";
    }
}

/// <summary>
/// An ordered pipeline: zero or more reversible filters followed by exactly one codec.
/// Canonical text form: <c>x86+lzma(dict=64M,lc=0,lp=2,pb=2)</c>.
/// </summary>
public sealed class PipelineSpec : IEquatable<PipelineSpec>
{
    public const int MaxStages = 8;

    public IReadOnlyList<StageSpec> Stages { get; }

    public PipelineSpec(IEnumerable<StageSpec> stages)
    {
        var list = new List<StageSpec>();
        foreach (var s in stages)
        {
            var p = s.Method is MethodBase mb ? mb.Normalize(s.Params) : s.Params;
            list.Add(new StageSpec(s.Method, p));
        }
        if (list.Count == 0) throw new ArgumentException("A pipeline needs a codec.");
        if (list.Count > MaxStages) throw new ArgumentException($"At most {MaxStages} stages.");
        for (int i = 0; i < list.Count - 1; i++)
            if (list[i].Method.Kind != MethodKind.Filter)
                throw new ArgumentException($"'{list[i].Method.Name}' is a codec; codecs must be the last stage.");
        if (list[^1].Method.Kind != MethodKind.Codec)
            throw new ArgumentException($"The last stage must be a codec, not '{list[^1].Method.Name}'.");
        Stages = list;
    }

    public static PipelineSpec Of(IMethod codec, ParamValues? p = null) => new([new StageSpec(codec, p ?? ParamValues.Empty)]);

    public StageSpec Codec => Stages[^1];
    public IEnumerable<StageSpec> Filters => Stages.Take(Stages.Count - 1);
    public bool IsStore => Stages.Count == 1 && Codec.Method.Id == MethodIds.Store;
    public bool Experimental => Stages.Any(s => s.Method.Experimental);
    public bool Deterministic => Stages.All(s => s.Method.Deterministic);

    public PipelineSpec WithCodec(StageSpec codec) => new(Filters.Append(codec));
    public PipelineSpec WithFilters(IEnumerable<StageSpec> filters) => new(filters.Append(Codec));

    public long EncodeMemory(long len) => Stages.Max(s => s.Method.EncodeMemory(s.Params, len)) + len;
    public long DecodeMemory(long len) => Stages.Max(s => s.Method.DecodeMemory(s.Params, len)) + len;

    /// <summary>Deterministic cost-model estimate in reference-core seconds.</summary>
    public double EstimateEncodeSeconds(long len) => Stages.Sum(s => len / (s.Method.EncodeSpeed(s.Params) * 1e6));
    public double EstimateDecodeSeconds(long len) => Stages.Sum(s => len / (s.Method.DecodeSpeed(s.Params) * 1e6));

    public byte[] Serialize()
    {
        var w = new BlobWriter().U((ulong)Stages.Count);
        foreach (var s in Stages) w.U((ulong)s.Method.Id).Bytes(s.Params.Serialize());
        return w.ToArray();
    }

    /// <summary>Reads the form written by <see cref="Serialize"/>. Like <see cref="BlockDescriptor.Deserialize"/>, it rejects
    /// method ids, parameter names and values this version does not know with <see cref="NewerVersionException"/>
    /// rather than dropping or clamping them.</summary>
    public static PipelineSpec Deserialize(ReadOnlySpan<byte> blob, MethodRegistry registry)
    {
        var r = new BlobReader(blob);
        int n = r.Int(MaxStages);
        var stages = new List<StageSpec>(n);
        for (int i = 0; i < n; i++)
        {
            int id = r.Int(65535);
            stages.Add(BlockDescriptor.StoredStage(id, ParamValues.Deserialize(r.Bytes(4096)), registry, null));
        }
        return BlockDescriptor.Pipeline(stages, null);
    }

    public ulong Hash => XxHash3.HashToUInt64(Serialize());

    public static PipelineSpec Parse(string text, MethodRegistry registry)
    {
        var stages = new List<StageSpec>();
        foreach (var part in SplitTopLevel(text))
        {
            string name = part;
            var p = ParamValues.Empty;
            int open = part.IndexOf('(');
            if (open >= 0)
            {
                if (!part.EndsWith(')')) throw new FormatException($"Missing ')' in '{part}'.");
                name = part[..open];
                p = ParamValues.Parse(part[(open + 1)..^1]);
            }
            name = name.Trim().ToLowerInvariant();
            var m = registry.ByName(name) ?? throw new FormatException($"Unknown method '{name}'. Known: {string.Join(", ", registry.All.Select(x => x.Name))}.");
            foreach (var kv in p.All)
                if (!m.Params.Any(d => d.Name == kv.Key))
                    throw new FormatException($"'{m.Name}' has no parameter '{kv.Key}'. Valid: {string.Join(", ", m.Params.Select(d => d.Name))}.");
            stages.Add(new StageSpec(m, p));
        }
        return new PipelineSpec(stages);
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == '+' && depth == 0)
            {
                var s = text[start..i].Trim();
                if (s.Length > 0) yield return s;
                start = i + 1;
            }
        }
        var last = text[start..].Trim();
        if (last.Length > 0) yield return last;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var s in Stages)
        {
            if (sb.Length > 0) sb.Append('+');
            sb.Append(s);
        }
        return sb.ToString();
    }

    public bool Equals(PipelineSpec? other) => other is not null && Serialize().AsSpan().SequenceEqual(other.Serialize());
    public override bool Equals(object? obj) => Equals(obj as PipelineSpec);
    public override int GetHashCode() => (int)Hash;
}
