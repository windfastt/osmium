using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32.SafeHandles;
using Osmium.Desktop.Jobs;

namespace Osmium.App;

/// <summary>
/// Moves files and folders to the Recycle Bin with the shell's <c>IFileOperation</c>, all items of a job in one
/// operation, so each can be restored from the Recycle Bin (a folder as one item). Nothing is ever deleted for good,
/// and nothing but the items handed over is moved:
/// <list type="bullet">
/// <item>only items on fixed local drives are handed to the shell (removable and network drives have no Recycle Bin);</item>
/// <item>the operation does not take connected elements along (an HTML page's "x_files" folder, or the page of such a
/// folder);</item>
/// <item>each item has a progress sink, and one more sink watches the whole operation. Both ask one
/// <see cref="RecycleGate"/>, which in <c>PreDeleteItem</c> refuses any delete the shell would not recycle (its flags
/// lack <c>TSF_DELETE_RECYCLE_IF_POSSIBLE</c>: a volume set to remove files immediately, an item larger than the
/// Recycle Bin, a drive without one) and any item that is not one handed over or part of one. A refusal, a failure or
/// a destroyed item stops the operation, so that item and every item after it stay where they are;</item>
/// <item>an item counts as moved only when the shell reports the new item it created in the Recycle Bin.</item>
/// </list>
/// </summary>
internal sealed partial class ShellRecycler : IRecycler
{
    private const uint FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400,
        FOF_NO_CONNECTED_ELEMENTS = 0x2000;
    private const uint FOFX_RECYCLEONDELETE = 0x0008_0000;
    /// <summary>
    /// Delete means recycle; only the items handed over move (never a connected "x_files" folder or "x.htm" page); no
    /// confirmation, progress or error windows.
    /// </summary>
    internal const uint OperationFlags = FOFX_RECYCLEONDELETE | FOF_ALLOWUNDO | FOF_NO_CONNECTED_ELEMENTS | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI;
    /// <summary>The flag the shell sets on an item it is about to move to the Recycle Bin rather than destroy.</summary>
    internal const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x0080;
    /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED): the answer that stops the operation before an item.</summary>
    internal const int Refuse = unchecked((int)0x8007_04C7);

    private const uint SIGDN_FILESYSPATH = 0x8005_8000;
    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint FILE_READ_ATTRIBUTES = 0x80, FILE_SHARE_ALL = 0x1 | 0x2 | 0x4, OPEN_EXISTING = 3, FILE_FLAG_BACKUP_SEMANTICS = 0x0200_0000;

    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IidFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>Creates the managed wrappers of the shell's objects; each is released as soon as it is no longer needed.</summary>
    internal static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>How often the shell asked before deleting an item, in the last <see cref="Recycle"/> call.</summary>
    internal int LastPreDeletes { get; private set; }

    /// <summary>
    /// The type of the volume that holds <paramref name="path"/>: the volume the path names, and the volume the item
    /// really lies on when a junction, symbolic link or mounted folder leads elsewhere. Unknown when either cannot be
    /// determined.
    /// </summary>
    public DriveType DriveTypeOf(string path)
    {
        var named = VolumeType(path);
        if (named != DriveType.Fixed) return named;
        return FinalPath(path) is { } final ? VolumeType(final) : DriveType.Unknown;
    }

    public IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths, Func<string, string?> recheck, CancellationToken ct)
    {
        if (paths.Count == 0) return [];
        IReadOnlyList<RecycleOutcome>? outcomes = null;
        ExceptionDispatchInfo? error = null;
        // IFileOperation works only in a single-threaded apartment; jobs run on thread-pool threads.
        var thread = new Thread(() =>
        {
            try { outcomes = RecycleOnThisThread(paths, recheck, ct); }
            catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
        }) { IsBackground = true, Name = "Osmium Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
        return outcomes!;
    }

    private List<RecycleOutcome> RecycleOnThisThread(IReadOnlyList<string> paths, Func<string, string?> recheck, CancellationToken ct)
    {
        var outcomes = new RecycleOutcome?[paths.Count];
        var items = new RecycleGate.Item?[paths.Count];
        var shellItems = new nint[paths.Count];
        var gate = new RecycleGate(recheck, ct);
        int performed = 0;
        bool aborted = false;
        Check(CoCreateInstance(in ClsidFileOperation, 0, CLSCTX_INPROC_SERVER, in IidFileOperation, out nint unknown), "Creating the shell's file operation");
        IFileOperation operation;
        try { operation = (IFileOperation)Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(unknown); }
        try
        {
            Check(operation.SetOperationFlags(OperationFlags), "Setting the operation's flags");
            Check(operation.Advise(new RecycleSink(gate, null), out uint cookie), "Watching the operation");
            int queued = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                // Checked again here: whoever calls, nothing on a drive without a Recycle Bin is handed to the shell.
                if (DriveTypeOf(path) is var drive and not DriveType.Fixed)
                {
                    outcomes[i] = new RecycleOutcome(path, false, $"it is not on a fixed local drive ({drive}), so it has no Recycle Bin");
                    continue;
                }
                if (gate.Expect(path) is not { } item)
                {
                    outcomes[i] = new RecycleOutcome(path, false, "it is, holds or lies inside another item of this operation");
                    continue;
                }
                int hr = SHCreateItemFromParsingName(path, 0, in IidShellItem, out shellItems[i]);
                if (hr < 0)
                {
                    shellItems[i] = 0;
                    gate.Forget(item);
                    outcomes[i] = new RecycleOutcome(path, false, "the shell could not find it: " + Describe(hr));
                    continue;
                }
                hr = operation.DeleteItem(shellItems[i], new RecycleSink(gate, item));
                if (hr < 0)
                {
                    gate.Forget(item);
                    outcomes[i] = new RecycleOutcome(path, false, "the shell would not take it: " + Describe(hr));
                    continue;
                }
                items[i] = item;
                queued++;
            }
            if (queued > 0 && !ct.IsCancellationRequested)
            {
                performed = operation.PerformOperations();
                aborted = operation.GetAnyOperationsAborted(out int any) >= 0 && any != 0;
            }
            operation.Unadvise(cookie);
        }
        finally
        {
            ((ComObject)(object)operation).FinalRelease();
            foreach (var shellItem in shellItems)
                if (shellItem != 0) Marshal.Release(shellItem);
        }
        LastPreDeletes = gate.PreDeletes;
        var result = new List<RecycleOutcome>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
            result.Add(outcomes[i] ?? (items[i] is { } item ? gate.Outcome(item, performed, aborted) : new RecycleOutcome(paths[i], false, "the shell did not take it")));
        result.AddRange(gate.Others());
        return result;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new IOException($"{what} failed: {Describe(hr)}", hr);
    }

    internal static string Describe(int hr) => $"{Marshal.GetExceptionForHR(hr)?.Message ?? "error"} (0x{hr:X8})";

    /// <summary>The file-system path of a shell item, or null (also when the shell cannot say).</summary>
    internal static string? PathOf(nint item)
    {
        if (item == 0) return null;
        try
        {
            var shellItem = (IShellItem)Wrappers.GetOrCreateObjectForComInstance(item, CreateObjectFlags.UniqueInstance);
            try
            {
                if (shellItem.GetDisplayName(SIGDN_FILESYSPATH, out nint name) < 0 || name == 0) return null;
                try { return Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally { ((ComObject)(object)shellItem).FinalRelease(); }
        }
        catch (Exception) { return null; } // no path: before a delete, that means the item stays
    }

    /// <summary>
    /// A path in the form the shell reports items in, for comparing them: full, long names (no 8.3 short names) where
    /// the item exists, no closing separator. Compared ignoring case, as Windows does.
    /// </summary>
    internal static string Key(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { full = path; }
        return Path.TrimEndingDirectorySeparator(LongPath(full));
    }

    private static unsafe string LongPath(string path)
    {
        var buffer = new char[Math.Max(path.Length + 1, 261)];
        while (true)
        {
            uint n;
            fixed (char* p = buffer) n = GetLongPathName(path, p, (uint)buffer.Length);
            if (n == 0) return path; // not there (any more), or not resolvable: as given
            if (n >= buffer.Length) { buffer = new char[n]; continue; } // n is the size it needs
            return new string(buffer, 0, (int)n);
        }
    }

    private static unsafe DriveType VolumeType(string path)
    {
        // The volume's root is a prefix of the path (with a closing separator), so this buffer is always large enough.
        var buffer = new char[Math.Max(path.Length + 2, 261)];
        fixed (char* p = buffer)
        {
            if (!GetVolumePathName(path, p, (uint)buffer.Length)) return DriveType.Unknown;
            uint type = GetDriveType(new string(p));
            return type <= (uint)DriveType.Ram ? (DriveType)type : DriveType.Unknown;
        }
    }

    /// <summary>The path of the item itself, with every junction, symbolic link and mounted folder resolved.</summary>
    private static unsafe string? FinalPath(string path)
    {
        using var handle = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (handle.IsInvalid) return null;
        var buffer = new char[512];
        while (true)
        {
            uint n;
            fixed (char* p = buffer) n = GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, 0); // normalized, drive letter
            if (n == 0) return null;
            if (n >= buffer.Length) { buffer = new char[n]; continue; } // n is the size it needs
            string final = new(buffer, 0, (int)n);
            return final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + final[8..]
                : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string pszPath, nint pbc, in Guid riid, out nint ppv);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumePathName(string lpszFileName, char* lpszVolumePathName, uint cchBufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string lpRootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetLongPathName(string lpszShortPath, char* lpszLongPath, uint cchBuffer);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);
}

/// <summary>
/// The decisions of one Recycle Bin operation, shared by all its progress sinks, and what the shell reported for each
/// item. A delete goes ahead only when the shell is about to recycle it and it is an item handed over, or part of one;
/// an item handed over is also checked once more with <paramref name="recheck"/> at that moment, the last one before it
/// moves. Once anything is refused, fails or is destroyed, every later delete is refused, so the rest stays in place.
/// </summary>
internal sealed class RecycleGate(Func<string, string?> recheck, CancellationToken ct)
{
    internal const string CannotTake =
        "the Recycle Bin cannot take it (its drive keeps no deleted files, or it is larger than the Recycle Bin), so the shell would have deleted it for good";

    /// <summary>An item handed over, by its <see cref="ShellRecycler.Key"/>.</summary>
    internal sealed class Item(string path, string key)
    {
        public string Path { get; } = path;
        public string Key { get; } = key;
        public bool Reached, Approved, Recycled, Destroyed;
        public int Failed;
        public string? Refusal, BinPath;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _holders = new(StringComparer.OrdinalIgnoreCase); // folders that hold an item
    private readonly Dictionary<string, RecycleOutcome> _others = new(StringComparer.OrdinalIgnoreCase); // by path, once
    private string? _stopped;

    /// <summary>How often the shell asked before deleting something.</summary>
    public int PreDeletes { get; private set; }

    /// <summary>Registers an item to be handed over; null when it is, holds or lies inside one already registered.</summary>
    public Item? Expect(string path)
    {
        var item = new Item(path, ShellRecycler.Key(path));
        lock (_lock)
        {
            if (_holders.Contains(item.Key) || Owner(item.Key) is not null) return null;
            _items.Add(item.Key, item);
            for (var parent = System.IO.Path.GetDirectoryName(item.Key); !string.IsNullOrEmpty(parent); parent = System.IO.Path.GetDirectoryName(parent))
                _holders.Add(parent);
            return item;
        }
    }

    /// <summary>An item that was not handed over after all.</summary>
    public void Forget(Item item)
    {
        lock (_lock) _items.Remove(item.Key);
    }

    /// <summary>
    /// The answer before the shell deletes <paramref name="shellItem"/>: S_OK, or <see cref="ShellRecycler.Refuse"/>.
    /// <paramref name="own"/> is the item whose sink asks, null for the sink of the whole operation.
    /// </summary>
    public int PreDelete(uint flags, Item? own, nint shellItem)
    {
        string? key = KeyOf(shellItem);
        lock (_lock)
        {
            PreDeletes++;
            var owner = key is null ? null : Owner(key);
            if (owner is not null) owner.Reached = true;
            if (own is not null) own.Reached = true;
            if (_stopped is not null) return ShellRecycler.Refuse;
            string? refusal =
                (flags & ShellRecycler.TSF_DELETE_RECYCLE_IF_POSSIBLE) == 0 ? CannotTake
                : ct.IsCancellationRequested ? "the job was canceled"
                : key is null ? "the shell named no path for an item it was about to move"
                : owner is null || (own is not null && !ReferenceEquals(owner, own)) ? $"the shell was about to move something else with it: {key}"
                : null;
            if (refusal is null && !owner!.Approved && string.Equals(key, owner.Key, StringComparison.OrdinalIgnoreCase))
            {
                // The item itself, about to move: it must still be exactly what the archive holds.
                try { refusal = recheck(owner.Path) is { } changed ? "checked again just before it moved: " + changed : null; }
                catch (Exception e) { refusal = $"it could not be checked again ({e.Message})"; }
                owner.Approved = refusal is null;
            }
            if (refusal is null) return 0;
            var blame = own ?? owner;
            if (blame is not null) blame.Refusal ??= refusal;
            _stopped = blame is null ? refusal : $"{blame.Path}: {refusal}";
            return ShellRecycler.Refuse;
        }
    }

    /// <summary>What the shell did with <paramref name="shellItem"/>; destroying anything stops the operation.</summary>
    public int PostDelete(Item? own, nint shellItem, int hrDelete, nint newItem)
    {
        string? key = KeyOf(shellItem);
        string? binPath = hrDelete >= 0 && newItem != 0 ? ShellRecycler.PathOf(newItem) : null;
        lock (_lock)
        {
            var owner = key is null ? own : Owner(key);
            if (owner is not null) owner.Reached = true;
            if (hrDelete >= 0 && newItem == 0)
            {
                // Deleted rather than moved to the Recycle Bin (the new item would be the one in the Recycle Bin).
                if (owner is not null) owner.Destroyed = true;
                else
                {
                    string what = key ?? "(an item the shell named no path for)";
                    _others[what] = new RecycleOutcome(what, false, "the operation deleted it rather than moved it to the Recycle Bin");
                }
                _stopped ??= $"{key ?? "an item"} was deleted rather than moved to the Recycle Bin";
                return ShellRecycler.Refuse;
            }
            if (owner is null)
            {
                // Moved although not handed over: reported once, when the shell names it.
                if (hrDelete >= 0 && key is not null)
                    _others.TryAdd(key, new RecycleOutcome(key, true, "the operation moved it to the Recycle Bin, although it was not handed over", binPath));
                return 0;
            }
            if (key is not null && !string.Equals(key, owner.Key, StringComparison.OrdinalIgnoreCase)) return 0; // part of an item
            if (hrDelete < 0)
            {
                owner.Failed = hrDelete;
                _stopped ??= $"{owner.Path}: the shell could not move it";
            }
            else
            {
                owner.Recycled = true;
                owner.BinPath = binPath ?? owner.BinPath;
            }
            return 0;
        }
    }

    /// <summary>What happened to one item, from the shell's reports and from whether it is still where it was.</summary>
    public RecycleOutcome Outcome(Item item, int performed, bool aborted)
    {
        bool inPlace = File.Exists(item.Path) || Directory.Exists(item.Path);
        lock (_lock)
        {
            if (item.Destroyed) return new RecycleOutcome(item.Path, false, "the shell deleted it, or part of it, rather than moved it to the Recycle Bin");
            if (item.Recycled && item.Refusal is null && item.Failed == 0 && !inPlace) return new RecycleOutcome(item.Path, true, null, item.BinPath);
            if (item.Refusal is { } refusal) return new RecycleOutcome(item.Path, false, refusal);
            if (item.Failed != 0) return new RecycleOutcome(item.Path, false, "the shell could not move it: " + ShellRecycler.Describe(item.Failed));
            if (!item.Reached)
                return new RecycleOutcome(item.Path, false, _stopped is { } why ? $"the operation stopped before it ({why})"
                    : ct.IsCancellationRequested ? "the job was canceled"
                    : performed < 0 ? "the operation stopped before it: " + ShellRecycler.Describe(performed)
                    : aborted ? "the operation stopped before it" : "the shell did not move it");
            return new RecycleOutcome(item.Path, false, inPlace ? "the shell did not move it" : "it is gone, but the shell did not report it in the Recycle Bin");
        }
    }

    /// <summary>Anything the operation moved or deleted that was not handed over (it should never happen).</summary>
    public List<RecycleOutcome> Others()
    {
        lock (_lock) return [.. _others.Values];
    }

    /// <summary>The item that is <paramref name="key"/> or holds it.</summary>
    private Item? Owner(string key)
    {
        for (string? p = key; !string.IsNullOrEmpty(p); p = System.IO.Path.GetDirectoryName(p))
            if (_items.TryGetValue(p, out var item)) return item;
        return null;
    }

    private static string? KeyOf(nint shellItem) => ShellRecycler.PathOf(shellItem) is { } path ? ShellRecycler.Key(path) : null;
}

/// <summary>
/// A progress sink of the operation: of one item, or of the whole operation. It hands every question to the
/// operation's <see cref="RecycleGate"/>. The shell may call it on any thread.
/// </summary>
[GeneratedComClass]
internal sealed partial class RecycleSink(RecycleGate gate, RecycleGate.Item? item) : IFileOperationProgressSink
{
    public int PreDeleteItem(uint dwFlags, nint psiItem)
    {
        try { return gate.PreDelete(dwFlags, item, psiItem); }
        catch (Exception) { return ShellRecycler.Refuse; } // whatever went wrong, nothing is deleted
    }

    public int PostDeleteItem(uint dwFlags, nint psiItem, int hrDelete, nint psiNewlyCreated)
    {
        try { return gate.PostDelete(item, psiItem, hrDelete, psiNewlyCreated); }
        catch (Exception) { return ShellRecycler.Refuse; } // stop rather than go on without knowing
    }

    public int StartOperations() => 0;
    public int FinishOperations(int hrResult) => 0;
    public int PreRenameItem(uint dwFlags, nint psiItem, nint pszNewName) => 0;
    public int PostRenameItem(uint dwFlags, nint psiItem, nint pszNewName, int hrRename, nint psiNewlyCreated) => 0;
    public int PreMoveItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName) => 0;
    public int PostMoveItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName, int hrMove, nint psiNewlyCreated) => 0;
    public int PreCopyItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName) => 0;
    public int PostCopyItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName, int hrCopy, nint psiNewlyCreated) => 0;
    public int PreNewItem(uint dwFlags, nint psiDestinationFolder, nint pszNewName) => 0;
    public int PostNewItem(uint dwFlags, nint psiDestinationFolder, nint pszNewName, nint pszTemplateName, uint dwFileAttributes, int hrNew, nint psiNewItem) => 0;
    public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => 0;
    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;
}

