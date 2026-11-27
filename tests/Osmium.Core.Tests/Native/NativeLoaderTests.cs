using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Osmium.Core.Codecs.Native;

namespace Osmium.Core.Tests.Native;

/// <summary>
/// The loader's decisions, in this process, for every case that loads no other copy of the library: a refused file
/// is never passed to the system loader, so nothing here can leave a library loaded from a temporary folder. Cases
/// that load a library from another folder run in a child process.
/// </summary>
[Collection("Native encoders")]
public sealed class NativeLoaderTests
{
    private static LoaderOptions Options(string folder, string? sha) => NativeLibraryLoader.Defaults() with
    {
        BaseDirectory = folder,
        ProgramFolderProblem = null,
        ExpectedSha256 = sha,
    };

    private static string RealLibrary => Path.Combine(AppContext.BaseDirectory, NativeTestSupport.LibraryFileName);

    private static string RealSha256()
    {
        if (!File.Exists(RealLibrary)) Assert.Skip($"{RealLibrary} is not built (native/build.sh)");
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(RealLibrary)));
    }

    [Fact]
    public void AnEmptyProgramFolderIsNotFound()
    {
        using var t = new NativeTestSupport.TempFolder();
        var r = NativeLibraryLoader.TryLoad(Options(t.Path, new string('0', 64)));
        Assert.Equal(NativeStatus.NotFound, r.Status);
        Assert.Null(r.Api);
    }

    [Fact]
    public void AFileWithAnotherHashIsAMismatchAndIsNeverLoaded()
    {
        using var t = new NativeTestSupport.TempFolder();
        File.WriteAllBytes(Path.Combine(t.Path, NativeTestSupport.LibraryFileName), [0x4D, 0x5A, 1, 2, 3]);
        var r = NativeLibraryLoader.TryLoad(Options(t.Path, RealSha256()));
        Assert.Equal(NativeStatus.Mismatch, r.Status);
        Assert.Contains("SHA-256", r.Message);
        Assert.Null(r.Api);
        // Not loaded: on Windows a loaded file could not be deleted.
        File.Delete(Path.Combine(t.Path, NativeTestSupport.LibraryFileName));
    }

    [Fact]
    public void APlantedLibraryWithTheRightExportsNeverRuns()
    {
        string? marker = NativeTestSupport.MarkerLibrary();
        if (marker == null) Assert.Skip("the marker library is not built (native/build.sh --tests)");
        using var t = new NativeTestSupport.TempFolder();
        string planted = Path.Combine(t.Path, NativeTestSupport.LibraryFileName);
        File.Copy(marker, planted);
        string markerFile = Path.Combine(t.Path, "marker.txt");
        Environment.SetEnvironmentVariable("OSMIUM_MARKER_FILE", markerFile);
        try
        {
            var mismatch = NativeLibraryLoader.TryLoad(Options(t.Path, RealSha256()));
            Assert.Equal(NativeStatus.Mismatch, mismatch.Status);
            var unconfirmed = NativeLibraryLoader.TryLoad(Options(t.Path, RealSha256()) with { ProgramFolderProblem = "a test" });
            Assert.Equal(NativeStatus.Disabled, unconfirmed.Status);
            Assert.False(File.Exists(markerFile), "code of the planted library ran");
            File.Delete(planted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OSMIUM_MARKER_FILE", null);
        }
    }

    [Fact]
    public void ABuildWithoutACompiledHashLoadsNothing()
    {
        var r = NativeLibraryLoader.TryLoad(Options(AppContext.BaseDirectory, null));
        Assert.Equal(NativeStatus.NotFound, r.Status);
        Assert.Contains("without the native library", r.Message);
        Assert.Null(r.Api);
    }

    [Fact]
    public void AnUnconfirmedProgramFolderLooksForNothing()
    {
        var r = NativeLibraryLoader.TryLoad(Options(AppContext.BaseDirectory, RealSha256())
            with { ProgramFolderProblem = "the program folder /x is not the folder of the running program, /y" });
        Assert.Equal(NativeStatus.Disabled, r.Status);
        Assert.Contains("not looked for", r.Message);
        Assert.Null(r.Path);
        Assert.Null(r.Api);
    }

    // ------------------------------------------------------------------ the program folder

    [Fact]
    public void TheTestProgramsFolderIsConfirmed()
    {
        var folder = NativeLibraryLoader.ConfirmProgramFolder(AppContext.BaseDirectory, Environment.ProcessPath, out var why);
        Assert.True(folder != null, why);
        Assert.Equal(NativeLibraryLoader.CanonicalFolder(AppContext.BaseDirectory), folder);
        Assert.Null(NativeLibraryLoader.Defaults().ProgramFolderProblem);
    }

    [Fact]
    public void TheProgramFolderMustBeTheRunningProgramsFolder()
    {
        using var t = new NativeTestSupport.TempFolder();
        string app = Path.Combine(t.Path, "app"), other = Path.Combine(t.Path, "other");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(other);
        string real = NativeLibraryLoader.CanonicalFolder(app);
        Assert.True(Path.IsPathFullyQualified(real));
        Assert.Equal(real, NativeLibraryLoader.CanonicalFolder(real));
        string exe = Path.Combine(app, "osmium.exe");
        // The same folder, with or without a trailing separator.
        Assert.Equal(real, NativeLibraryLoader.ConfirmProgramFolder(app, exe, out _));
        Assert.Equal(real, NativeLibraryLoader.ConfirmProgramFolder(app + Path.DirectorySeparatorChar, exe, out _));
        Assert.Equal(real, NativeLibraryLoader.ConfirmProgramFolder(Path.Combine(app, "sub", ".."), exe, out _));
        // A self-extracting bundle runs from a temporary folder; another host runs elsewhere; no path at all.
        Assert.Null(NativeLibraryLoader.ConfirmProgramFolder(other, exe, out var why));
        Assert.Contains("is not the folder of the running program", why);
        Assert.Null(NativeLibraryLoader.ConfirmProgramFolder(app, Path.Combine(other, "dotnet"), out _));
        Assert.Null(NativeLibraryLoader.ConfirmProgramFolder(app, null, out _));
        Assert.Null(NativeLibraryLoader.ConfirmProgramFolder(app, "", out _));
        Assert.Null(NativeLibraryLoader.ConfirmProgramFolder(null, exe, out _));
        // A folder that does not exist cannot be confirmed.
        Assert.Null(NativeLibraryLoader.ConfirmProgramFolder(Path.Combine(t.Path, "gone"), Path.Combine(t.Path, "gone", "osmium.exe"), out why));
        Assert.Contains("could not be resolved", why);
        // Letter case: the same folder on Windows, another (missing) folder elsewhere.
        string upper = Path.Combine(t.Path, "APP");
        if (OperatingSystem.IsWindows()) Assert.Equal(real, NativeLibraryLoader.ConfirmProgramFolder(upper, exe, out _));
        else Assert.Null(NativeLibraryLoader.ConfirmProgramFolder(upper, exe, out _));
    }

    [Fact]
    public void AFolderReachedThroughALinkIsItsTarget()
    {
        using var t = new NativeTestSupport.TempFolder();
        string app = Path.Combine(t.Path, "app"), link = Path.Combine(t.Path, "link");
        Directory.CreateDirectory(app);
        if (OperatingSystem.IsWindows()) NativeTestSupport.CreateJunction(link, app);   // a symbolic link needs a privilege
        else Directory.CreateSymbolicLink(link, app);
        string real = NativeLibraryLoader.CanonicalFolder(app);
        Assert.Equal(real, NativeLibraryLoader.CanonicalFolder(link));
        Assert.Equal(real, NativeLibraryLoader.ConfirmProgramFolder(link, Path.Combine(app, "osmium"), out _));
        Assert.Equal(real, NativeLibraryLoader.ConfirmProgramFolder(app, Path.Combine(link, "osmium"), out _));
        // A link on the way, not only at the end.
        string inner = Path.Combine(app, "inner");
        Directory.CreateDirectory(inner);
        Assert.Equal(NativeLibraryLoader.CanonicalFolder(inner),
            NativeLibraryLoader.ConfirmProgramFolder(Path.Combine(link, "inner"), Path.Combine(inner, "osmium"), out _));
    }

    /// <summary>Windows: the folder of a program started by its short (8.3) path, as from a TEMP variable like
    /// C:\Users\RUNNER~1\..., is the folder of its long path.</summary>
    [Fact]
    public void AShortFolderNameIsTheSameFolder()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only: short (8.3) names exist only there.");
        using var t = new NativeTestSupport.TempFolder();
        string app = Path.Combine(t.Path, "a folder with a long name");
        Directory.CreateDirectory(app);
        string? shortName = NativeTestSupport.ShortPath(app);
        if (shortName == null || string.Equals(Path.GetFileName(shortName), Path.GetFileName(app), StringComparison.OrdinalIgnoreCase))
            Assert.Skip($"the volume of {app} makes no short names");
        string real = NativeLibraryLoader.CanonicalFolder(app);
        Assert.Equal(real, NativeLibraryLoader.CanonicalFolder(shortName));
        Assert.Equal(real, NativeLibraryLoader.ConfirmProgramFolder(app, Path.Combine(shortName, "osmium.exe"), out _));
        Assert.Equal(real, NativeLibraryLoader.ConfirmProgramFolder(shortName, Path.Combine(app, "osmium.exe"), out _));
    }

    private sealed class StoppedBeforeTheLoad : Exception { }

    /// <summary>The loader's own path handling in a folder longer than MAX_PATH (and longer than the loader's first
    /// buffer for the real path): the folder is confirmed and the library is opened, hashed and held there. No program
    /// can start from such a folder on Windows, but a program folder reached through a link can resolve to one. The
    /// test stops before the system loader runs, so no library from a temporary folder stays loaded in this
    /// process.</summary>
    [Fact]
    public void ALibraryInAFolderLongerThanMaxPathIsOpenedAndChecked()
    {
        string sha = RealSha256();
        using var t = new NativeTestSupport.TempFolder();
        string folder = t.Path;
        foreach (var part in new[] { "Programme é ü ß", "日本語のフォルダ", "Ωmega Σ", new string('x', 150), new string('y', 150), new string('z', 150) })
            folder = Path.Combine(folder, part);
        Directory.CreateDirectory(folder);
        File.Copy(RealLibrary, Path.Combine(folder, NativeTestSupport.LibraryFileName));
        string? confirmed = NativeLibraryLoader.ConfirmProgramFolder(folder, Path.Combine(folder, "osmium.exe"), out var why);
        Assert.True(confirmed != null, why);
        Assert.True(confirmed.Length > 512, $"only {confirmed.Length} characters");
        Assert.Equal(confirmed, NativeLibraryLoader.CanonicalFolder(Path.Combine(folder, "sub", "..")));

        string? held = null;
        Assert.Throws<StoppedBeforeTheLoad>(() => NativeLibraryLoader.TryLoad(Options(confirmed, sha) with
        {
            WhileHeld = p => { held = p; throw new StoppedBeforeTheLoad(); },
        }));
        Assert.Equal(Path.Combine(confirmed, NativeTestSupport.LibraryFileName), held);

        // Another file there is refused by its hash, as anywhere.
        var mismatch = NativeLibraryLoader.TryLoad(Options(confirmed, new string('0', 64)));
        Assert.Equal(NativeStatus.Mismatch, mismatch.Status);
        Assert.Null(mismatch.Api);
    }

    [Theory]
    [InlineData("abi")]
    [InlineData("generation")]
    [InlineData("versions")]
    public void ALibraryReportingAnotherIdentityIsAMismatch(string what)
    {
        NativeTestSupport.RequireNative();
        var o = Options(AppContext.BaseDirectory, RealSha256());
        o = what switch
        {
            "abi" => o with { ExpectedAbi = o.ExpectedAbi + 1 },
            "generation" => o with { ExpectedGeneration = "native-0" },
            _ => o with { ExpectedVersions = "lzma-sdk 26.05; zstd 1.5.7" },
        };
        var r = NativeLibraryLoader.TryLoad(o);
        Assert.Equal(NativeStatus.Mismatch, r.Status);
        Assert.Null(r.Api);
        Assert.Contains(what == "abi" ? "interface number" : what == "generation" ? "native-0" : "26.05", r.Message);
    }

    [Fact]
    public void TheRealLibraryLoadsFromTheProgramFolder()
    {
        NativeTestSupport.RequireNative();
        var r = NativeLibraryLoader.TryLoad(NativeLibraryLoader.Defaults());
        Assert.Equal(NativeStatus.Active, r.Status);
        Assert.Equal(Path.Combine(NativeLibraryLoader.CanonicalFolder(AppContext.BaseDirectory), NativeTestSupport.LibraryFileName), r.Path);
        Assert.Equal(EncoderGeneration.Native1, EncoderGeneration.Active);
        Assert.StartsWith("native (LZMA SDK 26.06, Zstandard 1.5.7)", NativeEncoders.Describe());
    }

    [Fact]
    public void TheFileNameCarriesTheArchitecture()
    {
        Assert.Equal("osmium_native-x64.dll", NativeLibraryLoader.FileName(Architecture.X64, windows: true));
        Assert.Equal("osmium_native-arm64.dll", NativeLibraryLoader.FileName(Architecture.Arm64, windows: true));
        Assert.Equal("libosmium_native-x64.so", NativeLibraryLoader.FileName(Architecture.X64, windows: false));
    }

    [Fact]
    public void ConfigureAfterTheDecisionThrows()
    {
        _ = NativeEncoders.ActiveGeneration;   // decides, if nothing did yet
        Assert.True(NativeEncoders.IsDecided);
        var e = Assert.Throws<InvalidOperationException>(() => NativeEncoders.Configure(EncoderMode.Managed));
        Assert.Contains("next start", e.Message);
    }

    [Fact]
    public void TheModeNamesParse()
    {
        Assert.True(NativeEncoders.TryParse("auto", out var a) && a == EncoderMode.Auto);
        Assert.True(NativeEncoders.TryParse(" Native ", out var n) && n == EncoderMode.Native);
        Assert.True(NativeEncoders.TryParse("MANAGED", out var m) && m == EncoderMode.Managed);
        Assert.False(NativeEncoders.TryParse("fast", out _));
        Assert.False(NativeEncoders.TryParse(null, out _));
    }
}
