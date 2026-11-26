using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Osmium.Core.Tests.Foreign;

/// <summary>Test archives: the committed fixtures, and archives written here with in-process writers.</summary>
public static class ForeignData
{
    /// <summary>The full path of a committed fixture ("7z", "aes.7z").</summary>
    public static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    /// <summary>Every committed fixture, as paths relative to the fixture folder with '/' separators.</summary>
    public static IEnumerable<string> AllFixtures()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".md", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);
    }

    /// <summary>The fixtures' source tree: what in/ holds (see Fixtures/README.md).</summary>
    public static readonly (string Path, byte[] Data)[] FixtureTree =
    [
        ("in/empty.txt", []),
        ("in/hello.txt", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Hello from the Osmium test fixtures.\n", 40)))),
        ("in/sub/numbers.txt", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(1, 300).Select(i => i + "\n")))),
    ];

    /// <summary>The fixtures' code.bin: 64 copies of one 32-byte pattern holding a branch instruction for each filter.</summary>
    public static byte[] FixtureCode()
    {
        byte[] pattern =
        [
            0xE8, 0x01, 0x02, 0x00, 0x00, 0x90, 0x90, 0x90, 0x01, 0x00, 0x00, 0xEB, 0x00, 0xF0, 0x01, 0xF8,
            0x01, 0x00, 0x00, 0x94, 0x01, 0x00, 0x00, 0x90, 0x48, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00, 0x01,
        ];
        return [.. Enumerable.Repeat(pattern, 64).SelectMany(p => p)];
    }

    public static byte[] Text(int bytes, int seed = 1)
    {
        var rnd = new Random(seed);
        var sb = new StringBuilder(bytes);
        string[] words = ["archive", "osmium", "file", "data", "folder", "test", "zip", "stream", "block", "name"];
        while (sb.Length < bytes) sb.Append(words[rnd.Next(words.Length)]).Append(rnd.Next(8) == 0 ? '\n' : ' ');
        return Encoding.ASCII.GetBytes(sb.ToString(0, bytes));
    }

    /// <summary>A ZIP written by System.IO.Compression; names ending in '/' are folders.</summary>
    public static byte[] Zip(IEnumerable<(string Name, byte[] Data)> entries, CompressionLevel level = CompressionLevel.Optimal)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var e = zip.CreateEntry(name, level);
                e.LastWriteTime = new DateTimeOffset(2024, 5, 6, 7, 8, 10, TimeSpan.Zero);
                if (name.EndsWith('/')) continue;
                using var s = e.Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    /// <summary>A TAR written by System.Formats.Tar (PAX format); names ending in '/' are folders.</summary>
    public static byte[] Tar(IEnumerable<(string Name, byte[] Data)> entries)
    {
        var ms = new MemoryStream();
        using (var w = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var e = new PaxTarEntry(name.EndsWith('/') ? TarEntryType.Directory : TarEntryType.RegularFile, name)
                {
                    ModificationTime = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero),
                };
                if (!name.EndsWith('/')) e.DataStream = new MemoryStream(data);
                w.WriteEntry(e);
            }
        }
        return ms.ToArray();
    }

    public static byte[] GZip(byte[] data)
    {
        var ms = new MemoryStream();
        using (var g = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) g.Write(data);
        return ms.ToArray();
    }

    public static byte[] BZip2(byte[] data)
    {
        var ms = new MemoryStream();
        using (var b = new SharpCompress.Compressors.BZip2.BZip2Stream(new NonClosing(ms), SharpCompress.Compressors.CompressionMode.Compress, false))
            b.Write(data);
        return ms.ToArray();
    }

    public static byte[] Zstd(byte[] data, bool checksum = true)
    {
        using var c = new ZstdSharp.Compressor(3);
        c.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, checksum ? 1 : 0);
        return c.Wrap(data).ToArray();
    }

    public static string Write(TempDir t, string name, byte[] data)
    {
        string path = t.Sub(name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
