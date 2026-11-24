using System.Buffers.Binary;
using Osmium.Core.Analysis;
using Osmium.Core.Planning;
using static Osmium.Core.Tests.PlannerFixtures;

namespace Osmium.Core.Tests;

/// <summary>
/// <see cref="Planner.BuildSample"/> reads only the ranges each slice needs, but must return exactly the bytes the
/// 0.1.1 sampler returned: trial results, and therefore plans and archives, depend on them.
/// </summary>
public class PlannerSampleTests
{
    /// <summary>The 0.1.1 sampler, kept verbatim as the reference: it assembled the whole block for every slice.</summary>
    private static byte[] ReferenceSample(ArchivePlan plan, PlanGroup g, long rung)
    {
        var blocks = plan.Blocks.Where(b => b.Group == g.Id).ToList();
        long total = blocks.Sum(b => b.RawSize);
        if (total <= rung || g.Class is CompatClass.Audio or CompatClass.Image)
        {
            long len = Math.Min(total, rung);
            var whole = new byte[len];
            long pos = 0;
            foreach (var b in blocks)
            {
                if (pos >= len) break;
                var raw = WholeBlock(plan, b).AsSpan(0, (int)Math.Min(b.RawSize, len - pos)).ToArray();
                raw.CopyTo(whole.AsSpan((int)pos));
                pos += raw.Length;
            }
            return whole;
        }
        const int slices = 16;
        int sliceLen = (int)(rung / slices);
        sliceLen -= sliceLen % Math.Max(1, g.Alignment);
        var result = new byte[sliceLen * slices];
        for (int s = 0; s < slices; s++)
        {
            long start = (total - sliceLen) * s / (slices - 1);
            start -= start % Math.Max(1, g.Alignment);
            // locate block containing 'start'
            long acc = 0;
            int written = 0;
            foreach (var b in blocks)
            {
                if (start + written >= acc + b.RawSize) { acc += b.RawSize; continue; }
                var raw = WholeBlock(plan, b);
                int from = (int)(start + written - acc);
                int n = Math.Min(sliceLen - written, raw.Length - from);
                raw.AsSpan(from, n).CopyTo(result.AsSpan(s * sliceLen + written));
                written += n;
                acc += b.RawSize;
                if (written >= sliceLen) break;
            }
        }
        return result;
    }

    /// <summary>A block's raw bytes, read independently of <see cref="BlockAssembler"/> (padding is zero).</summary>
    private static byte[] WholeBlock(ArchivePlan plan, PlanBlock b)
    {
        var raw = new byte[b.RawSize];
        foreach (var part in b.Parts)
        {
            var unit = plan.Units[part.Unit];
            var file = File.ReadAllBytes(plan.Files[unit.File].Src.FullPath);
            file.AsSpan((int)(unit.Offset + part.UnitOffset), (int)part.Length).CopyTo(raw.AsSpan((int)part.BlockOffset));
        }
        return raw;
    }