// The shell's interfaces, every method in the order of shobjidl_core.h (the vtable order). Every method returns its
// HRESULT. Pointers to strings, shell items and other objects the calls here do not use are passed as nint.

/// <summary>IFileOperation (shobjidl_core.h).</summary>
[GeneratedComInterface]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
internal partial interface IFileOperation
{
    [PreserveSig] int Advise(IFileOperationProgressSink pfops, out uint pdwCookie);
    [PreserveSig] int Unadvise(uint dwCookie);
    [PreserveSig] int SetOperationFlags(uint dwOperationFlags);
    [PreserveSig] int SetProgressMessage(nint pszMessage);
    [PreserveSig] int SetProgressDialog(nint popd);
    [PreserveSig] int SetProperties(nint pproparray);
    [PreserveSig] int SetOwnerWindow(nint hwndOwner);
    [PreserveSig] int ApplyPropertiesToItem(nint psiItem);
    [PreserveSig] int ApplyPropertiesToItems(nint punkItems);
    [PreserveSig] int RenameItem(nint psiItem, nint pszNewName, nint pfopsItem);
    [PreserveSig] int RenameItems(nint pUnkItems, nint pszNewName);
    [PreserveSig] int MoveItem(nint psiItem, nint psiDestinationFolder, nint pszNewName, nint pfopsItem);
    [PreserveSig] int MoveItems(nint punkItems, nint psiDestinationFolder);
    [PreserveSig] int CopyItem(nint psiItem, nint psiDestinationFolder, nint pszCopyName, nint pfopsItem);
    [PreserveSig] int CopyItems(nint punkItems, nint psiDestinationFolder);
    [PreserveSig] int DeleteItem(nint psiItem, IFileOperationProgressSink pfopsItem);
    [PreserveSig] int DeleteItems(nint punkItems);
    [PreserveSig] int NewItem(nint psiDestinationFolder, uint dwFileAttributes, nint pszName, nint pszTemplateName, nint pfopsItem);
    [PreserveSig] int PerformOperations();
    [PreserveSig] int GetAnyOperationsAborted(out int pfAnyOperationsAborted);
}

