using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Osmium.Core.Codecs.Native;
using Osmium.Core.Distributed;

namespace Osmium.Core.Tests.Native;

/// <summary>
/// The cases that need a fresh process (tests/Osmium.Native.LoadProbe): the encoders are chosen once per process
/// and the library is never unloaded, so loading from another folder, choosing a mode, planting a library and
/// counting threads each run in a child process with its own mode.
/// </summary>
[Collection("Native encoders")]
public sealed class NativeProcessTests
{
    private readonly ITestOutputHelper _out;
    public NativeProcessTests(ITestOutputHelper output) => _out = output;

    private static Dictionary<string, string?> Env(params (string, string?)[] vars) => vars.ToDictionary(v => v.Item1, v => v.Item2);

    private LoadProbe.Result Run(string exe, string[] args, Dictionary<string, string?>? env = null, string? cwd = null)
    {
        var r = LoadProbe.Run(exe, args, env, cwd);
        _out.WriteLine($"{Path.GetFileName(exe)} {string.Join(' ', args)} -> exit {r.ExitCode}\n{r.Output}");
        Assert.Equal(0, r.ExitCode);
        return r;
    }

    private static string LibraryHere => Path.Combine(LoadProbe.BinDirectory(), NativeTestSupport.LibraryFileName);

    private static void RequireLibrary()
    {
        LoadProbe.Require();
        NativeTestSupport.RequireNative();
        Assert.True(File.Exists(LibraryHere), $"{LibraryHere} is missing beside the probe");
    }

    // ------------------------------------------------------------------ modes

    [Fact]
    public void ConfigureBeforeFirstUseDecidesAndASecondCallIsRefused()
    {
        var exe = LoadProbe.Require();
        var r = Run(exe, ["configure", "managed", "chosen in the test"]);
        Assert.Equal("False", r["decided_before"]);
        Assert.Equal("False", r["decided_after_configure"]);
        Assert.Equal("Managed", r["mode"]);
        Assert.Equal("Disabled", r["status"]);
        Assert.Equal("managed-1", r["generation"]);
        Assert.Equal("chosen in the test", r["message"]);
        Assert.Equal("False", r["mapped"]);
        Assert.StartsWith("refused", r["reconfigure"]);
    }

    [Fact]
    public void ConfigureOverridesTheEnvironmentVariable()
    {
        RequireLibrary();
        var r = Run(LoadProbe.Require(), ["configure", "auto"], Env(("OSMIUM_ENCODERS", "managed")));
        Assert.Equal("Active", r["status"]);
        Assert.Equal("native-1", r["generation"]);
    }

    [Fact]
    public void ManagedModeNeverLoadsTheLibrary()
    {
        var r = Run(LoadProbe.Require(), ["encode"], Env(("OSMIUM_ENCODERS", "managed")));
        Assert.StartsWith("ok", r["lzma"]);
        Assert.StartsWith("ok", r["zstd"]);
        Assert.Equal("managed-1", r["generation"]);
        Assert.Equal("False", r["mapped"]);
    }

    [Fact]
    public void AutoModeUsesTheLibraryBesideTheProgram()
    {
        RequireLibrary();
        var r = Run(LoadProbe.Require(), ["encode"]);
        Assert.StartsWith("ok", r["lzma"]);
        Assert.Equal("native-1", r["generation"]);
        Assert.Equal("True", r["mapped"]);
    }

    [Fact]
    public void NativeModeWithoutTheLibraryFailsEncodingWithTheReason()
    {
        using var t = new NativeTestSupport.TempFolder();
        var exe = LoadProbe.CopyTo(t.Path, library: false);
        var r = Run(exe, ["encode"], Env(("OSMIUM_ENCODERS", "native")));
        Assert.StartsWith("error: The native encoders are required but cannot be used", r["lzma"]);
        Assert.StartsWith("error: The native encoders are required", r["zstd"]);
        // A build with the library pins its hash and finds no file; a build without it never looks.
        Assert.Contains(NativeTestSupport.BuildPinsLibrary ? "not in the program folder" : "made without the native library", r["lzma"]);
    }

