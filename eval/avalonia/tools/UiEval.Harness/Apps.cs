using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace UiEval;

/// <summary>A launched app (the WinForms Osmium.App or the Avalonia preview) and what the harness knows about it.</summary>
internal sealed class AppRun : IDisposable
{
    public required string Kind { get; init; } // "winforms" | "avalonia"
    public required Process Process { get; init; }
    public required DateTime StartUtc { get; init; }
    public string? Log { get; init; }
    public string? Control { get; init; }
    public string Exe { get; init; } = "";

    public int Pid => Process.Id;
    public double SinceStartMs => (DateTime.UtcNow - StartUtc).TotalMilliseconds;

    /// <summary>Event-log lines written by the preview so far (JSON objects).</summary>
    public List<JsonObject> Events()
    {
        var list = new List<JsonObject>();
        if (Log is null || !File.Exists(Log)) return list;
        using var s = new FileStream(Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s, Encoding.UTF8);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            try { if (JsonNode.Parse(line) is JsonObject o && (o["pid"]?.GetValue<int>() ?? Pid) == Pid) list.Add(o); }
            catch (Exception) { }
        }
        return list;
    }

    public JsonObject? WaitEvent(string ev, int timeoutMs, Func<JsonObject, bool>? where = null, int after = 0)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var hit = Events().Skip(after).FirstOrDefault(e => e["ev"]?.GetValue<string>() == ev && (where?.Invoke(e) ?? true));
            if (hit is not null) return hit;
            if (Process.HasExited) return null;
            Thread.Sleep(25);
        }
        return null;
    }

    public int EventCount => Events().Count;

    /// <summary>Sends one command on the preview's control pipe and returns its JSON reply.</summary>
    public JsonObject? Command(string line, int timeoutMs = 60_000)
    {
        if (Control is null) return null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", Control, PipeDirection.InOut);
            pipe.Connect(Math.Min(timeoutMs, 15_000));
            using var w = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var r = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            w.WriteLine(line);
            var task = r.ReadLineAsync();
            if (!task.Wait(timeoutMs)) return new JsonObject { ["ok"] = false, ["error"] = "timeout" };
            return task.Result is { } s ? JsonNode.Parse(s) as JsonObject : null;
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.GetType().Name + ": " + e.Message };
        }
    }

    /// <summary>Asks the startup hook (DOTNET_STARTUP_HOOKS) in the process: "mem" or "gc".</summary>
    public JsonObject? Hook(string cmd)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "UiEvalHook-" + Pid, PipeDirection.InOut);
            pipe.Connect(10_000);
            using var w = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var r = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            w.WriteLine(cmd);
            return r.ReadLine() is { } s ? JsonNode.Parse(s) as JsonObject : null;
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.GetType().Name + ": " + e.Message };
        }
    }

    public IntPtr WaitMainWindow(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !Process.HasExited)
        {
            var h = Win.MainWindow(Pid);
            if (h != IntPtr.Zero) return h;
            Thread.Sleep(2);
        }
        return IntPtr.Zero;
    }

    public long PrivateBytes()
    {
        Process.Refresh();
        return Process.PrivateMemorySize64;
    }

    /// <summary>Asks the app to exit (WinForms: "--quit" forwarded over its IPC pipe; preview: "quit" on the control pipe),
    /// then kills it if it is still running. Returns how it ended.</summary>
    public string Quit(string? winformsExe = null)
    {
        if (Process.HasExited) return "already exited, code " + Process.ExitCode;
        try
        {
            if (Kind == "avalonia") Command("quit", 5000);
            else if (winformsExe is not null)
            {
                using var q = Process.Start(new ProcessStartInfo(winformsExe, "--quit") { UseShellExecute = false });
                q?.WaitForExit(15_000);
            }
        }
        catch (Exception) { }
        if (Process.WaitForExit(15_000)) return "exited, code " + Process.ExitCode;
        Process.Kill(true);
        Process.WaitForExit(5000);
        return "killed after 15 s";
    }

    public bool Attached { get; init; }

    public void Dispose()
    {
        if (!Attached && !Process.HasExited) { try { Process.Kill(true); } catch (Exception) { } }
        Process.Dispose();
    }

    /// <summary>A process the harness did not start itself (the window process the tray host starts).</summary>
    public static AppRun Attach(string kind, int pid, string log, string? control)
    {
        var p = Process.GetProcessById(pid);
        DateTime start;
        try { start = p.StartTime.ToUniversalTime(); } catch (Exception) { start = DateTime.UtcNow; }
        return new AppRun { Kind = kind, Process = p, StartUtc = start, Log = log, Control = control, Attached = true };
    }
}

internal static class Apps
{
    public static AppRun Start(string kind, string exe, string args, string? log, string? control, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        psi.Environment.Remove("DOTNET_STARTUP_HOOKS");
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        var p = Process.Start(psi)!;
        DateTime start;
        try { start = p.StartTime.ToUniversalTime(); } catch (Exception) { start = DateTime.UtcNow; }
        return new AppRun { Kind = kind, Process = p, StartUtc = start, Log = log, Control = control, Exe = exe };
    }

    /// <summary>Ends every leftover instance of both apps (a previous step that failed must not skew the next one).</summary>
    public static void KillAll()
    {
        foreach (var name in new[] { "Osmium.App", "OsmiumUiPreview" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(true); p.WaitForExit(5000); } catch (Exception) { }
                p.Dispose();
            }
    }
}
