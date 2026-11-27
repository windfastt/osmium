using Osmium.Core.Crypto;

namespace Osmium.Core.Tests.Crypto;

public class KdfParametersTests
{
    [Fact]
    public void ThePresetsArePinned()
    {
        Assert.Equal(KdfParameters.Argon2id(3, 262_144, 4), KdfPresets.Floor);
        Assert.Equal(KdfParameters.Argon2id(2, 1_048_576, 4), KdfPresets.Standard);
        Assert.Equal(KdfParameters.Argon2id(3, 2_097_152, 4), KdfPresets.Strong);
        Assert.Equal(KdfParameters.Argon2id(3, 4_194_304, 4), KdfPresets.Maximum);
        Assert.Equal(KdfParameters.Pbkdf2(600_000), KdfPresets.Compatible());
        Assert.Equal(KdfParameters.Pbkdf2(100_000), KdfPresets.Compatible(100_000));
        Assert.Same(KdfPresets.Standard, KdfPresets.Default);
        foreach (var p in new[] { KdfPresets.Floor, KdfPresets.Standard, KdfPresets.Strong, KdfPresets.Maximum, KdfPresets.Compatible() })
            Assert.Equal(KdfVerdictKind.Ok, p.Check().Kind);
        // The strongest preset is exactly the work above which a reader asks.
        Assert.Equal(KdfLimits.QuestionWorkKiB, KdfPresets.Maximum.WorkKiB);
        Assert.Equal((KdfLimits.MinArgon2MemoryKiB, KdfLimits.MinArgon2WorkKiB), (KdfPresets.Floor.MemoryKiB, KdfPresets.Floor.WorkKiB));
    }

    [Theory]
    [InlineData(0, KdfVerdictKind.Invalid)]
    [InlineData(-1, KdfVerdictKind.Invalid)]
    [InlineData(1, KdfVerdictKind.Weak)]
    [InlineData(99_999, KdfVerdictKind.Weak)]
    [InlineData(100_000, KdfVerdictKind.Ok)]
    [InlineData(20_000_000, KdfVerdictKind.Ok)]
    [InlineData(20_000_001, KdfVerdictKind.Invalid)]
    public void Pbkdf2IterationsAreCheckedAtEveryLimit(int iterations, KdfVerdictKind expected) =>
        Assert.Equal(expected, KdfParameters.Pbkdf2(iterations).Check().Kind);

    [Theory]
    // the algorithm's own minimums
    [InlineData(0, 262_144, 4, KdfVerdictKind.Invalid)]
    [InlineData(3, 262_144, 0, KdfVerdictKind.Invalid)]
    [InlineData(1, 31, 4, KdfVerdictKind.Invalid)]             // m < 8·p
    [InlineData(1, 32, 4, KdfVerdictKind.Weak)]                // m = 8·p
    // the reader's hard limits
    [InlineData(16, 262_144, 4, KdfVerdictKind.Ok)]
    [InlineData(17, 262_144, 4, KdfVerdictKind.Invalid)]
    [InlineData(3, 262_144, 64, KdfVerdictKind.Ok)]
    [InlineData(3, 262_144, 65, KdfVerdictKind.Invalid)]
    [InlineData(1, 8_388_608, 4, KdfVerdictKind.Ok)]           // 8 GiB
    [InlineData(1, 8_388_609, 4, KdfVerdictKind.Invalid)]
    [InlineData(4, 8_388_608, 4, KdfVerdictKind.Ok)]           // t·m = 32 GiB-passes
    [InlineData(16, 2_097_152, 4, KdfVerdictKind.Ok)]
    [InlineData(16, 2_097_153, 4, KdfVerdictKind.Invalid)]     // one over
    [InlineData(5, 8_388_608, 4, KdfVerdictKind.Invalid)]
    // the minimum: 256 MiB and 768 MiB-passes
    [InlineData(3, 262_144, 4, KdfVerdictKind.Ok)]
    [InlineData(3, 262_143, 4, KdfVerdictKind.Weak)]
    [InlineData(2, 262_144, 4, KdfVerdictKind.Weak)]
    [InlineData(1, 786_432, 4, KdfVerdictKind.Ok)]
    [InlineData(1, 786_431, 4, KdfVerdictKind.Weak)]
    [InlineData(1, 64, 1, KdfVerdictKind.Weak)]
    public void Argon2idParametersAreCheckedAtEveryLimit(int t, int m, int p, KdfVerdictKind expected) =>
        Assert.Equal(expected, KdfParameters.Argon2id(t, m, p).Check().Kind);

    [Fact]
    public void InvalidParametersSayWhy()
    {
        Assert.Equal("Key derivation cost 0 is invalid.", KdfParameters.Pbkdf2(0).Check().Reason);
        Assert.Equal("Key derivation cost 20000001 exceeds the safety limit.", KdfParameters.Pbkdf2(20_000_001).Check().Reason);
        Assert.Equal("Key derivation cost (Argon2id, 1 GiB, 17 passes, 4 lanes) exceeds the safety limit.", KdfParameters.Argon2id(17, 1 << 20, 4).Check().Reason);
        Assert.Equal("Key derivation cost (Argon2id, 31 KiB, 1 pass, 4 lanes) is invalid.", KdfParameters.Argon2id(1, 31, 4).Check().Reason);
        Assert.Equal("Key derivation function 9 is unknown.", new KdfParameters { Algorithm = (KdfAlgorithm)9 }.Check().Reason);
        Assert.Null(KdfPresets.Standard.Check().Reason);
    }

    [Fact]
    public void DescriptionsNameTheCost()
    {
        Assert.Equal("Argon2id, 1 GiB, 2 passes, 4 lanes", KdfPresets.Standard.Describe());
        Assert.Equal("Argon2id, 256 MiB, 3 passes, 4 lanes", KdfPresets.Floor.Describe());
        Assert.Equal("Argon2id, 4 GiB, 3 passes, 4 lanes", KdfPresets.Maximum.Describe());
        Assert.Equal("Argon2id, 64 KiB, 1 pass, 1 lane", KdfParameters.Argon2id(1, 64, 1).Describe());
        Assert.Equal("Argon2id, 4,100 KiB, 2 passes, 4 lanes", KdfParameters.Argon2id(2, 4100, 4).Describe());
        Assert.Equal("Argon2id, 1,536 MiB, 2 passes, 4 lanes", KdfParameters.Argon2id(2, 1536 << 10, 4).Describe());
        Assert.Equal("PBKDF2-SHA512, 600,000 iterations", KdfPresets.Compatible().Describe());
        Assert.Equal("PBKDF2-SHA512, 1 iteration", KdfParameters.Pbkdf2(1).Describe());
        Assert.Equal(KdfPresets.Standard.Describe(), KdfPresets.Standard.ToString());
        Assert.Equal((1L << 30, 2L << 20), (KdfPresets.Standard.MemoryBytes, KdfPresets.Standard.WorkKiB));
        Assert.Equal((0L, 0L), (KdfPresets.Compatible().MemoryBytes, KdfPresets.Compatible().WorkKiB));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1,023 bytes")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(64L << 20, "64 MiB")]
    [InlineData(1L << 30, "1 GiB")]
    [InlineData(1932735283, "1.8 GiB")]
    [InlineData(4L << 30, "4 GiB")]
    [InlineData(1879048192, "1.75 GiB")]
    public void SizesInMessagesUseBinaryUnits(long bytes, string expected) => Assert.Equal(expected, SizeText.Format(bytes));
}