    [Fact]
    public void AutoModeWithoutTheLibraryUsesTheBuiltInEncoders()
    {
        using var t = new NativeTestSupport.TempFolder();
        var exe = LoadProbe.CopyTo(t.Path, library: false);
        var r = Run(exe, ["encode"]);
        Assert.StartsWith("ok", r["lzma"]);
        Assert.Equal("managed-1", r["generation"]);
        Assert.Equal("False", r["mapped"]);
        var s = Run(exe, ["status"]);
        Assert.Equal("NotFound", s["status"]);
    }

    // ------------------------------------------------------------------ where the library is looked for

    [Fact]
    public void CopiesInTheCurrentFolderAndOnPathAreNeverLoaded()
    {
        RequireLibrary();
        using var t = new NativeTestSupport.TempFolder();
        string program = Path.Combine(t.Path, "program"), current = Path.Combine(t.Path, "current"), onPath = Path.Combine(t.Path, "on-path");
        var exe = LoadProbe.CopyTo(program, library: false);
        foreach (var d in new[] { current, onPath })
        {
            Directory.CreateDirectory(d);
            File.Copy(LibraryHere, Path.Combine(d, NativeTestSupport.LibraryFileName));
        }
        string path = onPath + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var r = Run(exe, ["status"], Env(("PATH", path), ("LD_LIBRARY_PATH", onPath)), cwd: current);
        Assert.Equal("NotFound", r["status"]);
        Assert.Equal("False", r["mapped"]);
    }

    [Fact]
    public void AGarbageFileInTheProgramFolderIsAMismatchAndNeverLoaded()
    {
        RequireLibrary();
        using var t = new NativeTestSupport.TempFolder();
        string garbage = Path.Combine(t.Path, "garbage.bin");
        File.WriteAllBytes(garbage, [0x7F, 0x45, 0x4C, 0x46, 0x4D, 0x5A, 0, 1, 2, 3]);
        var exe = LoadProbe.CopyTo(Path.Combine(t.Path, "program"), library: false, libraryReplacement: garbage);
        var r = Run(exe, ["status"]);
        Assert.Equal("Mismatch", r["status"]);
        Assert.Contains("SHA-256", r["message"]);
        Assert.Equal("False", r["mapped"]);
    }

    [Fact]
    public void APlantedLibraryInTheProgramFolderNeverRuns()
    {
        RequireLibrary();
        string? marker = NativeTestSupport.MarkerLibrary();
        if (marker == null) Assert.Fail("the marker library is not built (native/build.sh --tests or build.ps1 -Tests)");
        using var t = new NativeTestSupport.TempFolder();
        var exe = LoadProbe.CopyTo(Path.Combine(t.Path, "program"), library: false, libraryReplacement: marker);
        string markerFile = Path.Combine(t.Path, "marker.txt");
        var r = Run(exe, ["encode"], Env(("OSMIUM_MARKER_FILE", markerFile)));
        Assert.Equal("managed-1", r["generation"]);
        Assert.Equal("False", r["mapped"]);
        Assert.StartsWith("ok", r["lzma"]);
        Assert.False(File.Exists(markerFile), "code of the planted library ran");
    }

