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
