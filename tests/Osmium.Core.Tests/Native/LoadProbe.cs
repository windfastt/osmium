using System.Diagnostics;
using Osmium.Core.Codecs.Native;

namespace Osmium.Core.Tests.Native;

/// <summary>Runs tests/Osmium.Native.LoadProbe (built with Osmium.sln) in a child process and reads its key=value
/// findings. A child process starts with the encoder mode the test gives it, never the test process's own.</summary>
internal static class LoadProbe
{
    public sealed record Result(int ExitCode, IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Lines, string Output)
    {
        public string this[string key] => Values.TryGetValue(key, out var v) ? v : throw new KeyNotFoundException($"{key} not in:\n{Output}");
    }

    public static string ExeName => OperatingSystem.IsWindows() ? "Osmium.Native.LoadProbe.exe" : "Osmium.Native.LoadProbe";

    /// <summary>The probe's build folder, beside this test project's (same configuration and target framework).</summary>
    public static string BinDirectory()
    {
        var testBin = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        return Path.Combine(NativeTestSupport.RepoRoot(), "tests", "Osmium.Native.LoadProbe", "bin", testBin.Parent!.Name, testBin.Name);
    }

    /// <summary>The built probe; skips when it is not built, but fails in CI (OSMIUM_ENCODERS=native).</summary>
    public static string Require()
    {
        string exe = Path.Combine(BinDirectory(), ExeName);
        if (File.Exists(exe)) return exe;
        if (NativeEncoders.Mode == EncoderMode.Native) Assert.Fail($"{exe} is not built (it is part of Osmium.sln).");
        Assert.Skip($"{exe} is not built (build Osmium.sln).");
        return exe;
    }

    /// <summary>Copies the probe's program folder to <paramref name="folder"/>: without the native library, with it,
    /// or with another file in its place. Returns the copied program.</summary>
    public static string CopyTo(string folder, bool library, string? libraryReplacement = null)
    {
        Require();
        Directory.CreateDirectory(folder);
        string lib = NativeTestSupport.LibraryFileName;
        foreach (var f in Directory.GetFiles(BinDirectory()))
        {
            string name = Path.GetFileName(f);
            if (name.Equals(lib, StringComparison.OrdinalIgnoreCase) || name.StartsWith("osmium_native", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("libosmium_native", StringComparison.Ordinal))
                continue;
            File.Copy(f, Path.Combine(folder, name));
        }
        if (libraryReplacement != null) File.Copy(libraryReplacement, Path.Combine(folder, lib));
        else if (library) File.Copy(Path.Combine(BinDirectory(), lib), Path.Combine(folder, lib));
        return Path.Combine(folder, ExeName);
    }

    public static Result Run(string exe, string[] args, IReadOnlyDictionary<string, string?>? env = null, string? workingDirectory = null,
        int timeoutSeconds = 120)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exe)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove(NativeEncoders.EnvironmentVariable);
        psi.Environment.Remove("OSMIUM_MARKER_FILE");
        if (env != null)
            foreach (var (k, v) in env)
                if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSeconds * 1000))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Assert.Fail($"{Path.GetFileName(exe)} {string.Join(' ', args)} did not finish in {timeoutSeconds} s");
        }
        p.WaitForExit();
        string output = stdout.Result + stderr.Result;
        return Parse(p.ExitCode, output);
    }

    public static Result Parse(int exitCode, string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var values = new Dictionary<string, string>();
        foreach (var l in lines)
        {
            int eq = l.IndexOf('=');
            if (eq > 0) values[l[..eq]] = l[(eq + 1)..];
        }
        return new Result(exitCode, values, lines, output);
    }
}
