using System.Buffers.Binary;
using System.Text;
using Osmium.Core.Container;
using Osmium.Core.Crypto;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests.Crypto;

/// <summary>
/// No plaintext in encrypted archives (FORMAT §5): no name, path, metadata value or content of the archived files
/// appears in the archive's bytes, spare copies included, and every byte is a documented plaintext field (the header,
/// frame headers, the header copy with its zero padding and trailer, the footer) or the body of a frame that
/// authenticates.
/// </summary>
public class PlaintextLeakTests
{
    private const string Password = "plaintext leak test";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>4 KiB of text holding the marker, as a document would.</summary>
    private static readonly byte[] Text = KnownText();

    private static byte[] KnownText()
    {
        var sb = new StringBuilder();
        for (int i = 0; sb.Length < 4096; i++) sb.Append($"Line {i}: KNOWN-PLAINTEXT-MARKER in a document that nobody may read without its password.\n");
        return Encoding.UTF8.GetBytes(sb.ToString())[..4096];
    }

    /// <summary>
    /// The files: the secret text, a zstd-compressed file and an executable (a library the tests load, which holds
    /// none of Osmium's own constants), in a folder with a name of its own.
    /// </summary>
    private static string Files(TempDir t)
    {
        string root = t.Sub("in");
        string folder = Path.Combine(root, "secret-folder-91c2");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "SECRET-NAME-7f3a.txt"), Text);
        var logs = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"2025-01-02 03:04:{i % 60:D2} request {i * 7919 % 10007} served\n")));
        var (_, zst) = PipelineExecutor.Encode(PipelineSpec.Parse("zstd(level=3)", MethodRegistry.Default), logs, CodecContext.None);
        File.WriteAllBytes(Path.Combine(folder, "requests-0c4d.log.zst"), zst);
        File.WriteAllBytes(Path.Combine(folder, "tool-5b8e.exe"), File.ReadAllBytes(typeof(Assert).Assembly.Location));
        return root;
    }

    private static async Task<string> Build(TempDir t, string root, string kdf, bool spares)
    {
        var scan = Scanner.Scan([root], ct: Ct);
        var plan = await new Planner(new PlanOptions { Profile = Profile.ByName("fast"), MemoryBudget = 1L << 30, Threads = 2 }).BuildAsync(scan, Ct);
        string output = t.Sub($"{kdf}-{(spares ? "spares" : "none")}.osa");
        await ArchiveBuilder.BuildAsync(plan, new BuildOptions
        {
            OutputPath = output, Password = kdf == "none" ? null : Password, MemoryBudget = 1L << 30, Threads = 2,
            Kdf = kdf == "argon2id" ? EncryptedArchives.Small : KdfParameters.Pbkdf2(100_000),
            // the writer's own rule: spare copies from a threshold of block bytes on
            SpareCopies = SpareCopyPolicy.Auto, SpareThreshold = spares ? 0 : long.MaxValue,
        }, Ct);
        return output;
    }

    public static TheoryData<string, bool> Cases => new() { { "argon2id", false }, { "argon2id", true }, { "pbkdf2", false }, { "pbkdf2", true } };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NoNamePathMetadataOrContentAppearsInTheArchive(string kdf, bool spares)
    {
        using var t = new TempDir();
        string root = Files(t);
        string path = await Build(t, root, kdf, spares);
        Assert.Null(FirstLeak(path, root, spares, out long checkedWindows));
        Assert.True(checkedWindows > 100_000, $"only {checkedWindows} windows were checked");
        var archive = File.ReadAllBytes(path);
        Assert.Equal(-1, archive.AsSpan().IndexOf("Osmium"u8));
        Assert.Equal(-1, archive.AsSpan().IndexOf("KNOWN-PLAINTEXT-MARKER"u8));
    }

    [Fact]
    public async Task TheSameCheckFindsPlaintextInAnUnencryptedArchive()
    {
        // The paths are inside its compressed file list, but its header names the creator in the clear.
        using var t = new TempDir();
        string root = Files(t);
        Assert.StartsWith("8 bytes of the metadata 'creator'", FirstLeak(await Build(t, root, "none", spares: true), root, spares: true, out _));
    }

    /// <summary>The first 8-byte window of identifying data found in the archive's bytes, or null.</summary>
    private static string? FirstLeak(string path, string root, bool spares, out long checkedWindows)
    {
        var archive = File.ReadAllBytes(path);
        using var r = OsaReader.Open(path, () => Password);
        Assert.Equal(spares, r.HasSpareCopies);

        // The needles: identifying data only.
        var needles = new List<(string What, byte[] Bytes, bool Binary)>();
        foreach (var e in r.Entries)
        {
            needles.Add(($"path '{e.Path}'", Encoding.UTF8.GetBytes(e.Path), false));
            foreach (var part in e.Path.Split('/')) needles.Add(($"name '{part}'", Encoding.UTF8.GetBytes(part), false));
        }
        Assert.Contains(r.Entries, e => e.Path.EndsWith("/SECRET-NAME-7f3a.txt", StringComparison.Ordinal));
        foreach (var (key, value) in r.Index.Metadata)
            if (key != ArchiveIndex.SparesKey) needles.Add(($"metadata '{key}'", value, key is not ("creator" or "profile" or "plan")));
        Assert.True(r.Index.Metadata.ContainsKey("creator") && r.Index.Metadata.ContainsKey("plan"));
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            needles.Add(($"content of {Path.GetFileName(f)}", File.ReadAllBytes(f), !f.EndsWith(".txt", StringComparison.Ordinal)));

        var windows = new HashSet<ulong>();
        for (int i = 0; i + 8 <= archive.Length; i++) windows.Add(BinaryPrimitives.ReadUInt64LittleEndian(archive.AsSpan(i)));
        checkedWindows = 0;
        foreach (var (what, bytes, binary) in needles)
            for (int i = 0; i + 8 <= bytes.Length; i++)
            {
                var w = bytes.AsSpan(i, 8);
                // Runs of a few byte values (zeros, small numbers) match length fields and padding by chance, not by leak.
                if (binary && DistinctBytes(w) < 4) continue;
                checkedWindows++;
                if (windows.Contains(BinaryPrimitives.ReadUInt64LittleEndian(w)))
                    return $"8 bytes of the {what} (at {i}) appear in the archive at {archive.AsSpan().IndexOf(w)}.";
            }
        return null;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryByteIsADocumentedPlaintextFieldOrAFrameBodyThatAuthenticates(string kdf, bool spares)
    {
        using var t = new TempDir();
        string path = await Build(t, Files(t), kdf, spares);
        var b = File.ReadAllBytes(path);
        var tail = SpareArchives.TailOf(path, Password);
        using var r = OsaReader.Open(path, () => Password);
        var regions = new List<(long Start, long End)> { (0, r.Header.Length) };
        regions.AddRange(r.Blocks.Select(x => (x.Offset, x.Offset + x.FrameLength)));
        regions.Add((tail.IndexOffset, tail.IndexOffset + tail.IndexLength));
        if (spares)
        {
            regions.Add((tail.SpareOffset, tail.SpareOffset + tail.IndexLength));
            regions.Add((tail.HeaderCopyOffset, tail.IndexOffset));
            // H: the header's bytes, zeros, then the trailer
            Assert.Equal(r.Header.Raw, b[(int)tail.HeaderCopyOffset..(int)(tail.HeaderCopyOffset + r.Header.Length)]);
            Assert.DoesNotContain(b[(int)(tail.HeaderCopyOffset + r.Header.Length)..(int)tail.TrailerOffset], x => x != 0);
            Assert.Equal(b[(int)tail.SpareOffset..(int)(tail.SpareOffset + tail.IndexLength)], b[(int)tail.IndexOffset..(int)(tail.IndexOffset + tail.IndexLength)]);
        }
        regions.Add((b.Length - Osa.FooterSize, b.Length));
        long at = 0;
        foreach (var (start, end) in regions.OrderBy(x => x.Start))
        {
            Assert.Equal(at, start); // nothing between the parts, and nothing twice
            at = end;
        }
        Assert.Equal(b.Length, at);
        // Each frame is a frame header and its body; every body authenticates (the file list did when it was opened).
        var x = Extractor.Test(r, null, Ct);
        Assert.Equal((r.Entries.Count(e => !e.IsDirectory), 0), (x.FilesOk, x.FilesFailed));
        Assert.Empty(r.Warnings);
    }

    private static int DistinctBytes(ReadOnlySpan<byte> w)
    {
        Span<bool> seen = stackalloc bool[256];
        int n = 0;
        foreach (byte c in w)
            if (!seen[c]) { seen[c] = true; n++; }
        return n;
    }
}
