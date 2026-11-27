using System.Buffers.Binary;
using Osmium.Core.Analysis;

namespace Osmium.Core.Tests;

/// <summary>Header fields that come from the file are checked without overflowing before they are used.</summary>
public class SnifferHeaderTests
{
    /// <summary>"MZ" and a PE header offset near int.MaxValue: offset + 24 overflows.</summary>
    private static byte[] HostilePe()
    {
        var head = new byte[4096];
        head[0] = (byte)'M';
        head[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(0x3C), int.MaxValue - 10);
        return head;
    }

    /// <summary>A BMP header whose width times bits per pixel overflows a 32-bit row size.</summary>
    private static byte[] HostileBmp(int width, int height, int bpp)
    {
        var head = new byte[1000];
        head[0] = (byte)'B';
        head[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(2), head.Length);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(28), (short)bpp);
        return head;
    }

    [Fact]
    public void APeOffsetThatWouldOverflowIsRejected()
    {
        Assert.False(Sniffer.TryParsePe(HostilePe(), out _, out _));
        using var t = new TempDir();
        File.WriteAllBytes(t.Sub("app.exe"), HostilePe());
        Assert.NotEqual(CompatClass.CodeX86, Sniffer.Analyze(t.Sub("app.exe")).Class);
    }

    [Theory]
    [InlineData(int.MaxValue, 1, 32)]   // width × 32 wraps around to a row size of 0
    [InlineData(1 << 27, 1, 24)]        // the row size alone is larger than the file
    [InlineData(4, int.MaxValue, 32)]   // so are the rows together
    public void BmpRowsThatCannotFitTheFileAreRejected(int width, int height, int bpp)
    {
        var head = HostileBmp(width, height, bpp);
        Assert.False(Sniffer.TryParseBmp(head, head.Length, out var image));
        Assert.Null(image);
        using var t = new TempDir();
        File.WriteAllBytes(t.Sub("picture.bmp"), head);
        Assert.NotEqual(CompatClass.Image, Sniffer.Analyze(t.Sub("picture.bmp")).Class);
    }

    [Fact]
    public void AValidBmpHeaderIsStillAccepted()
    {
        var head = HostileBmp(100, 3, 24); // 300 bytes of pixels after the 54-byte header
        Assert.True(Sniffer.TryParseBmp(head, head.Length, out var image));
        Assert.Equal(300, image!.RowBytes);
    }
}
