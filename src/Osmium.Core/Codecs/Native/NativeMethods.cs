using System.Runtime.InteropServices;

namespace Osmium.Core.Codecs.Native;

/// <summary>The structures and status codes of native/include/osmium_native.h (ABI 1), field for field.</summary>
internal static class Osn
{
    public const int Ok = 0, EParam = 1, EOutputFull = 2, ENoMem = 3, EMemCap = 4, EAborted = 5, EInternal = 6;
    public const ulong MaxInput = 1UL << 31;
    public const uint LzmaFlagTestThreadError = 1;

    public static string StatusName(int s) => s switch
    {
        Ok => "OSN_OK", EParam => "OSN_E_PARAM", EOutputFull => "OSN_E_OUTPUT_FULL", ENoMem => "OSN_E_NOMEM",
        EMemCap => "OSN_E_MEMCAP", EAborted => "OSN_E_ABORTED", EInternal => "OSN_E_INTERNAL", _ => $"status {s}",
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct OsnLzmaParams
{
    public uint Size, DictSize, Lc, Lp, Pb, Fb, MatchFinder, Threads, Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OsnZstdParams
{
    public uint Size, Level, WindowLog, Ldm, LdmMinMatch, Strategy, ChunkSize, Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OsnStats
{
    public uint Size;
    public int UpstreamCode;
    public ulong PeakBytes, Allocations, LiveBytesAfter;
    public uint ThreadsUsed, ThreadRetries;
}

/// <summary>The library's exports as function pointers, resolved once by <see cref="NativeLibraryLoader"/>. No
/// DllImport by name exists for the library, so the runtime's own library probing is never involved.</summary>
internal sealed unsafe class NativeApi
{
    public static readonly string[] ExportNames =
        ["osn_abi", "osn_generation", "osn_versions", "osn_lzma_bound", "osn_zstd_bound", "osn_lzma_encode", "osn_zstd_encode"];

    public required string Path { get; init; }
    public required nint Handle { get; init; }
    public delegate* unmanaged[Cdecl]<int> Abi;
    public delegate* unmanaged[Cdecl]<byte*> Generation;
    public delegate* unmanaged[Cdecl]<byte*> Versions;
    public delegate* unmanaged[Cdecl]<ulong, ulong*, int> LzmaBound;
    public delegate* unmanaged[Cdecl]<ulong, ulong*, int> ZstdBound;
    public delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, nuint*, OsnLzmaParams*,
        delegate* unmanaged[Cdecl]<void*, ulong, int>, void*, ulong, OsnStats*, int> LzmaEncode;
    public delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, nuint*, OsnZstdParams*,
        delegate* unmanaged[Cdecl]<void*, ulong, int>, void*, ulong, OsnStats*, int> ZstdEncode;

    /// <summary>Resolves every export; null when one is missing (never an exception).</summary>
    public static NativeApi? Resolve(nint handle, string path)
    {
        var p = new nint[ExportNames.Length];
        for (int i = 0; i < p.Length; i++)
            if (!NativeLibrary.TryGetExport(handle, ExportNames[i], out p[i]) || p[i] == 0) return null;
        return new NativeApi
        {
            Path = path,
            Handle = handle,
            Abi = (delegate* unmanaged[Cdecl]<int>)p[0],
            Generation = (delegate* unmanaged[Cdecl]<byte*>)p[1],
            Versions = (delegate* unmanaged[Cdecl]<byte*>)p[2],
            LzmaBound = (delegate* unmanaged[Cdecl]<ulong, ulong*, int>)p[3],
            ZstdBound = (delegate* unmanaged[Cdecl]<ulong, ulong*, int>)p[4],
            LzmaEncode = (delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, nuint*, OsnLzmaParams*,
                delegate* unmanaged[Cdecl]<void*, ulong, int>, void*, ulong, OsnStats*, int>)p[5],
            ZstdEncode = (delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, nuint*, OsnZstdParams*,
                delegate* unmanaged[Cdecl]<void*, ulong, int>, void*, ulong, OsnStats*, int>)p[6],
        };
    }

    // ------------------------------------------------------------------ safe calls for tests (any argument, any length)

    /// <summary>osn_lzma_bound / osn_zstd_bound; <paramref name="nullBound"/> passes a null result pointer.</summary>
    internal int RawBound(bool zstd, ulong n, out ulong bound, bool nullBound = false)
    {
        ulong b = 0;
        int s = nullBound ? (zstd ? ZstdBound(n, null) : LzmaBound(n, null)) : (zstd ? ZstdBound(n, &b) : LzmaBound(n, &b));
        bound = b;
        return s;
    }

    /// <summary>osn_lzma_encode with arrays (null passes a null pointer) and explicit lengths, which may disagree
    /// with the arrays only when the array is null (a length is never passed beyond an array).</summary>
    internal int RawLzma(byte[]? src, ulong srcLen, byte[]? dst, ulong dstCap, OsnLzmaParams? p, ulong memCap,
        uint statsSize, out ulong written, out OsnStats stats, bool nullDstLen = false)
    {
        CheckLength(src, srcLen, input: true);
        CheckLength(dst, dstCap, input: false);
        var param = p.GetValueOrDefault();
        var st = new OsnStats { Size = statsSize };
        nuint n = 99;
        int status;
        fixed (byte* s = src) fixed (byte* d = dst)
            status = LzmaEncode(s, (nuint)srcLen, d, (nuint)dstCap, nullDstLen ? null : &n, p.HasValue ? &param : null,
                null, null, memCap, statsSize == 0 ? null : &st);
        written = n;
        stats = st;
        return status;
    }

    /// <summary>osn_zstd_encode, as <see cref="RawLzma"/>.</summary>
    internal int RawZstd(byte[]? src, ulong srcLen, byte[]? dst, ulong dstCap, OsnZstdParams? p, ulong memCap,
        uint statsSize, out ulong written, out OsnStats stats, bool nullDstLen = false)
    {
        CheckLength(src, srcLen, input: true);
        CheckLength(dst, dstCap, input: false);
        var param = p.GetValueOrDefault();
        var st = new OsnStats { Size = statsSize };
        nuint n = 99;
        int status;
        fixed (byte* s = src) fixed (byte* d = dst)
            status = ZstdEncode(s, (nuint)srcLen, d, (nuint)dstCap, nullDstLen ? null : &n, p.HasValue ? &param : null,
                null, null, memCap, statsSize == 0 ? null : &st);
        written = n;
        stats = st;
        return status;
    }

    /// <summary>An input length beyond its array is passed only above <see cref="Osn.MaxInput"/>, which the library
    /// refuses before it reads anything; an output capacity is never larger than its array.</summary>
    private static void CheckLength(byte[]? a, ulong n, bool input)
    {
        if (a != null && n > (ulong)a.Length && !(input && n > Osn.MaxInput))
            throw new ArgumentOutOfRangeException(nameof(n), "a length beyond its array");
    }

    public static uint LzmaParamsSize => (uint)sizeof(OsnLzmaParams);
    public static uint ZstdParamsSize => (uint)sizeof(OsnZstdParams);
    public static uint StatsSize => (uint)sizeof(OsnStats);

    /// <summary>A constant string of the library, read as ASCII up to its terminating zero (at most 256 bytes).</summary>
    public static string ReadConstant(byte* s)
    {
        if (s == null) return "";
        int n = 0;
        while (n < 256 && s[n] != 0) n++;
        return System.Text.Encoding.ASCII.GetString(s, n);
    }
}

/// <summary>Windows functions the loader uses, all from KERNEL32.dll (a KnownDLL, always from System32).</summary>
internal static partial class Kernel32
{
    public const uint LoadLibrarySearchSystem32 = 0x00000800;
    public const int ErrorInsufficientBuffer = 122;
    public const int FileIdInfoClass = 18;

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint LoadLibraryEx(string fileName, nint file, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static unsafe partial uint GetModuleFileName(nint module, char* buffer, uint size);

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public fixed byte FileId[16];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]   // FILETIME is two DWORDs: 4-byte alignment
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation info);

    public const uint FileReadAttributes = 0x80;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;   // needed to open a folder

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static unsafe partial uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, char* buffer, uint size, uint flags);
}

/// <summary>The C library function the loader uses on Linux.</summary>
internal static unsafe partial class Libc
{
    /// <summary>realpath(3) with a null buffer: the result is allocated with malloc and released with free
    /// (NativeMemory.Free).</summary>
    [LibraryImport("libc", EntryPoint = "realpath", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial byte* RealPath(string path, byte* resolved);
}
