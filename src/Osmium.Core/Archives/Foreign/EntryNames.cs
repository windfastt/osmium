using System.Globalization;
using Osmium.Core.Container;

namespace Osmium.Core.Archives.Foreign;

/// <summary>
/// The naming rules for entries of foreign archives, in the order the guarded writer applies them: normalise the
/// name, refuse what can never be safe, then map the characters Windows does not allow (reported as renamed).
/// </summary>
internal static class EntryNames
{
    public const int MaxComponent = 255, MaxTotal = 32_000;

    /// <summary>
    /// Rules 1 and 2: '\' becomes '/', leading "./" and "." or empty components go. Refused (the reason is returned,
    /// <paramref name="normalized"/> is then null): absolute paths, drive letters, network paths, ".." components,
    /// reserved device names (also with an extension, "aux.txt"), a component over 255 characters, a path over 32,000
    /// characters or deeper than <paramref name="maxDepth"/>.
    /// </summary>
    public static string? Normalize(string name, int maxDepth, out string? normalized)
    {
        normalized = null;
        string s = name.Replace('\\', '/');
        if (s.StartsWith("//", StringComparison.Ordinal)) return "a network path";
        if (s.StartsWith('/')) return "an absolute path";
        if (s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] == ':') return "a path with a drive letter";
        var parts = new List<string>();
        foreach (var part in s.Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") return "'..' in the path";
            if (part.Length > MaxComponent) return $"a name longer than {MaxComponent} characters";
            if (IsDeviceName(part) is { } device) return $"the reserved device name '{device}'";
            parts.Add(part);
        }
        if (parts.Count == 0) return "an empty name";
        if (parts.Count > maxDepth) return $"more than {maxDepth.ToString(CultureInfo.InvariantCulture)} folders deep";
        string joined = string.Join('/', parts);
        if (joined.Length > MaxTotal) return "a path longer than 32,000 characters";
        normalized = joined;
        return null;
    }

    /// <summary>The device name a component names ("CON", "aux.txt", "COM1 .log"), or null.</summary>
    private static string? IsDeviceName(string part)
    {
        int dot = part.IndexOf('.');
        string stem = (dot < 0 ? part : part[..dot]).TrimEnd(' ');
        if (stem.Length is < 3 or > 7) return null;
        return PathSafety.Validate(stem) is { } why && why.StartsWith("reserved device name", StringComparison.Ordinal) ? stem : null;
    }

    /// <summary>
    /// Rule 3: in each component, ': * ? " &lt; &gt; |' and control characters become '_', and a trailing dot or space
    /// becomes '_' too ("x.exe:Zone.Identifier" → "x.exe_Zone.Identifier", "name." → "name_"). The same name always
    /// maps to the same result. Returns whether anything changed.
    /// </summary>
    public static bool MapForWindows(string normalized, out string mapped)
    {
        var chars = normalized.ToCharArray();
        bool changed = false;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c < 32 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                chars[i] = '_';
                changed = true;
            }
            bool endOfComponent = i == chars.Length - 1 || chars[i + 1] == '/';
            if (endOfComponent && chars[i] is '.' or ' ')
            {
                chars[i] = '_';
                changed = true;
            }
        }
        mapped = new string(chars);
        return changed;
    }
}