    /// <summary>The standalone CLI is a single-file program that usually sits in Downloads, where a planted library
    /// could wait, and the app's jobs run in a native AOT program. In both, assemblies have no location; the program
    /// confirms its folder like any other build and loads only the library whose hash it knows: a planted one is
    /// refused before any of its code runs; the real one beside it is used.</summary>
    [Theory]
    [InlineData("single-file", "OSMIUM_LOADPROBE_SINGLEFILE", "-p:PublishSingleFile=true --self-contained false")]
    [InlineData("native AOT", "OSMIUM_LOADPROBE_AOT", "-p:PublishAot=true")]
    public void AProgramWithoutAssemblyFilesLoadsOnlyTheCheckedLibraryBesideIt(string build, string variable, string publish)
    {
        string? probe = Environment.GetEnvironmentVariable(variable);
        if (probe != null && probe.StartsWith("none:", StringComparison.Ordinal))
            Assert.Skip($"no {build} probe on this runner ({probe[5..].Trim()})");
        if (string.IsNullOrEmpty(probe))
        {
            if (NativeEncoders.Mode == EncoderMode.Native) Assert.Fail($"{variable} is not set (native.yml publishes the {build} probe, or sets \"none: <why>\").");
            Assert.Skip($"needs the {build} probe: dotnet publish tests/Osmium.Native.LoadProbe -r <rid> {publish}, path in {variable}");
        }
        RequireLibrary();
        string? marker = NativeTestSupport.MarkerLibrary();
        if (marker == null) Assert.Fail("the marker library is not built");
        using var t = new NativeTestSupport.TempFolder();
        string exe = Path.Combine(t.Path, Path.GetFileName(probe));
        File.Copy(probe, exe);
        string lib = Path.Combine(t.Path, NativeTestSupport.LibraryFileName);
        File.Copy(marker, lib);
        string markerFile = Path.Combine(t.Path, "marker.txt");
        var r = Run(exe, ["encode"], Env(("OSMIUM_MARKER_FILE", markerFile)));
        Assert.Equal("managed-1", r["generation"]);
        Assert.Equal("False", r["mapped"]);
        Assert.False(File.Exists(markerFile), $"code of the library planted beside the {build} program ran");
        var s = Run(exe, ["status"], Env(("OSMIUM_MARKER_FILE", markerFile)));
        Assert.Equal("Mismatch", s["status"]);
        Assert.Equal(NativeLibraryLoader.CanonicalFolder(t.Path), s["program_folder"]);
        Assert.False(File.Exists(markerFile));

        // Not loaded: on Windows a loaded file could not be deleted.
        File.Delete(lib);
        File.Copy(LibraryHere, lib);
        var real = Run(exe, ["encode"]);
        Assert.Equal("native-1", real["generation"]);
        Assert.Equal("True", real["mapped"]);
        Assert.StartsWith("ok", real["lzma"]);
    }

    /// <summary>A program folder with non-ASCII names, as long as the system lets a program start from. Windows
    /// starts no program whose path, or whose working folder, is longer than MAX_PATH (260 characters with the
    /// terminating zero), long paths enabled or not, so Osmium never runs from a longer folder there: the folder is
    /// made as long as every file of the program allows, and a library in a longer folder is covered in this process
    /// (NativeLoaderTests.ALibraryInAFolderLongerThanMaxPathIsOpenedAndChecked). On Linux the folder is longer than
    /// 260 characters.</summary>
    [Fact]
    public void ALongAndNonAsciiProgramFolderWorks()
    {
        RequireLibrary();
        using var t = new NativeTestSupport.TempFolder();
        string folder = t.Path;
        foreach (var part in new[] { "Programme é ü ß", "日本語のフォルダ", "Ωmega Σ" })
            folder = Path.Combine(folder, part);
        if (OperatingSystem.IsWindows())
        {
            // Room for one more folder name such that every file of the program stays at 259 characters or fewer.
            int longest = Directory.GetFiles(LoadProbe.BinDirectory()).Max(f => Path.GetFileName(f).Length);
            int room = 259 - folder.Length - 1 - longest - 1;
            if (room < 1) Assert.Skip($"the temporary folder {t.Path} leaves no room under MAX_PATH");
            folder = Path.Combine(folder, new string('x', Math.Min(room, 200)));
            Assert.True(Path.Combine(folder, LoadProbe.ExeName).Length < 260);
        }
        else
        {
            foreach (var part in new[] { new string('x', 60), new string('y', 60), new string('z', 60) })
                folder = Path.Combine(folder, part);
            Assert.True(folder.Length > 260, $"only {folder.Length} characters");
        }
        var exe = LoadProbe.CopyTo(folder, library: true);
        var s = Run(exe, ["status"], cwd: t.Path);
        Assert.Equal("Active", s["status"]);
        Assert.Equal(NativeLibraryLoader.CanonicalFolder(folder), s["program_folder"]);
        var r = Run(exe, ["load", folder], cwd: t.Path);
        Assert.Equal("Active", r["status"]);
        Assert.Equal(Path.GetFullPath(Path.Combine(folder, NativeTestSupport.LibraryFileName)), r["path"]);
    }

