using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Proto.Desktop;

/// <summary>
/// Where a process's private bytes are: the managed heap (GC committed), native heaps (the CRT heap Skia, HarfBuzz and
/// ANGLE allocate from, summed with HeapSummary), other private committed memory (CLR loader and JIT code heaps,
/// thread stacks, GPU/driver allocations) and written pages of mapped DLLs, plus JIT statistics. No UI framework types.
/// </summary>
internal static class MemoryReport
{
    public static object Collect(object? extra = null)
    {
        var p = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        long privateCommitted = 0, mappedWritable = 0, imageWritable = 0;
        var images = new Dictionary<IntPtr, long>();
        IntPtr addr = IntPtr.Zero;
        int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (VirtualQuery(addr, out var m, (IntPtr)mbiSize) != IntPtr.Zero)
        {
            long size = m.RegionSize.ToInt64();
            if (m.State == MEM_COMMIT)
            {
                bool writable = (m.Protect & (PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
                if (m.Type == MEM_PRIVATE) privateCommitted += size;
                else if (m.Type == MEM_MAPPED && writable) mappedWritable += size;
                else if (m.Type == MEM_IMAGE && writable)
                {
                    imageWritable += size;
                    images[m.AllocationBase] = images.GetValueOrDefault(m.AllocationBase) + size;
                }
            }
            long next = m.BaseAddress.ToInt64() + size;
            if (next <= addr.ToInt64()) break;
            addr = (IntPtr)next;
        }
        long heaps = 0;
        int heapCount = 0;
        var handles = new IntPtr[256];
        int n = GetProcessHeaps(handles.Length, handles);
        for (int i = 0; i < Math.Min(n, handles.Length); i++)
        {
            var s = new HEAP_SUMMARY { cb = Marshal.SizeOf<HEAP_SUMMARY>() };
            if (HeapSummary(handles[i], 0, ref s)) { heaps += s.cbCommitted.ToInt64(); heapCount++; }
        }
        var topImages = images.OrderByDescending(kv => kv.Value).Take(8).Select(kv =>
        {
            var sb = new System.Text.StringBuilder(260);
            GetModuleFileName(kv.Key, sb, sb.Capacity);
            return $"{Path.GetFileName(sb.ToString())} {kv.Value / 1024} KiB";
        }).ToArray();
        static double M(long b) => Math.Round(b / 1048576.0, 1);
        return new
        {
            privateMiB = M(p.PrivateMemorySize64),
            workingSetMiB = M(p.WorkingSet64),
            gcHeapMiB = M(gc.HeapSizeBytes),
            gcCommittedMiB = M(gc.TotalCommittedBytes),
            gcFragmentedMiB = M(gc.FragmentedBytes),
            nativeHeapsMiB = M(heaps),
            nativeHeapCount = heapCount,
            privateCommittedMiB = M(privateCommitted),
            otherPrivateMiB = M(privateCommitted - heaps - gc.TotalCommittedBytes),
            mappedWritableMiB = M(mappedWritable),
            imageWritableMiB = M(imageWritable),
            topImageWritable = topImages,
            jitMethods = System.Runtime.JitInfo.GetCompiledMethodCount(false),
            jitIlKiB = System.Runtime.JitInfo.GetCompiledILBytes(false) / 1024,
            jitMs = Math.Round(System.Runtime.JitInfo.GetCompilationTime(false).TotalMilliseconds, 1),
            threads = p.Threads.Count,
            modules = p.Modules.Count,
            assemblies = AppDomain.CurrentDomain.GetAssemblies().Length,
            extra,
        };
    }

    public static void FullGc()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }

    private const uint MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000, MEM_MAPPED = 0x40000, MEM_IMAGE = 0x1000000;
    private const uint PAGE_READWRITE = 0x04, PAGE_WRITECOPY = 0x08, PAGE_EXECUTE_READWRITE = 0x40, PAGE_EXECUTE_WRITECOPY = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HEAP_SUMMARY { public int cb; public IntPtr cbAllocated, cbCommitted, cbReserved, cbMaxReserve; }

    [DllImport("kernel32.dll")] private static extern IntPtr VirtualQuery(IntPtr address, out MEMORY_BASIC_INFORMATION info, IntPtr length);
    [DllImport("kernel32.dll")] private static extern int GetProcessHeaps(int count, IntPtr[] heaps);
    [DllImport("kernel32.dll")] private static extern bool HeapSummary(IntPtr heap, int flags, ref HEAP_SUMMARY summary);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetModuleFileName(IntPtr module, System.Text.StringBuilder sb, int size);
}
