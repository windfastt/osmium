namespace Osmium.Desktop.Jobs;

/// <summary>What happened to one item handed to <see cref="IRecycler.Recycle"/>.</summary>
/// <param name="Path">The file or folder, as it was handed over.</param>
/// <param name="Recycled">The Recycle Bin confirmed that it now holds the item.</param>
/// <param name="Reason">Why the item was not moved; null when it was.</param>
/// <param name="BinPath">Where the item is inside the Recycle Bin, when the system says.</param>
public sealed record RecycleOutcome(string Path, bool Recycled, string? Reason = null, string? BinPath = null);

/// <summary>
/// Moves files and folders to the Recycle Bin, where they can be restored. An implementation must never delete
/// anything for good: an item the Recycle Bin cannot take stays where it is, and its outcome says why.
/// </summary>
public interface IRecycler
{
    /// <summary>The type of the drive that holds <paramref name="path"/>. Only <see cref="DriveType.Fixed"/> drives have a Recycle Bin.</summary>
    DriveType DriveTypeOf(string path);

    /// <summary>
    /// Moves <paramref name="paths"/>, and nothing else, to the Recycle Bin in one operation. Returns one outcome per
    /// path, in the same order, then an outcome for anything else the operation moved or deleted although it was not
    /// handed over (which must not happen). Items not reached once <paramref name="ct"/> is canceled stay where they are.
    /// </summary>
    /// <param name="recheck">Called with each path as late as possible before it moves: a reason keeps it where it is.</param>
    IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths, Func<string, string?> recheck, CancellationToken ct);
}
