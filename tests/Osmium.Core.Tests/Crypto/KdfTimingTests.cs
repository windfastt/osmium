using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Osmium.Core.Crypto;

namespace Osmium.Core.Tests.Crypto;

/// <summary>
/// How long one derivation of the lightest protection and of the Standard and Strong presets takes on the machine that
/// runs the tests, through the same path an archive uses. The times go to the test output with the processor count and
/// architecture; the test fails only when Standard takes more than 15 seconds.
/// </summary>
[Collection(HeavyKeyDerivation.Name)]
[Trait("Category", "Timing")]
public class KdfTimingTests
{
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
        var sw = Stopwatch.StartNew();
        var master = PasswordKdf.DeriveMaster("correct horse battery staple", Convert.FromHexString(vector.Salt), p, run, creating: false);
        double seconds = sw.Elapsed.TotalSeconds;
        try { Assert.Equal(vector.Tag, Convert.ToHexStringLower(master)); }
        finally { CryptographicOperations.ZeroMemory(master); }

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"{preset} ({p.Describe()}): {seconds:F2} s; {Environment.ProcessorCount} processors, "
            + $"{RuntimeInformation.OSArchitecture}, {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}");
        if (seconds > 3) output?.WriteLine("slow runner: more than 3 seconds");
        if (preset == "Standard") Assert.True(seconds <= 15, $"Standard took {seconds:F1} s, more than 15 s.");
    }
}