    /// <summary>Windows: from the hash until after the load, the held file cannot be written, deleted or renamed, and
    /// the load succeeds with it held. (Linux cannot deny writes; it ships nothing.)</summary>
    [Fact]
    public void TheCheckedFileIsHeldUntilItIsLoaded()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only: Linux cannot deny writes to an open file, and Osmium ships no Linux library.");
        RequireLibrary();
        using var t = new NativeTestSupport.TempFolder();
        string folder = Path.Combine(t.Path, "program");
        var exe = LoadProbe.CopyTo(folder, library: true);
        var r = Run(exe, ["held", folder]);
        Assert.Equal("refused", r["held_write"]);
        Assert.Equal("refused", r["held_delete"]);
        Assert.Equal("refused", r["held_rename"]);
        Assert.Equal("True", r["held_still_there"]);
        Assert.Equal("Active", r["status"]);
        _out.WriteLine($"renaming the program folder while the file was held: {r["held_rename_folder"]}");
    }

    // ------------------------------------------------------------------ threads

    [Fact]
    public void NoHelperThreadIsLeftAfterACancelledOrFinishedEncode()
    {
        RequireLibrary();
        var r = Run(LoadProbe.Require(), ["threads"]);
        int baseline = int.Parse(r["baseline"]);
        Assert.Equal("yes", r["cancelled"]);
        Assert.Equal("2", r["threads_used"]);
        Assert.True(int.Parse(r["during"]) >= baseline + 1, "no helper thread was running during the encode");
        Assert.Equal(baseline, int.Parse(r["after_cancel"]));
        Assert.Equal("2", r["finished_threads_used"]);
        Assert.Equal(baseline, int.Parse(r["after_finish"]));
    }

    // ------------------------------------------------------------------ cluster

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>Starts a worker in a child process with the given mode; returns it once it listens.</summary>
    private static Process StartWorker(string exe, int port, string key, string mode, List<string> lines)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "worker", port.ToString(), key }) psi.ArgumentList.Add(a);
        psi.Environment[NativeEncoders.EnvironmentVariable] = mode;
        var p = Process.Start(psi)!;
        var listening = new ManualResetEventSlim();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (lines) lines.Add(e.Data);
            if (e.Data.StartsWith("listening=", StringComparison.Ordinal)) listening.Set();
        };
        p.BeginOutputReadLine();
        Assert.True(listening.Wait(TimeSpan.FromSeconds(30)), "the worker did not start");
        Thread.Sleep(200);
        return p;
    }

    private static void StopWorker(Process p)
    {
        p.StandardInput.Close();
        if (!p.WaitForExit(15000)) p.Kill(entireProcessTree: true);
        p.WaitForExit();
        p.Dispose();
    }

    [Theory]
    [InlineData("managed", "auto")]
    [InlineData("auto", "managed")]
    public void NodesOfDifferentGenerationsRefuseEachOther(string workerMode, string coordinatorMode)
    {
        RequireLibrary();
        var exe = LoadProbe.Require();
        int port = FreePort();
        string key = ClusterKey.Generate().ToString();
        var lines = new List<string>();
        var worker = StartWorker(exe, port, key, workerMode, lines);
        LoadProbe.Result client;
        try
        {
            client = Run(exe, ["connect", port.ToString(), key], Env(("OSMIUM_ENCODERS", coordinatorMode)));
        }
        finally
        {
            StopWorker(worker);
        }
        string workerGen = workerMode == "managed" ? "managed-1" : "native-1";
        string clientGen = coordinatorMode == "managed" ? "managed-1" : "native-1";
        string log;
        lock (lines) log = string.Join("\n", lines);
        _out.WriteLine("worker:\n" + log);
        Assert.Equal(clientGen, client["generation"]);
        Assert.StartsWith("error:", client["connect"]);
        Assert.Contains("different Osmium engine build or encoder generation", client["connect"]);
        Assert.Contains($"this computer uses encoder generation {clientGen}", client["connect"]);
        Assert.Contains($"generation={workerGen}", log);
        Assert.Contains("rejected", log);
        Assert.Contains($"This computer uses encoder generation {workerGen}.", log);
    }

    [Fact]
    public void NodesOfTheSameGenerationConnect()
    {
        RequireLibrary();
        var exe = LoadProbe.Require();
        int port = FreePort();
        string key = ClusterKey.Generate().ToString();
        var lines = new List<string>();
        var worker = StartWorker(exe, port, key, "auto", lines);
        try
        {
            var client = Run(exe, ["connect", port.ToString(), key], Env(("OSMIUM_ENCODERS", "native")));
            Assert.Equal("ok", client["connect"]);
        }
        finally
        {
            StopWorker(worker);
        }
    }
}
