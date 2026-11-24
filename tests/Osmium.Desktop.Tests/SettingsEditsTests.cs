using System.Reflection;
using System.Text.Json;
using Osmium.Core.Distributed;
using Osmium.Desktop.Settings;

namespace Osmium.Desktop.Tests;

/// <summary>The Settings page applies only what the user edited, so changes made elsewhere are never reverted.</summary>
public class SettingsEditsTests
{
    private static string Json(OsmiumSettings s) => JsonSerializer.Serialize(s, SettingsStore.Json);

    [Fact]
    public void ChangesMadeElsewhereSurviveApply()
    {
        var current = new OsmiumSettings();
        var working = current.Clone();                         // the Settings page was opened
        string key = ClusterKey.Generate().ToString();
        current.Compression.Profile = ProfileName.Ultra;       // then: tray "Default profile"
        current.Watch.Enabled = true;                          //       tray "Watch folders"
        current.Cluster.LendThisPc = true;                     //       tray "Lend this PC"
        current.Cluster.ClusterKey = key;                      //       Cluster page "Save"
        working.Performance.Threads = 4;                       // the user's own edit on the page

        var saved = SettingsEdits.Apply(current, working, ["Performance.Threads"]);

        Assert.Equal(4, saved.Performance.Threads);
        Assert.Equal(ProfileName.Ultra, saved.Compression.Profile);
        Assert.True(saved.Watch.Enabled);
        Assert.True(saved.Cluster.LendThisPc);
        Assert.Equal(key, saved.Cluster.ClusterKey);
    }

    [Fact]
    public void AnEditedSettingTakesTheEditedValue()
    {
        var current = new OsmiumSettings();
        var working = current.Clone();
        current.Compression.Profile = ProfileName.Ultra;
        working.Compression.Profile = ProfileName.Fast;
        Assert.Equal(ProfileName.Fast, SettingsEdits.Apply(current, working, ["Compression.Profile"]).Compression.Profile);
    }

    [Fact]
    public void NothingEditedSavesTheCurrentSettingsUnchanged()
    {
        var current = new OsmiumSettings();
        var stale = current.Clone();
        current.Cluster.ClusterKey = ClusterKey.Generate().ToString();
        current.Codecs.AllowCm = false;
        stale.General.Theme = ThemeMode.Dark;                  // changed on the page but never marked as edited
        var saved = SettingsEdits.Apply(current, stale, []);
        Assert.Equal(Json(current), Json(saved));
        Assert.NotSame(current, saved);
    }

    [Fact]
    public void TheResultSharesNoListsWithItsInputs()
    {
        var current = new OsmiumSettings();
        var working = current.Clone();
        working.Compression.Excludes.Add("*.bak");
        var saved = SettingsEdits.Apply(current, working, ["Compression.Excludes", "Compression.Excludes"]);
        Assert.Contains("*.bak", saved.Compression.Excludes);
        working.Compression.Excludes.Add("*.old");             // later edits on the page
        current.Compression.Excludes.Clear();
        Assert.DoesNotContain("*.old", saved.Compression.Excludes);
        Assert.Contains("Thumbs.db", saved.Compression.Excludes);
    }

    [Fact]
    public void TheWatchFolderListIsCopiedOnlyWhenEdited()
    {
        var current = new OsmiumSettings();
        var working = current.Clone();
        working.Watch.Folders.Add(new WatchFolderConfig { Path = "/data/inbox", Profile = ProfileName.Max });
        Assert.Empty(SettingsEdits.Apply(current, working, ["Watch.Enabled"]).Watch.Folders);

        var saved = SettingsEdits.Apply(current, working, [SettingsEdits.WatchFolders]);
        var folder = Assert.Single(saved.Watch.Folders);
        Assert.Equal("/data/inbox", folder.Path);
        Assert.Equal(ProfileName.Max, folder.Profile);
        Assert.NotSame(working.Watch.Folders[0], folder);
    }

    [Fact]
    public void RestoringACategoryResetsOnlyThatCategory()
    {
        var current = new OsmiumSettings();
        current.Cluster.ClusterKey = ClusterKey.Generate().ToString();
        current.Cluster.WorkerPort = 47000;
        current.Performance.Threads = 3;
        var working = current.Clone();
        working.Cluster = new ClusterSettings();               // what "Restore defaults" does to the working copy

        var saved = SettingsEdits.Apply(current, working, SettingsEdits.KeysOf("Cluster"));

        Assert.Equal("", saved.Cluster.ClusterKey);
        Assert.Equal(46510, saved.Cluster.WorkerPort);
        Assert.Equal(3, saved.Performance.Threads);
    }

    [Fact]
    public void KeysOfACategoryCoverItsSettings()
    {
        var watch = SettingsEdits.KeysOf("Watch folders");
        Assert.Contains("Watch.Enabled", watch);
        Assert.Contains(SettingsEdits.WatchFolders, watch);
        var cluster = SettingsEdits.KeysOf("Cluster");
        Assert.Contains("Cluster.ClusterKey", cluster);
        Assert.DoesNotContain(SettingsEdits.WatchFolders, cluster);
        Assert.Empty(SettingsEdits.KeysOf("No such category"));
    }

    [Fact]
    public void UnknownKeysAreRejected()
    {
        var s = new OsmiumSettings();
        Assert.Throws<ArgumentException>(() => SettingsEdits.Apply(s, s.Clone(), ["Nope.Nothing"]));
    }

    [Fact]
    public void EveryPersistedPropertyOfACategoryHasAnEditKey()
    {
        // A category property that is neither a [Setting] nor a special edit key could be changed on the page
        // (e.g. by "Restore defaults") but would never be saved by Apply.
        foreach (var cat in typeof(OsmiumSettings).GetProperties().Where(p => p.PropertyType.GetCustomAttribute<SettingsCategoryAttribute>() is not null))
            foreach (var p in cat.PropertyType.GetProperties())
            {
                string key = $"{cat.Name}.{p.Name}";
                Assert.True(p.GetCustomAttribute<SettingAttribute>() is not null || key == SettingsEdits.WatchFolders, key);
            }
    }
}
