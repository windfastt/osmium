using Osmium.Core.Planning;
using Osmium.Desktop.Settings;

namespace Osmium.Desktop.Watch;

/// <summary>
/// Decides when a file in a watched folder is "settled" (no changes for SettleSeconds and openable exclusively)
/// and should be compressed. Pure logic with an injectable clock and file probe, so it is unit-testable.
/// </summary>
public sealed class WatchFolderEngine(Func<DateTime>? clock = null, Func<string, bool>? canOpenExclusively = null)
{
    private sealed record PendingFile(WatchFolderConfig Config, DateTime LastChange, long Size, DateTime Mtime);

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Func<string, bool> _probe = canOpenExclusively ?? DefaultProbe;
    private readonly Dictionary<string, PendingFile> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    public bool Accepts(WatchFolderConfig c, string path)
    {
        var name = Path.GetFileName(path);
        string rel = Path.GetRelativePath(c.Path, path).Replace('\\', '/');
        if (rel.StartsWith("..")) return false;
        if (!c.IncludeSubfolders && rel.Contains('/')) return false;
        if (!string.IsNullOrEmpty(c.OutputSubfolder) && rel.StartsWith(c.OutputSubfolder + "/", StringComparison.OrdinalIgnoreCase)) return false;
        bool Any(string patterns) => patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => Glob.ToRegex(p).IsMatch(name));
        return Any(c.Include) && !Any(c.Exclude);
    }

    /// <summary>Called for every file-system event (created/changed/renamed).</summary>
    public void Touch(WatchFolderConfig c, string path, long size, DateTime mtime)
    {
        if (!c.Enabled || !Accepts(c, path)) return;
        lock (_lock)
        {
            if (_done.Contains(path + "|" + mtime.Ticks)) return;
            _pending[path] = new PendingFile(c, _now(), size, mtime);
        }
    }

    /// <summary>Returns files that are ready to compress (each exactly once per version).</summary>
    public List<(WatchFolderConfig Config, string Path)> Tick(Func<string, (long Size, DateTime Mtime)?> stat)
    {
        var ready = new List<(WatchFolderConfig, string)>();
        lock (_lock)
        {
            var now = _now();
            foreach (var (path, p) in _pending.ToList())
            {
                if ((now - p.LastChange).TotalSeconds < p.Config.SettleSeconds) continue;
                var st = stat(path);
                if (st is null) { _pending.Remove(path); continue; }                        // deleted
                if (st.Value.Size != p.Size || st.Value.Mtime != p.Mtime)                    // still changing
                {
                    _pending[path] = p with { LastChange = now, Size = st.Value.Size, Mtime = st.Value.Mtime };
                    continue;
                }
                if (st.Value.Size < p.Config.MinSize) { _pending.Remove(path); continue; }
                if (!_probe(path)) { _pending[path] = p with { LastChange = now }; continue; } // locked by a writer
                _pending.Remove(path);
                _done.Add(path + "|" + p.Mtime.Ticks);
                ready.Add((p.Config, path));
            }
        }
        return ready;
    }

    private static bool DefaultProbe(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
