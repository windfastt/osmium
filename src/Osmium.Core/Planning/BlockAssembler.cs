namespace Osmium.Core.Planning;

/// <summary>Reads the raw bytes of a planned block from the source files (padding is zero).</summary>
public static class BlockAssembler
{
    public static byte[] Assemble(ArchivePlan plan, PlanBlock block, int maxLength) =>
        AssembleRange(plan, block, 0, Math.Min(maxLength, (int)block.RawSize));

    public static byte[] AssembleRange(ArchivePlan plan, PlanBlock block, long from, int length)
    {
        var buf = new byte[length];
        AssembleRange(plan, block, from, buf);
        return buf;
    }

    /// <summary>Fills <paramref name="destination"/> with the block's bytes from <paramref name="from"/> on, reading only
    /// the parts of the source files that overlap that range (padding is zero).</summary>
    public static void AssembleRange(ArchivePlan plan, PlanBlock block, long from, Span<byte> destination)
    {
        destination.Clear();
        long end = from + destination.Length;
        foreach (var part in block.Parts)
        {
            long ps = part.BlockOffset, pe = part.BlockOffset + part.Length;
            if (pe <= from || ps >= end) continue;
            long s = Math.Max(ps, from), e = Math.Min(pe, end);
            var unit = plan.Units[part.Unit];
            var src = plan.Files[unit.File].Src;
            long fileOffset = unit.Offset + part.UnitOffset + (s - ps);
            ReadExact(src.FullPath, fileOffset, destination.Slice((int)(s - from), (int)(e - s)));
        }
    }

    public static void ReadExact(string path, long offset, Span<byte> dst)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int total = 0;
        while (total < dst.Length)
        {
            int n = RandomAccess.Read(h, dst[total..], offset + total);
            if (n <= 0) throw new SourceChangedException(path, "file became shorter while it was being archived");
            total += n;
        }
    }
}

public sealed class SourceChangedException(string path, string why) : IOException($"'{path}' {why}. Archive not written.")
{
    public string SourcePath => path;
}
