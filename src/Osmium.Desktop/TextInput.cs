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

    /// <summary>
    /// The password to use from a dialog's text. A NEW password has control characters stripped (they are invisible in
    /// a masked box); one typed to open an existing archive is used as typed, since an older version or
    /// <c>osmium a --password</c> may have set a password that contains a tab.
    /// </summary>
    public static string PasswordFromText(string text, bool isNew) => isNew ? StripControlChars(text) : text;

    /// <summary>What one key press does in the console password prompt.</summary>
    public enum PromptAction { Append, Backspace, Enter, Cancel, Ignore }

    /// <summary>
    /// Classifies a console key press. Ctrl+C is the control character U+0003 (what TreatControlCAsInput delivers) or C
    /// with Control but not Alt: Windows reports AltGr as Ctrl+Alt, so AltGr+C (e.g. '&amp;' on some layouts) is text.
    /// Tab is a password character; keys that produce no character, Esc and other control characters are ignored.
    /// </summary>
    public static PromptAction ClassifyPromptKey(ConsoleKey key, ConsoleModifiers modifiers, char keyChar)
    {
        if (key == ConsoleKey.Enter) return PromptAction.Enter;
        if (keyChar == '\u0003' || (key == ConsoleKey.C && modifiers.HasFlag(ConsoleModifiers.Control) && !modifiers.HasFlag(ConsoleModifiers.Alt)))
            return PromptAction.Cancel;
        if (key == ConsoleKey.Backspace) return PromptAction.Backspace;
        if (keyChar == '\t') return PromptAction.Append;
        if (keyChar == '\0' || IsControl(keyChar) || char.IsControl(keyChar)) return PromptAction.Ignore;
        return PromptAction.Append;
    }

    private static bool IsControl(char c) => c < (char)0x20 || c == (char)0x7F;
}
