namespace Osmium.Desktop.Jobs;

/// <summary>Which entries of an archive an extract job takes (<see cref="JobRequest.Include"/>) and where they go
/// (<see cref="JobRequest.IncludeBase"/>). Archive paths are '/'-separated and compared ordinally, as stored.</summary>
public static class EntrySelection
{
    /// <summary>
    /// A test of archive paths: true for each path in <paramref name="include"/> and for everything below a path that
    /// is a folder ("docs" selects "docs" and "docs/a.txt", never "docs2/a.txt"). A trailing '/' is ignored, and an
    /// empty path (the archive's root) selects everything. Null or empty <paramref name="include"/>: everything.
    /// </summary>
    public static Func<string, bool> Build(IReadOnlyList<string>? include)
    {
        if (include is null || include.Count == 0) return _ => true;
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in include)
        {
            string path = p.TrimEnd('/');
            if (path.Length == 0) return _ => true;
            chosen.Add(path);
        }
        var lookup = chosen.GetAlternateLookup<ReadOnlySpan<char>>();
        return path =>
        {
            if (chosen.Contains(path)) return true;
            // Each folder above the path, shortest first: "a/b/c" → "a", "a/b".
            for (int i = path.IndexOf('/'); i > 0; i = path.IndexOf('/', i + 1))
                if (lookup.Contains(path.AsSpan(0, i))) return true;
            return false;
        };
    }

    /// <summary>
    /// The output path of <paramref name="path"/> when the contents of the archive folder <paramref name="includeBase"/>
    /// go to the output root: "docs/img/a.png" with base "docs" → "img/a.png". Null for paths outside the base and for
    /// the base itself. A null or empty base (or "/"): the path unchanged.
    /// </summary>
    public static string? MapPath(string path, string? includeBase)
    {
        string? folder = includeBase?.TrimEnd('/');
        if (string.IsNullOrEmpty(folder)) return path;
        return path.Length > folder.Length + 1 && path[folder.Length] == '/' && path.StartsWith(folder, StringComparison.Ordinal)
            ? path[(folder.Length + 1)..]
            : null;
    }
}
