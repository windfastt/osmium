using System.Diagnostics;
using Osmium.Core.Codecs;
using Osmium.Core.Codecs.Native;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Native;

/// <summary>The contract of the native interface and of its managed wrapper.</summary>
[Collection("Native encoders")]
public sealed class NativeShimTests
{
    private static NativeApi Api()
    {
        NativeTestSupport.RequireNative();
        return NativeEncoders.ForEncode()!;
    }

    private static readonly OsnLzmaParams LzmaBase = new()
    {
        Size = NativeApi.LzmaParamsSize, DictSize = 1 << 16, Lc = 3, Lp = 0, Pb = 2, Fb = 32, MatchFinder = 1, Threads = 1,
    };

    private static readonly OsnZstdParams ZstdBase = new()
    {
        Size = NativeApi.ZstdParamsSize, Level = 3, WindowLog = 10,
    };

    private static readonly byte[] Small = "abcabcabcabcabcabcabc"u8.ToArray();

    private static int Lzma(NativeApi api, OsnLzmaParams p) =>
        api.RawLzma(Small, (ulong)Small.Length, new byte[512], 512, p, 1UL << 32, 0, out _, out _);

    private static int Zstd(NativeApi api, OsnZstdParams p) =>
        api.RawZstd(Small, (ulong)Small.Length, new byte[512], 512, p, 1UL << 32, 0, out _, out _);

    public static TheoryData<string, uint, uint> LzmaEdges => new()
    {
        { "dict", (1 << 16) - 1, 1 << 16 }, { "dict", (1 << 29) + 1, 1 << 29 }, { "lc", 9, 8 }, { "lp", 5, 4 },
        { "pb", 5, 4 }, { "fb", 4, 5 }, { "fb", 274, 273 }, { "mf", 2, 0 }, { "threads", 0, 1 }, { "threads", 3, 2 },
        { "flags", 2, 0 }, { "size", 32, 36 },
    };

    [Theory]
    [MemberData(nameof(LzmaEdges))]
    public void LzmaRejectsEachParameterJustOutsideItsRange(string field, uint outside, uint inside)
    {
        var api = Api();
        OsnLzmaParams Set(uint v)
        {
            var p = LzmaBase;
            switch (field)
            {
                case "dict": p.DictSize = v; break;
                case "lc": p.Lc = v; break;
                case "lp": p.Lp = v; break;
                case "pb": p.Pb = v; break;
                case "fb": p.Fb = v; break;
                case "mf": p.MatchFinder = v; break;
                case "threads": p.Threads = v; break;
                case "flags": p.Flags = v; break;
                case "size": p.Size = v; break;
            }
            return p;
        }
        Assert.Equal(Osn.EParam, Lzma(api, Set(outside)));
        // A 512 MiB dictionary allocates its whole 4 GiB match finder even for 21 bytes, beyond this call's cap:
        // accepted means anything but OSN_E_PARAM.
        int status = Lzma(api, Set(inside));
        if (field == "dict" && inside == 1 << 29) Assert.Contains(status, new[] { Osn.Ok, Osn.EMemCap });
        else Assert.Equal(Osn.Ok, status);
    }

    public static TheoryData<string, uint, uint> ZstdEdges => new()
    {
        { "level", 0, 1 }, { "level", 23, 22 }, { "wlog", 9, 10 }, { "wlog", 32, 31 }, { "ldm", 2, 1 }, { "strategy", 10, 9 },
        { "chunk", (64 << 10) - 1, 64 << 10 }, { "chunk", 32 << 20, 16 << 20 }, { "chunk", 3 << 16, 1 << 17 },
        { "flags", 1, 0 }, { "size", 36, 32 },
    };

    [Theory]
    [MemberData(nameof(ZstdEdges))]
    public void ZstdRejectsEachParameterJustOutsideItsRange(string field, uint outside, uint inside)
    {
        var api = Api();
        OsnZstdParams Set(uint v)
        {
            var p = ZstdBase;
            switch (field)
            {
                case "level": p.Level = v; break;
                case "wlog": p.WindowLog = v; break;
                case "ldm": p.Ldm = v; break;
                case "strategy": p.Strategy = v; break;
                case "chunk": p.ChunkSize = v; break;
                case "flags": p.Flags = v; break;
                case "size": p.Size = v; break;
            }
            return p;
        }
        Assert.Equal(Osn.EParam, Zstd(api, Set(outside)));
        Assert.Equal(Osn.Ok, Zstd(api, Set(inside)));
    }

