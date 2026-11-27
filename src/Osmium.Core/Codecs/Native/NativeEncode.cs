using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Codecs.Native;

/// <summary>The effective LZMA parameters, after the same clamps the built-in encoder applies.</summary>
internal readonly record struct LzmaSettings(int Dict, int Lc, int Lp, int Pb, int Fb, int MatchFinder);

/// <summary>The effective Zstandard parameters: <see cref="LdmMinMatch"/> is 0 or 4…4096 and only set with
/// <see cref="Ldm"/>, exactly as the built-in encoder passes them.</summary>
internal readonly record struct ZstdSettings(int Level, int WindowLog, bool Ldm, int LdmMinMatch, int Strategy);

/// <summary>
/// Calls the native encoders: one output allocation at the bound (never a retry), a memory cap derived from the
/// method's estimate, progress through <see cref="CodecContext.Checkpoint"/> (so pause blocks and cancellation stops
/// the encoder), and every native status turned into the exception the built-in encoder would throw.
/// </summary>
internal static unsafe class NativeEncode
{
    private const long OneMiB = 1 << 20;
    private const ulong HelperThreadMinimum = 1 << 20;

    /// <summary>1 while one native LZMA encode in this process uses the match finder's helper thread.</summary>
    private static int s_helperInUse;

    /// <summary>Tests only: flags added to the next LZMA calls on this thread (OSN_LZMA_FLAG_TEST_*).</summary>
    [ThreadStatic] internal static uint TestLzmaFlags;
    /// <summary>Tests only: lets this thread's LZMA encodes use the match finder's helper thread under the policy
    /// below. Off in Osmium: for some inputs (40 MiB of mixed data at a 32 MiB dictionary) the helper thread makes the
    /// LZMA SDK write other bytes, deterministic but not those of one thread, so using it would make a block's bytes
    /// depend on what else ran at the time. native-1 encodes LZMA with one thread.</summary>
    [ThreadStatic] internal static bool TestAllowHelperThread;
    /// <summary>Tests only: replaces the memory cap of the next calls on this thread.</summary>
    [ThreadStatic] internal static ulong? TestMemCap;
    /// <summary>The statistics of the last native encode on this thread (tests and measurements).</summary>
    [ThreadStatic] internal static OsnStats LastStats;

