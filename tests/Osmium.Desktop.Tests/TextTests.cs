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
