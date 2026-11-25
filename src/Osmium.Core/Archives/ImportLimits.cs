using Osmium.Core.Container; // MemoryBudgets

namespace Osmium.Core.Archives;

/// <summary>
/// What an archive may make Osmium do while it is listed, tested or extracted. The defaults suit any real archive;
/// an archive that passes them is refused with <see cref="EntryLimitException"/>, <see cref="ExpansionLimitException"/>
/// or <see cref="FreeSpaceException"/>. Change one value with <c>with</c>: <c>ImportLimits.Default with { MaxEntries = 10 }</c>.
/// </summary>
public sealed record ImportLimits
{
    /// <summary>Most entries (files, folders, links) an archive may hold.</summary>
    public long MaxEntries { get; init; } = 1_000_000;
    /// <summary>Most '/'-separated components in an entry's path.</summary>
    public int MaxPathDepth { get; init; } = 64;
    /// <summary>Stop only when output passes this …</summary>
    public long ExpansionFloorBytes { get; init; } = 1L << 30;
    /// <summary>… and this many times the archive's file length.</summary>
    public double MaxExpansionRatio { get; init; } = 1000;
    /// <summary>Keep at least max(this, <see cref="FreeSpaceReserveFraction"/> × volume size) free on the destination's
    /// volume after extraction. With both reserves at 0, only output that does not fit at all is refused.</summary>
    public long FreeSpaceReserveBytes { get; init; } = 1L << 30;
    public double FreeSpaceReserveFraction { get; init; } = 0.02;
    /// <summary>Memory for decoding (dictionaries, solid blocks, blocks decoded at once).</summary>
    public long MemoryBudgetBytes { get; init; } = MemoryBudgets.Default();
    /// <summary>After the user confirmed, or CLI --force: lifts the expansion limit (not the free-space reserve, and
    /// never a path or link rule).</summary>
    public bool AllowLargeExpansion { get; init; }
    public static ImportLimits Default { get; } = new();
}
