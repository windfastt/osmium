namespace Osmium.Desktop.Jobs;

/// <summary>The paths Windows gives for where a file really lies, in the form drive types are asked for.</summary>
public static class FinalPaths
{
    /// <summary>
    /// A path from GetFinalPathNameByHandle in the form with a drive letter or a share: "\\?\C:\a" as "C:\a",
    /// "\\?\UNC\server\share\a" as "\\server\share\a". Null for any other form, such as "\\?\Volume{…}\a" for a volume
    /// without a drive letter: its drive type is then unknown, so the item stays. (Without its prefix such a path would
    /// name a place below the current folder.)
    /// </summary>
    public static string? ToDos(string final)
    {
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + final[8..];
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
        else if (final.StartsWith(@"\\", StringComparison.Ordinal)) return final; // a share
        return final.Length >= 3 && char.IsAsciiLetter(final[0]) && final[1] == ':' && final[2] == '\\' ? final : null;
    }
}
