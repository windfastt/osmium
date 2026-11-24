using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using Osmium.Core.Container;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;

namespace Osmium.Core.Distributed;

/// <summary>A connected remote worker as seen by the coordinator.</summary>
public sealed class RemoteNode : IAsyncDisposable
{
    private readonly SecureChannel _ch;
    private readonly TcpClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<ulong, Pending> _pending = new();
    private long _nextId;
    private int _busy;
    private DateTime _lastSeen = DateTime.UtcNow;
    private readonly Action<string>? _log;

    public string Endpoint { get; }
    public string Name => _ch.PeerName;
    public int Slots => _ch.PeerSlots;
    public long Memory => _ch.PeerMemory;
    public int Busy => _busy;
    public bool Dead { get; private set; }
    public bool Quarantined { get; set; }
    public bool Usable => !Dead && !Quarantined;
    public long TasksDone;

    private sealed class Pending
    {
        public TaskCompletionSource<(byte Status, string Error, byte[] Header, byte[] Data)> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Header = [];
        public MemoryStream Data = new();
    }

    private RemoteNode(string endpoint, TcpClient client, SecureChannel ch, Action<string>? log)
    {
        Endpoint = endpoint; _client = client; _ch = ch; _log = log;
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(HeartbeatAsync);
    }

    public static async Task<RemoteNode> ConnectAsync(string endpoint, ClusterKey key, Action<string>? log, CancellationToken ct)
    {
        var (host, port) = ParseEndpoint(endpoint);
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            var ch = await SecureChannel.ConnectAsync(client.GetStream(), key, Environment.MachineName, timeout.Token);
            return new RemoteNode($"{host}:{port}", client, ch, log);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static (string Host, int Port) ParseEndpoint(string ep)
    {
        int colon = ep.LastIndexOf(':');
        return colon > 0 && int.TryParse(ep[(colon + 1)..], out int p) ? (ep[..colon].Trim('[', ']'), p) : (ep, Protocol.DefaultPort);
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(2000, _cts.Token);
                await _ch.SendAsync(Protocol.Heartbeat, ReadOnlyMemory<byte>.Empty, _cts.Token);
                if ((DateTime.UtcNow - _lastSeen).TotalSeconds > 10) Fail("no heartbeat for 10 s");
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested || Dead) { }
        catch (Exception e) { Fail(e.Message); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (type, body) = await _ch.ReceiveAsync(_cts.Token);
                _lastSeen = DateTime.UtcNow;
                switch (type)
                {
                    case Protocol.Heartbeat: break;
                    case Protocol.ResultBegin:
                    {
                        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(body);
                        if (_pending.TryGetValue(id, out var p)) p.Header = body;
                        break;
                    }
                    case Protocol.ResultData:
                    {
                        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(body);
                        if (_pending.TryGetValue(id, out var p)) p.Data.Write(body, 8, body.Length - 8);
                        break;
                    }
                    case Protocol.ResultEnd:
                    {
                        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(body);
                        if (_pending.TryRemove(id, out var p))
                        {
                            var r = new BlobReader(p.Header);
                            r.U64();
                            byte status = r.B();
                            string err = r.Str(4096);
                            p.Tcs.TrySetResult((status, err, p.Header[r.Position..], p.Data.ToArray()));
                        }
                        break;
                    }
                    default: throw new InvalidDataException($"Unexpected message 0x{type:X2} from worker.");
                }
            }
        }
        catch (Exception e) when (!_cts.IsCancellationRequested) { Fail(e.Message); }
        catch (Exception) { }
    }

    private void Fail(string why)
    {
        if (Dead) return;
        Dead = true;
        _log?.Invoke($"worker {Name} ({Endpoint}) lost: {why}");
        foreach (var p in _pending.Values) p.Tcs.TrySetException(new IOException($"worker {Name} lost: {why}"));
        _pending.Clear();
        _cts.Cancel();
    }

    private async Task<(byte Status, string Error, byte[] Header, byte[] Data)> CallAsync(byte kind, IReadOnlyList<PipelineSpec> specs, byte[] input, CancellationToken ct)
    {
        if (Dead) throw new IOException("worker is not connected");
        ulong id = (ulong)Interlocked.Increment(ref _nextId);
        var p = new Pending();
        _pending[id] = p;
        Interlocked.Increment(ref _busy);
        try
        {
            var h = new BlobWriter().U64(id).B(kind).U((ulong)input.Length).U((ulong)specs.Count);
            foreach (var s in specs) h.Str(s.ToString());
            await _ch.SendLargeAsync(Protocol.TaskBegin, h.ToArray(), Protocol.TaskData, Protocol.TaskEnd, id, input, ct);
            using var reg = ct.Register(() =>
            {
                _ = _ch.SendAsync(Protocol.TaskCancel, BitConverter.GetBytes(id), CancellationToken.None);
                p.Tcs.TrySetCanceled();
            });
            var result = await p.Tcs.Task;
            Interlocked.Increment(ref TasksDone);
            return result;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            Interlocked.Decrement(ref _busy);
        }
    }

    public async Task<long[]> TrialsAsync(IReadOnlyList<PipelineSpec> specs, byte[] sample, CancellationToken ct)
    {
        var (status, err, header, _) = await CallAsync(Protocol.KindTrials, specs, sample, ct);
        if (status != 0) throw new IOException($"worker {Name}: {err}");
        var r = new BlobReader(header);
        int n = r.Int(1024);
        if (n != specs.Count) throw new InvalidDataException("worker returned a wrong number of results");
        var sizes = new long[n];
        for (int i = 0; i < n; i++) sizes[i] = r.I();
        return sizes;
    }

    public async Task<CompressedBlock> CompressAsync(PipelineSpec spec, byte[] raw, CancellationToken ct)
    {
        var (status, err, header, data) = await CallAsync(Protocol.KindCompress, [spec], raw, ct);
        if (status != 0) throw new IOException($"worker {Name}: {err}");
        var r = new BlobReader(header);
        var desc = BlockDescriptor.Deserialize(r.Bytes(1 << 16), MethodRegistry.Default);
        long len = (long)r.U();
        if (len != data.Length) throw new InvalidDataException("worker result length mismatch");
        return new CompressedBlock(desc, data, Name);
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!Dead) await _ch.SendAsync(Protocol.Bye, ReadOnlyMemory<byte>.Empty, CancellationToken.None); } catch (Exception) { }
        Dead = true;
        _cts.Cancel();
        await _ch.DisposeAsync();
        _client.Dispose();
    }
}

