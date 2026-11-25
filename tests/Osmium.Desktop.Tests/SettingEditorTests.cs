using Osmium.Core.Pipeline;
using Osmium.Desktop.Settings;

namespace Osmium.Desktop.Tests;

/// <summary>How settings and codec parameters are presented and edited.</summary>
public class SettingEditorTests
{
    private static readonly List<SettingDescriptor> All = SettingReflector.Describe(new OsmiumSettings());
    private static SettingDescriptor Setting(string key) => All.Single(d => d.Key == key);

    [Fact]
    public void VerificationWarnsWhenTurnedOff()
    {
        var verify = Setting("Compression.VerifyBlocks");
        Assert.True(verify.WarnWhenOff);
        Assert.True(verify.NeedsConfirmation(false));
        Assert.False(verify.NeedsConfirmation(true));
    }

    [Fact]
    public void ExperimentalFeaturesWarnWhenTurnedOn()
    {
        var experimental = Setting("Experimental.Enabled");
        Assert.False(experimental.WarnWhenOff);
        Assert.True(experimental.NeedsConfirmation(true));
        Assert.False(experimental.NeedsConfirmation(false));
    }

    [Fact]
    public void SettingsWithoutAWarningNeverAsk()
    {
        foreach (var d in All.Where(d => d.ValueType == typeof(bool) && d.WarningText is null))
        {
            Assert.False(d.NeedsConfirmation(true), d.Key);
            Assert.False(d.NeedsConfirmation(false), d.Key);
        }
    }

    [Fact]
    public void EveryWarningIsAskedBeforeLeavingTheDefault()
    {
        // The question belongs to the change away from the default (safe) state, whichever direction that is.
        foreach (var d in All.Where(d => d.WarningText is not null))
        {
            Assert.Equal(typeof(bool), d.ValueType);
            bool defaultOn = d.Default is true;
            Assert.True(d.NeedsConfirmation(!defaultOn), d.Key);
            Assert.False(d.NeedsConfirmation(defaultOn), d.Key);
        }
    }

    [Theory]
    [InlineData("Cluster.WorkerPort", false)]          // "46510", not "46,510"
    [InlineData("Integration.CoalesceMs", false)]
    [InlineData("Performance.Threads", false)]
    [InlineData("Compression.BlockSizeMiB", false)]
    [InlineData("Security.KdfIterations", true)]       // "600,000"
    [InlineData("Performance.MemoryLimitMiB", true)]
    public void OnlyLargeRangesGroupDigits(string key, bool grouped) => Assert.Equal(grouped, Setting(key).GroupDigits);

    public static TheoryData<string> Methods() => [.. MethodRegistry.Default.All.Select(m => m.Name)];

    [Theory]
    [MemberData(nameof(Methods))]
    public void EveryParameterValueSurvivesItsEditor(string method)
    {
        var m = MethodRegistry.Default.Get(method);
        foreach (var p in m.Params)
        {
            var values = new[] { p.Min, p.Max, p.Default, (p.Min + p.Max) / 2 }.Concat(p.SearchValues ?? []).Select(p.Clamp).Distinct();
            foreach (long v in values)
            {
                var state = ParamValues.Empty.With(p.Name, v);
                var d = ParamDescriptors.For(m, p, () => state, s => state = s);
                object? shown = d.Get();
                Assert.NotNull(shown);
                Assert.IsType(d.ValueType, shown);
                Assert.Null(d.Validate(shown));
                state = ParamValues.Empty;                   // the editor writes the value back on its own
                d.Set(shown);
                Assert.Equal(v, state.Get(p.Name, long.MinValue));
            }
        }
    }

    [Fact]
    public void ParametersUseTheirNaturalEditors()
    {
        var lzma = MethodRegistry.Default.Get("lzma");
        var state = ParamValues.Empty;
        var editors = ParamDescriptors.For(lzma, () => state, s => state = s).ToDictionary(d => d.Key);

        var mf = editors["lzma.mf"];                             // a choice: shown by name
        Assert.Equal(new[] { "bt2", "bt4" }, mf.Choices!.ToArray());
        Assert.Equal("bt4", mf.Get());
        mf.Set("bt2");
        Assert.Equal(0, state.Get("mf", -1));

        var dict = editors["lzma.dict"];                         // a size: shown as "64M"
        Assert.Equal(SettingEditor.ByteSize, dict.Editor);
        Assert.Equal("64M", dict.Get());
        Assert.Null(dict.Validate("256M"));
        Assert.Contains("between 64K and 512M", dict.Validate("1K"));
        Assert.Contains("for example 64M", dict.Validate("lots"));
        dict.Set("1536K");
        Assert.Equal(1536L << 10, state.Get("dict", -1));
        Assert.Equal("1536K", dict.Get());
        Assert.Null(dict.Validate(" 64M "));                     // padding is ignored by validation and by Set alike
        dict.Set(" 64M ");
        Assert.Equal(64L << 20, state.Get("dict", -1));

        Assert.Equal(typeof(long), editors["lzma.lc"].ValueType); // a number: shown as a number

        var zstd = MethodRegistry.Default.Get("zstd");
        var ldm = ParamDescriptors.For(zstd, () => state, s => state = s).Single(d => d.Key == "zstd.ldm");
        Assert.Equal(typeof(bool), ldm.ValueType);               // on/off: a checkbox
        Assert.Equal(false, ldm.Get());
        ldm.Set(true);
        Assert.Equal(1, state.Get("ldm", -1));
    }
}
