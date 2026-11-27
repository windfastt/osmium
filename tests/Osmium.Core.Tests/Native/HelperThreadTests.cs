using Osmium.Core.Codecs;
using Osmium.Core.Codecs.Native;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Native;

/// <summary>
/// The LZMA SDK's match finder can run on a helper thread, but for some inputs it then writes other bytes (40 MiB
/// of mixed data at a 32 MiB dictionary: same length, different matches), so Osmium never uses it: a block's bytes
/// must not depend on what else ran at the time. The library keeps the mode, and its policy (two allowed threads, a
/// block of at least 1 MiB, one encode at a time) stays tested behind a test switch.
/// </summary>
[Collection("Native encoders")]
public sealed class HelperThreadTests
{
    private static (MethodBase M, ParamValues P) Lzma()
    {
        var m = (MethodBase)MethodRegistry.Default.Get("lzma");
        return (m, m.Normalize(ParamValues.Of(("dict", 16L << 20), ("fb", 32L))));
    }

    [Fact]
    public void OsmiumEncodesLzmaWithOneThreadEvenWhenTwoAreAllowed()
    {
        NativeTestSupport.RequireNative();
        var (m, p) = Lzma();
        m.Encode(EncoderVectorTests.Input("mixed-2m"), p, new CodecContext { MaxThreads = 2 });
        Assert.Equal(1u, NativeEncode.LastStats.ThreadsUsed);
        m.Encode(VectorInputs.Large(8 << 20, 42), p, new CodecContext { MaxThreads = 4 });
        Assert.Equal(1u, NativeEncode.LastStats.ThreadsUsed);
    }

    [Fact]
    public void TheHelperThreadNeedsTwoThreadsAndAMebibyte()
    {
        NativeTestSupport.RequireNative();
        var (m, p) = Lzma();
        var big = EncoderVectorTests.Input("mixed-2m");
        var small = EncoderVectorTests.Input("mixed-256k");
        NativeEncode.TestAllowHelperThread = true;
        try
        {
            var one = m.Encode(big, p, new CodecContext { MaxThreads = 1 });
            Assert.Equal(1u, NativeEncode.LastStats.ThreadsUsed);
            var two = m.Encode(big, p, new CodecContext { MaxThreads = 2 });
            Assert.Equal(2u, NativeEncode.LastStats.ThreadsUsed);
            Assert.Equal(one, two);
            m.Encode(small, p, new CodecContext { MaxThreads = 2 });
            Assert.Equal(1u, NativeEncode.LastStats.ThreadsUsed);
        }
        finally
        {
            NativeEncode.TestAllowHelperThread = false;
        }
    }

    [Fact]
    public void OnlyOneEncodeAtATimeGetsTheHelperThread()
    {
        NativeTestSupport.RequireNative();
        var (m, p) = Lzma();
        var big = VectorInputs.Large(16 << 20, 41);
        var second = EncoderVectorTests.Input("mixed-2m");
        byte[]? secondOutput = null;
        uint secondThreads = 0;
        var ctx = new CodecContext
        {
            MaxThreads = 2,
            Progress = _ =>
            {
                if (secondOutput != null) return;
                // While this encode holds the helper thread, another encode on another thread runs with one.
                var t = new Thread(() =>
                {
                    NativeEncode.TestAllowHelperThread = true;
                    secondOutput = m.Encode(second, p, new CodecContext { MaxThreads = 2 });
                    secondThreads = NativeEncode.LastStats.ThreadsUsed;
                });
                t.Start();
                t.Join();
            },
        };
        NativeEncode.TestAllowHelperThread = true;
        try
        {
            m.Encode(big, p, ctx);
            Assert.Equal(2u, NativeEncode.LastStats.ThreadsUsed);
            Assert.Equal(1u, secondThreads);
            var again = m.Encode(second, p, new CodecContext { MaxThreads = 2 });
            Assert.Equal(2u, NativeEncode.LastStats.ThreadsUsed);   // released again
            Assert.Equal(again, secondOutput);
        }
        finally
        {
            NativeEncode.TestAllowHelperThread = false;
        }
    }
}