    [Fact]
    public void ZstdLdmMinimumOnlyWithLdmAndWithinItsRange()
    {
        var api = Api();
        var p = ZstdBase;
        p.LdmMinMatch = 64;
        Assert.Equal(Osn.EParam, Zstd(api, p));
        p.Ldm = 1;
        Assert.Equal(Osn.Ok, Zstd(api, p));
        foreach (var (v, status) in new[] { (3u, Osn.EParam), (4u, Osn.Ok), (4096u, Osn.Ok), (4097u, Osn.EParam) })
        {
            p.LdmMinMatch = v;
            Assert.Equal(status, Zstd(api, p));
        }
    }

    [Fact]
    public void NullPointersAndBadLengthsAreRefused()
    {
        var api = Api();
        var b = new byte[64];
        const ulong cap = 1UL << 30;
        Assert.Equal(Osn.EParam, api.RawLzma(null, 4, b, 64, LzmaBase, cap, 0, out _, out _));
        Assert.Equal(Osn.EParam, api.RawLzma(b, 4, null, 16, LzmaBase, cap, 0, out _, out _));
        Assert.Equal(Osn.EParam, api.RawLzma(b, 4, b, 64, LzmaBase, cap, 0, out _, out _, nullDstLen: true));
        Assert.Equal(Osn.EParam, api.RawLzma(b, 4, b, 64, null, cap, 0, out _, out _));
        Assert.Equal(Osn.EParam, api.RawLzma(b, 4, b, 64, LzmaBase, 0, 0, out _, out _));
        Assert.Equal(Osn.EParam, api.RawLzma(b, (1UL << 31) + 1, b, 64, LzmaBase, cap, 0, out _, out _));
        Assert.Equal(Osn.EParam, api.RawLzma(b, 4, b, 64, LzmaBase, cap, 8, out _, out _));
        Assert.Equal(Osn.EParam, api.RawZstd(null, 4, b, 64, ZstdBase, cap, 0, out _, out _));
        Assert.Equal(Osn.EParam, api.RawZstd(b, 4, null, 16, ZstdBase, cap, 0, out _, out _));
        Assert.Equal(Osn.EParam, api.RawZstd(b, 4, b, 64, ZstdBase, 0, 0, out _, out _));
        Assert.Equal(Osn.EParam, api.RawZstd(b, (1UL << 31) + 1, b, 64, ZstdBase, cap, 0, out _, out _));
        Assert.Equal(Osn.EParam, api.RawZstd(b, 4, b, 64, ZstdBase, cap, 8, out _, out _));
        // A valid call reports its statistics.
        Assert.Equal(Osn.Ok, api.RawZstd(b, 4, b, 64, ZstdBase, cap, NativeApi.StatsSize, out var n, out var stats));
        Assert.True(n > 0 && stats.PeakBytes > 0 && stats.LiveBytesAfter == 0 && stats.ThreadsUsed == 1);
    }

    [Theory]
    [InlineData("lzma")]
    [InlineData("zstd")]
    public void EmptyAndOneByteInputsRoundTrip(string codec)
    {
        Api();
        var m = (MethodBase)MethodRegistry.Default.Get(codec);
        var p = m.Normalize(ParamValues.Empty);
        foreach (var input in new[] { Array.Empty<byte>(), [0x41] })
        {
            var packed = m.Encode(input, p, CodecContext.None);
            Assert.Equal(input, m.Decode(packed, input.Length, p, CodecContext.None));
        }
    }

    [Theory]
    [InlineData("lzma", 1u)]
    [InlineData("lzma", 2u)]
    [InlineData("zstd", 1u)]
    public void AnOutputBelowItsNeedIsRefusedWithNothingWrittenPastTheCapacity(string codec, uint threads)
    {
        var api = Api();
        var input = EncoderVectorTests.Input("mixed-1m17");
        var full = codec == "lzma"
            ? NativeEncode.Lzma(api, input, new LzmaSettings(1 << 20, 3, 0, 2, 64, 1), 1L << 30, CodecContext.None)
            : NativeEncode.Zstd(api, input, new ZstdSettings(9, 20, false, 0, 0), 1L << 30, CodecContext.None);
        foreach (int cap in new[] { 0, 1, 17, full.Length / 2, full.Length - 1 })
        {
            var dst = new byte[full.Length + 64];
            Array.Fill(dst, (byte)0xC3);
            ulong n;
            int status;
            if (codec == "lzma")
            {
                var p = LzmaBase;
                p.DictSize = 1 << 20; p.Fb = 64; p.Threads = threads;
                status = api.RawLzma(input, (ulong)input.Length, dst, (ulong)cap, p, 1UL << 32, 0, out n, out _);
            }
            else
            {
                var p = ZstdBase;
                p.Level = 9; p.WindowLog = 20;
                status = api.RawZstd(input, (ulong)input.Length, dst, (ulong)cap, p, 1UL << 32, 0, out n, out _);
            }
            Assert.Equal(Osn.EOutputFull, status);
            Assert.Equal(0UL, n);
            Assert.True(dst.AsSpan(cap).IndexOfAnyExcept((byte)0xC3) < 0, $"bytes written past a capacity of {cap}");
        }
        // The wrapper always passes the bound, so this status from it is a bug and fails the block; nothing retries.
        var e = Assert.Throws<InvalidOperationException>(() => NativeEncode.Check(Osn.EOutputFull, "LZMA", 10, 141, default));
        Assert.Contains("did not fit in its bound", e.Message);
    }

