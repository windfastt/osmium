using System.Text;
using Osmium.Core.Container;

namespace Osmium.Cli.Tests;

/// <summary>A password option never ends in an archive without encryption: a missing or empty password is refused
/// before anything is written, standard input is read as UTF-8, and a password on the command line is warned about.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class PasswordOptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osmium-cli-" + Guid.NewGuid().ToString("N")[..8]);

    public PasswordOptionTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "in"));
        File.WriteAllText(Path.Combine(_dir, "in", "secret.txt"), "the contents to protect");
    }

    public void Dispose()
    {
        Cli.StandardInputOverride = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(params string[] parts) => Path.Combine([_dir, .. parts]);

    private static void Stdin(byte[] bytes) => Cli.StandardInputOverride = () => new MemoryStream(bytes);

    [Fact]
    public async Task ABarePasswordOptionIsAnErrorAndWritesNothing()
    {
        var r = await CliRunner.RunAsync(["a", P("out.osa"), P("in"), "--profile", "store", "--password"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--password needs a value", r.Error);
        Assert.False(File.Exists(P("out.osa")));
        Assert.DoesNotContain("true", r.Output);
    }

    [Theory]
    [InlineData("--password-stdin")]
    [InlineData("--encrypt")]
    [InlineData("--profile")]
    [InlineData("-q")]
    public async Task APasswordOptionNeverTakesTheNextOptionAsItsValue(string next)
    {
        Stdin(Encoding.UTF8.GetBytes("from stdin\n"));
        var r = await CliRunner.RunAsync(["a", P("out.osa"), P("in"), "--password", next, "store"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--password needs a value", r.Error);
        Assert.Contains("--password=<password> for one that starts with --", r.Error);
        Assert.False(File.Exists(P("out.osa")));
    }

    [Fact]
    public async Task AnyOptionWithoutItsValueIsAnError()
    {
        var r = await CliRunner.RunAsync(["a", P("out.osa"), P("in"), "--profile", "--no-verify"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--profile needs a value", r.Error);
        Assert.False(File.Exists(P("out.osa")));
    }

    [Fact]
    public async Task APasswordThatStartsWithTwoDashesIsGivenWithAnEqualsSign()
    {
        var a = await CliRunner.RunAsync(["a", P("out.osa"), P("in"), "--profile", "store", "-q", "--password=--odd one"]);
        Assert.Equal(0, a.ExitCode);
        Assert.Throws<PasswordRequiredException>(() => OsaReader.Open(P("out.osa"), null));
        Assert.Equal(0, (await CliRunner.RunAsync(["t", P("out.osa"), "--password=--odd one"])).ExitCode);
        Assert.Equal(1, (await CliRunner.RunAsync(["t", P("out.osa"), "--password=--odd"])).ExitCode);
    }

    [Fact]
    public async Task BothPasswordOptionsAtOnceAreRefused()
    {
        Stdin(Encoding.UTF8.GetBytes("from stdin\n"));
        var r = await CliRunner.RunAsync(["a", P("out.osa"), P("in"), "--password", "on the line", "--password-stdin"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("Give the password once", r.Error);
        Assert.False(File.Exists(P("out.osa")));
    }

    [Theory]
    [InlineData("--password=")]
    [InlineData("--password-stdin")]
    public async Task AnEmptyPasswordIsRefusedAndWritesNothing(string option)
    {
        foreach (var input in new[] { "", "\n", "\r\n" })
        {
            Stdin(Encoding.UTF8.GetBytes(input));
            var r = await CliRunner.RunAsync(["a", P("out.osa"), P("in"), "--profile", "store", option]);
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("An empty password cannot protect an archive.", r.Error);
            Assert.False(File.Exists(P("out.osa")));
        }
    }

    [Fact]
    public async Task APasswordOnStandardInputIsReadAsUtf8()
    {
        const string password = "Pässwörd ü 鍵";
        Stdin(Encoding.UTF8.GetBytes(password + "\r\n"));
        var a = await CliRunner.RunAsync(["a", P("out.osa"), P("in"), "--profile", "store", "--password-stdin", "-q"]);
        Assert.Equal(0, a.ExitCode);
        Assert.DoesNotContain(password, a.Output + a.Error);
        // Encrypted, with exactly that password (not its bytes read in the console's code page).
        Assert.Throws<PasswordRequiredException>(() => OsaReader.Open(P("out.osa"), () => null).Dispose());
        using (var reader = OsaReader.Open(P("out.osa"), () => password)) Assert.True(reader.Header.Encrypted);

        Stdin(Encoding.UTF8.GetBytes(password + "\n"));
        var x = await CliRunner.RunAsync(["x", P("out.osa"), P("back"), "--password-stdin"]);
        Assert.Equal(0, x.ExitCode);
        Assert.Equal("the contents to protect", File.ReadAllText(P("back", "in", "secret.txt")));

        Stdin(Encoding.UTF8.GetBytes("Passwörd\n"));
        var wrong = await CliRunner.RunAsync(["x", P("out.osa"), P("wrong"), "--password-stdin"]);
        Assert.Equal(1, wrong.ExitCode);
        Assert.False(Directory.Exists(P("wrong")));
    }

    [Fact]
    public async Task OnlyOneTrailingLineEndIsRemoved()
    {
        Stdin(Encoding.UTF8.GetBytes(" spaced \r\nsecond line"));
        Assert.Equal(0, (await CliRunner.RunAsync(["a", P("out.osa"), P("in"), "--profile", "store", "--password-stdin", "-q"])).ExitCode);
        using var reader = OsaReader.Open(P("out.osa"), () => " spaced ");
        Assert.True(reader.Header.Encrypted);
    }

    [Fact]
    public async Task AByteOrderMarkIsNotPartOfThePassword()
    {
        Stdin([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("secret\r\n")]);
        Assert.Equal(0, (await CliRunner.RunAsync(["a", P("out.osa"), P("in"), "--profile", "store", "--password-stdin", "-q"])).ExitCode);
        using (var reader = OsaReader.Open(P("out.osa"), () => "secret")) Assert.True(reader.Header.Encrypted);
        Assert.Equal(0, (await CliRunner.RunAsync(["t", P("out.osa"), "--password", "secret"])).ExitCode);
    }

    [Fact]
    public async Task APasswordOnTheCommandLineIsWarnedAbout()
    {
        var r = await CliRunner.RunAsync(["a", P("out.osa"), P("in"), "--profile", "store", "--password", "s3cret", "-q"]);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("can be seen by other programs", r.Error);
        Assert.DoesNotContain("s3cret", r.Output + r.Error);
        using var reader = OsaReader.Open(P("out.osa"), () => "s3cret");
        Assert.True(reader.Header.Encrypted);
    }

    [Fact]
    public async Task EncryptWithoutAPasswordNeedsAConsole()
    {
        if (!Console.IsInputRedirected) Assert.Skip("Standard input is a console here, so the run would ask for the password.");
        var r = await CliRunner.RunAsync(["a", P("out.osa"), P("in"), "--profile", "store", "--encrypt"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--encrypt needs a password", r.Error);
        Assert.False(File.Exists(P("out.osa")));
    }

    [Fact]
    public async Task UsageListsTheStandardInputOptionFirst()
    {
        var r = await CliRunner.RunAsync(["help"]);
        foreach (var line in r.Output.Split('\n').Where(l => l.Contains("--password", StringComparison.Ordinal)))
            Assert.True(line.IndexOf("--password-stdin", StringComparison.Ordinal) < line.IndexOf("--password <pw>", StringComparison.Ordinal), line);
        Assert.Contains("--encrypt", r.Output);
    }
}
