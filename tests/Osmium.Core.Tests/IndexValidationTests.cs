using Osmium.Core.Container;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests;

/// <summary>
/// A damaged or crafted index is reported as corrupt data, never as an out-of-range crash, and a segment can never
/// point outside its block, whatever offsets and lengths the index holds.
/// </summary>
public class IndexValidationTests
{
    private static readonly MethodRegistry Reg = MethodRegistry.Default;

    private static ArchiveIndex Index(int blocks, params Segment[] segments)
    {
        var idx = new ArchiveIndex();
        for (int i = 0; i < blocks; i++)
            idx.Blocks.Add(new BlockInfo(i, 0, Osa.FrameHeaderSize + 10, 100, 0, new BlockDescriptor(PipelineSpec.Parse("store", Reg), [100])));
        idx.Entries.Add(new ArchiveEntry
        {
            Path = "a.txt", Kind = EntryKind.File, Size = segments.Sum(s => s.Length), Segments = segments,
        });
        return idx;
    }

    private static Exception Read(ArchiveIndex idx) =>
        Assert.ThrowsAny<IOException>(() => ArchiveIndex.Deserialize(idx.Serialize(), 0, Reg));

    [Fact]
    public void ValidIndexReadsBack()
    {
        var idx = ArchiveIndex.Deserialize(Index(1, new Segment(0, 0, 60), new Segment(0, 60, 40)).Serialize(), 0, Reg);
        Assert.Equal(100, idx.Entries.Single().Size);
    }

    [Fact]
    public void FileInAnArchiveWithoutBlocksIsCorrupt()
    {
        var e = Read(Index(0, new Segment(0, 0, 10)));
        Assert.IsType<CorruptDataException>(e);
    }

    [Fact]
    public void SegmentInAMissingBlockIsRejected()
    {
        // Block 1 of a one-block archive: rejected by the reader's bounds check (an InvalidDataException).
        Assert.ThrowsAny<Exception>(() => ArchiveIndex.Deserialize(Index(1, new Segment(1, 0, 10)).Serialize(), 0, Reg));
    }

    [Theory]
    [InlineData(-10L, 20L)]             // a negative offset, stored as a huge unsigned value
    [InlineData(50L, long.MaxValue)]    // offset + length overflows
    [InlineData(101L, 1L)]              // starts after the block
    [InlineData(90L, 11L)]              // ends after the block
    [InlineData(0L, 0L)]                // empty segment
    public void SegmentOutsideItsBlockIsCorrupt(long offset, long length)
    {
        var e = Read(Index(1, new Segment(0, offset, length)));
        Assert.IsType<CorruptDataException>(e);
        Assert.Contains("outside its block", e.Message);
    }

    [Fact]
    public void BlockWithWrappedRawSizeIsCorrupt()
    {
        // A raw size of 2^64-1 reads back as -1; it must not make every segment look in range.
        var idx = new ArchiveIndex();
        idx.Blocks.Add(new BlockInfo(0, 0, Osa.FrameHeaderSize + 10, -1, 0, new BlockDescriptor(PipelineSpec.Parse("store", Reg), [100])));
        idx.Entries.Add(new ArchiveEntry
        {
            Path = "a.txt", Kind = EntryKind.File, Size = long.MinValue + 100,
            Segments = [new Segment(0, 0, long.MinValue), new Segment(0, 0, 100)],
        });
        var e = Assert.ThrowsAny<IOException>(() => ArchiveIndex.Deserialize(idx.Serialize(), 0, Reg));
        Assert.IsType<CorruptDataException>(e);
    }

    [Fact]
    public void IntWithNegativeLimitAllowsNoValue()
    {
        Assert.Throws<InvalidDataException>(() => new BlobReader(new BlobWriter().U(0).ToArray()).Int(-1));
        Assert.Equal(0, new BlobReader(new BlobWriter().U(0).ToArray()).Int(0));
    }
}