    /// <summary>A little-endian int32 table (detected as stride-4 records), cut to an odd length.</summary>
    private static byte[] Int32Table(int bytes, ulong seed)
    {
        var r = new Rng(seed);
        var d = new byte[(bytes + 3) & ~3];
        for (int i = 0; i < d.Length / 4; i++) BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(i * 4), 5_000_000 + i * 13 + r.Below(8));
        return d[..bytes];
    }

    private static string Folder(TempDir t, string layout)
    {
        var dir = t.Sub(layout);
        Directory.CreateDirectory(dir);
        switch (layout)
        {
            case "records": // file starts are padded to the 4-byte stride, files span blocks
                int[] sizes = [30_001, 70_003, 5_002, 140_001, 9_999, 65_537];
                for (int i = 0; i < sizes.Length; i++) File.WriteAllBytes(Path.Combine(dir, $"t{i}.dat"), Int32Table(sizes[i], (ulong)i + 1));
                break;
            case "text": // many small files per block
                for (int i = 0; i < 40; i++) File.WriteAllBytes(Path.Combine(dir, $"n{i:D2}.txt"), Synthetic.Text(1000 + i * 487, (ulong)i + 1));
                break;
            case "chunks": // chunk dedup: units start inside files, repeated chunks are stored once
                var a = Int32Table(150_001, 7);
                File.WriteAllBytes(Path.Combine(dir, "a.dat"), a);
                File.WriteAllBytes(Path.Combine(dir, "twice.dat"), [.. Int32Table(70_001, 8), .. a, .. a]);
                break;
        }
        return dir;
    }

    [Theory]
    [InlineData("records")]
    [InlineData("text")]
    [InlineData("chunks")]
    public async Task SamplesAreIdenticalToTheReferenceForEveryRungAndAlignment(string layout)
    {
        using var t = new TempDir();
        var dir = Folder(t, layout);
        var options = Options(Profile.Max, new ScriptedTrials((s, sample) => sample.Length)) with
        {
            BlockCap = 64 << 10, // the smallest block size: several blocks per group
            Dedup = layout == "chunks" ? DedupMode.Chunks : DedupMode.Files,
        };
        var plan = await new Planner(options).BuildAsync(Scanner.Scan([dir], ct: Ct), Ct);
        var g = plan.Groups.MaxBy(x => x.Size)!;
        var blocks = plan.Blocks.Where(b => b.Group == g.Id).ToList();
        long total = blocks.Sum(b => b.RawSize);

        // the layout is the one this test is about
        Assert.True(blocks.Count >= 4, $"{blocks.Count} blocks");
        Assert.Contains(blocks, b => b.Parts.Count >= 2);
        Assert.True(g.Units.Any(u => plan.Blocks.Count(b => b.Parts.Any(p => p.Unit == u)) >= 2), "no unit spans two blocks");
        if (layout != "text") Assert.Equal((CompatClass.Structured, 4), (g.Class, g.Alignment));
        if (layout == "records") Assert.True(total > g.Size, "no padding");
        if (layout == "chunks") Assert.Contains(g.Units, u => plan.Units[u].Offset > 0);

        long[] rungs = [16, 100, 4096, 16 << 10, 65_536, 100_003, 250_000, total - 1, total, total + 1, 4 * total];
        var (alignment, cls) = (g.Alignment, g.Class);
        try
        {
            foreach (int a in new[] { alignment, 1, 2, 3, 4, 12, 16, 64 }.Distinct())
                foreach (var c in new[] { cls, CompatClass.Audio }) // Audio (and Image) samples are prefixes
                    foreach (long rung in rungs)
                    {
                        (g.Alignment, g.Class) = (a, c);
                        var expected = ReferenceSample(plan, g, rung);
                        var actual = Planner.BuildSample(plan, g, rung);
                        Assert.True(expected.AsSpan().SequenceEqual(actual), $"{layout}: alignment {a}, {c}, rung {rung}: {expected.Length} vs {actual.Length} bytes");
                    }
        }
        finally { (g.Alignment, g.Class) = (alignment, cls); }
    }

    [Fact]
    public async Task AssembleRangeReadsAnyWindowOfABlock()
    {
        using var t = new TempDir();
        var dir = Folder(t, "records");
        var plan = await new Planner(Options(Profile.Max, new ScriptedTrials((s, sample) => sample.Length)) with { BlockCap = 64 << 10 })
            .BuildAsync(Scanner.Scan([dir], ct: Ct), Ct);
        foreach (var b in plan.Blocks)
        {
            var whole = WholeBlock(plan, b);
            Assert.Equal(whole, BlockAssembler.Assemble(plan, b, (int)b.RawSize));
            foreach (var (from, len) in new (long, int)[] { (0, 0), (0, 1), (1, 7), (4093, 9000), (b.RawSize - 5, 5), (b.RawSize / 3, (int)(b.RawSize / 3)) })
            {
                Assert.Equal(whole.AsSpan((int)from, len).ToArray(), BlockAssembler.AssembleRange(plan, b, from, len));
                var dst = new byte[len + 2];
                dst.AsSpan().Fill(0xEE); // stale bytes must not survive in padding
                BlockAssembler.AssembleRange(plan, b, from, dst.AsSpan(1, len));
                Assert.Equal(whole.AsSpan((int)from, len).ToArray(), dst[1..^1]);
                Assert.Equal((byte)0xEE, dst[0]);
                Assert.Equal((byte)0xEE, dst[^1]);
            }
        }
    }
}