    [Theory]
    [InlineData("lzma", 1)]
    [InlineData("lzma", 2)]
    [InlineData("zstd", 1)]
    public void ProgressIsMonotonicAndReachesTheInputLength(string codec, int threads)
    {
        Api();
        var m = (MethodBase)MethodRegistry.Default.Get(codec);
        var p = m.Normalize(ParamValues.Of(codec == "lzma" ? [("dict", 4L << 20), ("fb", 32L)] : [("level", 3L)]));
        var input = EncoderVectorTests.Input("text-2m");
        var seen = new List<long>();
        NativeEncode.TestAllowHelperThread = threads == 2;   // the library's two-thread mode (unused by Osmium)
        try
        {
            m.Encode(input, p, new CodecContext { MaxThreads = threads, Progress = seen.Add });
            Assert.Equal((uint)threads, NativeEncode.LastStats.ThreadsUsed);
        }
        finally
        {
            NativeEncode.TestAllowHelperThread = false;
        }
        Assert.True(seen.Count >= 2, $"only {seen.Count} progress calls");
        for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] >= seen[i - 1], "progress went backwards");
        Assert.Equal(input.Length, seen[^1]);
    }

    [Theory]
    [InlineData("lzma", 1)]
    [InlineData("lzma", 2)]
    [InlineData("zstd22", 1)]
    public void CancellationStopsTheEncoderWithinOneSecond(string codec, int threads)
    {
        Api();
        var m = (MethodBase)MethodRegistry.Default.Get(codec == "lzma" ? "lzma" : "zstd");
        var p = codec == "lzma"
            ? m.Normalize(ParamValues.Of(("dict", 16L << 20)))
            : m.Normalize(ParamValues.Of(("level", 22L), ("wlog", 27L)));
        var input = codec == "lzma" ? VectorInputs.Large(16 << 20, 11) : VectorInputs.Large(72 << 20, 12);
        // This tests cancellation, not the memory estimate (zstd 22 at window 27 needs about 706 MiB, more than its
        // estimate allows: see NativeMemoryTests).
        NativeEncode.TestMemCap = 2UL << 30;
        NativeEncode.TestAllowHelperThread = threads == 2;   // the library's two-thread mode (unused by Osmium)
        using var cts = new CancellationTokenSource();
        var sw = new Stopwatch();
        int calls = 0;
        var ctx = new CodecContext
        {
            Token = cts.Token,
            MaxThreads = threads,
            Progress = _ =>
            {
                if (++calls == 2) { sw.Start(); cts.Cancel(); }
            },
        };
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => m.Encode(input, p, ctx));
            Assert.Equal((uint)threads, NativeEncode.LastStats.ThreadsUsed);
        }
        finally
        {
            NativeEncode.TestMemCap = null;
            NativeEncode.TestAllowHelperThread = false;
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"the encoder took {sw.Elapsed.TotalMilliseconds:F0} ms to stop");
        Assert.Equal(0UL, NativeEncode.LastStats.LiveBytesAfter);
    }

    [Fact]
    public void AnExceptionFromTheProgressCallbackComesBackAsItself()
    {
        Api();
        var m = (MethodBase)MethodRegistry.Default.Get("lzma");
        var p = m.Normalize(ParamValues.Of(("dict", 4L << 20)));
        var ctx = new CodecContext { Progress = _ => throw new TimeZoneNotFoundException("from the callback") };
        var e = Assert.Throws<TimeZoneNotFoundException>(() => m.Encode(EncoderVectorTests.Input("text-2m"), p, ctx));
        Assert.Equal("from the callback", e.Message);
    }

    [Fact]
    public async Task PauseBlocksProgressAndResumeGivesTheSameBytes()
    {
        Api();
        var m = (MethodBase)MethodRegistry.Default.Get("lzma");
        var p = m.Normalize(ParamValues.Of(("dict", 4L << 20), ("fb", 32L)));
        var input = EncoderVectorTests.Input("text-2m");
        var reference = m.Encode(input, p, CodecContext.None);
        var gate = new PauseGate();
        bool paused = false;
        var sw = Stopwatch.StartNew();
        var ctx = new CodecContext
        {
            Gate = gate,
            Progress = done =>
            {
                if (paused) return;
                paused = true;
                gate.Pause();
                _ = Task.Delay(400).ContinueWith(t => gate.Resume(), TaskScheduler.Default);
            },
        };
        var output = await Task.Run(() => m.Encode(input, p, ctx), TestContext.Current.CancellationToken);
        Assert.True(sw.ElapsedMilliseconds >= 350, $"the encode did not wait for the pause ({sw.ElapsedMilliseconds} ms)");
        Assert.Equal(reference, output);
    }

    [Fact]
    public void ManyEncodesInParallelGiveTheSameBytesAsOneByOne()
    {
        Api();
        var lzma = (MethodBase)MethodRegistry.Default.Get("lzma");
        var zstd = (MethodBase)MethodRegistry.Default.Get("zstd");
        var lp = lzma.Normalize(ParamValues.Of(("dict", 2L << 20), ("fb", 64L)));
        var zp = zstd.Normalize(ParamValues.Of(("level", 9L)));
        var inputs = new[] { "mixed-256k", "mixed-1m17", "text-2m", "mixed-2m" }.Select(EncoderVectorTests.Input).ToArray();
        var refL = inputs.Select(i => lzma.Encode(i, lp, CodecContext.None)).ToArray();
        var refZ = inputs.Select(i => zstd.Encode(i, zp, CodecContext.None)).ToArray();
        Parallel.For(0, 4 * inputs.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, k =>
        {
            int i = k % inputs.Length;
            Assert.Equal(refL[i], lzma.Encode(inputs[i], lp, new CodecContext { MaxThreads = 2 }));
            Assert.Equal(refZ[i], zstd.Encode(inputs[i], zp, CodecContext.None));
        });
    }

    [Fact]
    public void AHelperThreadThatCannotStartFallsBackToOneThreadWithTheSameBytes()
    {
        Api();
        var m = (MethodBase)MethodRegistry.Default.Get("lzma");
        var p = m.Normalize(ParamValues.Of(("dict", 4L << 20), ("fb", 64L)));
        var input = EncoderVectorTests.Input("mixed-2m");
        NativeEncode.TestAllowHelperThread = true;   // the library's two-thread mode (unused by Osmium)
        try
        {
            var reference = m.Encode(input, p, new CodecContext { MaxThreads = 2 });
            Assert.Equal(2u, NativeEncode.LastStats.ThreadsUsed);
            NativeEncode.TestLzmaFlags = Osn.LzmaFlagTestThreadError;
            var output = m.Encode(input, p, new CodecContext { MaxThreads = 2 });
            Assert.Equal(1u, NativeEncode.LastStats.ThreadRetries);
            Assert.Equal(1u, NativeEncode.LastStats.ThreadsUsed);
            Assert.Equal(reference, output);
        }
        finally
        {
            NativeEncode.TestLzmaFlags = 0;
            NativeEncode.TestAllowHelperThread = false;
        }
    }

    [Theory]
    [InlineData("lzma")]
    [InlineData("zstd")]
    public void AMemoryCapBelowTheNeedFailsLoudlyAndLeaksNothing(string codec)
    {
        Api();
        var m = (MethodBase)MethodRegistry.Default.Get(codec);
        var p = m.Normalize(ParamValues.Empty);
        NativeEncode.TestMemCap = 1 << 20;
        try
        {
            var e = Assert.Throws<InvalidOperationException>(() => m.Encode(EncoderVectorTests.Input("mixed-1m17"), p, CodecContext.None));
            Assert.Contains("Use only the built-in encoders", e.Message);
            Assert.Contains("--encoders managed", e.Message);
            Assert.DoesNotContain("OSMIUM_ENCODERS", e.Message);
            Assert.Equal(0UL, NativeEncode.LastStats.LiveBytesAfter);
        }
        finally
        {
            NativeEncode.TestMemCap = null;
        }
    }
}
