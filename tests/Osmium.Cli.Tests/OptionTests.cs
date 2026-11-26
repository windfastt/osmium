namespace Osmium.Cli.Tests;

/// <summary>Every command refuses options it does not read, so a mistyped or foreign one ("-p secret", "--pasword",
/// the attached "-psecret") never leaves an archive unprotected while the run says it succeeded.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class OptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osmium-cli-" + Guid.NewGuid().ToString("N")[..8]);

    public OptionTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "in"));
        File.WriteAllText(Path.Combine(_dir, "in", "a.txt"), "the contents");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(params string[] parts) => Path.Combine([_dir, .. parts]);

    [Theory]
    [InlineData(new[] { "-p", "secret" }, "'-p'")]
    [InlineData(new[] { "-psecret" }, "'-p…'")]
    [InlineData(new[] { "--pasword", "secret" }, "'--pa…'")]
    [InlineData(new[] { "--pw=secret" }, "'--pw'")]
    [InlineData(new[] { "--password:secret" }, "'--pa…'")]
    [InlineData(new[] { "--psecret" }, "'--ps…'")]
    public async Task AnUnknownOptionIsAnErrorAndWritesNothing(string[] options, string shown)
    {
        var r = await CliRunner.RunAsync(["a", P("out.osa"), P("in"), "--profile", "store", .. options]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains($"Unknown option {shown} for osmium a", r.Error);
        Assert.DoesNotContain("secret", r.Error + r.Output);
        Assert.False(File.Exists(P("out.osa")));
    }

    [Theory]
    [InlineData("x", "--overwrit", "--ov…")]
    [InlineData("t", "--overwrite", "--ov…")]
    [InlineData("l", "--quiet", "--qu…")]
    [InlineData("info", "-v", "-v")]
    [InlineData("salvage", "--include", "--in…")]
    public async Task EveryCommandRefusesOptionsItDoesNotRead(string command, string option, string shown)
    {
        var made = await CliRunner.RunAsync(["a", P("ok.osa"), P("in"), "--profile", "store", "-q"]);
        Assert.Equal(0, made.ExitCode);
        var r = await CliRunner.RunAsync([command, P("ok.osa"), P("dest"), option, "x"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains($"Unknown option '{shown}' for osmium {command}", r.Error);
        Assert.False(Directory.Exists(P("dest")));
    }

    [Fact]
    public async Task TestReadsTheSameIncludeAndForceAsExtract()
    {
        string zip = P("a.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(P("in"), zip);
        var some = await CliRunner.RunAsync(["t", zip, "--include", "*.txt"]);
        Assert.True(some.ExitCode == 0, some.Error);
        Assert.Contains("OK — 1 file(s) read.", some.Output);
        var none = await CliRunner.RunAsync(["t", zip, "--include", "*.bin"]);
        Assert.Contains("OK — 0 file(s) read.", none.Output);

        // An archive that claims to expand far past 1,000 times its size: refused, and tested with --force.
        var bytes = File.ReadAllBytes(zip);
        for (int i = 0; i + 4 <= bytes.Length; i++)
        {
            uint sig = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            if (sig == 0x04034b50) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 22), 0x7FFFFFFF);
            if (sig == 0x02014b50) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), 0x7FFFFFFF);
        }
        File.WriteAllBytes(zip, bytes);
        var refused = await CliRunner.RunAsync(["t", zip]);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("Use --force", refused.Error);
        var forced = await CliRunner.RunAsync(["t", zip, "--force"]);
        Assert.DoesNotContain("Unknown option", forced.Error);
        Assert.Equal(2, forced.ExitCode); // read, and the file is not the size it claims
        Assert.Contains("FAILED", forced.Output);
    }

    [Fact]
    public async Task ListAndInfoTakeTheForceThatOpeningReads()
    {
        string zip = P("a.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(P("in"), zip);
        foreach (var command in new[] { "l", "info" })
        {
            var r = await CliRunner.RunAsync([command, zip, "--force"]);
            Assert.True(r.ExitCode == 0, r.Error);
        }
    }

    /// <summary>Each command run once on the paths it has, with the options it takes: the runner fails any run whose
    /// code reads an option its row of the table leaves out. (methods and license read none, and print the codecs'
    /// and the notices' own text.)</summary>
    [Fact]
    public async Task EveryCommandReadsOnlyTheOptionsItDeclares()
    {
        string osa = P("o.osa"), zip = P("o.zip"), rules = P("rules.json");
        File.WriteAllText(rules, "{ \"rules\": [] }");
        string[][] runs =
        [
            ["a", osa, P("in"), "--profile", "store", "-q", "--spare-copies", "never", "--no-verify"],
            ["a", zip, P("in"), "-q"],
            ["l", osa, "-v"], ["l", zip, "-v"],
            ["t", osa, "-q"], ["t", zip, "-q", "--include", "*"],
            ["info", osa], ["info", zip],
            ["x", osa, P("x1"), "-q", "--include", "*"], ["x", zip, P("x2"), "-q", "--skip"],
            ["salvage", osa, P("x3"), "-q"],
            ["explain", osa], ["analyze", P("in")], ["profiles"], ["version"],
            ["rules", "check", rules],
            ["lab", Path.Combine(P("in"), "a.txt"), "--profile", "fast", "--sample", "64K"],
            ["tune", Path.Combine(P("in"), "a.txt"), "--profile", "fast"],
            ["keygen"],
        ];
        foreach (var run in runs)
        {
            var r = await CliRunner.RunAsync(run);
            Assert.True(r.ExitCode is 0 or 2, $"osmium {string.Join(' ', run)}: {r.Error}");
        }
    }

    [Fact]
    public async Task OptionsOfOsaArchivesAreRefusedForAZipFile()
    {
        var r = await CliRunner.RunAsync(["a", P("out.zip"), P("in"), "--threads", "2"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--threads does not apply to ZIP files", r.Error);
        Assert.False(File.Exists(P("out.zip")));
    }

    [Fact]
    public async Task TwoDashesEndTheOptions()
    {
        File.WriteAllText(P("-dash.txt"), "a file whose name starts with a dash");
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _dir;
        try
        {
            var r = await CliRunner.RunAsync(["a", P("out.osa"), "--profile", "store", "-q", "--", "-dash.txt"]);
            Assert.True(r.ExitCode == 0, r.Error);
            var l = await CliRunner.RunAsync(["l", P("out.osa")]);
            Assert.Contains("-dash.txt", l.Output);
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    [Fact]
    public async Task HelpAfterACommandPrintsTheUsage()
    {
        var r = await CliRunner.RunAsync(["x", "--help"]);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Usage: osmium <command> [options]", r.Output);
    }
}
