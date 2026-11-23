using Osmium.Core.Codecs.Cm;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Codecs;

/// <summary>Pipeline adapter for the Osmium context-mixing codec.</summary>
public sealed class CmMethod : MethodBase
{
    private static readonly ParamDef[] Defs =
    [
        new("mem", "Model memory (log2 of 64-byte buckets)", ParamType.Int, 16, 26, 22,
            "Hash-table size: 20 = 64 MiB, 22 = 256 MiB, 24 = 1 GiB, 26 = 4 GiB. Same memory is needed to decompress.", [18, 20, 22, 24]),
        new("models", "Model set (flags)", ParamType.Int, 0, (int)CmModels.All, (int)CmModels.Default,
            "Bit flags selecting context models: orders 2/3/4/5/6/8, word, word pair, sparse A/B/C, match, record.",
            [(int)CmModels.Default, (int)CmModels.Text, (int)CmModels.Binary, (int)CmModels.All]),
        new("limit", "Counter adaptation limit", ParamType.Int, 2, 1023, 255,
            "Higher = more stationary statistics, lower = adapts faster to changing data.", [60, 127, 255, 1023]),
        new("lr", "Mixer learning rate", ParamType.Int, 1, 64, 16, "First-layer mixer learning rate.", [8, 12, 16, 24]),
        new("lr2", "Final mixer learning rate", ParamType.Int, 1, 64, 10, "Second-layer mixer learning rate.", [6, 10, 16]),
        new("minlen", "Match model minimum length", ParamType.Int, 3, 32, 6, "Shortest repeat the match model will follow.", [4, 6, 8, 12]),
        new("stride", "Record stride", ParamType.Int, 0, 65536, 0,
            "Record length for the record model (0 = off; set automatically when a stride is detected)."),
        new("apm", "SSE adaptation rate", ParamType.Int, 4, 9, 7, "Adaptation shift of the secondary estimation stages.", [6, 7]),
    ];

    public override int Id => MethodIds.Cm;
    public override string Name => "cm";
    public override string DisplayName => "Osmium CM (context mixing)";
    public override MethodKind Kind => MethodKind.Codec;
    public override string Description =>
        "Osmium's own context-mixing compressor: usually the smallest output, but slow (~0.5 MB/s) in BOTH directions.";
    public override IReadOnlyList<ParamDef> Params => Defs;
    public override double EncodeSpeed(ParamValues p) => 0.6;
    public override double DecodeSpeed(ParamValues p) => 0.6;

    public CmOptions ToOptions(ParamValues p, long length) => ToOptions(p).FitTo(length);

    public CmOptions ToOptions(ParamValues p)
    {
        var models = (CmModels)(int)Get(p, "models");
        int stride = (int)Get(p, "stride");
        if (stride > 0) models |= CmModels.Record;
        return new CmOptions
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
        CmCodec.Encode(input, ToOptions(p, input.Length), ctx.Token, done => ctx.Checkpoint(done));

    public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx)
    {
        var output = new byte[outputLength];
        CmCodec.Decode(input, output, ToOptions(p, outputLength), ctx.Token, done => ctx.Checkpoint(done));
        return output;
    }
}
