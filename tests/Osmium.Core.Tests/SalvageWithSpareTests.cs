using Osmium.Core.Container;

namespace Osmium.Core.Tests;

/// <summary><c>osmium salvage</c> on archives of container 1.1 and on archives written as Osmium 0.1.x wrote them.</summary>
public class SalvageWithSpareTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertRecovered(Dictionary<string, byte[]> files, string destination, SalvageResult res)
    {
        Assert.True(res.IndexRecovered, string.Join(" | ", res.Summary()));
        Assert.Equal(files.Count, res.FilesRecovered);
        Assert.Empty(res.Lost);
        foreach (var (name, data) in files)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine([destination, .. name.Split('/')])));
    }

    private static OsaWriterOptions WithSpares(string? password = null) => new() { Password = password, SpareCopies = SpareCopyPolicy.Always };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnIntactArchiveWithSpareCopiesSalvagesAsComplete(bool encrypted)
    {
        using var t = new TempDir();
        string? pw = encrypted ? SpareArchives.Password : null;
        var files = SpareArchives.Files(4, 3000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, WithSpares(pw));
        var tail = SpareArchives.TailOf(path, pw);

        var res = Salvage.Run(path, t.Sub("out"), () => pw, null, Ct);
        Assert.Equal(0, res.ExitCode);
        Assert.True(res.Complete);
        Assert.Equal(0, res.UnrecognizedBytes); // the header copy is recognised
        Assert.Equal(0, res.FramesSetAside);
        Assert.Equal(tail.IndexOffset, res.IndexFrame!.Offset);
        var spare = Assert.Single(res.Frames, f => f.IsSpareCopy);
        Assert.Equal((tail.SpareOffset, SalvageFrameState.Duplicate), (spare.Offset, spare.State));
        Assert.Equal("Everything was recovered.", res.Summary().Last());
        Assert.DoesNotContain(res.Summary(), s => s.Contains("duplicate", StringComparison.Ordinal));
        AssertRecovered(files, t.Sub("out"), res);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithTheFileListDestroyedSalvageRestoresEveryFileFromItsSpareCopy(bool encrypted)
    {
        using var t = new TempDir();
        string? pw = encrypted ? SpareArchives.Password : null;
        var files = SpareArchives.Files(4, 3000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, WithSpares(pw));
        var tail = SpareArchives.TailOf(path, pw);
        var rnd = new Random(3);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => rnd.NextBytes(b.AsSpan((int)tail.IndexOffset, (int)tail.IndexLength)));

        var res = Salvage.Run(bad, t.Sub("out"), () => pw, null, Ct);
        Assert.Equal(0, res.ExitCode);
        Assert.Equal(tail.SpareOffset, res.IndexFrame!.Offset);
        Assert.Equal(tail.IndexLength, res.UnrecognizedBytes); // the destroyed frame, and nothing else
        Assert.StartsWith("Everything was recovered: the archive is damaged", res.Summary().Last());
        AssertRecovered(files, t.Sub("out"), res);
    }

    [Theory]
    [InlineData("zeroed")]
    [InlineData("crc")]
    [InlineData("marker")]
    public void WithTheHeaderDamagedAnEncrypted11ArchiveSalvagesAtLeastAsWellAsA10One(string how)
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(4, 3000);
        SalvageResult Salvaged(string name, OsaWriterOptions options)
        {
            string path = SpareArchives.Write(t.Sub(name + ".osa"), files, options with { Password = SpareArchives.Password });
            long header = SpareArchives.TailOf(path, SpareArchives.Password).HeaderLength;
            string bad = SpareArchives.Damage(path, t.Sub(name + "-bad.osa"), b =>
            {
                if (how == "zeroed") Array.Clear(b, 0, (int)header);
                else b[how == "crc" ? header - 1 : 1] ^= 0x10;
            });
            return Salvage.Run(bad, t.Sub(name + "-out"), () => SpareArchives.Password, null, Ct);
        }
        var older = Salvaged("v10", new OsaWriterOptions { HeaderMinor = 0, SpareThreshold = long.MaxValue });
        var newer = Salvaged("v11", WithSpares());
        Assert.True(newer.FilesRecovered >= older.FilesRecovered, $"1.1 recovered {newer.FilesRecovered}, 1.0 {older.FilesRecovered}");
        Assert.True(newer.BlocksVerified >= older.BlocksVerified);
        Assert.True(newer.ExitCode <= older.ExitCode);
        if (how != "zeroed") AssertRecovered(files, t.Sub("v11-out"), newer);
    }

    [Fact]
    public async Task ASalvagedReadOnlyProgramKeepsTheMarkOnARealStream()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "needs NTFS alternate data streams");
        using var t = new TempDir();
        try
        {
            string archive = await ExtractSafetyTests.ZonedArchiveWithAReadOnlyProgram(t, Ct);
            var res = Salvage.Run(archive, t.Sub("salvaged"), null, null, Ct);
            Assert.Equal(0, res.ExitCode);
            Assert.Empty(res.Warnings);
            string exe = Path.Combine(t.Sub("salvaged"), "in", "setup.exe");
            Assert.True(File.GetAttributes(exe).HasFlag(FileAttributes.ReadOnly));
            Assert.Contains("ZoneId=3", File.ReadAllText(exe + ":Zone.Identifier"));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(t.Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AnEncryptedArchiveWithADamagedMarkerIsRebuiltWhateverItsMinorVersion(ushort minor)
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(3, 4000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files,
            new OsaWriterOptions { Password = SpareArchives.Password, HeaderMinor = minor, SpareThreshold = long.MaxValue });
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => b[1] ^= 0x20);

        var res = Salvage.Run(bad, t.Sub("out"), () => SpareArchives.Password, null, Ct);
        Assert.Equal(0, res.ExitCode);
        Assert.False(res.HeaderIntact);
        AssertRecovered(files, t.Sub("out"), res);
    }
}
