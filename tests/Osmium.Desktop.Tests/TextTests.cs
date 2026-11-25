using Osmium.Desktop.Tray;

namespace Osmium.Desktop.Tests;

public class TextTests
{
    private const char Del = (char)0x7F, Nul = (char)0, UnitSeparator = (char)0x1F;

    [Fact]
    public void ControlCharactersAreStrippedFromPasswords()
    {
        Assert.Equal("correct horse", TextInput.StripControlChars("correct" + Del + " horse\r\n"));
        Assert.Equal("ab", TextInput.StripControlChars("\ta" + Nul + "b" + UnitSeparator));
        Assert.Equal("", TextInput.StripControlChars("\r\n\t"));
        const string Unicode = "pässwörd ✓ 密码"; // printable non-ASCII stays
        Assert.Same(Unicode, TextInput.StripControlChars(Unicode));
    }

    [Fact]
    public void OnlyNewPasswordsAreStripped()
    {
        Assert.Equal("ab", TextInput.PasswordFromText("a\tb", isNew: true));
        Assert.Equal("a\tb", TextInput.PasswordFromText("a\tb", isNew: false));
    }

    [Theory]
    [InlineData(ConsoleKey.C, ConsoleModifiers.Control, '\u0003', TextInput.PromptAction.Cancel)]
    [InlineData(ConsoleKey.C, ConsoleModifiers.Control, 'c', TextInput.PromptAction.Cancel)]
    [InlineData(ConsoleKey.C, ConsoleModifiers.Control | ConsoleModifiers.Alt, '&', TextInput.PromptAction.Append)] // AltGr+C
    [InlineData(ConsoleKey.Enter, 0, '\r', TextInput.PromptAction.Enter)]
    [InlineData(ConsoleKey.Backspace, 0, '\b', TextInput.PromptAction.Backspace)]
    [InlineData(ConsoleKey.Backspace, ConsoleModifiers.Control, '\u007f', TextInput.PromptAction.Backspace)]
    [InlineData(ConsoleKey.Tab, 0, '\t', TextInput.PromptAction.Append)]
    [InlineData(ConsoleKey.Escape, 0, '\u001b', TextInput.PromptAction.Ignore)]
    [InlineData(ConsoleKey.LeftArrow, 0, '\0', TextInput.PromptAction.Ignore)]
    [InlineData(ConsoleKey.A, 0, 'a', TextInput.PromptAction.Append)]
    public void PromptKeysAreClassified(ConsoleKey key, ConsoleModifiers mods, char ch, TextInput.PromptAction expected) =>
        Assert.Equal(expected, TextInput.ClassifyPromptKey(key, mods, ch));

    [Fact]
    public void MenuTextShowsAmpersands()
    {
        Assert.Equal("Tom && Jerry.osa — Completed", TrayText.MenuText("Tom & Jerry.osa — Completed"));
        Assert.Equal("plain", TrayText.MenuText("plain"));
    }

    [Fact]
    public void ExitAsksOnlyWhenSomethingWouldBeLost()
    {
        Assert.Null(TrayText.ExitQuestion(false, []));
        Assert.Equal("Jobs are still running. Exiting cancels them.\n\nExit Osmium?", TrayText.ExitQuestion(true, []));
        Assert.Equal("Unsaved changes on the Settings page will be lost.\n\nExit Osmium?", TrayText.ExitQuestion(false, ["Settings"]));
        Assert.Equal("Jobs are still running. Exiting cancels them.\nUnsaved changes on the Rules, Cluster and Settings pages will be lost.\n\nExit Osmium?",
            TrayText.ExitQuestion(true, ["Rules", "Cluster", "Settings"]));
    }
}
