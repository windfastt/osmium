using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("Osmium.Native.LoadProbe")]

namespace Osmium.Core.Codecs.Native;

/// <summary>Why the native encoders are or are not in use.</summary>
public enum NativeStatus
{
    /// <summary>The library is loaded and every check passed.</summary>
    Active,
    /// <summary>Not looked for: the built-in encoders were chosen, or the program folder could not be confirmed.</summary>
    Disabled,
    /// <summary>No library beside the program, or this build of Osmium knows no library hash for this architecture.</summary>
    NotFound,
    /// <summary>The file is there but could not be read or loaded (for example blocked by security software).</summary>
    LoadFailed,
    /// <summary>The file is not the one this build of Osmium expects (hash, file identity, exports, interface number,
    /// generation or versions differ). It is never used.</summary>
    Mismatch,
}

/// <summary>Everything the loader decides on; <see cref="NativeLibraryLoader.Defaults"/> gives the real values, tests
/// pass others.</summary>
internal sealed record LoaderOptions
{
    public required string BaseDirectory { get; init; }
    public required Architecture Architecture { get; init; }
    public required bool Windows { get; init; }
    /// <summary>Why the program folder could not be confirmed (nothing is then looked for), or null.</summary>
    public required string? ProgramFolderProblem { get; init; }
    /// <summary>Lower-case hex SHA-256 the file must have; null when this build was made without the library.</summary>
    public required string? ExpectedSha256 { get; init; }
    public required int ExpectedAbi { get; init; }
    public required string ExpectedGeneration { get; init; }
    public required string ExpectedVersions { get; init; }
    /// <summary>Tests only: called with the path while the file is held, after the hash matched and before it is
    /// loaded (to check that nobody can replace or delete it then).</summary>
    public Action<string>? WhileHeld { get; init; }
}

internal readonly record struct LoadResult(NativeStatus Status, string Message, NativeApi? Api, string? Path);

/// <summary>
/// Loads osmium_native from exactly one path, the program's own folder plus the file name for the process
/// architecture, and only after the file's SHA-256 matched the value compiled into Osmium.Core: no code of any other
/// file ever runs. The file is held open with writes and deletes denied from the hash until after the load, and the
/// loaded module's file identity is compared with it afterwards. When the program folder cannot be confirmed (see
/// ConfirmProgramFolder), nothing is looked for.
/// </summary>
internal static class NativeLibraryLoader
{
    /// <summary>Largest file accepted for hashing; the library is about 1 MB.</summary>
    internal const long MaxLibrarySize = 64L << 20;

