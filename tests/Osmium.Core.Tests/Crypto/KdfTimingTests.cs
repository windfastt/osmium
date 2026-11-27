using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Osmium.Core.Crypto;

namespace Osmium.Core.Tests.Crypto;

/// <summary>
/// How long one derivation of the lightest protection and of the Standard and Strong presets takes on the machine that
/// runs the tests, through the same path an archive uses (the median of three). The times go to the test output with
/// the processor count and architecture, and to <see cref="KdfTimingReport"/>; the test fails only when Standard takes
/// more than 15 seconds.
/// </summary>
[Collection(HeavyKeyDerivation.Name)]
[Trait("Category", "Timing")]
public class KdfTimingTests
{
    private const int Runs = 3;

    [Theory]
    [InlineData("Floor")]
    [InlineData("Standard")]
    [InlineData("Strong")]
    public void OneDerivationTakes(string preset)
    {
        var (p, vector) = preset switch
        {
            "Floor" => (KdfPresets.Floor, Argon2idVectors.Heavy[0]),
            "Standard" => (KdfPresets.Standard, Argon2idVectors.Heavy[1]),
            _ => (KdfPresets.Strong, Argon2idVectors.Heavy[2]),
        };
        Argon2idHeavyTests.SkipUnlessMemoryFor(p.MemoryBytes);
        var run = new KdfRun { MemoryLimitBytes = long.MaxValue, Cancellation = TestContext.Current.CancellationToken };
        var times = new List<double>();
        for (int i = 0; i < Runs; i++)
        {
            var sw = Stopwatch.StartNew();
            var master = PasswordKdf.DeriveMaster("correct horse battery staple", Convert.FromHexString(vector.Salt), p, run, creating: false);
            times.Add(sw.Elapsed.TotalSeconds);
            try { Assert.Equal(vector.Tag, Convert.ToHexStringLower(master)); }
            finally { CryptographicOperations.ZeroMemory(master); }
        }
        double median = times.Order().ElementAt(Runs / 2);

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"{preset} ({p.Describe()}): median {median:F2} s of {string.Join(", ", times.Select(t => t.ToString("F2", CultureInfo.InvariantCulture)))}; "
            + $"{Environment.ProcessorCount} processors, {RuntimeInformation.OSArchitecture}, {RuntimeInformation.OSDescription}");
        if (median > 3) output?.WriteLine("slow runner: more than 3 seconds");
        KdfTimingReport.Add("timing", preset, p, median, Runs);
        if (preset == "Standard") Assert.True(median <= 15, $"Standard took {median:F1} s, more than 15 s.");
    }
}

/// <summary>
/// On GitHub Actions, the derivation times also go into the job's step summary (<c>GITHUB_STEP_SUMMARY</c>) and into
/// <c>artifacts/selftest/kdf-timings-&lt;arch&gt;.md</c> in the workspace, as one markdown table. Elsewhere nothing is
/// written. Best effort: a write that fails never fails a test.
/// </summary>
internal static class KdfTimingReport
{
    private static readonly object Lock = new();
    private static readonly HashSet<string> Started = [];

    public static void Add(string test, string name, KdfParameters p, double medianSeconds, int runs)
    {
        string arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        string row = string.Create(CultureInfo.InvariantCulture,
            $"| {test} | {name} | {SizeText.ExactKiB(p.MemoryKiB)} | {p.TimeCost} | {p.Lanes} | {medianSeconds * 1000:F0} | {runs} | {Environment.ProcessorCount} | {arch} | {RuntimeInformation.OSDescription} |");
        lock (Lock)
        {
            if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary) Append(summary, row);
            if (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is { Length: > 0 } workspace)
                Append(Path.Combine(workspace, "artifacts", "selftest", $"kdf-timings-{arch}.md"), row);
        }
    }

    private static void Append(string path, string row)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var lines = new List<string>();
            if (Started.Add(path))
            {
                lines.Add("");
                lines.Add("### Key derivation times");
                lines.Add("");
                lines.Add("| test | preset | m | t | p | median ms | runs | cores | architecture | system |");
                lines.Add("|---|---|---|---|---|---|---|---|---|---|");
            }
            lines.Add(row);
            File.AppendAllLines(path, lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // a report that cannot be written is no test failure
        }
    }
}