/// <summary>A set of connected workers plus the local machine.</summary>
public sealed class Cluster : IAsyncDisposable
{
    public List<RemoteNode> Nodes { get; } = [];
    public int LocalSlots { get; init; } = Environment.ProcessorCount;
    public Action<string>? Log { get; init; }
    public int RemoteSlots => Nodes.Where(n => n.Usable).Sum(n => n.Slots);

    public static async Task<Cluster> ConnectAsync(IEnumerable<string> endpoints, ClusterKey key, int localSlots, Action<string>? log, CancellationToken ct)
    {
        var c = new Cluster { LocalSlots = localSlots, Log = log };
        foreach (var ep in endpoints.Distinct())
        {
            try
            {
                var n = await RemoteNode.ConnectAsync(ep, key, log, ct);
                c.Nodes.Add(n);
                log?.Invoke($"connected to worker {n.Name} ({n.Endpoint}): {n.Slots} slot(s), {n.Memory >> 20} MiB");
            }
            catch (Exception e) when (e is IOException or SocketException or InvalidDataException or OperationCanceledException)
            {
                log?.Invoke($"cannot use worker {ep}: {e.Message}");
            }
        }
        return c;
    }

    public ITrialRunner TrialRunner(MemoryGate gate, double spotCheckRate = 0.05) => new DistributedTrialRunner(this, new LocalTrialRunner(LocalSlots, gate), spotCheckRate);
    /// <param name="shipSymmetric">Experimental: also ship CM/PPMd blocks (their local verification costs as much as compressing).</param>
    public IBlockCompressor BlockCompressor(bool shipSymmetric = false) => new DistributedBlockCompressor(this, shipSymmetric);

    public async ValueTask DisposeAsync()
    {
        foreach (var n in Nodes) await n.DisposeAsync();
    }
}

