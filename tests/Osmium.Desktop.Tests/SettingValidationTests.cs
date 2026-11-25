using Osmium.Core.Distributed;
using Osmium.Desktop.Jobs;
using Osmium.Desktop.Settings;
using Osmium.Desktop.Tray;

namespace Osmium.Desktop.Tests;

public class SettingValidationTests
{
    private static SettingDescriptor Descriptor(OsmiumSettings s, string key) => SettingReflector.Describe(s).Single(d => d.Key == key);

    [Fact]
    public void TheClusterKeySettingIsValidatedWithTheKeyParser()
    {
        var d = Descriptor(new OsmiumSettings(), "Cluster.ClusterKey");
        Assert.NotNull(d.TextValidator);
        string key = ClusterKey.Generate().ToString();
        Assert.Null(d.Validate(""));                                   // no key is allowed
        Assert.Null(d.Validate("   "));
        Assert.Null(d.Validate(key));
        Assert.Null(d.Validate(" " + key.ToLowerInvariant().Replace('-', ' ') + "\r\n"));
        Assert.Contains("too short", d.Validate(key[..^3]));
        Assert.Contains("start with OSM1-", d.Validate("hunter2"));
        Assert.Contains("checksum", d.Validate(key[..12] + (key[12] == 'A' ? 'B' : 'A') + key[13..]));
    }

    [Fact]
    public void NumericValidationIsUnchanged()
    {
        var port = Descriptor(new OsmiumSettings(), "Cluster.WorkerPort");
        Assert.Null(port.TextValidator);
        Assert.Null(port.Validate(46510));
        Assert.NotNull(port.Validate(80));
        Assert.NotNull(port.Validate(70000));
    }

    [Fact]
    public void ClusterKeysAreSavedInCanonicalForm()
    {
        string key = ClusterKey.Generate().ToString();
        Assert.Equal(key, ClusterSettings.NormalizeClusterKey(key));
        Assert.Equal(key, ClusterSettings.NormalizeClusterKey((char)0xFEFF + key.ToLowerInvariant().Replace("-", " - ") + "\n"));
        Assert.Equal("", ClusterSettings.NormalizeClusterKey(" \t "));
        Assert.Equal("OSM1-not-a-key", ClusterSettings.NormalizeClusterKey("OSM1-not-a-key"));   // kept, so the error shows where it is used
    }

    [Fact]
    public void TheTooltipKeepsAWarningLineWithinTheWindowsLimit()
    {
        var job = new JobSnapshot(Guid.NewGuid(), new string('x', 300), JobKind.Compress, JobState.Running, "Compressing", 0.5, 1, 1, 2,
            TimeSpan.FromSeconds(3), null, null, null, null, DateTime.Now, []);
        Assert.Equal(TrayText.Tooltip([job]), TrayText.Tooltip([job], null));
        Assert.Equal("Osmium — idle", TrayText.Tooltip([], " "));
        string warned = TrayText.Tooltip([job], "Not lending this PC: invalid cluster key");
        Assert.True(warned.Length <= TrayText.TooltipMax, warned.Length.ToString());
        Assert.EndsWith("\nNot lending this PC: invalid cluster key", warned);
        Assert.StartsWith("Osmium — compressing ", warned);
        Assert.Contains("xxx…\n", warned);                                       // the job line was shortened, not the warning
        Assert.Equal("Osmium — idle\nNot lending this PC: the worker stopped", TrayText.Tooltip([], "Not lending this PC: the worker stopped"));
        Assert.True(TrayText.Tooltip([job], new string('w', 400)).Length <= TrayText.TooltipMax);
    }
}