    private sealed class ProgressState(CodecContext ctx)
    {
        public readonly CodecContext Ctx = ctx;
        public Exception? Error;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnProgress(void* user, ulong done)
    {
        // No exception may cross into native code: every one is stored and rethrown after the call returns.
        try
        {
            var state = (ProgressState)GCHandle.FromIntPtr((nint)user).Target!;
            try
            {
                state.Ctx.Checkpoint((long)done);
                return 0;
            }
            catch (Exception e)
            {
                state.Error = e;
                return 1;
            }
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>The native allocation limit for one block: the method's encode-memory estimate minus the bound-sized
    /// output buffer, plus 1 MiB. The input is counted by the pipeline, and the exact-length copy is made after the
    /// native memory is freed, so input + output + native memory stays within the block's reservation.</summary>
    internal static ulong MemCap(long encodeMemory, ulong bound)
    {
        long cap = encodeMemory - (long)Math.Min(bound, long.MaxValue / 2) + OneMiB;
        return (ulong)Math.Max(cap, OneMiB);
    }

    public static byte[] Lzma(NativeApi api, ReadOnlyMemory<byte> input, LzmaSettings s, long encodeMemory, CodecContext ctx)
    {
        ulong n = (ulong)input.Length;
        ulong bound = NativeBounds.Lzma(n);
        ulong nativeBound;
        if (api.LzmaBound(n, &nativeBound) != Osn.Ok || nativeBound != bound)
            throw new InvalidOperationException($"Native LZMA encoder: its bound for {n} bytes is {nativeBound}, Osmium's is {bound}.");
        var output = GC.AllocateUninitializedArray<byte>(NativeBounds.ArrayLength(bound));
        ulong memCap = TestMemCap ?? MemCap(encodeMemory, bound);

        // The helper thread (tests only, see TestAllowHelperThread) for a block of at least 1 MiB, when the caller
        // allows two threads and no other native LZMA encode in the process has it (claimed with one compare-exchange,
        // released in finally).
        bool helper = TestAllowHelperThread && ctx.MaxThreads >= 2 && n >= HelperThreadMinimum
            && Interlocked.CompareExchange(ref s_helperInUse, 1, 0) == 0;
        try
        {
            var p = new OsnLzmaParams
            {
                Size = (uint)sizeof(OsnLzmaParams), DictSize = (uint)s.Dict, Lc = (uint)s.Lc, Lp = (uint)s.Lp, Pb = (uint)s.Pb,
                Fb = (uint)s.Fb, MatchFinder = (uint)s.MatchFinder, Threads = helper ? 2u : 1u, Flags = TestLzmaFlags,
            };
            var state = new ProgressState(ctx);
            var handle = GCHandle.Alloc(state);
            try
            {
                var stats = new OsnStats { Size = (uint)sizeof(OsnStats) };
                nuint written = 0;
                int status;
                fixed (byte* src = input.Span)
                fixed (byte* dst = output)
                    status = api.LzmaEncode(src, (nuint)n, dst, (nuint)output.Length, &written, &p, &OnProgress,
                        (void*)GCHandle.ToIntPtr(handle), memCap, &stats);
                LastStats = stats;
                if (state.Error != null) ExceptionDispatchInfo.Throw(state.Error);
                Check(status, "LZMA", n, bound, stats);
                return output.AsSpan(0, checked((int)written)).ToArray();
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            if (helper) Volatile.Write(ref s_helperInUse, 0);
        }
    }

    public static byte[] Zstd(NativeApi api, ReadOnlyMemory<byte> input, ZstdSettings s, long encodeMemory, CodecContext ctx, uint chunkSize = 0)
    {
        ulong n = (ulong)input.Length;
        ulong bound = NativeBounds.Zstd(n);
        ulong nativeBound;
        if (api.ZstdBound(n, &nativeBound) != Osn.Ok || nativeBound != bound)
            throw new InvalidOperationException($"Native Zstandard encoder: its bound for {n} bytes is {nativeBound}, Osmium's is {bound}.");
        var output = GC.AllocateUninitializedArray<byte>(NativeBounds.ArrayLength(bound));
        ulong memCap = TestMemCap ?? MemCap(encodeMemory, bound);
        var p = new OsnZstdParams
        {
            Size = (uint)sizeof(OsnZstdParams), Level = (uint)s.Level, WindowLog = (uint)s.WindowLog, Ldm = s.Ldm ? 1u : 0u,
            LdmMinMatch = s.Ldm ? (uint)s.LdmMinMatch : 0u, Strategy = (uint)s.Strategy, ChunkSize = chunkSize,
        };
        var state = new ProgressState(ctx);
        var handle = GCHandle.Alloc(state);
        try
        {
            var stats = new OsnStats { Size = (uint)sizeof(OsnStats) };
            nuint written = 0;
            int status;
            fixed (byte* src = input.Span)
            fixed (byte* dst = output)
                status = api.ZstdEncode(src, (nuint)n, dst, (nuint)output.Length, &written, &p, &OnProgress,
                    (void*)GCHandle.ToIntPtr(handle), memCap, &stats);
            LastStats = stats;
            if (state.Error != null) ExceptionDispatchInfo.Throw(state.Error);
            Check(status, "Zstandard", n, bound, stats);
            return output.AsSpan(0, checked((int)written)).ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Turns a native status into the exception the built-in encoder would throw.</summary>
    internal static void Check(int status, string codec, ulong length, ulong bound, in OsnStats stats)
    {
        switch (status)
        {
            case Osn.Ok:
                return;
            case Osn.ENoMem:
                throw new OutOfMemoryException($"The native {codec} encoder ran out of memory.");
            case Osn.EMemCap:
                throw new InvalidOperationException(
                    "The native encoder needed more memory than Osmium allows for this block. Turn on Use only the " +
                    "built-in encoders (or run with --encoders managed) and report this.");
            case Osn.EOutputFull:
                throw new InvalidOperationException(
                    $"Native {codec} encoder: the output of a {length}-byte block did not fit in its bound of {bound} bytes.");
            case Osn.EAborted:
                throw new OperationCanceledException();
            default:
                throw new InvalidOperationException(
                    $"Native {codec} encoder failed: {Osn.StatusName(status)}" +
                    (status == Osn.EInternal ? $" (upstream code {stats.UpstreamCode})" : "") + ".");
        }
    }
}
