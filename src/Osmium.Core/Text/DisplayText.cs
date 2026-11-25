using System.Globalization;
using System.Text;

namespace Osmium.Core.Text;

/// <summary>Text from an archive (names, comments, messages) made safe to show in a terminal, a list or a dialog.</summary>
public static class DisplayText
{
    /// <summary>
    /// Makes the characters that change how text looks or where it goes visible: C0 and C1 control characters
    /// (including ESC and DEL) become <c>\u001b</c>-style escapes, bidirectional controls (U+061C, U+200E, U+200F,
    /// U+202A–U+202E, U+2066–U+2069) become <c>&lt;U+202E&gt;</c>-style markers, and lone surrogates become U+FFFD.
    /// Everything else, including letters of every script and emoji, is unchanged.
    /// </summary>
    public static string Escape(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb?.Append(c).Append(s[i + 1]);
                i++;
                continue;
            }
            string? replacement = char.IsSurrogate(c) ? "�"
                : c < 0x20 || c is >= '\u007F' and <= '\u009F' ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)
                : IsDirectionControl(c) ? "<U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) + ">"
                : null;
            if (replacement is null)
            {
                sb?.Append(c);
                continue;
            }
            sb ??= new StringBuilder(s.Length + 16).Append(s, 0, i);
            sb.Append(replacement);
        }
        return sb?.ToString() ?? s;
    }

    private static bool IsDirectionControl(char c) =>
        c is '؜' or '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');
}
