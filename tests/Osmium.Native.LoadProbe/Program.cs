using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Osmium.Core.Codecs.Native;
using Osmium.Core.Distributed;
using Osmium.Core.Pipeline;

namespace Osmium.Native.LoadProbe;

/// <summary>
/// Runs one native-encoder scenario in a fresh process and prints its findings as key=value lines. Commands:
///   status                    the encoder decision, and whether the library is mapped into the process
///   configure MODE [REASON]   NativeEncoders.Configure, then status, then a second Configure (must be refused)
///   encode                    an LZMA and a Zstandard block with the active encoders, decoded back
///   load FOLDER               the loader with FOLDER as the program folder
///   held FOLDER               as load, and while the file is held: try to write, delete and rename it, and to rename
///                             its folder
///   threads                   helper threads before, during and after a cancelled and a finished two-thread encode
///   worker PORT KEY           a cluster worker on the loopback interface until standard input closes
///   connect PORT KEY          a handshake with such a worker
/// </summary>
public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            switch (args.Length > 0 ? args[0] : "")
            {
                case "status": Status(); return 0;
                case "configure": return Configure(args);
                case "encode": return Encode();
                case "load": return Load(args[1], held: false);
                case "held": return Load(args[1], held: true);
                case "threads": return Threads();
                case "compress": return await CompressAsync(args[1], args[2], args[3]);
                case "worker": return await WorkerAsync(int.Parse(args[1]), args[2]);
                case "connect": return await ConnectAsync(int.Parse(args[1]), args[2]);
                default: Console.Error.WriteLine("unknown command"); return 2;
            }
        }
        catch (Exception e)
        {
            Print("exception", e.GetType().Name + ": " + e.Message);
            return 3;
        }
    }

    private static void Print(string key, object? value) =>
        Console.WriteLine($"{key}={value?.ToString()?.Replace('\n', ' ').Replace('\r', ' ')}");

    private static string LibraryName => NativeLibraryLoader.FileName(RuntimeInformation.ProcessArchitecture, OperatingSystem.IsWindows());

    /// <summary>Whether any osmium_native library (real or not) is mapped into this process.</summary>
    private static bool LibraryMapped()
    {
        if (OperatingSystem.IsWindows())
        {
            using var p = Process.GetCurrentProcess();
            return p.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.StartsWith("osmium_native", StringComparison.OrdinalIgnoreCase));
        }
        return File.ReadAllText("/proc/self/maps").Contains("osmium_native", StringComparison.Ordinal);
    }

    private static void Status()
    {
        Print("mode", NativeEncoders.Mode);
        Print("status", NativeEncoders.Status);
        Print("generation", NativeEncoders.ActiveGeneration.Id);
        Print("message", NativeEncoders.StatusMessage);
        Print("describe", NativeEncoders.Describe());
        Print("program_folder", NativeLibraryLoader.ConfirmProgramFolder(AppContext.BaseDirectory, Environment.ProcessPath, out var why) ?? "unconfirmed: " + why);
        Print("mapped", LibraryMapped());
        Print("fingerprint", Convert.ToHexString(Protocol.EngineFingerprint()));
    }

    private static int Configure(string[] args)
    {
        if (!NativeEncoders.TryParse(args[1], out var mode)) { Print("configure", "unknown mode"); return 2; }
        Print("decided_before", NativeEncoders.IsDecided);
        NativeEncoders.Configure(mode, args.Length > 2 ? args[2] : null);
        Print("decided_after_configure", NativeEncoders.IsDecided);
        Status();
        try
        {
            NativeEncoders.Configure(EncoderMode.Auto);
            Print("reconfigure", "accepted");
        }
        catch (InvalidOperationException e)
        {
            Print("reconfigure", "refused: " + e.Message);
        }
        return 0;
    }

    private static int Encode()
    {
        var reg = MethodRegistry.Default;
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("a block of native encoder test data, ", 30000)));
        foreach (var spec in new[] { "lzma(dict=1M)", "zstd(level=3)" })
        {
            var p = PipelineSpec.Parse(spec, reg);
            var codec = (MethodBase)p.Codec.Method;
            try
            {
                var packed = codec.Encode(data, p.Codec.Params, CodecContext.None);
                var back = codec.Decode(packed, data.Length, p.Codec.Params, CodecContext.None);
                Print(codec.Name, back.AsSpan().SequenceEqual(data) ? $"ok {packed.Length}" : "decoded differently");
            }
            catch (Exception e)
            {
                Print(codec.Name, "error: " + e.Message);
            }
        }
        Print("generation", NativeEncoders.ActiveGeneration.Id);
        Print("mapped", LibraryMapped());
        return 0;
    }

    private static int Load(string folder, bool held)
    {
        var o = NativeLibraryLoader.Defaults() with { BaseDirectory = folder };
        if (held)
        {
            o = o with
            {
                WhileHeld = path =>
                {
                    Print("held_write", Attempt(() => { using var f = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); f.WriteByte(0); }));
                    Print("held_delete", Attempt(() => File.Delete(path)));
                    Print("held_rename", Attempt(() => File.Move(path, path + ".moved")));
                    string dir = Path.GetDirectoryName(path)!;
                    Print("held_rename_folder", Attempt(() => Directory.Move(dir, dir + "-moved")));
                    Print("held_still_there", File.Exists(path));
                },
            };
        }
        var r = NativeLibraryLoader.TryLoad(o);
        Print("status", r.Status);
        Print("message", r.Message);
        Print("path", r.Path);
        Print("mapped", LibraryMapped());
        return 0;
    }

    private static string Attempt(Action a)
    {
        try { a(); return "succeeded"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "refused"; }
    }

    // ------------------------------------------------------------------ threads

    private static int Threads()
    {
        // Osmium never uses the helper thread; the library's two-thread mode is what this checks for leftover threads.
        NativeEncode.TestAllowHelperThread = true;
        var reg = MethodRegistry.Default;
        var spec = PipelineSpec.Parse("lzma(dict=16M,fb=32)", reg);
        var codec = (MethodBase)spec.Codec.Method;
        var small = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("warm up the encoder and the runtime ", 100)));
        codec.Encode(small, spec.Codec.Params, CodecContext.None);   // loads the library, starts the runtime's own threads
        if (NativeEncoders.Status != NativeStatus.Active) { Print("threads", "native encoders not active"); return 0; }
        Thread.Sleep(500);
        int baseline = HelperThreads();
        Print("baseline", baseline);

        var big = new byte[16 << 20];
        var rng = new Random(5);
        for (int i = 0; i < big.Length; i += 4096) rng.NextBytes(big.AsSpan(i, 2048));   // half random, half zeros
        int during = -1;
        using (var cts = new CancellationTokenSource())
        {
            int calls = 0;
            var ctx = new CodecContext
            {
                Token = cts.Token, MaxThreads = 2,
                Progress = _ => { if (++calls == 3) { during = HelperThreads(); cts.Cancel(); } },
            };
            Print("cancelled", Attempt2(() => codec.Encode(big, spec.Codec.Params, ctx)));
        }
        Print("during", during);
        Print("threads_used", NativeEncode.LastStats.ThreadsUsed);
        Print("after_cancel", Settle(baseline));
        codec.Encode(big.AsMemory(0, 2 << 20), spec.Codec.Params, new CodecContext { MaxThreads = 2 });
        Print("finished_threads_used", NativeEncode.LastStats.ThreadsUsed);
        Print("after_finish", Settle(baseline));
        if (!OperatingSystem.IsWindows()) Print("thread_names", string.Join(",", ThreadNames().Order()));
        return 0;
    }

    private static string Attempt2(Action a)
    {
        try { a(); return "no"; }
        catch (OperationCanceledException) { return "yes"; }
    }

    /// <summary>Waits up to 2 s for the helper-thread count to return to the baseline; returns the last count.</summary>
    private static int Settle(int baseline)
    {
        int n = HelperThreads();
        for (int i = 0; i < 40 && n > baseline; i++) { Thread.Sleep(50); n = HelperThreads(); }
        return n;
    }

    private static string? s_encoderThreadName;

    /// <summary>Linux: the names of the process's threads. A thread started by native code inherits the name of the
    /// thread that started it; the runtime names its own threads (".NET TP Worker" and so on).</summary>
    private static List<string> ThreadNames()
    {
        var names = new List<string>();
        foreach (var t in Directory.GetDirectories("/proc/self/task"))
        {
            try { names.Add(File.ReadAllText(Path.Combine(t, "comm")).Trim()); }
            catch (IOException) { }   // the thread ended meanwhile
        }
        return names;
    }

    /// <summary>Windows: threads whose start address lies inside the library's image. Linux: threads that carry the
    /// encoding thread's own name, the one native helper threads inherit (the encoding thread itself included).</summary>
    private static int HelperThreads()
    {
        if (!OperatingSystem.IsWindows())
        {
            s_encoderThreadName ??= File.ReadAllText("/proc/thread-self/comm").Trim();
            return ThreadNames().Count(n => n == s_encoderThreadName);
        }
        using var p = Process.GetCurrentProcess();
        var module = p.Modules.Cast<ProcessModule>().FirstOrDefault(m => m.ModuleName.Equals(LibraryName, StringComparison.OrdinalIgnoreCase));
        if (module == null) return 0;
        nint lo = module.BaseAddress, hi = module.BaseAddress + module.ModuleMemorySize;
        int count = 0;
        foreach (ProcessThread t in p.Threads)
        {
            nint h = OpenThread(0x0040 /* THREAD_QUERY_INFORMATION */, false, (uint)t.Id);
            if (h == 0) continue;
            try
            {
                if (NtQueryInformationThread(h, 9 /* ThreadQuerySetWin32StartAddress */, out nint start, nint.Size, 0) == 0
                    && start >= lo && start < hi)
                    count++;
            }
            finally { CloseHandle(h); }
        }
        return count;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtQueryInformationThread(nint thread, int infoClass, out nint info, int length, nint returnLength);

    // ------------------------------------------------------------------ archives

    private static async Task<int> CompressAsync(string source, string archive, string profile)
    {
        var scan = Osmium.Core.Planning.Scanner.Scan([source]);
        var plan = await new Osmium.Core.Planning.Planner(new Osmium.Core.Planning.PlanOptions
        {
            Profile = Osmium.Core.Planning.Profile.ByName(profile), Threads = 2, MemoryBudget = 2L << 30,
        }).BuildAsync(scan, CancellationToken.None);
        var r = await Osmium.Core.Planning.ArchiveBuilder.BuildAsync(plan, new Osmium.Core.Planning.BuildOptions
        {
            OutputPath = archive, Threads = 2, MemoryBudget = 2L << 30,
        }, CancellationToken.None);
        Print("generation", NativeEncoders.ActiveGeneration.Id);
        Print("archive", r.Path);
        Print("size", r.OutputBytes);
        Print("blocks", r.Blocks);
        return 0;
    }

    // ------------------------------------------------------------------ cluster

    private static async Task<int> WorkerAsync(int port, string key)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = new WorkerServer(new WorkerOptions
        {
            Key = ClusterKey.Parse(key), Port = port, Bind = IPAddress.Loopback, Slots = 1, Memory = 1L << 30,
            Discovery = false, Log = line => Print("log", line),
        });
        Print("generation", NativeEncoders.ActiveGeneration.Id);
        var run = server.RunAsync(cts.Token);
        Print("listening", port);
        await Task.Run(() => Console.In.ReadToEnd(), cts.Token).ContinueWith(_ => { });
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        return 0;
    }

    private static async Task<int> ConnectAsync(int port, string key)
    {
        Print("generation", NativeEncoders.ActiveGeneration.Id);
        try
        {
            await using var node = await RemoteNode.ConnectAsync($"127.0.0.1:{port}", ClusterKey.Parse(key), null, CancellationToken.None);
            Print("connect", "ok");
        }
        catch (Exception e)
        {
            Print("connect", "error: " + e.Message);
        }
        return 0;
    }
}
