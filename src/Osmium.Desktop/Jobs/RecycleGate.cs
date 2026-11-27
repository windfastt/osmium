using System.Runtime.InteropServices;

namespace Osmium.Desktop.Jobs;

/// <summary>
/// The decisions of one Recycle Bin operation, shared by all its progress sinks, and what the system reported for each
/// item. A delete goes ahead only when the system is about to recycle it and it is an item handed over, or part of one.
/// The first delete that touches an item, whether of the item itself or of anything inside it, first checks the item
/// once more with the recheck it was given: the last check before any of it moves. Once anything is refused, fails or
/// is destroyed, every later delete is refused, so the rest stays in place.
/// The system's recycler names what it is about to delete by its path; nothing here depends on the shell, so these
/// rules are tested anywhere.
/// </summary>
public sealed class RecycleGate
{
    public const string CannotTake =
        "the Recycle Bin cannot take it (its drive keeps no deleted files, or it is larger than the Recycle Bin), so the shell would have deleted it for good";

    /// <summary>An item handed over, by its key (see the constructor).</summary>
    public sealed class Item
    {
        internal Item(string path, string key)
        {
            Path = path;
            Key = key;
        }

        /// <summary>The item as it was handed over.</summary>
        public string Path { get; }
        internal string Key { get; }
        internal bool Reached, Approved, Recycled, Destroyed;
        internal int Failed;
        internal string? Refusal, BinPath;
    }

    private readonly Func<string, string?> _recheck;
    private readonly CancellationToken _ct;
    private readonly Func<string, string> _key;
    private readonly object _lock = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _holders = new(StringComparer.OrdinalIgnoreCase); // folders that hold an item
    private readonly Dictionary<string, RecycleOutcome> _others = new(StringComparer.OrdinalIgnoreCase); // by path, once
    private string? _stopped; // why the operation stopped, once it has
    private bool _stoppedByCancel;

    /// <summary>Why an item stays that the operation did not reach, or reached only once it had stopped.</summary>
    private string StoppedEarlier => _stoppedByCancel ? RecycleCandidates.Canceled : $"an earlier item failed ({_stopped})";

    /// <param name="recheck">The last check of an item, with the path it was handed over as: a reason keeps it where it is.</param>
    /// <param name="ct">The job's cancellation: once it is canceled, nothing more is deleted.</param>
    /// <param name="key">The form of a path in which the items handed over and the paths the system names are compared
    /// (ignoring case, as Windows does); by default the full path without a closing separator.</param>
    public RecycleGate(Func<string, string?> recheck, CancellationToken ct, Func<string, string>? key = null)
    {
        _recheck = recheck;
        _ct = ct;
        _key = key ?? FullKey;
    }

    /// <summary>How often the system asked before deleting something.</summary>
    public int PreDeletes { get; private set; }