    public static string FileName(Architecture arch, bool windows)
    {
        string a = arch switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => arch.ToString().ToLowerInvariant() };
        return windows ? $"osmium_native-{a}.dll" : $"libosmium_native-{a}.so";
    }

    /// <summary>
    /// The program's folder: <paramref name="baseDirectory"/> (AppContext.BaseDirectory), confirmed by the folder of
    /// the running program (<paramref name="processPath"/>, Environment.ProcessPath). Both must be the same canonical
    /// folder, otherwise null and the reason: a bundle that extracts itself runs from a temporary folder, and a
    /// program started through another host ("dotnet x.dll") cannot show where it is. Normal, single-file and
    /// native AOT builds all pass (Assembly.Location, empty in the last two, is not used).
    /// </summary>
    public static string? ConfirmProgramFolder(string? baseDirectory, string? processPath, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(baseDirectory)) { reason = "the program folder is unknown"; return null; }
        if (string.IsNullOrEmpty(processPath)) { reason = "the running program's path is unknown"; return null; }
        string? processDir = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(processDir)) { reason = "the running program's folder is unknown"; return null; }
        string a, b;
        try
        {
            a = CanonicalFolder(baseDirectory);
            b = CanonicalFolder(processDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            reason = "the program folder could not be resolved: " + e.Message;
            return null;
        }
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(a, b, cmp))
        {
            reason = $"the program folder {a} is not the folder of the running program, {b}";
            return null;
        }
        return a;
    }

    /// <summary>The folder's real path, as the system resolves it, without a trailing separator: every link and
    /// junction on the way is followed and, on Windows, short (8.3) names and letter case become the stored ones, so
    /// two spellings of one folder give one string. Throws IOException when the folder cannot be opened.</summary>
    internal static string CanonicalFolder(string folder)
    {
        string full = Path.GetFullPath(folder);
        return Path.TrimEndingDirectorySeparator(OperatingSystem.IsWindows() ? FinalPathWindows(full) : RealPathUnix(full));
    }

    private static unsafe string FinalPathWindows(string full)
    {
        string longPath = full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
        using SafeFileHandle folder = Kernel32.CreateFile(longPath, Kernel32.FileReadAttributes,
            (uint)(FileShare.ReadWrite | FileShare.Delete), 0, Kernel32.OpenExisting, Kernel32.FileFlagBackupSemantics, 0);
        if (folder.IsInvalid) throw new IOException($"{full} could not be opened (error {Marshal.GetLastPInvokeError()})");
        char[] buffer = new char[512];
        while (true)
        {
            uint n;
            fixed (char* p = buffer) n = Kernel32.GetFinalPathNameByHandle(folder, p, (uint)buffer.Length, 0);
            if (n == 0) throw new IOException($"the real path of {full} is unknown (error {Marshal.GetLastPInvokeError()})");
            if (n < (uint)buffer.Length)
            {
                // Success: n characters without the terminating zero, in the \\?\ form.
                string s = new(buffer, 0, (int)n);
                return s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + s[8..]
                    : s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..]
                    : s;
            }
            // Too small: n is the size needed, with the zero. No Windows path is longer than 32,767 characters.
            if (n > 32_768) throw new IOException($"the real path of {full} is longer than any Windows path");
            buffer = new char[n];
        }
    }

    private static unsafe string RealPathUnix(string full)
    {
        byte* real = Libc.RealPath(full, null);
        if (real == null) throw new IOException($"the real path of {full} is unknown (error {Marshal.GetLastPInvokeError()})");
        try
        {
            return Marshal.PtrToStringUTF8((nint)real)!;
        }
        finally
        {
            NativeMemory.Free(real);
        }
    }

    public static LoaderOptions Defaults()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        bool windows = OperatingSystem.IsWindows();
        string? folder = ConfirmProgramFolder(AppContext.BaseDirectory, Environment.ProcessPath, out string problem);
        return new LoaderOptions
        {
            BaseDirectory = folder ?? AppContext.BaseDirectory,
            Architecture = arch,
            Windows = windows,
            ProgramFolderProblem = folder == null ? problem : null,
            ExpectedSha256 = NativeExpected.Sha256For(windows ? (arch == Architecture.Arm64 ? "win-arm64" : "win-x64") :
                arch == Architecture.X64 ? "linux-x64" : "none"),
            ExpectedAbi = NativeExpected.Abi,
            ExpectedGeneration = NativeExpected.Generation,
            ExpectedVersions = NativeExpected.Versions,
        };
    }

    public static LoadResult TryLoad(LoaderOptions o)
    {
        if (o.ProgramFolderProblem != null)
            return new(NativeStatus.Disabled, "the native library is not looked for: " + o.ProgramFolderProblem, null, null);
        if (o.Architecture is not (Architecture.X64 or Architecture.Arm64) || (!o.Windows && o.Architecture != Architecture.X64))
            return new(NativeStatus.NotFound, $"no native library is built for {RuntimeInformation.OSDescription} {o.Architecture}", null, null);
        if (o.ExpectedSha256 == null)
            return new(NativeStatus.NotFound, "this build of Osmium was made without the native library", null, null);

        string path = Path.Combine(Path.GetFullPath(o.BaseDirectory), FileName(o.Architecture, o.Windows));
        FileStream held;
        try
        {
            // FileShare.Read only: nobody can write, replace or delete the file while it is hashed and loaded.
            held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
        }
        catch (FileNotFoundException) { return new(NativeStatus.NotFound, $"{Path.GetFileName(path)} is not in the program folder", null, path); }
        catch (DirectoryNotFoundException) { return new(NativeStatus.NotFound, "the program folder does not exist", null, path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(NativeStatus.LoadFailed, $"{Path.GetFileName(path)} could not be opened: {e.Message}", null, path);
        }
        using (held)
        {
            string actual;
            try
            {
                long length = held.Length;
                if (length > MaxLibrarySize)
                    return new(NativeStatus.Mismatch, $"{Path.GetFileName(path)} is {length} bytes, larger than any native library", null, path);
                actual = Convert.ToHexStringLower(SHA256.HashData(held));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new(NativeStatus.LoadFailed, $"{Path.GetFileName(path)} could not be read: {e.Message}", null, path);
            }
            if (!string.Equals(actual, o.ExpectedSha256, StringComparison.Ordinal))
                return new(NativeStatus.Mismatch,
                    $"{Path.GetFileName(path)} has SHA-256 {actual}; this build of Osmium expects {o.ExpectedSha256}", null, path);
            o.WhileHeld?.Invoke(path);

            nint handle;
            if (o.Windows)
            {
                handle = Kernel32.LoadLibraryEx(path, 0, Kernel32.LoadLibrarySearchSystem32);
                if (handle == 0)
                    return new(NativeStatus.LoadFailed, $"Windows could not load {Path.GetFileName(path)} (error {Marshal.GetLastPInvokeError()})", null, path);
                string? identity = CheckIdentity(handle, held.SafeFileHandle);
                if (identity != null) return new(NativeStatus.Mismatch, identity, null, path);
            }
            else if (!NativeLibrary.TryLoad(path, out handle))
                return new(NativeStatus.LoadFailed, $"{Path.GetFileName(path)} could not be loaded", null, path);

            var api = NativeApi.Resolve(handle, path);
            if (api == null) return new(NativeStatus.Mismatch, $"{Path.GetFileName(path)} lacks an export of the native interface", null, path);
            return CheckIdentityStrings(api, o, path);
        }
    }

    private static unsafe LoadResult CheckIdentityStrings(NativeApi api, LoaderOptions o, string path)
    {
        int abi = api.Abi();
        if (abi != o.ExpectedAbi)
            return new(NativeStatus.Mismatch, $"the native library has interface number {abi}; this build of Osmium expects {o.ExpectedAbi}", null, path);
        string generation = NativeApi.ReadConstant(api.Generation());
        if (generation != o.ExpectedGeneration)
            return new(NativeStatus.Mismatch, $"the native library is encoder generation '{generation}'; this build of Osmium expects '{o.ExpectedGeneration}'", null, path);
        string versions = NativeApi.ReadConstant(api.Versions());
        if (versions != o.ExpectedVersions)
            return new(NativeStatus.Mismatch, $"the native library contains '{versions}'; this build of Osmium expects '{o.ExpectedVersions}'", null, path);
        return new(NativeStatus.Active, $"{generation} ({versions})", api, path);
    }

    /// <summary>Windows: the loaded module's file must be the very file that was hashed (same volume and file id).
    /// Returns null when it is, otherwise the reason.</summary>
    private static string? CheckIdentity(nint module, SafeFileHandle held)
    {
        string? loadedPath = ModulePath(module);
        if (loadedPath == null) return "the loaded module's path could not be read";
        SafeFileHandle loaded;
        try { loaded = File.OpenHandle(loadedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"the loaded module's file could not be opened: {e.Message}";
        }
        using (loaded)
        {
            var a = FileIdentity(held);
            var b = FileIdentity(loaded);
            if (a == null || b == null) return "the file identity of the native library could not be read";
            return a == b ? null : "the loaded module is not the file that was checked";
        }
    }

    /// <summary>GetModuleFileNameW with a buffer that grows until the whole path fits (long paths included).</summary>
    internal static unsafe string? ModulePath(nint module)
    {
        for (uint size = 512; size <= 65536; size *= 2)
        {
            var buffer = new char[size];
            uint n;
            fixed (char* p = buffer) n = Kernel32.GetModuleFileName(module, p, size);
            if (n == 0) return null;
            if (n < size && Marshal.GetLastPInvokeError() != Kernel32.ErrorInsufficientBuffer) return new string(buffer, 0, (int)n);
        }
        return null;
    }

    private static unsafe string? FileIdentity(SafeFileHandle h)
    {
        Kernel32.FileIdInfo info;
        if (Kernel32.GetFileInformationByHandleEx(h, Kernel32.FileIdInfoClass, &info, (uint)sizeof(Kernel32.FileIdInfo)))
            return $"{info.VolumeSerialNumber:x16}:{Convert.ToHexString(new ReadOnlySpan<byte>(info.FileId, 16))}";
        if (Kernel32.GetFileInformationByHandle(h, out var old))
            return $"{old.VolumeSerialNumber:x8}:{old.FileIndexHigh:x8}{old.FileIndexLow:x8}";
        return null;
    }
}
