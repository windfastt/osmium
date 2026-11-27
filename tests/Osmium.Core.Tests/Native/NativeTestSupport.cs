using System.Runtime.InteropServices;
using Osmium.Core.Codecs.Native;

namespace Osmium.Core.Tests.Native;

internal static class NativeTestSupport
{
    /// <summary>Skips (with the reason) unless the native encoders are active. With OSMIUM_ENCODERS=native, as in CI,
    /// a missing library fails instead, so it can never pass as a skip.</summary>
    public static void RequireNative()
    {
        if (NativeEncoders.Status == NativeStatus.Active) return;
        if (NativeEncoders.Mode == EncoderMode.Native)
            Assert.Fail("OSMIUM_ENCODERS=native, but the native encoders cannot be used: " + NativeEncoders.StatusMessage);
        Assert.Skip($"The native encoders are not active ({NativeEncoders.StatusMessage}); CI runs this with OSMIUM_ENCODERS=native.");
    }

    /// <summary>The Osmium checkout these tests were built in: the nearest folder above the test program that holds
    /// Osmium.sln and native/generation.txt. (Not the source file's path: CI builds map source paths to "/_/".)</summary>
    public static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Osmium.sln")) && File.Exists(Path.Combine(d.FullName, "native", "generation.txt")))
                return d.FullName;
        throw new InvalidOperationException($"No Osmium checkout above {AppContext.BaseDirectory}.");
    }

    /// <summary>A folder of this test project's sources, in the checkout.</summary>
    public static string SourceFolder(params string[] parts) =>
        Path.Combine([RepoRoot(), "tests", "Osmium.Core.Tests", .. parts]);

    public static string Rid => OperatingSystem.IsWindows()
        ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64")
        : "linux-x64";

    public static string LibraryFileName => NativeLibraryLoader.FileName(RuntimeInformation.ProcessArchitecture, OperatingSystem.IsWindows());

    /// <summary>True when this build of Osmium knows the SHA-256 of a native library for this process (it was built
    /// with the library in artifacts/native/), false for a build without one. The probe is built in the same
    /// solution build, so the same holds for it.</summary>
    public static bool BuildPinsLibrary => NativeLibraryLoader.Defaults().ExpectedSha256 != null;

    /// <summary>The test-only marker library built by native/build.sh --tests (or build.ps1 -Tests), or null.</summary>
    public static string? MarkerLibrary()
    {
        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string name = OperatingSystem.IsWindows() ? $"osmium_native_marker-{arch}.dll" : $"libosmium_native_marker-{arch}.so";
        string path = Path.Combine(RepoRoot(), "artifacts", "native-test", Rid, name);
        return File.Exists(path) ? path : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetShortPathNameW(string longPath, char[] shortPath, int size);

    /// <summary>Windows: the short (8.3) spelling of an existing path, or null.</summary>
    public static string? ShortPath(string path)
    {
        var buffer = new char[1024];
        int n = GetShortPathNameW(path, buffer, buffer.Length);
        return n == 0 || n >= buffer.Length ? null : new string(buffer, 0, n);
    }

    /// <summary>Windows: a junction at <paramref name="link"/> to the folder <paramref name="target"/> (unlike a
    /// symbolic link, it needs no privilege).</summary>
    public static void CreateJunction(string link, string target)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    /// <summary>A new empty folder under the system temporary folder, deleted by <see cref="IDisposable.Dispose"/>.</summary>
    public sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "osmium-native-test-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