    /// <summary>Registers an item to be handed over; null when it is, holds or lies inside one already registered.</summary>
    public Item? Expect(string path)
    {
        var item = new Item(path, _key(path));
        lock (_lock)
        {
            if (_holders.Contains(item.Key) || Owner(item.Key) is not null) return null;
            _items.Add(item.Key, item);
            for (var parent = Path.GetDirectoryName(item.Key); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                _holders.Add(parent);
            return item;
        }
    }

    /// <summary>An item that was not handed over after all.</summary>
    public void Forget(Item item)
    {
        lock (_lock) _items.Remove(item.Key);
    }

    /// <summary>
    /// Whether the system may delete <paramref name="path"/> now: null when it may, else why not. A refusal stops the
    /// operation, so that item and every item after it stay where they are.
    /// </summary>
    /// <param name="recycles">The system is about to move it to the Recycle Bin rather than delete it for good.</param>
    /// <param name="own">The item whose sink asks; null for the sink of the whole operation.</param>
    /// <param name="path">What the system is about to delete; null when it names no path.</param>
    public string? PreDelete(bool recycles, Item? own, string? path)
    {
        string? key = path is null ? null : _key(path);
        lock (_lock)
        {
            PreDeletes++;
            var owner = key is null ? null : Owner(key);
            if (owner is not null) owner.Reached = true;
            if (own is not null) own.Reached = true;
            if (_stopped is { } stopped)
            {
                // The operation stopped at an earlier item: this one stays, and says so, unless it has an outcome of
                // its own already (it failed, moved or was deleted itself).
                var waiting = own ?? owner;
                if (waiting is { Failed: 0, Recycled: false, Destroyed: false }) waiting.Refusal ??= StoppedEarlier;
                return stopped;
            }
            string? refusal =
                !recycles ? CannotTake
                : _ct.IsCancellationRequested ? RecycleCandidates.Canceled
                : key is null ? "the shell named no path for an item it was about to move"
                : owner is null || (own is not null && !ReferenceEquals(owner, own)) ? $"the shell was about to move something else with it: {key}"
                : null;
            if (refusal is null && !owner!.Approved)
            {
                // The first delete that touches the item, the item itself or anything inside it: before any of it moves,
                // it must still be exactly what the archive holds.
                try { refusal = _recheck(owner.Path) is { } changed ? "checked again just before it moved: " + changed : null; }
                catch (Exception e) { refusal = $"it could not be checked again ({e.Message})"; }
                // The check reads every file again, which takes a while: a cancel meanwhile keeps the item too.
                if (_ct.IsCancellationRequested) refusal = RecycleCandidates.Canceled;
                owner.Approved = refusal is null;
            }
            if (refusal is null) return null;
            var blame = own ?? owner;
            if (blame is not null) blame.Refusal ??= refusal;
            _stopped = blame is null ? refusal : $"{blame.Path}: {refusal}";
            _stoppedByCancel = refusal == RecycleCandidates.Canceled;
            return refusal;
        }
    }

    /// <summary>
    /// What the system did with <paramref name="path"/>. False when it deleted something rather than moved it to the
    /// Recycle Bin: the operation must then stop.
    /// </summary>
    /// <param name="own">The item whose sink reports; null for the sink of the whole operation.</param>
    /// <param name="path">What the system deleted; null when it names no path.</param>
    /// <param name="hrDelete">The system's result of the delete; negative when it failed.</param>
    /// <param name="recycled">The system reported the new item it created in the Recycle Bin.</param>
    /// <param name="binPath">Where that new item is, when the system says.</param>
    public bool PostDelete(Item? own, string? path, int hrDelete, bool recycled, string? binPath)
    {
        string? key = path is null ? null : _key(path);
        lock (_lock)
        {
            var owner = key is null ? own : Owner(key);
            if (owner is not null) owner.Reached = true;
            if (hrDelete >= 0 && !recycled)
            {
                // Deleted rather than moved to the Recycle Bin (the new item would be the one in the Recycle Bin).
                if (owner is not null) owner.Destroyed = true;
                else
                {
                    string what = key ?? "(an item the shell named no path for)";
                    _others[what] = new RecycleOutcome(what, false, "the operation deleted it rather than moved it to the Recycle Bin");
                }
                _stopped ??= $"{key ?? "an item"} was deleted rather than moved to the Recycle Bin";
                return false;
            }
            if (owner is null)
            {
                // Moved although not handed over: reported once, when the shell names it.
                if (hrDelete >= 0 && key is not null)
                    _others.TryAdd(key, new RecycleOutcome(key, true, "the operation moved it to the Recycle Bin, although it was not handed over", binPath));
                return true;
            }
            if (key is not null && !string.Equals(key, owner.Key, StringComparison.OrdinalIgnoreCase)) return true; // part of an item
            if (hrDelete < 0)
            {
                owner.Failed = hrDelete;
                _stopped ??= $"{owner.Path}: the shell could not move it";
            }
            else
            {
                owner.Recycled = true;
                owner.BinPath = binPath ?? owner.BinPath;
            }
            return true;
        }
    }

    /// <summary>What happened to one item, from the system's reports and from whether it is still where it was.</summary>
    /// <param name="performed">The result of the whole operation; negative when it failed.</param>
    /// <param name="aborted">The system reported that the operation stopped before its end.</param>
    public RecycleOutcome Outcome(Item item, int performed, bool aborted)
    {
        bool inPlace = File.Exists(item.Path) || Directory.Exists(item.Path);
        lock (_lock)
        {
            if (item.Destroyed) return new RecycleOutcome(item.Path, false, "the shell deleted it, or part of it, rather than moved it to the Recycle Bin");
            if (item.Recycled && item.Refusal is null && item.Failed == 0 && !inPlace) return new RecycleOutcome(item.Path, true, null, item.BinPath);
            if (item.Refusal is { } refusal) return new RecycleOutcome(item.Path, false, refusal);
            if (item.Failed != 0) return new RecycleOutcome(item.Path, false, "the shell could not move it: " + Describe(item.Failed));
            if (!item.Reached)
                return new RecycleOutcome(item.Path, false, _stopped is not null ? StoppedEarlier
                    : _ct.IsCancellationRequested ? RecycleCandidates.Canceled
                    : performed < 0 ? "the operation stopped before it: " + Describe(performed)
                    : aborted ? "the operation stopped before it" : "the shell did not move it");
            return new RecycleOutcome(item.Path, false, inPlace ? "the shell did not move it" : "it is gone, but the shell did not report it in the Recycle Bin");
        }
    }

    /// <summary>Anything the operation moved or deleted that was not handed over (it should never happen).</summary>
    public List<RecycleOutcome> Others()
    {
        lock (_lock) return [.. _others.Values];
    }

    /// <summary>A system error code in words, with the code.</summary>
    public static string Describe(int hr) => $"{Marshal.GetExceptionForHR(hr)?.Message ?? "error"} (0x{hr:X8})";

    /// <summary>The item that is <paramref name="key"/> or holds it.</summary>
    private Item? Owner(string key)
    {
        for (string? p = key; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if (_items.TryGetValue(p, out var item)) return item;
        return null;
    }

    private static string FullKey(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { full = path; }
        return Path.TrimEndingDirectorySeparator(full);
    }
}
