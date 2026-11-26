using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime;
using System.Text;

/// <summary>
/// .NET startup hook (DOTNET_STARTUP_HOOKS). Listens on the pipe "UiEvalHook-{pid}" and answers one command per
/// connection with one JSON line:
///   mem  the process's private bytes, working set and managed heap
///   gc   a full blocking, compacting collection (twice, finalizers in between), then the same numbers
/// </summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "UI evaluation hook" };
        t.Start();
    }

    private static void Loop()
    {
        string name = "UiEvalHook-" + Environment.ProcessId;
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                var cmd = reader.ReadLine()?.Trim();
                long before = Process.GetCurrentProcess().PrivateMemorySize64;
                if (cmd == "gc")
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
                var p = Process.GetCurrentProcess();
                var info = GC.GetGCMemoryInfo();
                writer.WriteLine(
                    $"{{\"ok\":true,\"cmd\":\"{cmd}\",\"privateBefore\":{before},\"private\":{p.PrivateMemorySize64},\"workingSet\":{p.WorkingSet64}," +
                    $"\"gcHeap\":{GC.GetTotalMemory(false)},\"gcCommitted\":{info.TotalCommittedBytes},\"threads\":{p.Threads.Count},\"handles\":{p.HandleCount}}}");
                pipe.WaitForPipeDrain();
            }
            catch (Exception)
            {
                Thread.Sleep(100);
            }
        }
    }
}
