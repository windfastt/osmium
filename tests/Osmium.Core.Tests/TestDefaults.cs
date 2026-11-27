using Osmium.Core.Crypto;

namespace Osmium.Core.Tests;

/// <summary>
/// Key derivations at real sizes (256 MiB and more) run in this collection, never in parallel with each other or with
/// other tests, so that at most one large work area exists at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeavyKeyDerivation
{
    public const string Name = "Heavy key derivation";
}

/// <summary>Sets the test hooks of the key derivation for one test and clears them when disposed.</summary>
internal sealed class KdfSeams : IDisposable
{
    private long _allocations, _allocatedBytes, _frees, _nonZeroWords;

    public KdfSeams(bool inspectWipe = true)
    {
        Argon2id.Allocating = n =>
        {
            Interlocked.Increment(ref _allocations);
            Interlocked.Add(ref _allocatedBytes, n);
        };
        if (inspectWipe)
            Argon2id.BeforeFree = (area, words) =>
            {
                Interlocked.Add(ref _nonZeroWords, NonZeroWords(area, words));
                Interlocked.Increment(ref _frees);
            };
        else Argon2id.BeforeFree = (_, _) => Interlocked.Increment(ref _frees);
    }

    /// <summary>Work areas allocated (one per Argon2id derivation).</summary>
    public long Allocations => Interlocked.Read(ref _allocations);
    public long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);
    public long Frees => Interlocked.Read(ref _frees);
    /// <summary>Words of the work areas that were not zero when they were freed.</summary>
    public long NonZeroWordsAtFree => Interlocked.Read(ref _nonZeroWords);

    private static long NonZeroWords(nint area, long words)
    {
        var chunk = new long[1 << 16];
        long nonZero = 0;
        for (long done = 0; done < words; done += chunk.Length)
        {
            int n = (int)Math.Min(chunk.Length, words - done);
            System.Runtime.InteropServices.Marshal.Copy(area + (nint)(done * 8), chunk, 0, n);
            nonZero += chunk.AsSpan(0, n).Length - chunk.AsSpan(0, n).Count(0L);
        }
        return nonZero;
    }

    public void Dispose()
    {
        Argon2id.Allocating = null;
        Argon2id.BeforeFree = null;
    }
}
