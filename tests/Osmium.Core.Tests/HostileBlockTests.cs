using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests;

/// <summary>A stage that fails on hostile input fails its own block as corrupt data, never with a raw runtime exception.</summary>
public class HostileBlockTests
{
    private sealed class ThrowingCodec(Exception error) : MethodBase
    {
        public override int Id => 63;
        public override string Name => "throws";
        public override string DisplayName => "throws";
        public override MethodKind Kind => MethodKind.Codec;
        public override string Description => "a codec whose decoder fails";
        public override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx) => input.ToArray();
        public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx) => throw error;
    }

    public static TheoryData<string> Errors() =>
        ["IndexOutOfRange", "Overflow", "ArgumentOutOfRange", "Argument", "InvalidCast", "DivideByZero", "NullReference"];

    private static Exception Make(string name) => name switch
    {
        "IndexOutOfRange" => new IndexOutOfRangeException("index"),
        "Overflow" => new OverflowException("overflow"),
        "ArgumentOutOfRange" => new ArgumentOutOfRangeException("p", "range"),
        "Argument" => new ArgumentException("argument"),
        "InvalidCast" => new InvalidCastException("cast"),
        "DivideByZero" => new DivideByZeroException(),
        _ => new NullReferenceException("null"),
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public void AStageThatThrowsIsReportedAsCorruptData(string error)
    {
        var codec = new ThrowingCodec(Make(error));
        var reg = new MethodRegistry(MethodRegistry.Default.All.Append(codec));
        var spec = PipelineSpec.Parse("x86v2+throws", reg);
        var data = new byte[100];
        var (desc, payload) = PipelineExecutor.Encode(spec, data, CodecContext.None);
        var e = Assert.Throws<CorruptDataException>(() => PipelineExecutor.Decode(desc, payload, CodecContext.None));
        Assert.StartsWith("Block cannot be decoded: throws failed", e.Message);
        Assert.Equal(Make(error).GetType(), e.InnerException?.GetType());
    }

    [Theory]
    [InlineData("cm")]
    [InlineData("cm2")]
    public void ATruncatedContextMixingStreamFailsWhereItEnds(string codec)
    {
        // 100 bytes of payload claiming 16 MiB: the decoder must stop at the end of the payload, not decode garbage
        // for the declared size. Progress is reported every 64 KiB, so it must never get that far.
        var m = MethodRegistry.Default.Get(codec);
        var p = ParamValues.Of(("mem", 16));
        var data = Synthetic.Text(64 << 10);
        var payload = m.Encode(data, p, CodecContext.None);
        long furthest = 0;
        var ctx = new CodecContext { Progress = done => furthest = Math.Max(furthest, done) };
        var e = Assert.Throws<InvalidDataException>(() => m.Decode(payload.AsMemory(0, 100), 16 << 20, p, ctx));
        Assert.EndsWith("stream is truncated.", e.Message);
        Assert.True(furthest < 1 << 20, $"{codec} decoded {furthest} bytes from 100");
        // the complete stream still decodes, and one byte too many is still rejected at the end
        Assert.Equal(data, m.Decode(payload, data.Length, p, CodecContext.None));
        byte[] longer = [.. payload, 0];
        Assert.Throws<InvalidDataException>(() => m.Decode(longer, data.Length, p, CodecContext.None));
    }

    [Fact]
    public void CancellationAndCorruptionKeepTheirOwnTypes()
    {
        var reg = new MethodRegistry(MethodRegistry.Default.All.Append(new ThrowingCodec(new OperationCanceledException())));
        var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("throws", reg), new byte[10], CodecContext.None);
        Assert.Throws<OperationCanceledException>(() => PipelineExecutor.Decode(desc, payload, CodecContext.None));
        var reg2 = new MethodRegistry(MethodRegistry.Default.All.Append(new ThrowingCodec(new InvalidDataException("bad stream"))));
        var (desc2, payload2) = PipelineExecutor.Encode(PipelineSpec.Parse("throws", reg2), new byte[10], CodecContext.None);
        Assert.Equal("bad stream", Assert.Throws<InvalidDataException>(() => PipelineExecutor.Decode(desc2, payload2, CodecContext.None)).Message);
    }
}
