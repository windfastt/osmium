namespace Osmium.Cli.Tests;

[Collection(ConsoleCollection.Name)]
public class UsageTests
{
    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task HelpPrintsTheCommandsAndSucceeds(string command)
    {
        var r = await CliRunner.RunAsync([command]);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Usage: osmium <command> [options]", r.Output);
        foreach (var line in new[] { "a <archive.osa>", "x <archive> [dest]", "l <archive> [-v]", "t <archive>", "salvage <archive> <dest>" })
            Assert.Contains(line, r.Output);
        Assert.Empty(r.Error);
    }

    [Fact]
    public async Task AnUnknownCommandFails()
    {
        var r = await CliRunner.RunAsync(["frobnicate"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("Unknown command 'frobnicate'", r.Error);
        Assert.Empty(r.Output);
    }
}
