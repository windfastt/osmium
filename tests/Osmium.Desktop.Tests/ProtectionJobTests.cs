using System.Reflection;
using Osmium.Core.Container;
using Osmium.Core.Crypto;
using Osmium.Core.Planning;
using Osmium.Desktop.Jobs;
using Osmium.Desktop.Settings;

namespace Osmium.Desktop.Tests;

/// <summary>Compress jobs that encrypt run one at a time: each holds a key derivation's work area of up to 1 GiB.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProtectionJobCollection
{
    public const string Name = "Protection jobs";
}

/// <summary>New encrypted archives from compress jobs get the password protection of Settings → Security.</summary>
[Collection(ProtectionJobCollection.Name)]
public sealed class ProtectionJobTests : IDisposable
{
    private const string Password = "a passphrase of five long words";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "osm-protection-jobs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _input;

    public ProtectionJobTests()
    {
        _input = Path.Combine(_root, "in");
        Directory.CreateDirectory(_input);
        File.WriteAllText(Path.Combine(_input, "a.txt"), string.Concat(Enumerable.Repeat("a line of text\n", 200)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static OsmiumSettings Settings(PasswordProtection protection = PasswordProtection.Standard)
    {
        var s = new OsmiumSettings();
        s.Compression.Profile = ProfileName.Store;
        s.Performance.Threads = 2;
        s.Security.Protection = protection;
        return s;
    }

    /// <summary>One compress job with the password, in a scope where the settings' protection is used as Osmium uses
    /// it (the manager is made inside the scope, so its jobs run in it).</summary>
    private async Task<JobSnapshot> Compress(OsmiumSettings s, string? input = null)
    {
        using var asOsmium = TestProtection.AsOsmiumDoes();
        using var jm = new JobManager(() => s, () => new RuleSet());
        var done = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        jm.JobFinished += j => done.TrySetResult(j);
        jm.Enqueue(new JobRequest { Kind = JobKind.Compress, Inputs = [input ?? _input], Output = Path.Combine(_root, "out.osa"), Password = Password });
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(120), Ct);
    }

    private static ArchiveHeader HeaderOf(string path)
    {
        using var fs = File.OpenRead(path);
        return ArchiveHeader.Read(fs);
    }

    private static void SkipWithoutMemoryForStandard()
    {
        if (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes < 4L << 30) Assert.Skip("needs 4 GiB of memory for Standard protection");
    }

    [Fact]
    public async Task TheDefaultSettingsWriteArgon2idStandard()
    {
        SkipWithoutMemoryForStandard();
        var job = await Compress(Settings());
        Assert.True(job.State == JobState.Completed, job.Error);
        var h = HeaderOf(job.Output!);
        Assert.Equal((Osa.FlagEncrypted, KdfPresets.Standard), (h.Flags, h.Encryption!.Kdf));
    }

    [Fact]
    public async Task HideExactSizesPadsTheBlocks()
    {
        SkipWithoutMemoryForStandard();
        var s = Settings();
        s.Security.HideExactSizes = true;
        var job = await Compress(s);
        Assert.True(job.State == JobState.Completed, job.Error);
        Assert.Equal(Osa.FlagEncrypted | Osa.FlagPaddedBlocks, HeaderOf(job.Output!).Flags);
    }

    [Fact]
    public async Task CompatibleWritesPbkdf2WithTheSettingsIterationsAndNeverHidesSizes()
    {
        var s = Settings(PasswordProtection.Compatible);
        s.Security.KdfIterations = 100_000;
        s.Security.HideExactSizes = true; // a hand-edited file: Compatible protection cannot hide sizes
        var job = await Compress(s);
        Assert.True(job.State == JobState.Completed, job.Error);
        var h = HeaderOf(job.Output!);
        Assert.Equal((Osa.FlagEncrypted, KdfParameters.Pbkdf2(100_000)), (h.Flags, h.Encryption!.Kdf));
    }

    [Fact]
    public async Task IterationsOutOfRangeFailTheJobBeforeAnythingIsScanned()
    {
        var s = Settings(PasswordProtection.Compatible);
        s.Security.KdfIterations = 50_000;
        // An input that does not exist: a scan would have failed on it, with another message.
        var job = await Compress(s, Path.Combine(_root, "missing"));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("The PBKDF2 iterations in the settings must be between 100,000 and 20,000,000.", job.Error);
        Assert.False(File.Exists(Path.Combine(_root, "out.osa")));
    }

    [Fact]
    public void OnlyTheTestAssemblySetsTheSwitchThatMakesEveryProtectionPbkdf2()
    {
        // No method of Osmium's desktop code stores to the switch (stsfld) or calls the setter of the per-test override.
        var field = typeof(SecuritySettings).GetField(nameof(SecuritySettings.UseIterationsForTests), BindingFlags.Static | BindingFlags.NonPublic)!;
        var setter = typeof(SecuritySettings).GetProperty(nameof(SecuritySettings.UseIterationsHere), BindingFlags.Static | BindingFlags.NonPublic)!.SetMethod!;
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var setters = new List<string>();
        foreach (var type in typeof(SecuritySettings).Assembly.GetTypes())
            foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
            {
                if (method == setter) continue;
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null) continue;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    int token = BitConverter.ToInt32(il, i + 1);
                    if ((il[i] == 0x80 && token == field.MetadataToken) || (il[i] is 0x28 or 0x6F && token == setter.MetadataToken))
                        setters.Add($"{type.FullName}.{method.Name}");
                }
            }
        Assert.Empty(setters);
        Assert.True(SecuritySettings.UseIterationsForTests); // this assembly's module initializer
        Assert.Equal(KdfParameters.Pbkdf2(600_000), new SecuritySettings().Kdf()); // what the other tests of this assembly get
    }
}
