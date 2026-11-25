using Osmium.Core.IO;

namespace Osmium.Core.Pipeline;

/// <summary>A pipeline plus the input size each stage saw when encoding (FORMAT §2.2).</summary>
public sealed record BlockDescriptor(PipelineSpec Spec, long[] InputSizes)
{
    public long RawSize => InputSizes[0];

    /// <summary>
    /// Set when this version of Osmium cannot decode the block, because it uses a method, parameter or parameter value
    /// of a newer version. Only <see cref="DeserializeOrStandIn"/> returns such descriptors (for the archive index, so
    /// that the archive still opens and lists): <see cref="Spec"/> is then a stand-in that names what is missing and
    /// throws this exception when it is run.
    /// </summary>
    public NewerVersionException? NewerVersion { get; private init; }

    /// <summary>The descriptor bytes as read, kept for stand-ins so that they serialize unchanged.</summary>
    private byte[]? Stored { get; init; }

    public byte[] Serialize()
    {
        if (Stored is not null) return Stored.ToArray();
        var w = new BlobWriter().U((ulong)Spec.Stages.Count);
        for (int i = 0; i < Spec.Stages.Count; i++)
        {
            var s = Spec.Stages[i];
            w.U((ulong)s.Method.Id).U((ulong)InputSizes[i]).Bytes(s.Params.Serialize());
        }
        return w.ToArray();
    }

    /// <summary>
    /// Reads a descriptor. A method id, a parameter name or a parameter value this version does not know raises
    /// <see cref="NewerVersionException"/>. Values are checked, never clamped: a value written by a newer version
    /// would decode differently.
    /// </summary>
    /// <param name="location">Where the descriptor is from, e.g. "block 3" or "the index", for the error message.</param>
    public static BlockDescriptor Deserialize(ReadOnlySpan<byte> blob, MethodRegistry registry, string? location = null)
    {
        var (ids, sizes, parameters) = ReadStages(blob);
        var stages = new List<StageSpec>(ids.Length);
        for (int i = 0; i < ids.Length; i++) stages.Add(StoredStage(ids[i], parameters[i], registry, location));
        return new BlockDescriptor(Pipeline(stages, location), sizes);
    }

    /// <summary>
    /// Like <see cref="Deserialize"/>, but a block that needs a newer version of Osmium yields a stand-in descriptor
    /// carrying <see cref="NewerVersion"/> instead of an exception, so that only that block is undecodable, never the
    /// whole archive (FORMAT §2.2). Structurally invalid descriptors still throw.
    /// </summary>
    public static BlockDescriptor DeserializeOrStandIn(ReadOnlySpan<byte> blob, MethodRegistry registry, string location)
    {
        var (ids, sizes, parameters) = ReadStages(blob);
        var stages = new List<StageSpec>(ids.Length);
        try
        {
            for (int i = 0; i < ids.Length; i++) stages.Add(StoredStage(ids[i], parameters[i], registry, location));
        }
        catch (NewerVersionException e)
        {
            return new BlockDescriptor(PipelineSpec.Of(new NewerVersionStandIn(e)), [sizes[0]])
            {
                NewerVersion = e,
                Stored = blob.ToArray(),
            };
        }
        return new BlockDescriptor(Pipeline(stages, location), sizes);
    }

    private static (int[] Ids, long[] Sizes, ParamValues[] Params) ReadStages(ReadOnlySpan<byte> blob)
    {
        var r = new BlobReader(blob);
        int n = r.Int(PipelineSpec.MaxStages);
        if (n < 1) throw new InvalidDataException("Empty pipeline descriptor.");
        var ids = new int[n];
        var sizes = new long[n];
        var parameters = new ParamValues[n];
        for (int i = 0; i < n; i++)
        {
            ids[i] = r.Int(65535);
            ulong size = r.U();
            if (size > (ulong)BlockLimits.MaxRawSize) throw new InvalidDataException($"Stage size {size} exceeds the 1 GiB block limit.");
            sizes[i] = (long)size;
            parameters[i] = ParamValues.Deserialize(r.Bytes(4096));
        }
        if (!r.End) throw new InvalidDataException("Trailing bytes in pipeline descriptor.");
        return (ids, sizes, parameters);
    }

    /// <summary>
    /// Resolves one stored stage: the method must exist and every parameter must be one it declares, within the
    /// declared range. Only parameters that are absent take their defaults (when the spec is built); nothing is clamped
    /// or dropped, unlike specs built from user or planner input (<see cref="MethodBase.Normalize"/>).
    /// </summary>
    internal static StageSpec StoredStage(int id, ParamValues p, MethodRegistry registry, string? location)
    {
        var m = registry.ById(id) ?? throw new UnknownMethodException(id, location);
        foreach (var kv in p.All)
        {
            var def = m.Params.FirstOrDefault(d => d.Name == kv.Key)
                ?? throw new NewerVersionException($"parameter {m.Name}.{kv.Key}", location);
            if (!def.Accepts(kv.Value)) throw new NewerVersionException($"parameter {m.Name}.{kv.Key}={kv.Value}", location);
        }
        return new StageSpec(m, p);
    }

    /// <summary>Builds the pipeline of stored stages; an impossible stage order (a codec before a filter) is corrupt data.</summary>
    internal static PipelineSpec Pipeline(List<StageSpec> stages, string? location)
    {
        try { return new PipelineSpec(stages); }
        catch (ArgumentException e) { throw new CorruptDataException($"Invalid pipeline{(location is null ? "" : " in " + location)}: {e.Message}", e); }
    }
}

