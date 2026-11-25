using Osmium.Core.Text;

namespace Osmium.Core.Tests;

public class DisplayTextTests
{
    [Theory]
    [InlineData("a\u001b[31mred", "a\\u001b[31mred")]               // ESC: a terminal colour sequence stays inert
    [InlineData("nul\0here", "nul\\u0000here")]
    [InlineData("del\u007f", "del\\u007f")]
    [InlineData("next\u0085line", "next\\u0085line")]                // C1 NEL
    [InlineData("tab\tand\nline", "tab\\u0009and\\u000aline")]
    [InlineData("invoice‮fdp.exe", "invoice<U+202E>fdp.exe")]  // RIGHT-TO-LEFT OVERRIDE
    [InlineData("a⁦b⁩c", "a<U+2066>b<U+2069>c")]
    [InlineData("؜‎‏‪‫‬‭⁧⁨", "<U+061C><U+200E><U+200F><U+202A><U+202B><U+202C><U+202D><U+2067><U+2068>")]
    public void ControlAndDirectionCharactersBecomeVisible(string input, string expected) =>
        Assert.Equal(expected, DisplayText.Escape(input));

    [Fact]
    public void LoneSurrogatesBecomeReplacementCharacters()
    {
        Assert.Equal("a�b", DisplayText.Escape("a\uD800b"));
        Assert.Equal("a�b", DisplayText.Escape("a\uDC00b"));
        Assert.Equal("�", DisplayText.Escape("\uD83D"));             // a high surrogate at the end
        Assert.Equal("��", DisplayText.Escape("\uDE00\uD83D")); // a pair in the wrong order
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain name.txt")]
    [InlineData("ünïcödé — файл.txt")]
    [InlineData("報告書 2026.docx")]
    [InlineData("מסמך.pdf")]                    // right-to-left letters are not direction controls
    [InlineData("photo \U0001F600\U0001F1E9\U0001F1EA.jpg")]                  // surrogate pairs, a flag
    [InlineData("\U0001F469‍\U0001F469‍\U0001F467 family.png")]    // zero-width joiners
    public void OtherTextIsUnchanged(string input) => Assert.Same(input, DisplayText.Escape(input));

    [Fact]
    public void EscapingKeepsWhatSurroundsTheCharacter() =>
        Assert.Equal("😀\\u001b😀<U+202E>報", DisplayText.Escape("😀\u001b😀‮報"));
}
