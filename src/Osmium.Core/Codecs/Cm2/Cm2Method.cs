using Osmium.Core.Pipeline;

namespace Osmium.Core.Codecs.Cm2;

/// <summary>Pipeline adapter for the second-generation Osmium context-mixing codec (method id 8).</summary>
public sealed class Cm2Method : MethodBase
{
    /// <summary>Largest model memory (log2 of 64-byte lines): 24 = a 1 GiB table.</summary>
    public const int MaxMem = 24;

    private static readonly ParamDef[] Defs =
    [
        new("mem", "Model memory (log2 of 64-byte lines)", ParamType.Int, 16, MaxMem, 22,
            "Hash-table size: 20 = 64 MiB, 22 = 256 MiB, 24 = 1 GiB. Same memory is needed to decompress.", [18, 20, 22, 24], Tunable: false),
        new("models", "Model set (flags)", ParamType.Int, 0, (int)Cm2Models.All, (int)Cm2Models.Default,
            "Bit flags selecting context models: orders 2/3/4/5/6/8, word, word pair, sparse A/B/C, match, record, x86 (8192).",
            [(int)Cm2Models.Default, (int)Cm2Models.Text, (int)Cm2Models.Binary, (int)Cm2Models.Exe, (int)Cm2Models.All]),
        new("limit", "Counter adaptation limit", ParamType.Int, 2, 1023, 255,
            "Order-0/1 counters: higher = more stationary statistics, lower = adapts faster to changing data.", [60, 127, 255, 1023]),
        new("lr", "Mixer learning rate", ParamType.Int, 1, 64, 16, "First-layer mixer learning rate.", [8, 12, 16, 24]),
        new("lr2", "Final mixer learning rate", ParamType.Int, 1, 64, 10, "Second-layer mixer learning rate.", [6, 10, 16]),
        new("minlen", "Match model minimum length", ParamType.Int, 3, 32, 6, "Shortest repeat the match model will follow.", [4, 6, 8, 12]),
        new("stride", "Record stride", ParamType.Int, 0, 65536, 0,
            "Record length for the record model (0 = off; set automatically when a stride is detected).", Tunable: false),
        new("apm", "SSE adaptation rate", ParamType.Int, 4, 9, 7, "Adaptation shift of the secondary estimation stages.", [6, 7]),
    ];

    public override int Id => MethodIds.Cm2;
    public override string Name => "cm2";
    public override string DisplayName => "Osmium CM2 (context mixing, v2)";
    public override MethodKind Kind => MethodKind.Codec;
    public override string Description =>
        "Osmium's second-generation context-mixing compressor (bit histories, x86/x64 contexts): usually the smallest output, but slow (~0.3 MB/s) in BOTH directions. Needs Osmium 0.2.0 or newer to decompress.";
    public override IReadOnlyList<ParamDef> Params => Defs;
    // Measured at about 0.9x cm's speed on the same input and memory.
    public override double EncodeSpeed(ParamValues p) => 0.55;
    public override double DecodeSpeed(ParamValues p) => 0.55;

    public Cm2Options ToOptions(ParamValues p, long length) => ToOptions(p).FitTo(length);

    public Cm2Options ToOptions(ParamValues p)
    {
        var models = (Cm2Models)(int)Get(p, "models");
        int stride = (int)Get(p, "stride");
        if (stride > 0) models |= Cm2Models.Record;
        return new Cm2Options
        {
            TableBits = (int)Get(p, "mem"),
            Models = models,
            CounterLimit = (int)Get(p, "limit"),
            MixerRate = (int)Get(p, "lr"),
            FinalMixerRate = (int)Get(p, "lr2"),
            MatchMinLen = (int)Get(p, "minlen"),
            MatchBits = Math.Clamp((int)Get(p, "mem"), 16, 24),
            RecordStride = stride,
            ApmRate = (int)Get(p, "apm"),
        }.Validate();
    }

    public override long EncodeMemory(ParamValues p, long len) => ToOptions(p, len).EstimatedMemory + len * 2;
    public override long DecodeMemory(ParamValues p, long len) => ToOptions(p, len).EstimatedMemory + len;

    public override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx) =>
        Cm2Codec.Encode(input, ToOptions(p, input.Length), ctx.Token, done => ctx.Checkpoint(done));

    public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx)
    {
        var output = new byte[outputLength];
        Cm2Codec.Decode(input, output, ToOptions(p, outputLength), ctx.Token, done => ctx.Checkpoint(done));
        return output;
    }
}
