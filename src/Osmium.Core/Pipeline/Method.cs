namespace Osmium.Core.Pipeline;

public enum MethodKind { Codec, Filter }

public enum ParamType { Int, Size, Bool, Choice }

/// <summary>
/// Describes one tunable parameter. The UI renders editors from this; the tuner walks
/// <see cref="SearchValues"/> as an ordered dimension (±1 step = neighbouring value).
/// </summary>
public sealed record ParamDef(
    string Name,
    string Label,
    ParamType Type,
    long Min,
    long Max,
    long Default,
    string Description,
    IReadOnlyList<long>? SearchValues = null,
    IReadOnlyList<string>? Choices = null,
    bool Experimental = false,
    bool Tunable = true)
{
    /// <summary>For values from user or planner input. Values read from an archive are checked with
    /// <see cref="Accepts"/> instead and never clamped.</summary>
    public long Clamp(long v) => Math.Clamp(v, Min, Max);

    public bool Accepts(long v) => v >= Min && v <= Max;

    public IReadOnlyList<long> Dimension =>
        !Tunable ? [Default] : SearchValues ?? (Type == ParamType.Bool ? [0, 1]
            : Max - Min <= 16 ? Enumerable.Range(0, (int)(Max - Min + 1)).Select(i => Min + i).ToArray()
            : [Default]);
}

/// <summary>Pause/cancel/progress plumbing handed to every codec and filter.</summary>
public sealed class CodecContext
{
    public static readonly CodecContext None = new();

    public CancellationToken Token { get; init; }
    public PauseGate? Gate { get; init; }
    /// <summary>Called with the number of input bytes consumed so far by the current stage.</summary>
    public Action<long>? Progress { get; init; }
    /// <summary>Threads one encode may use (1 by default). Every encoder uses one thread for now: the LZMA SDK's
    /// helper thread would change the bytes of some blocks.</summary>
    public int MaxThreads { get; init; } = 1;

    public void Checkpoint(long done)
    {
        Gate?.WaitIfPaused(Token);
        Token.ThrowIfCancellationRequested();
        Progress?.Invoke(done);
    }
}

/// <summary>A resettable gate: <see cref="WaitIfPaused"/> blocks while paused.</summary>
public sealed class PauseGate
{
    private readonly ManualResetEventSlim _open = new(true);
    public bool IsPaused => !_open.IsSet;
    public void Pause() => _open.Reset();
    public void Resume() => _open.Set();
    public void WaitIfPaused(CancellationToken ct)
    {
        if (!_open.IsSet) _open.Wait(ct);
    }
}

/// <summary>A codec or a reversible filter.</summary>
public interface IMethod
{
    int Id { get; }
    /// <summary>Short machine name used in pipeline strings and rules (e.g. "lzma", "x86").</summary>
    string Name { get; }
    string DisplayName { get; }
    MethodKind Kind { get; }
    string Description { get; }
    bool Experimental { get; }
    /// <summary>True when output is byte-identical on every platform (managed, single-threaded, integer).</summary>
    bool Deterministic { get; }
    IReadOnlyList<ParamDef> Params { get; }

    byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx);
    byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx);

    long EncodeMemory(ParamValues p, long inputLength);
    long DecodeMemory(ParamValues p, long outputLength);
    /// <summary>Reference single-core throughput in MB/s (cost model; deterministic, not measured).</summary>
    double EncodeSpeed(ParamValues p);
    double DecodeSpeed(ParamValues p);
}

/// <summary>Shared defaults/normalization for method implementations.</summary>
public abstract class MethodBase : IMethod
{
    public abstract int Id { get; }
    public abstract string Name { get; }
    public abstract string DisplayName { get; }
    public abstract MethodKind Kind { get; }
    public abstract string Description { get; }
    public virtual bool Experimental => false;
    public virtual bool Deterministic => true;
    public virtual IReadOnlyList<ParamDef> Params => [];

    public abstract byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx);
    public abstract byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx);

    public virtual long EncodeMemory(ParamValues p, long inputLength) => inputLength * 2 + (1 << 20);
    public virtual long DecodeMemory(ParamValues p, long outputLength) => outputLength * 2 + (1 << 20);
    public virtual double EncodeSpeed(ParamValues p) => 200;
    public virtual double DecodeSpeed(ParamValues p) => 400;

    /// <summary>
    /// Fills defaults, drops unknown names, clamps to declared ranges. Right for specs built from user or planner input
    /// (pipeline text, rules, the tuner); specs read from an archive are checked first and rejected instead
    /// (<see cref="BlockDescriptor.Deserialize"/>), so for them this only fills defaults.
    /// </summary>
    public ParamValues Normalize(ParamValues p)
    {
        var list = new List<KeyValuePair<string, long>>();
        foreach (var d in Params)
            list.Add(new(d.Name, d.Clamp(p.Get(d.Name, d.Default))));
        return ParamValues.From(list);
    }

    protected long Get(ParamValues p, string name)
    {
        foreach (var d in Params)
            if (d.Name == name) return d.Clamp(p.Get(name, d.Default));
        throw new ArgumentException($"Unknown parameter '{name}' for {Name}.");
    }

    protected static void Chunked(ReadOnlySpan<byte> input, CodecContext ctx, int chunk, Action<ReadOnlySpan<byte>> sink)
    {
        int pos = 0;
        while (pos < input.Length)
        {
            int n = Math.Min(chunk, input.Length - pos);
            sink(input.Slice(pos, n));
            pos += n;
            ctx.Checkpoint(pos);
        }
    }

    protected static void ReadExactly(Stream s, Span<byte> dst)
    {
        int total = 0;
        while (total < dst.Length)
        {
            int r = s.Read(dst[total..]);
            if (r <= 0) throw new InvalidDataException($"Stream ended after {total} of {dst.Length} bytes.");
            total += r;
        }
    }
}
