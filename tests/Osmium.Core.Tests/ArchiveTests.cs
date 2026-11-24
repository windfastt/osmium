using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Osmium.Core.Codecs.Cm;
using Osmium.Core.Container;
using Osmium.Core.Distributed;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "osmium-test-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string Sub(string p) => System.IO.Path.Combine(Path, p);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class ArchiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void MakeTree(string root)
    {
        var rnd = new Random(42);
        Directory.CreateDirectory(System.IO.Path.Combine(root, "src", "deep", "er"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "empty-dir"));
        var text = string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i}: the quick brown fox {i % 17}\n"));
        File.WriteAllText(System.IO.Path.Combine(root, "src", "a.txt"), text);
        File.WriteAllText(System.IO.Path.Combine(root, "src", "deep", "copy-of-a.txt"), text); // duplicate
        File.WriteAllBytes(System.IO.Path.Combine(root, "src", "empty.bin"), []);
        var rand = new byte[300_000]; rnd.NextBytes(rand);
        File.WriteAllBytes(System.IO.Path.Combine(root, "src", "random.bin"), rand);
        var rec = new byte[200_000];
        for (int i = 0; i < rec.Length / 4; i++) BitConverter.TryWriteBytes(rec.AsSpan(i * 4), i * 3 + rnd.Next(3));
        File.WriteAllBytes(System.IO.Path.Combine(root, "src", "deep", "er", "records.dat"), rec);
        File.WriteAllText(System.IO.Path.Combine(root, "src", "ünïcödé — файл.txt"), "unicode name");
    }

    private static async Task<BuildResult> Compress(string input, string output, string profile, string? password = null,
        DedupMode? dedup = null, long? blockCap = null)
    {
        var scan = Scanner.Scan([input]);
        var plan = await new Planner(new PlanOptions { Profile = Profile.ByName(profile), Dedup = dedup, BlockCap = blockCap, MemoryBudget = 2L << 30 }).BuildAsync(scan, Ct);
        return await ArchiveBuilder.BuildAsync(plan, new BuildOptions { OutputPath = output, Password = password, KdfIterations = 1000, MemoryBudget = 2L << 30 }, Ct);
    }

    private static void AssertTreesEqual(string a, string b)
    {
        var fa = Directory.GetFiles(a, "*", SearchOption.AllDirectories).Select(f => System.IO.Path.GetRelativePath(a, f)).Order().ToList();
        var fb = Directory.GetFiles(b, "*", SearchOption.AllDirectories).Select(f => System.IO.Path.GetRelativePath(b, f)).Order().ToList();
        Assert.Equal(fa, fb);
        foreach (var f in fa) Assert.Equal(File.ReadAllBytes(System.IO.Path.Combine(a, f)), File.ReadAllBytes(System.IO.Path.Combine(b, f)));
        var da = Directory.GetDirectories(a, "*", SearchOption.AllDirectories).Select(d => System.IO.Path.GetRelativePath(a, d)).Order();
        var db = Directory.GetDirectories(b, "*", SearchOption.AllDirectories).Select(d => System.IO.Path.GetRelativePath(b, d)).Order();
        Assert.Equal(da, db);
    }

    [Theory]
    [InlineData("store", null)]
    [InlineData("fast", null)]
    [InlineData("balanced", null)]
    [InlineData("max", null)]
    [InlineData("balanced", "chunks")]
    [InlineData("max", "files")]
    public async Task ArchiveRoundTripsTree(string profile, string? dedup)
    {
        using var t = new TempDir();
        MakeTree(t.Sub("in"));
        var mode = dedup is null ? (DedupMode?)null : Enum.Parse<DedupMode>(dedup, true);
        var r = await Compress(t.Sub("in"), t.Sub("a.osa"), profile, dedup: mode, blockCap: 128 << 10);
        Assert.True(File.Exists(r.Path));
        using var reader = OsaReader.Open(r.Path);
        var test = Extractor.Test(reader, null, Ct);
        Assert.Equal(0, test.FilesFailed);
        Assert.Empty(test.Errors);
        var res = Extractor.Extract(reader, t.Sub("out"), null, null, null, Ct);
        Assert.Empty(res.Errors);
        AssertTreesEqual(t.Sub("in"), t.Sub("out/in"));
    }

    [Fact]
    public async Task ChunkDedupStoresDuplicateContentOnce()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Sub("in"));
        var data = new byte[600_000];
        new Random(1).NextBytes(data);
        File.WriteAllBytes(t.Sub("in/one.bin"), data);
        File.WriteAllBytes(t.Sub("in/two.bin"), data.Concat(new byte[] { 1, 2, 3 }).ToArray());
        var r = await Compress(t.Sub("in"), t.Sub("a.osa"), "balanced", dedup: DedupMode.Chunks);
        Assert.True(r.OutputBytes < data.Length * 1.2, $"expected dedup, got {r.OutputBytes}");
        using var reader = OsaReader.Open(r.Path);
        Extractor.Extract(reader, t.Sub("out"), null, null, null, Ct);
        AssertTreesEqual(t.Sub("in"), t.Sub("out/in"));
    }

    [Fact]
    public async Task EncryptedArchiveNeedsTheRightPassword()
    {
        using var t = new TempDir();
        MakeTree(t.Sub("in"));
        var r = await Compress(t.Sub("in"), t.Sub("e.osa"), "balanced", password: "correct horse");
        Assert.Throws<PasswordRequiredException>(() => OsaReader.Open(r.Path));
        Assert.Throws<WrongPasswordException>(() => OsaReader.Open(r.Path, () => "wrong"));
        using var reader = OsaReader.Open(r.Path, () => "correct horse");
        Assert.True(reader.Header.Encrypted);
        Extractor.Extract(reader, t.Sub("out"), null, null, null, Ct);
        AssertTreesEqual(t.Sub("in"), t.Sub("out/in"));
        // file names must not appear in plaintext
        var bytes = File.ReadAllBytes(r.Path);
        Assert.Equal(-1, bytes.AsSpan().IndexOf("records.dat"u8));
    }

    [Fact]
    public async Task EveryCorruptedByteIsDetected()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Sub("in"));
        File.WriteAllText(t.Sub("in/x.txt"), string.Concat(Enumerable.Repeat("osmium corruption test ", 400)));
        var r = await Compress(t.Sub("in"), t.Sub("a.osa"), "balanced");
        var original = File.ReadAllBytes(r.Path);
        int undetected = 0;
        for (int pos = 0; pos < original.Length; pos++)
        {
            var bad = (byte[])original.Clone();
            bad[pos] ^= 0x41;
            File.WriteAllBytes(t.Sub("bad.osa"), bad);
            try
            {
                using var reader = OsaReader.Open(t.Sub("bad.osa"));
                var res = Extractor.Test(reader, null, Ct);
                if (res.FilesFailed == 0 && res.Errors.Count == 0) undetected++;
            }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
        }
        Assert.Equal(0, undetected);
    }

    [Fact]
    public async Task TamperedEncryptedArchiveFailsAuthentication()
    {
        using var t = new TempDir();
        MakeTree(t.Sub("in"));
        var r = await Compress(t.Sub("in"), t.Sub("e.osa"), "fast", password: "pw");
        var b = File.ReadAllBytes(r.Path);
        b[b.Length / 2] ^= 1;
        File.WriteAllBytes(r.Path, b);
        var ex = Record.Exception(() =>
        {
            using var reader = OsaReader.Open(r.Path, () => "pw");
            var res = Extractor.Test(reader, null, Ct);
            if (res.Errors.Count == 0 && res.FilesFailed == 0) throw new Exception("not detected");
        });
        Assert.True(ex is null or IOException or InvalidDataException, ex?.Message);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x")]
    [InlineData("a\\..\\..\\x")]
    [InlineData("CON")]
    [InlineData("nul.tar.gz")]
    [InlineData("COM\u00B9.txt")]
    [InlineData("CONIN$")]
    [InlineData("file.txt:stream")]
    [InlineData("trailing. ")]
    [InlineData("a//b")]
    public void UnsafePathsAreRejected(string path)
    {
        Assert.NotNull(PathSafety.Validate(path));
        Assert.Throws<CorruptDataException>(() => PathSafety.Resolve("/tmp/dest", path));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("dir/sub/file.name.ext")]
    [InlineData("ünïcödé/файл.txt")]
    public void SafePathsAreAccepted(string path) => Assert.Null(PathSafety.Validate(path));

    [Fact]
    public void CmOutputIsPinnedByGoldenHash()
    {
        // Freezes CM v1: any change to the predictor changes this hash and must become a new format version.
        var input = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"golden vector {i * 7919 % 1000} osmium\n")));
        var packed = CmCodec.Encode(input, new CmOptions { TableBits = 18, MatchBits = 16 }, Ct);
        Assert.Equal(Golden.CmHash, XxHash3.HashToUInt64(packed).ToString("X16"));
        var back = new byte[input.Length];
        CmCodec.Decode(packed, back, new CmOptions { TableBits = 18, MatchBits = 16 }, Ct);
        Assert.Equal(input, back);
    }

    [Fact]
    public void RulesResolveWithPrecedenceAndForbid()
    {
        var reg = MethodRegistry.Default;
        var rs = RuleSet.Parse("""
            { "rules": [
              { "id": "logs", "match": { "globs": ["**/*.log"] }, "actions": { "pipeline": "ppmd(order=12)" } },
              { "id": "big", "priority": 5, "match": { "minSize": 1000 }, "actions": { "params": { "ppmd.order": "16" } } },
              { "id": "nocm", "match": { "extensions": ["log"] }, "actions": { "forbidCodecs": ["ppmd"] } },
              { "id": "skip", "match": { "globs": ["tmp/**"] }, "actions": { "exclude": true } }
            ] }
            """);
        Assert.Empty(rs.Check(reg));
        var e = rs.Resolve("var/app.log", 5000, null, reg);
        Assert.Null(e.Pipeline); // forbidden ppmd wins over the forced pipeline
        Assert.Equal(16, e.Params["ppmd.order"]);
        Assert.True(rs.Resolve("tmp/x/y.bin", 1, null, reg).Exclude);
        Assert.False(rs.Resolve("src/y.bin", 1, null, reg).Exclude);
    }

    [Fact]
    public async Task ClusterHandshakeEncryptsAndRejectsWrongKeys()
    {
        var key = ClusterKey.Generate();
        Assert.Equal(key.ToString(), ClusterKey.Parse(key.ToString()).ToString());
        int port = FreePort();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = new WorkerServer(new WorkerOptions { Key = key, Port = port, Bind = IPAddress.Loopback, Slots = 2, Memory = 1L << 30, Discovery = false });
        var run = server.RunAsync(cts.Token);
        await Task.Delay(300, Ct);

        // wrong key is refused
        await Assert.ThrowsAnyAsync<Exception>(() => RemoteNode.ConnectAsync($"127.0.0.1:{port}", ClusterKey.Generate(), null, Ct));

        await using var cluster = await Cluster.ConnectAsync([$"127.0.0.1:{port}"], key, 1, null, Ct);
        Assert.Single(cluster.Nodes);
        var reg = MethodRegistry.Default;
        var sample = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("distributed trial sample ", 3000)));
        var specs = new[] { "lzma(dict=1M)", "zstd(level=9)", "ppmd(order=6,mem=16M)", "cm(mem=16)" }.Select(s => PipelineSpec.Parse(s, reg)).ToList();
        var remote = await cluster.Nodes[0].TrialsAsync(specs, sample, Ct);
        var local = await new LocalTrialRunner(2, new MemoryGate(1L << 30)).RunAsync(specs, sample, Ct);
        Assert.Equal(local, remote); // deterministic codecs agree across nodes

        var block = await cluster.Nodes[0].CompressAsync(specs[0], sample, Ct);
        Assert.Equal(sample, PipelineExecutor.Decode(block.Descriptor, block.Payload, CodecContext.None));
        cts.Cancel();
        await run;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

internal static class Golden
{
    public const string CmHash = "3718EC00D27EDC17"; // recorded on Linux x64; Windows CI must match
}
