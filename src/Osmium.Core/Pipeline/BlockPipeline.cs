using Osmium.Core.IO;

namespace Osmium.Core.Pipeline;

/// <summary>A pipeline plus the input size each stage saw when encoding (FORMAT §2.2).</summary>
public sealed record BlockDescriptor(PipelineSpec Spec, long[] InputSizes)
{
    public long RawSize => InputSizes[0];

    public byte[] Serialize()
    {
        var w = new BlobWriter().U((ulong)Spec.Stages.Count);
        for (int i = 0; i < Spec.Stages.Count; i++)
        {
            var s = Spec.Stages[i];
            w.U((ulong)s.Method.Id).U((ulong)InputSizes[i]).Bytes(s.Params.Serialize());
        }
        return w.ToArray();
    }

    public static BlockDescriptor Deserialize(ReadOnlySpan<byte> blob, MethodRegistry registry)
    {
        var r = new BlobReader(blob);
        int n = r.Int(PipelineSpec.MaxStages);
        if (n < 1) throw new InvalidDataException("Empty pipeline descriptor.");
        var stages = new List<StageSpec>(n);
        var sizes = new long[n];
        for (int i = 0; i < n; i++)
        {
            int id = r.Int(65535);
            sizes[i] = (long)r.U();
            if (sizes[i] > BlockLimits.MaxRawSize) throw new InvalidDataException($"Stage size {sizes[i]} exceeds the 1 GiB block limit.");
            var m = registry.ById(id) ?? throw new UnknownMethodException(id);
            var p = ParamValues.Deserialize(r.Bytes(4096));
            foreach (var kv in p.All)
            {
                var def = m.Params.FirstOrDefault(d => d.Name == kv.Key)
                    ?? throw new CorruptDataException($"Method '{m.Name}' has no parameter '{kv.Key}' (archive made by a newer Osmium?).");
                if (kv.Value < def.Min || kv.Value > def.Max)
                    throw new CorruptDataException($"Parameter {m.Name}.{kv.Key}={kv.Value} is out of range.");
            }
            stages.Add(new StageSpec(m, p));
        }
        if (!r.End) throw new InvalidDataException("Trailing bytes in pipeline descriptor.");
        return new BlockDescriptor(new PipelineSpec(stages), sizes);
    }
}

/// <summary>Thrown for structurally invalid or corrupted archive data.</summary>
public class CorruptDataException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class UnknownMethodException(int id)
    : CorruptDataException($"Method {id} is not supported by this version of Osmium (archive made by a newer version?).")
{
    public int MethodId => id;
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
