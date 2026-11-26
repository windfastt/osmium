using System.Globalization;
using Osmium.Core.Text;

namespace Osmium.Core.Archives.Foreign;

/// <summary>
/// The limits of one listing, Test or Extract of a foreign archive: every decompressed byte goes through
/// <see cref="Count"/> (including bytes read through entries nobody asked for), which also observes cancellation at
/// least every MiB. Declared sizes are only used for early refusals; the counters never trust them.
/// </summary>
internal sealed class LimitGuard(ImportLimits limits, long archiveLength, CancellationToken ct)
{
    private const long CancelInterval = 1 << 20;
    private long _nextCancelCheck = CancelInterval;

    public ImportLimits Limits { get; } = limits;
    public CancellationToken Token { get; } = ct;
    /// <summary>Decompressed bytes so far.</summary>
    public long Output { get; private set; }

    public void Count(long bytes)
    {
        if (bytes <= 0) return;
        Output = Output > long.MaxValue - bytes ? long.MaxValue : Output + bytes;
        if (Output >= _nextCancelCheck)
        {
            Token.ThrowIfCancellationRequested();
            _nextCancelCheck = Output + CancelInterval;
        }
        if (Exceeds(Output)) throw new ExpansionLimitException(Limits.MaxExpansionRatio, Output);
    }

    /// <summary>Refuses before anything is decoded when the declared output already passes the rule.</summary>
    public void CheckDeclared(long declaredOutput)
    {
        if (Exceeds(declaredOutput)) throw new ExpansionLimitException(Limits.MaxExpansionRatio, declaredOutput);
    }

    private bool Exceeds(long output) =>
        !Limits.AllowLargeExpansion && output > Limits.ExpansionFloorBytes && output > Limits.MaxExpansionRatio * archiveLength;

    public static void CheckEntryCount(long count, ImportLimits limits)
    {
        if (count > limits.MaxEntries)
            throw new EntryLimitException($"This archive holds {(count == long.MaxValue ? "too many" : count.ToString("N0", CultureInfo.InvariantCulture))} entries, more than the {limits.MaxEntries.ToString("N0", CultureInfo.InvariantCulture)} Osmium accepts.");
    }

    /// <summary>Refuses a decoder that would need more than <see cref="ImportLimits.MemoryBudgetBytes"/>; call it
    /// before the decoder is constructed (decoders allocate their declared memory at once).</summary>
    public static void CheckMemory(string name, long bytes, ImportLimits limits)
    {
        if (bytes < 0 || bytes > limits.MemoryBudgetBytes) throw MemoryRefusal(name, bytes);
    }

    public static NotSupportedException MemoryRefusal(string name, long bytes) =>
        new($"{DisplayText.Escape(name)} needs more memory than allowed ({(bytes < 0 ? "more than 8,000,000" : Math.Ceiling(bytes / 1048576.0).ToString("N0", CultureInfo.InvariantCulture))} MB).");

    /// <summary>Sum of sizes that cannot overflow.</summary>
    public static long Add(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}

/// <summary>Free space on the destination's volume, with a seam for tests.</summary>
internal static class FreeSpace
{
    private static readonly AsyncLocal<Func<string, (long Free, long Total)?>?> Override = new();

    /// <summary>For tests: report these numbers instead of the volume's own while the result is not disposed (this
    /// async flow only).</summary>
    internal static IDisposable Simulate(Func<string, (long Free, long Total)?> probe)
    {
        var previous = Override.Value;
        Override.Value = probe;
        return new Restore(previous);
    }

    private sealed class Restore(Func<string, (long, long)?>? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }

    /// <summary>Free and total bytes of the volume holding <paramref name="path"/>; null when it does not say.</summary>
    public static (long Free, long Total)? Probe(string path)
    {
        if (Override.Value is { } probe) return probe(path);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            if (!drive.IsReady) return null;
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static long Reserve(ImportLimits limits, long total)
    {
        double r = Math.Max(limits.FreeSpaceReserveBytes, limits.FreeSpaceReserveFraction * total);
        return r >= long.MaxValue ? long.MaxValue : (long)r;
    }

    /// <summary>Before extracting: the declared output must fit and leave the reserve.</summary>
    public static void CheckBefore(string destination, long declared, ImportLimits limits)
    {
        if (declared <= 0 || Probe(destination) is not { } p) return;
        long reserve = Reserve(limits, p.Total);
        if ((double)p.Free - declared < reserve)
            throw new FreeSpaceException($"Not enough free space: the files need {Bytes(declared)} and {Bytes(reserve)} must stay free, but the drive has {Bytes(p.Free)} free.");
    }

    /// <summary>While extracting: the volume must keep the reserve free.</summary>
    public static void CheckWhileWriting(string destination, ImportLimits limits)
    {
        if (Probe(destination) is not { } p) return;
        long reserve = Reserve(limits, p.Total);
        if (p.Free < reserve)
            throw new FreeSpaceException($"Extraction stopped: the drive has only {Bytes(p.Free)} free, and {Bytes(reserve)} must stay free.");
    }

    internal static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1 << 20 => string.Create(CultureInfo.InvariantCulture, $"{b / 1024.0:F1} KB"),
        < 1 << 30 => string.Create(CultureInfo.InvariantCulture, $"{b / 1048576.0:F1} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{b / 1073741824.0:F2} GB"),
    };
}