/// <summary>Splits trial batches across the local machine and remote workers in proportion to free slots.
/// A deterministic ~5 % of remote results are recomputed locally; any mismatch quarantines the worker.</summary>
public sealed class DistributedTrialRunner(Cluster cluster, LocalTrialRunner local, double spotCheckRate) : ITrialRunner
{
    public async Task<long[]> RunAsync(IReadOnlyList<PipelineSpec> specs, byte[] sample, CancellationToken ct)
    {
        var nodes = cluster.Nodes.Where(n => n.Usable && specs.All(s => s.EncodeMemory(sample.Length) <= n.Memory)).ToList();
        if (nodes.Count == 0 || specs.Count < 2) return await local.RunAsync(specs, sample, ct);
        var result = new long[specs.Count];
        var assignment = new List<int>[nodes.Count + 1];
        for (int i = 0; i < assignment.Length; i++) assignment[i] = [];
        // weighted round-robin: slot 0 is local
        var weights = new[] { cluster.LocalSlots }.Concat(nodes.Select(n => Math.Max(1, n.Slots - n.Busy))).ToArray();
        var credit = new double[weights.Length];
        for (int i = 0; i < specs.Count; i++)
        {
            int best = 0;
            for (int k = 0; k < weights.Length; k++)
            {
                credit[k] += weights[k];
                if (credit[k] > credit[best]) best = k;
            }
            credit[best] -= weights.Sum();
            assignment[best].Add(i);
        }

        async Task RunLocal(List<int> idx)
        {
            if (idx.Count == 0) return;
            var sizes = await local.RunAsync(idx.Select(i => specs[i]).ToList(), sample, ct);
            for (int j = 0; j < idx.Count; j++) result[idx[j]] = sizes[j];
        }

        async Task RunRemote(RemoteNode node, List<int> idx)
        {
            if (idx.Count == 0) return;
            var sub = idx.Select(i => specs[i]).ToList();
            long[] sizes;
            try { sizes = await node.TrialsAsync(sub, sample, ct); }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                cluster.Log?.Invoke($"trials on {node.Name} failed ({e.Message}); running locally");
                await RunLocal(idx);
                return;
            }
            // deterministic spot checks
            var check = idx.Select((specIdx, j) => (specIdx, j)).Where(t => sub[t.j].Deterministic && (sub[t.j].Hash % 1000) < spotCheckRate * 1000).ToList();
            if (check.Count > 0)
            {
                var local2 = await local.RunAsync(check.Select(t => sub[t.j]).ToList(), sample, ct);
                for (int k = 0; k < check.Count; k++)
                {
                    if (local2[k] != sizes[check[k].j])
                    {
                        node.Quarantined = true;
                        cluster.Log?.Invoke($"worker {node.Name} returned a wrong trial result for {sub[check[k].j]}; quarantined");
                        await RunLocal(idx);
                        return;
                    }
                }
            }
            for (int j = 0; j < idx.Count; j++) result[idx[j]] = sizes[j];
        }

        var tasks = new List<Task> { RunLocal(assignment[0]) };
        for (int k = 0; k < nodes.Count; k++) tasks.Add(RunRemote(nodes[k], assignment[k + 1]));
        await Task.WhenAll(tasks);
        return result;
    }
}

/// <summary>Ships asymmetric-codec blocks (cheap to verify) to idle workers; verifies every result locally.</summary>
public sealed class DistributedBlockCompressor(Cluster cluster, bool shipSymmetric = false) : IBlockCompressor
{
    private int _localBusy;

    public static bool WorthShipping(PipelineSpec s) =>
        s.EncodeSpeed() > 0 && s.Codec.Method.DecodeSpeed(s.Codec.Params) / s.Codec.Method.EncodeSpeed(s.Codec.Params) >= 4;

    public async Task<CompressedBlock> CompressAsync(PipelineSpec spec, byte[] raw, CodecContext ctx)
    {
        if ((shipSymmetric || WorthShipping(spec)) && Volatile.Read(ref _localBusy) >= cluster.LocalSlots)
        {
            var node = cluster.Nodes.Where(n => n.Usable && n.Busy < n.Slots && spec.EncodeMemory(raw.Length) <= n.Memory)
                .OrderBy(n => (double)n.Busy / n.Slots).FirstOrDefault();
            if (node is not null)
            {
                try
                {
                    var c = await node.CompressAsync(spec, raw, ctx.Token);
                    var back = PipelineExecutor.Decode(c.Descriptor, c.Payload, new CodecContext { Token = ctx.Token });
                    if (c.Descriptor.Spec.Equals(spec) && back.AsSpan().SequenceEqual(raw)) return c;
                    node.Quarantined = true;
                    cluster.Log?.Invoke($"worker {node.Name} returned a block that does not decode correctly; quarantined");
                }
                catch (Exception e) when (e is IOException or InvalidDataException)
                {
                    cluster.Log?.Invoke($"block on {node.Name} failed ({e.Message}); compressing locally");
                }
            }
        }
        Interlocked.Increment(ref _localBusy);
        try { return await LocalBlockCompressor.Instance.CompressAsync(spec, raw, ctx); }
        finally { Interlocked.Decrement(ref _localBusy); }
    }
}

internal static class SpecSpeed
{
    public static double EncodeSpeed(this PipelineSpec s) => s.Codec.Method.EncodeSpeed(s.Codec.Params);
}
