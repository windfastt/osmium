using System.Reflection;

namespace Osmium.Desktop.Settings;

/// <summary>
/// Saves only what the user changed on the Settings page. The page edits a working copy; by the time it is applied,
/// the current settings may have changed elsewhere (a tray toggle, the Cluster page saving a key). Writing the whole
/// working copy back would silently revert those changes, so only the edited values are copied onto a fresh clone of
/// the current settings.
/// </summary>
public static class SettingsEdits
{
    /// <summary>Edit key of the watch-folder list, which has its own editor instead of a <see cref="SettingDescriptor"/>.</summary>
    public const string WatchFolders = "Watch.Folders";

    private static readonly string WatchCategory = typeof(WatchSettings).GetCustomAttribute<SettingsCategoryAttribute>()!.Name;

    /// <summary>
    /// Returns a clone of <paramref name="current"/> in which the settings named by <paramref name="editedKeys"/>
    /// (descriptor keys such as <c>Compression.Profile</c>, or <see cref="WatchFolders"/>) have the values of
    /// <paramref name="working"/>. Nothing in the result is shared with either argument.
    /// </summary>
    public static OsmiumSettings Apply(OsmiumSettings current, OsmiumSettings working, IEnumerable<string> editedKeys)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(editedKeys);
        var result = current.Clone();
        var source = working.Clone();
        var from = Index(source);
        var to = Index(result);
        foreach (var key in editedKeys.Distinct(StringComparer.Ordinal))
        {
            if (key == WatchFolders)
            {
                result.Watch.Folders = source.Watch.Folders;
                continue;
            }
            if (!from.TryGetValue(key, out var value) || !to.TryGetValue(key, out var target))
                throw new ArgumentException($"Unknown setting '{key}'.", nameof(editedKeys));
            target.Set(value.Get());
        }
        return result;
    }

    /// <summary>The edit keys of one category: what "Restore defaults" on that category changes.</summary>
    public static IReadOnlyList<string> KeysOf(string category)
    {
        var keys = SettingReflector.Describe(new OsmiumSettings()).Where(d => d.Category == category).Select(d => d.Key).ToList();
        if (category == WatchCategory) keys.Add(WatchFolders);
        return keys;
    }

    private static Dictionary<string, SettingDescriptor> Index(OsmiumSettings s) =>
        SettingReflector.Describe(s).ToDictionary(d => d.Key, StringComparer.Ordinal);
}
