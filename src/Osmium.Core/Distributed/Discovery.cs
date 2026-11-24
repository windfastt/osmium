using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Osmium.Core.IO;

namespace Osmium.Core.Distributed;

public sealed record DiscoveredWorker(string Endpoint, string Name, int Slots);

/// <summary>
/// LAN discovery over UDP. Probes and answers carry the cluster tag, a timestamp, a nonce and an HMAC under a key
/// derived from the pairing key, so workers only answer members of their own cluster and replays are rejected.
/// </summary>
public static class Discovery
{
    private static byte[] Mac(ClusterKey key, ReadOnlySpan<byte> data) => HMACSHA256.HashData(key.DiscoveryKey, data)[..16];

    public static async Task<List<DiscoveredWorker>> ProbeAsync(ClusterKey key, TimeSpan wait, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var nonce = RandomNumberGenerator.GetBytes(16);
        var body = new BlobWriter().Raw("OSMD"u8).B(1).B(1).Raw(key.Tag).Raw(nonce).U64((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToArray();
        var probe = body.Concat(Mac(key, body)).ToArray();
        foreach (var target in BroadcastAddresses())
            try { await udp.SendAsync(probe, new IPEndPoint(target, Protocol.DiscoveryPort), ct); } catch (SocketException) { }

        var found = new Dictionary<string, DiscoveredWorker>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            while (true)
            {
                var res = await udp.ReceiveAsync(timeout.Token);
                var b = res.Buffer;
                if (b.Length < 16 + 8) continue;
                var data = b.AsSpan(0, b.Length - 16);
                if (!CryptographicOperations.FixedTimeEquals(Mac(key, data), b.AsSpan(b.Length - 16))) continue;
                var r = new BlobReader(data);
                if (!r.Raw(4).SequenceEqual("OSMD"u8) || r.B() != 1 || r.B() != 2) continue;
                if (!r.Raw(8).SequenceEqual(key.Tag) || !r.Raw(16).SequenceEqual(nonce)) continue;
                int port = r.Int(65535);
                string name = r.Str(128);
                int slots = r.Int(4096);
                var ep = $"{res.RemoteEndPoint.Address}:{port}";
                found[ep] = new DiscoveredWorker(ep, name, slots);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return found.Values.ToList();
    }

    public static async Task RespondAsync(ClusterKey key, int tcpPort, string name, int slots, Action<string>? log, CancellationToken ct)
    {
        UdpClient udp;
        try { udp = new UdpClient(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort)); }
        catch (SocketException e) { log?.Invoke($"discovery disabled: {e.Message}"); return; }
        using (udp)
        {
            var seen = new ConcurrentDictionary<string, DateTime>();
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try { res = await udp.ReceiveAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { continue; }
                var b = res.Buffer;
                if (b.Length != 4 + 2 + 8 + 16 + 8 + 16) continue;
                var data = b.AsSpan(0, b.Length - 16);
                if (!CryptographicOperations.FixedTimeEquals(Mac(key, data), b.AsSpan(b.Length - 16))) continue;
                var r = new BlobReader(data);
                if (!r.Raw(4).SequenceEqual("OSMD"u8) || r.B() != 1 || r.B() != 1) continue;
                if (!r.Raw(8).SequenceEqual(key.Tag)) continue;
                var nonce = r.Raw(16).ToArray();
                long ts = (long)r.U64();
                if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300) continue;
                string nk = Convert.ToHexString(nonce);
                if (!seen.TryAdd(nk, DateTime.UtcNow)) continue; // replay
                foreach (var old in seen.Where(kv => (DateTime.UtcNow - kv.Value).TotalMinutes > 10).Select(kv => kv.Key).ToList()) seen.TryRemove(old, out _);
                var body = new BlobWriter().Raw("OSMD"u8).B(1).B(2).Raw(key.Tag).Raw(nonce).U((ulong)tcpPort).Str(name).U((ulong)slots).ToArray();
                try { await udp.SendAsync(body.Concat(Mac(key, body)).ToArray(), res.RemoteEndPoint, ct); } catch (SocketException) { }
            }
        }
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        yield return IPAddress.Loopback;
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch (NetworkInformationException) { yield break; }
        foreach (var nic in nics.Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bc = new byte[4];
                for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(bc);
            }
    }
}
