namespace Osmium.Desktop;

/// <summary>Clean-up for text typed or pasted into password and key boxes.</summary>
public static class TextInput
{
    /// <summary>
    /// Removes control characters (U+0000–U+001F and U+007F): a pasted tab or line break, or the DEL character that
    /// some edit controls type for Ctrl+Backspace. They are invisible in a masked box, so a password containing one
    /// could never be typed again.
    /// </summary>
    public static string StripControlChars(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Any(IsControl)) return text;
        return new string(text.Where(c => !IsControl(c)).ToArray());
    }

    private static bool IsControl(char c) => c < (char)0x20 || c == (char)0x7F;
}
