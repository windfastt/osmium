using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Osmium.Desktop.Ipc;

/// <summary>Message sent from a short-lived secondary instance (e.g. an Explorer verb) to the running tray app.</summary>
public sealed record IpcMessage
{
    public int V { get; init; } = 1;
    /// <summary>invoke | show | quit | ping</summary>
    public string Type { get; init; } = "invoke";
    /// <summary>compress-dialog | compress-quick | extract-here | extract-to | open | test | sendto</summary>
    public string? Verb { get; init; }
    public string? Profile { get; init; }
    public string[] Args { get; init; } = [];
    public string? Cwd { get; init; }
    public string? Page { get; init; }
}

public static class IpcProtocol
{
    public const int MaxMessage = 4 << 20;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string PipeName(string userKey, int sessionId) => $"Osmium.App.{Sanitize(userKey)}.{sessionId}";

    private static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

    public static async Task WriteAsync(Stream s, IpcMessage m, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(m, Json);
        if (body.Length > MaxMessage) throw new InvalidDataException("IPC message too large.");
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, body.Length);
        await s.WriteAsync(len, ct);
        await s.WriteAsync(body, ct);
        await s.FlushAsync(ct);
    }

    public static async Task<IpcMessage?> ReadAsync(Stream s, CancellationToken ct)
    {
        var len = new byte[4];
        try { await s.ReadExactlyAsync(len, ct); } catch (EndOfStreamException) { return null; }
        int n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n <= 0 || n > MaxMessage) throw new InvalidDataException("Invalid IPC message length.");
        var body = new byte[n];
        await s.ReadExactlyAsync(body, ct);
        var m = JsonSerializer.Deserialize<IpcMessage>(body, Json) ?? throw new InvalidDataException("Empty IPC message.");
        // Only absolute paths are accepted: a relative path would be resolved against the tray app's directory.
        if (m.Args.Any(a => !Path.IsPathFullyQualified(a))) throw new InvalidDataException("IPC paths must be absolute.");
        return m;
    }

    /// <summary>Forwards a message to the primary instance. Returns false if no primary instance answered.</summary>
    public static async Task<bool> ForwardAsync(string pipeName, IpcMessage m, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            await using var c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await c.ConnectAsync((int)timeout.TotalMilliseconds, ct);
            await WriteAsync(c, m, ct);
            var reply = await ReadAsync(c, ct);
            return reply?.Type == "ok";
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }
}

/// <summary>
/// Pipe server for the primary instance. <paramref name="createPipe"/> lets the Windows app supply a pipe with a
/// restrictive ACL (current user only, no network SID); tests use the default.
/// </summary>
public sealed class IpcServer(string pipeName, Func<IpcMessage, Task> onMessage, Func<string, NamedPipeServerStream>? createPipe = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private NamedPipeServerStream Create() =>
        createPipe?.Invoke(pipeName) ?? new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = Create();
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(100, CancellationToken.None); continue; }
            _ = HandleAsync(pipe, ct);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                var m = await IpcProtocol.ReadAsync(pipe, ct);
                if (m is null) return;
                if (m.V != 1) { await IpcProtocol.WriteAsync(pipe, new IpcMessage { Type = "error", Verb = "version" }, ct); return; }
                if (m.Type != "ping") await onMessage(m);
                await IpcProtocol.WriteAsync(pipe, new IpcMessage { Type = "ok" }, ct);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) try { await _loop; } catch (OperationCanceledException) { }
    }
}

public sealed record InvocationBatch(string Verb, string? Profile, IReadOnlyList<string> Paths);

/// <summary>
/// Explorer starts one process per selected file even with MultiSelectModel=Player. Invocations with the same
/// (verb, profile) arriving within a sliding window are merged into one batch (BLUEPRINT §12.3).
/// </summary>
public sealed class InvocationCoalescer(TimeSpan window, TimeSpan maxWait, int maxPaths = 10_000, Func<DateTime>? clock = null)
{
    private sealed class Pending
    {
        public required string Verb;
        public string? Profile;
        public DateTime Opened, Last;
        public readonly List<string> Paths = [];
        public readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<(string, string?), Pending> _pending = [];
    private readonly object _lock = new();

    public void Add(IpcMessage m)
    {
        if (m.Verb is null) return;
        lock (_lock)
        {
            var key = (m.Verb, m.Profile);
            var now = _now();
            if (!_pending.TryGetValue(key, out var p)) _pending[key] = p = new Pending { Verb = m.Verb, Profile = m.Profile, Opened = now };
            p.Last = now;
            foreach (var a in m.Args)
            {
                var full = Path.GetFullPath(a);
                if (p.Paths.Count < maxPaths && p.Seen.Add(full)) p.Paths.Add(full);
            }
        }
    }

    public List<InvocationBatch> Due()
    {
        var due = new List<InvocationBatch>();
        lock (_lock)
        {
            var now = _now();
            foreach (var (key, p) in _pending.ToList())
            {
                if (now - p.Last >= window || now - p.Opened >= maxWait || p.Paths.Count >= maxPaths)
                {
                    due.Add(new InvocationBatch(p.Verb, p.Profile, p.Paths));
                    _pending.Remove(key);
                }
            }
        }
        return due;
    }

    /// <summary>Output archive name for a quick-compress batch (one item, common parent, or dated name).</summary>
    public static string ArchiveNameFor(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1)
        {
            var p = paths[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dir = Path.GetDirectoryName(p)!;
            var name = File.Exists(p) ? Path.GetFileNameWithoutExtension(p) : Path.GetFileName(p);
            return Unique(Path.Combine(dir, name + ".osa"));
        }
        var parents = paths.Select(p => Path.GetDirectoryName(p.TrimEnd(Path.DirectorySeparatorChar))!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (parents.Count == 1)
        {
            var parentName = Path.GetFileName(parents[0].TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(parentName)) parentName = "Archive";
            return Unique(Path.Combine(parents[0], parentName + ".osa"));
        }
        return Unique(Path.Combine(parents[0], $"Archive {DateTime.Now:yyyy-MM-dd HHmm}.osa"));
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var c = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(c)) return c;
        }
    }
}
