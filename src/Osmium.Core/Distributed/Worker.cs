using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Osmium.Core.Container;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Distributed;

public sealed record WorkerOptions
{
    public required ClusterKey Key { get; init; }
    public int Port { get; init; } = Protocol.DefaultPort;
    public IPAddress Bind { get; init; } = IPAddress.Any;
    public int Slots { get; init; } = Environment.ProcessorCount;
    public long Memory { get; init; } = MemoryBudgets.Default();
    public string Name { get; init; } = Environment.MachineName;
    public bool Discovery { get; init; } = true;
    public Action<string>? Log { get; init; }
}

public sealed record WorkerStats(int Connections, int Running, long TasksDone, long BytesIn, long BytesOut);

/// <summary>
/// A worker node: executes trial batches and block compressions for authenticated coordinators.
/// Workers only ever receive anonymous byte blobs and pipeline specs — never file names, never keys.
/// (They DO see file contents in plaintext; do not lend work to machines you do not trust.)
/// </summary>
public sealed class WorkerServer(WorkerOptions o)
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, o.Slots));
    private readonly SemaphoreSlim _handshakes = new(8);
    private readonly MemoryGate _mem = new(o.Memory);
    private int _connections, _running;
    private long _done, _in, _out;

    public WorkerStats Stats => new(_connections, _running, Interlocked.Read(ref _done), Interlocked.Read(ref _in), Interlocked.Read(ref _out));

    private void Log(string s) => o.Log?.Invoke(s);

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(o.Bind, o.Port);
        listener.Start();
        Log($"worker '{o.Name}' listening on {o.Bind}:{o.Port} with {o.Slots} slot(s), {o.Memory >> 20} MiB budget");
        Task? disc = o.Discovery ? Discovery.RespondAsync(o.Key, o.Port, o.Name, o.Slots, Log, ct) : null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
            if (disc is not null) try { await disc; } catch (OperationCanceledException) { }
        }
    }

    private sealed class Incoming(ulong id, byte kind, string[] specs, byte[] buffer)
    {
        public ulong Id = id;
        public byte Kind = kind;
        public string[] Specs = specs;
        public byte[] Buffer = buffer;
        public int Filled;
        public CancellationTokenSource Cts = new();
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        client.NoDelay = true;
        SecureChannel ch;
        if (!await _handshakes.WaitAsync(0, ct)) { client.Dispose(); return; }
        try
        {
            using var hsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            ch = await SecureChannel.AcceptAsync(client.GetStream(), o.Key, o.Name, o.Slots, o.Memory, hsTimeout.Token);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or SocketException or System.Security.Cryptography.CryptographicException)
        {
            Log($"rejected {remote}: {e.Message}");
            client.Dispose();
            await Task.Delay(1000, CancellationToken.None); // slow down guessing
            return;
        }
        finally { _handshakes.Release(); }

        Interlocked.Increment(ref _connections);
        Log($"coordinator '{ch.PeerName}' connected from {remote}");
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new ConcurrentDictionary<ulong, Incoming>();
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!connCts.IsCancellationRequested)
                {
                    await Task.Delay(2000, connCts.Token);
                    await ch.SendAsync(Protocol.Heartbeat, BitConverter.GetBytes(_running), connCts.Token);
                }
            }
            catch (Exception) { connCts.Cancel(); }
        });
        try
        {
            while (!connCts.IsCancellationRequested)
            {
                var (type, body) = await ch.ReceiveAsync(connCts.Token);
                switch (type)
                {
                    case Protocol.Heartbeat: break;
                    case Protocol.Bye: return;
                    case Protocol.TaskBegin:
                    {
                        var r = new BlobReader(body);
                        ulong id = r.U64();
                        byte kind = r.B();
                        long len = (long)r.U();
                        int n = r.Int(1024);
                        var specs = new string[n];
                        for (int i = 0; i < n; i++) specs[i] = r.Str(8192);
                        if (len > BlockLimits.MaxRawSize) { await SendFailure(ch, id, "input too large", connCts.Token); break; }
                        tasks[id] = new Incoming(id, kind, specs, new byte[len]);
                        break;
                    }
                    case Protocol.TaskData:
                    {
                        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(body);
                        if (!tasks.TryGetValue(id, out var t)) break;
                        int n = body.Length - 8;
                        if (t.Filled + n > t.Buffer.Length) throw new InvalidDataException("Task data overflow.");
                        body.AsSpan(8).CopyTo(t.Buffer.AsSpan(t.Filled));
                        t.Filled += n;
                        break;
                    }
                    case Protocol.TaskEnd:
                    {
                        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(body);
                        if (tasks.TryGetValue(id, out var t)) _ = ExecuteAsync(ch, t, tasks, connCts.Token);
                        break;
                    }
                    case Protocol.TaskCancel:
                    {
                        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(body);
                        if (tasks.TryRemove(id, out var t)) t.Cts.Cancel();
                        break;
                    }
                    default: throw new InvalidDataException($"Unknown message 0x{type:X2}.");
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or SocketException or EndOfStreamException)
        {
            Log($"coordinator '{ch.PeerName}' disconnected: {e.Message}");
        }
        finally
        {
            connCts.Cancel();
            foreach (var t in tasks.Values) t.Cts.Cancel();
            Interlocked.Decrement(ref _connections);
            await ch.DisposeAsync();
            client.Dispose();
        }
    }

    private static async Task SendFailure(SecureChannel ch, ulong id, string error, CancellationToken ct)
    {
        var w = new BlobWriter().U64(id).B(1).Str(error).ToArray();
        await ch.SendAsync(Protocol.ResultBegin, w, ct);
        await ch.SendAsync(Protocol.ResultEnd, BitConverter.GetBytes(id), ct);
    }

    private async Task ExecuteAsync(SecureChannel ch, Incoming t, ConcurrentDictionary<ulong, Incoming> tasks, CancellationToken connCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(connCt, t.Cts.Token);
        var ct = linked.Token;
        try
        {
            if (t.Filled != t.Buffer.Length) { await SendFailure(ch, t.Id, "incomplete input", connCt); return; }
            var reg = MethodRegistry.Default;
            var specs = t.Specs.Select(s => PipelineSpec.Parse(s, reg)).ToList();
            long mem = specs.Max(s => s.EncodeMemory(t.Buffer.Length));
            if (mem > o.Memory) { await SendFailure(ch, t.Id, $"needs {mem >> 20} MiB, worker allows {o.Memory >> 20} MiB", connCt); return; }
            Interlocked.Add(ref _in, t.Buffer.Length);
            if (t.Kind == Protocol.KindTrials)
            {
                var sizes = new long[specs.Count];
                await Parallel.ForEachAsync(Enumerable.Range(0, specs.Count), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = o.Slots }, async (i, token) =>
                {
                    await _slots.WaitAsync(token);
                    Interlocked.Increment(ref _running);
                    try
                    {
                        using var lease = _mem.Acquire(specs[i].EncodeMemory(t.Buffer.Length), token);
                        try { sizes[i] = PipelineExecutor.TrialSize(specs[i], t.Buffer, new CodecContext { Token = token }); }
                        catch (Exception e) when (e is not OperationCanceledException) { sizes[i] = -1; }
                    }
                    finally { Interlocked.Decrement(ref _running); _slots.Release(); }
                });
                var w = new BlobWriter().U64(t.Id).B(0).Str("").U((ulong)sizes.Length);
                foreach (var s in sizes) w.I(s);
                await ch.SendAsync(Protocol.ResultBegin, w.ToArray(), connCt);
                await ch.SendAsync(Protocol.ResultEnd, BitConverter.GetBytes(t.Id), connCt);
            }
            else
            {
                await _slots.WaitAsync(ct);
                Interlocked.Increment(ref _running);
                try
                {
                    using var lease = _mem.Acquire(mem, ct);
                    var (desc, payload) = PipelineExecutor.Encode(specs[0], t.Buffer, new CodecContext { Token = ct });
                    // Workers verify their own output before returning it; the coordinator verifies again.
                    var back = PipelineExecutor.Decode(desc, payload, new CodecContext { Token = ct });
                    if (!back.AsSpan().SequenceEqual(t.Buffer)) { await SendFailure(ch, t.Id, "self-verification failed", connCt); return; }
                    var header = new BlobWriter().U64(t.Id).B(0).Str("").Bytes(desc.Serialize()).U((ulong)payload.Length).ToArray();
                    await ch.SendLargeAsync(Protocol.ResultBegin, header, Protocol.ResultData, Protocol.ResultEnd, t.Id, payload, connCt);
                    Interlocked.Add(ref _out, payload.Length);
                }
                finally { Interlocked.Decrement(ref _running); _slots.Release(); }
            }
            Interlocked.Increment(ref _done);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or ArgumentException)
        {
            try { await SendFailure(ch, t.Id, e.Message, connCt); } catch (Exception) { }
        }
        finally
        {
            tasks.TryRemove(t.Id, out _);
            t.Buffer = [];
        }
    }
}