/// <summary>Thrown for structurally invalid or corrupted archive data.</summary>
public class CorruptDataException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// The archive uses something this version of Osmium does not know (a method id, a parameter name or value, a format
/// feature) because a newer version made it. The message says so instead of calling the archive corrupt. Derives from
/// <see cref="CorruptDataException"/>, so existing handlers still catch it.
/// </summary>
public class NewerVersionException(string feature, string? location = null)
    : CorruptDataException($"This archive needs a newer version of Osmium: {location ?? "it"} uses {feature}.")
{
    /// <summary>What this version does not know, e.g. "method id 8" or "parameter cm.models=8191".</summary>
    public string Feature => feature;

    /// <summary>Where it is used, e.g. "block 3" or "the index"; null when not known.</summary>
    public string? Location => location;

    /// <summary>A new exception with the same details, for throwing again (an exception object is not shared between throws).</summary>
    internal virtual NewerVersionException Again() => new(Feature, Location);
}

public sealed class UnknownMethodException(int id, string? location = null) : NewerVersionException($"method id {id}", location)
{
    public int MethodId => id;

    internal override NewerVersionException Again() => new UnknownMethodException(MethodId, Location);
}

/// <summary>
/// The only stage of the stand-in pipeline of a block that needs a newer version: listings show its name, and
/// running it throws the <see cref="NewerVersionException"/> it stands for.
/// </summary>
internal sealed class NewerVersionStandIn(NewerVersionException reason) : IMethod
{
    public int Id => -1;
    public string Name => $"needs a newer Osmium ({reason.Feature})";
    public string DisplayName => Name;
    public MethodKind Kind => MethodKind.Codec;
    public string Description => reason.Message;
    public bool Experimental => false;
    public bool Deterministic => true;
    public IReadOnlyList<ParamDef> Params => [];

    public byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx) => throw reason.Again();
    public byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx) => throw reason.Again();
    public long EncodeMemory(ParamValues p, long inputLength) => 0;
    public long DecodeMemory(ParamValues p, long outputLength) => 0;
    public double EncodeSpeed(ParamValues p) => 1;
    public double DecodeSpeed(ParamValues p) => 1;
}

public static class BlockLimits
{
    public const long MaxRawSize = 1L << 30;
}

/// <summary>Runs pipelines over whole in-memory blocks.</summary>
public static class PipelineExecutor
{
    public static (BlockDescriptor Descriptor, byte[] Payload) Encode(PipelineSpec spec, ReadOnlyMemory<byte> raw, CodecContext ctx)
    {
        if (raw.Length > BlockLimits.MaxRawSize) throw new ArgumentException("Block exceeds 1 GiB.");
        var sizes = new long[spec.Stages.Count];
        ReadOnlyMemory<byte> cur = raw;
        for (int i = 0; i < spec.Stages.Count; i++)
        {
            var s = spec.Stages[i];
            sizes[i] = cur.Length;
            bool isCodec = i == spec.Stages.Count - 1;
            cur = s.Method.Encode(cur, s.Params, isCodec ? ScaleProgress(ctx, cur.Length, raw.Length) : Silent(ctx));
        }
        return (new BlockDescriptor(spec, sizes), AsArray(cur));
    }

    public static byte[] Decode(BlockDescriptor d, ReadOnlyMemory<byte> payload, CodecContext ctx)
    {
        var spec = d.Spec;
        int n = spec.Stages.Count;
        ReadOnlyMemory<byte> cur = payload;
        for (int i = n - 1; i >= 0; i--)
        {
            var s = spec.Stages[i];
            long expect = d.InputSizes[i];
            if (expect > BlockLimits.MaxRawSize) throw new InvalidDataException("Stage size exceeds the block limit.");
            bool isCodec = i == n - 1;
            var outBuf = s.Method.Decode(cur, (int)expect, s.Params, isCodec ? ScaleProgress(ctx, expect, d.RawSize) : Silent(ctx));
            if (outBuf.Length != expect)
                throw new InvalidDataException($"{s.Method.Name} produced {outBuf.Length} bytes, expected {expect}.");
            cur = outBuf;
        }
        return AsArray(cur);
    }

    /// <summary>Exact compressed size of <paramref name="sample"/> under <paramref name="spec"/> (used by the tuner).</summary>
    public static long TrialSize(PipelineSpec spec, ReadOnlyMemory<byte> sample, CodecContext ctx) =>
        Encode(spec, sample, ctx).Payload.Length;

    private static byte[] AsArray(ReadOnlyMemory<byte> m) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(m, out var seg) && seg.Offset == 0 && seg.Count == seg.Array!.Length
            ? seg.Array : m.ToArray();

    private static CodecContext Silent(CodecContext ctx) => new() { Token = ctx.Token, Gate = ctx.Gate };

    private static CodecContext ScaleProgress(CodecContext ctx, long stageLen, long rawLen)
    {
        if (ctx.Progress is null || stageLen <= 0) return ctx;
        double k = (double)rawLen / stageLen;
        var p = ctx.Progress;
        return new CodecContext { Token = ctx.Token, Gate = ctx.Gate, Progress = done => p((long)(done * k)) };
    }
}
