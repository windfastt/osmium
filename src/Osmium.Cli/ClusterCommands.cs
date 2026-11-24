using Osmium.Core.Container;
using Osmium.Core.Distributed;
using Osmium.Core.Planning;

namespace Osmium.Cli;

/// <summary>Connects to workers for a compression job (--workers host:port,…|auto --cluster-key OSM1-…).</summary>
internal sealed class ClusterSetup : IAsyncDisposable
{
    private readonly Cluster _cluster;
    public ITrialRunner Trials { get; }
    public IBlockCompressor Compressor { get; }
    public int ExtraSlots => _cluster.RemoteSlots;

    private ClusterSetup(Cluster c, long budget, bool shipSymmetric)
    {
        _cluster = c;
        Trials = c.TrialRunner(new MemoryGate(budget));
        Compressor = c.BlockCompressor(shipSymmetric);
    }

    public static async Task<ClusterSetup?> CreateAsync(Args a, PlanOptions po, CancellationToken ct)
    {
        var spec = a.Get("workers");
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var keyText = a.Get("cluster-key") ?? Environment.GetEnvironmentVariable("OSMIUM_CLUSTER_KEY")
            ?? throw new ArgumentException("--workers needs --cluster-key (or OSMIUM_CLUSTER_KEY). Create one with 'osmium keygen'.");
        if ((a.Has("password") || a.Has("password-stdin")) && !a.Has("allow-remote-encrypted"))
        {
            Console.Error.WriteLine("note: remote workers are disabled for encrypted archives (they would see plaintext). Use --allow-remote-encrypted to override.");
            return null;
        }
        var key = ClusterKey.Parse(keyText);
        var endpoints = new List<string>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var found = await Discovery.ProbeAsync(key, TimeSpan.FromSeconds(1.5), ct);
                Console.WriteLine($"discovery: {found.Count} worker(s) found" + (found.Count > 0 ? ": " + string.Join(", ", found.Select(f => $"{f.Name}@{f.Endpoint}")) : ""));
                endpoints.AddRange(found.Select(f => f.Endpoint));
            }
            else endpoints.Add(part);
        }
        var cluster = await Cluster.ConnectAsync(endpoints, key, po.Threads, s => Console.WriteLine("cluster: " + s), ct);
        if (cluster.Nodes.Count == 0) { Console.WriteLine("cluster: no usable workers; running locally"); return null; }
        return new ClusterSetup(cluster, po.MemoryBudget, a.Has("ship-symmetric"));
    }

    public ValueTask DisposeAsync() => _cluster.DisposeAsync();
}

internal static class WorkerCommand
{
    public static int KeyGen()
    {
        Console.WriteLine(ClusterKey.Generate());
        Console.Error.WriteLine("Share this key with every machine in the cluster (osmium worker --key … / osmium a … --cluster-key …).");
        Console.Error.WriteLine("Anyone with the key can send this machine work and will see the data being compressed.");
        return 0;
    }

    public static async Task<int> RunAsync(Args a, CancellationToken ct)
    {
        // A worker is only useful with the key the other PCs already use: a fresh random key on every start (the
        // old behaviour) was never saved and matched nobody.
        var keyText = a.Get("key") ?? Environment.GetEnvironmentVariable("OSMIUM_CLUSTER_KEY");
        if (string.IsNullOrWhiteSpace(keyText))
            throw new ArgumentException(
                "osmium worker needs the cluster key that your other PCs use. Create one with 'osmium keygen' " +
                "(or copy it from the Cluster page of the Osmium app), then run: osmium worker --key OSM1-… " +
                "(or set OSMIUM_CLUSTER_KEY).");
        var server = new WorkerServer(new WorkerOptions
        {
            Key = ClusterKey.Parse(keyText),
            Port = a.Int("port", Protocol.DefaultPort),
            Slots = a.Int("slots", Environment.ProcessorCount),
            Memory = a.Size("mem", MemoryBudgets.Default()),
            Name = a.Get("name", Environment.MachineName),
            Discovery = !a.Has("no-discovery"),
            Log = s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}"),
        });
        await server.RunAsync(ct);
        return 0;
    }
}
