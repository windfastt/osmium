namespace Osmium.Desktop.Tests;

public class AboutInfoTests
{
    [Theory]
    [InlineData("0.1.2+9a13971d166303229363fea0f0cdcd2e77be3a07", "0.1.2 (commit 9a13971)")]
    [InlineData("0.1.1-ci.9+A7D1EF3E9DCBEE063CEBB6827295AD25D22D006A", "0.1.1-ci.9 (commit a7d1ef3)")]
    [InlineData("0.1.2", "0.1.2")]
    [InlineData("1.0.0+build.5", "1.0.0+build.5")]                  // not a commit hash: unchanged
    [InlineData("1.0.0+abc", "1.0.0+abc")]                          // too short for a commit
    [InlineData("+9a13971d16", "+9a13971d16")]
    public void VersionsShowTheirShortCommit(string informational, string shown) =>
        Assert.Equal(shown, AboutInfo.DisplayVersion(informational));

    [Fact]
    public void TheEngineFingerprintIsEightStableHexDigits()
    {
        string f = AboutInfo.EngineFingerprint();
        Assert.Equal(8, f.Length);
        Assert.All(f, c => Assert.True(Uri.IsHexDigit(c), f));
        Assert.Equal(f, AboutInfo.EngineFingerprint());
        Assert.StartsWith(f, Convert.ToHexString(Core.Distributed.Protocol.EngineFingerprint()));
    }
}