/// <summary>IFileOperationProgressSink (shobjidl_core.h), implemented by <see cref="RecycleSink"/>.</summary>
[GeneratedComInterface]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
internal partial interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int hrResult);
    [PreserveSig] int PreRenameItem(uint dwFlags, nint psiItem, nint pszNewName);
    [PreserveSig] int PostRenameItem(uint dwFlags, nint psiItem, nint pszNewName, int hrRename, nint psiNewlyCreated);
    [PreserveSig] int PreMoveItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName);
    [PreserveSig] int PostMoveItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName, int hrMove, nint psiNewlyCreated);
    [PreserveSig] int PreCopyItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName);
    [PreserveSig] int PostCopyItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, nint pszNewName, int hrCopy, nint psiNewlyCreated);
    [PreserveSig] int PreDeleteItem(uint dwFlags, nint psiItem);
    [PreserveSig] int PostDeleteItem(uint dwFlags, nint psiItem, int hrDelete, nint psiNewlyCreated);
    [PreserveSig] int PreNewItem(uint dwFlags, nint psiDestinationFolder, nint pszNewName);
    [PreserveSig] int PostNewItem(uint dwFlags, nint psiDestinationFolder, nint pszNewName, nint pszTemplateName, uint dwFileAttributes, int hrNew, nint psiNewItem);
    [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}

/// <summary>IShellItem (shobjidl_core.h).</summary>
[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig] int BindToHandler(nint pbc, nint bhid, nint riid, out nint ppv);
    [PreserveSig] int GetParent(out nint ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, out nint ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(nint psi, uint hint, out int piOrder);
}
