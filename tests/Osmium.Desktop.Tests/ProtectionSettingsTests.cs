using Osmium.Core.Crypto;
using Osmium.Desktop.Settings;

namespace Osmium.Desktop.Tests;

/// <summary>Settings → Security → Password protection, and settings files that hold what the page would not allow.</summary>
public class ProtectionSettingsTests
{
    private static SettingDescriptor Setting(OsmiumSettings s, string key) => SettingReflector.Describe(s).Single(d => d.Key == key);

    private static OsmiumSettings Load(string json)
    {
        string dir = Path.Combine(Path.GetTempPath(), "osmium-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, json);
            var store = new SettingsStore(path);
            var s = store.Load();
            Assert.Null(store.LastError);
            return s;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(PasswordProtection.Standard, 2, 1 << 20)]
    [InlineData(PasswordProtection.Strong, 3, 2 << 20)]
    [InlineData(PasswordProtection.Maximum, 3, 4 << 20)]
    public void EveryArgon2idChoiceIsAPreset(PasswordProtection protection, int t, int m)
    {
        var p = new SecuritySettings { Protection = protection }.Kdf();
        Assert.Equal(KdfParameters.Argon2id(t, m, 4), p);
        Assert.Equal(KdfVerdictKind.Ok, p.Check().Kind);
    }

    [Fact]
    public void CompatibleUsesTheIterationsSetting()
    {
        Assert.Equal(KdfParameters.Pbkdf2(600_000), new SecuritySettings { Protection = PasswordProtection.Compatible }.Kdf());
        var p = new SecuritySettings { Protection = PasswordProtection.Compatible, KdfIterations = 100_000 }.Kdf();
        Assert.Equal(KdfParameters.Pbkdf2(100_000), p);
        Assert.Equal(KdfVerdictKind.Ok, p.Check().Kind);
        Assert.Equal(KdfVerdictKind.Ok, new SecuritySettings { Protection = PasswordProtection.Compatible, KdfIterations = 20_000_000 }.Kdf().Check().Kind);
        Assert.Equal(KdfPresets.Standard, new SecuritySettings().Kdf()); // the default
    }

    [Fact]
    public void TheEditorKeepsTheIterationsInTheirRange()
    {
        var d = Setting(new OsmiumSettings(), "Security.KdfIterations");
        Assert.Null(d.Validate(100_000));
        Assert.Null(d.Validate(20_000_000));
        Assert.NotNull(d.Validate(99_999));
        Assert.NotNull(d.Validate(20_000_001));
        Assert.True(d.GroupDigits);
    }

    [Fact]
    public void AHandEditedProtectionThatDoesNotExistIsStandard()
    {
        var s = Load("""{ "security": { "protection": 7 } }""");
        Assert.Equal((PasswordProtection)7, s.Security.Protection);
        Assert.Equal(KdfPresets.Standard, s.Security.Kdf());
    }

    [Fact]
    public void HandEditedIterationsOutsideTheRangeAreRefused()
    {
        var s = Load("""{ "security": { "protection": "Compatible", "kdfIterations": 50000 } }""");
        var e = Assert.Throws<ArgumentException>(() => s.Security.Kdf());
        Assert.Equal("The PBKDF2 iterations in the settings must be between 100,000 and 20,000,000.", e.Message);
        // with an Argon2id choice the iterations are not used, so they are not checked either
        Assert.Equal(KdfPresets.Strong, Load("""{ "security": { "protection": "Strong", "kdfIterations": 5 } }""").Security.Kdf());
    }

    [Fact]
    public void ASettingsFileOfOsmium01GetsStandardAndKeepsItsIterations()
    {
        var s = Load("""{ "schemaVersion": 1, "security": { "encryptByDefault": true, "kdfIterations": 1000000, "rememberForSession": true } }""");
        Assert.Equal(PasswordProtection.Standard, s.Security.Protection);
        Assert.Equal(1_000_000, s.Security.KdfIterations);
        Assert.Equal(KdfPresets.Standard, s.Security.Kdf());
        s.Security.Protection = PasswordProtection.Compatible;
        Assert.Equal(KdfParameters.Pbkdf2(1_000_000), s.Security.Kdf());
    }

    [Fact]
    public void TheIterationsAreShownOnlyForCompatible()
    {
        foreach (var protection in Enum.GetValues<PasswordProtection>())
        {
            var s = new OsmiumSettings();
            s.Security.Protection = protection;
            var all = SettingReflector.Describe(s);
            var d = all.Single(x => x.Key == "Security.KdfIterations");
            Assert.Equal(protection == PasswordProtection.Compatible, SettingReflector.Evaluate(d.VisibleWhen, all, d.Category));
        }
    }

    [Fact]
    public void TheSettingsSayWhatTheyDo()
    {
        var s = new OsmiumSettings();
        Assert.Equal("Ticks Encrypt on the Compress page. Explorer quick-compress and watch folders do not encrypt.",
            Setting(s, "Security.EncryptByDefault").Description);
        var protection = Setting(s, "Security.Protection");
        Assert.Equal(["Standard", "Strong", "Maximum", "Compatible"], protection.Choices);
        Assert.Contains("Compatible: PBKDF2, which Osmium 0.1 can open, but guessing is far cheaper.", protection.Description);
        foreach (var word in new[] { "unbreakable", "uncrackable", "military" })
            Assert.DoesNotContain(word, protection.Description, StringComparison.OrdinalIgnoreCase);
    }
}
