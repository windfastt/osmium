using Osmium.Core.Codecs.Cm;

namespace Osmium.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CmRoundTripsSmallInput()
    {
        var data = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello osmium ", 200)));
        var opt = new CmOptions { TableBits = 16, MatchBits = 16 };
        var packed = CmCodec.Encode(data, opt);
        var outBuf = new byte[data.Length];
        CmCodec.Decode(packed, outBuf, opt);
        Assert.Equal(data, outBuf);
        Assert.True(packed.Length < data.Length / 10);
    }
}
