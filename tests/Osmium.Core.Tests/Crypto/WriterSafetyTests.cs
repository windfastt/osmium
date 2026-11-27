using System.Buffers.Binary;
using System.IO.Hashing;
using System.Reflection;
using Osmium.Core.Container;
using Osmium.Core.Crypto;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Crypto;

/// <summary>What the writer refuses before it writes anything, and that it never seals a frame number twice.</summary>
public class WriterSafetyTests
{
    internal const string Password = "writer safety";
    internal static readonly KdfParameters Small = KdfParameters.Argon2id(1, 64, 1);
    private static readonly MethodRegistry Reg = MethodRegistry.Default;
    private static readonly byte[] Id = [.. Enumerable.Range(0x10, 16).Select(i => (byte)i)];
    private static readonly byte[] Salt = [.. Enumerable.Range(0x40, 32).Select(i => (byte)i)];

    private static (BlockDescriptor, UInt128, byte[]) Block(int seed, int size = 3000)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", Reg), data, CodecContext.None);
        return (desc, BlockBody.Hash128(data), payload);
    }

    /// <summary>Blocks of random bytes, one file each, written with these options.</summary>
    internal static string Write(string path, OsaWriterOptions options, int blocks = 3, int size = 3000)
    {
        using var w = new OsaWriter(path, options);
        var entries = new List<ArchiveEntry>();
        for (int i = 0; i < blocks; i++)
        {
            var data = new byte[size + i];
            new Random(i).NextBytes(data);
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", Reg), data, CodecContext.None);
            int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
            entries.Add(new ArchiveEntry
            {
                Path = $"f{i}.bin", Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)],
                Hash = XxHash128.HashToUInt128(data), ModifiedUtc = new DateTime(2025, 1, 2, 3, 4, 6, DateTimeKind.Utc),
            });
        }
        w.Finish(entries, new Dictionary<string, byte[]> { ["profile"] = "fast"u8.ToArray() });
        w.Commit(overwrite: true);
        return path;
    }

    [Fact]
    public void AFailedBlockWriteStopsTheWriterForGood()
    {
        using var t = new TempDir();
        bool fail = false;
        using var w = new OsaWriter(t.Sub("a.osa"), new OsaWriterOptions
        {
            Password = Password, Kdf = Small, TestBeforeWrite = _ => { if (fail) throw new IOException("the disk is full"); },
        });
        var (d, h, p) = Block(1);
        Assert.Equal(0, w.WriteBlock(d, h, p, 3000));
        Assert.Equal(1, w.Seals);
        fail = true;
        Assert.Equal("the disk is full", Assert.Throws<IOException>(() => w.WriteBlock(d, h, p, 3000)).Message);
        Assert.Equal(2, w.Seals); // frame 1 was sealed, and may be half in the file
        fail = false;
        // Going on would seal frame 1 again over other data (or the index with another layout): refused.
        Assert.Equal(OsaWriter.FailedEarlier, Assert.Throws<InvalidOperationException>(() => w.WriteBlock(d, h, p, 3000)).Message);
        Assert.Equal(OsaWriter.FailedEarlier, Assert.Throws<InvalidOperationException>(() => w.Finish([])).Message);
        Assert.Equal(2, w.Seals);
    }

    [Fact]
    public void AFailedFinishCannotBeRepeated()
    {
        using var t = new TempDir();
        int failAt = int.MaxValue;
        using var w = new OsaWriter(t.Sub("a.osa"), new OsaWriterOptions
        {
            Password = Password, Kdf = Small, SpareCopies = SpareCopyPolicy.Always,
            TestBeforeWrite = n => { if (n >= failAt) throw new IOException("the disk is full"); },
        });
        var (d, h, p) = Block(1);
        w.WriteBlock(d, h, p, 3000);
        failAt = 0; // the first write of Finish: the spare copy of the index, already sealed
        Assert.Throws<IOException>(() => w.Finish([]));
        int seals = w.Seals;
        failAt = int.MaxValue;
        Assert.Equal(OsaWriter.FailedEarlier, Assert.Throws<InvalidOperationException>(() => w.Finish([])).Message);
        Assert.Equal(seals, w.Seals);
    }

    [Fact]
    public void AnUnencryptedWriterStopsTooAfterAFailure()
    {
        using var t = new TempDir();
        bool fail = true;
        // write 0 is the header, in the constructor
        using var w = new OsaWriter(t.Sub("a.osa"), new OsaWriterOptions { TestBeforeWrite = n => { if (fail && n > 0) throw new IOException("gone"); } });
        var (d, h, p) = Block(2);
        Assert.Throws<IOException>(() => w.WriteBlock(d, h, p, 3000));
        fail = false;
        Assert.Throws<InvalidOperationException>(() => w.WriteBlock(d, h, p, 3000));
        Assert.Equal(0, w.Seals);
    }

    [Fact]
    public void TheIndexOfAnArgon2idArchiveIsPaddedAndItsSpareCopyIsTheSameBytes()
    {
        using var t = new TempDir();
        string path = Write(t.Sub("a.osa"), new OsaWriterOptions { Password = Password, Kdf = Small, SpareCopies = SpareCopyPolicy.Always });
        var (header, plain, a, b) = IndexFrames(path);
        Assert.Equal(Osa.FlagEncrypted, header.Flags); // no flag for it: the padding is part of kdf 2
        Assert.True(a.AsSpan().SequenceEqual(b));
        long exact = ExactLength(plain);
        Assert.Equal(BlockBody.PaddedLength(exact), plain.Length);
        Assert.True(plain.Length > exact, "the padding added nothing for this length; the test needs one it changes");
        Assert.DoesNotContain(plain.AsSpan((int)exact).ToArray(), x => x != 0);
    }

    [Fact]
    public void ThePbkdf2IndexIsNeverPadded()
    {
        using var t = new TempDir();
        string path = Write(t.Sub("a.osa"), new OsaWriterOptions { Password = Password, KdfIterations = 1000, SpareCopies = SpareCopyPolicy.Always });
        var (header, plain, a, b) = IndexFrames(path);
        Assert.Equal(KdfAlgorithm.Pbkdf2Sha512, header.Encryption!.Kdf.Algorithm);
        Assert.True(a.AsSpan().SequenceEqual(b));
        Assert.Equal(ExactLength(plain), plain.Length);
    }

    /// <summary>The header, A's decrypted body, and the bytes of A and of B.</summary>
    internal static (ArchiveHeader Header, byte[] Plain, byte[] A, byte[] B) IndexFrames(string path, string password = Password)
    {
        var bytes = File.ReadAllBytes(path);
        var header = ArchiveHeader.Read(new MemoryStream(bytes));
        long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(bytes.Length - 32));
        long indexLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(bytes.Length - 24));
        var trailer = SpareTrailer.Read(bytes.AsSpan((int)indexOffset - 32, 32))!.Value;
        long spare = indexOffset - trailer.CopyLength - indexLength;
        var a = bytes.AsSpan((int)indexOffset, (int)indexLength).ToArray();
        var b = bytes.AsSpan((int)spare, (int)indexLength).ToArray();
        using var key = ArchiveKey.Derive(password, header.Encryption!);
        var plain = key.Open(header.ArchiveId, Osa.IndexFrameNumber, a.AsSpan(Osa.FrameHeaderSize), header.Raw);
        return (header, plain, a, b);
    }

    /// <summary>The length of a body's prefix and payload, without padding.</summary>
    internal static long ExactLength(byte[] body)
    {
        var r = new BlobReader(body);
        r.Bytes(1 << 16);
        r.Raw(16);
        r.U64();
        long payload = (long)r.U();
        return r.Position + 4 + payload;
    }

    [Theory]
    [InlineData("")]
    [InlineData("surrogate")]
    public void PasswordsThatCannotBeUsedAreRefusedBeforeAnythingIsWritten(string which)
    {
        using var t = new TempDir();
        string password = which == "" ? "" : "ab" + "\uD800";
        using var seams = new KdfSeams();
        var e = Assert.Throws<ArgumentException>(() => new OsaWriter(t.Sub("a.osa"), new OsaWriterOptions { Password = password, Kdf = Small }));
        Assert.Equal(which == "" ? "A password cannot be empty." : "The password contains a character that cannot be stored (an unpaired surrogate).", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(t.Path));
        Assert.Equal(0, seams.Allocations);
    }

    [Fact]
    public void ProtectionTheWriterWouldRefuseIsRefusedBeforeAnythingIsWritten()
    {
        using var t = new TempDir();
        using var seams = new KdfSeams();
        var memory = Assert.Throws<KdfMemoryLimitException>(() =>
            new OsaWriter(t.Sub("a.osa"), new OsaWriterOptions { Password = Password, Kdf = KdfPresets.Standard, KdfMemoryLimit = 512L << 20 }));
        Assert.Equal("This password protection needs 1 GiB of memory to make the archive, more than the 512 MiB Osmium may use (Memory limit).", memory.Message);
        Assert.Throws<ArgumentException>(() => new OsaWriter(t.Sub("a.osa"), new OsaWriterOptions { Password = Password, KdfIterations = 0 }));
        Assert.Throws<ArgumentException>(() => new OsaWriter(t.Sub("a.osa"), new OsaWriterOptions { Password = Password, Kdf = KdfParameters.Argon2id(17, 1 << 20, 4) }));
        using (TestDefaults.AsOsmiumDoes())
        {
            Assert.Throws<WeakProtectionException>(() => new OsaWriter(t.Sub("a.osa"), new OsaWriterOptions { Password = Password, KdfIterations = 99_999 }));
            Assert.Throws<WeakProtectionException>(() => new OsaWriter(t.Sub("a.osa"), new OsaWriterOptions { Password = Password, Kdf = Small }));
        }
        Assert.Empty(Directory.GetFileSystemEntries(t.Path));
        Assert.Equal(0, seams.Allocations);
    }

    [Fact]
    public void ACancelledDerivationLeavesNoFile()
    {
        using var t = new TempDir();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new OsaWriter(t.Sub("a.osa"), new OsaWriterOptions { Password = Password, Kdf = Small, Cancellation = cts.Token }));
        Assert.Empty(Directory.GetFileSystemEntries(t.Path));
    }

    [Fact]
    public void TheProtectionResolvesAsTheOptionsSay()
    {
        Assert.Null(new OsaWriterOptions().ResolvedKdf);
        Assert.Equal(KdfPresets.Standard, new OsaWriterOptions { Password = Password }.ResolvedKdf);
        Assert.Equal(KdfParameters.Pbkdf2(150_000), new OsaWriterOptions { Password = Password, KdfIterations = 150_000 }.ResolvedKdf);
        Assert.Equal(Small, new OsaWriterOptions { Password = Password, Kdf = Small, KdfIterations = 150_000 }.ResolvedKdf);
        Assert.Null(OsaWriter.CheckProtection(new OsaWriterOptions()));
        Assert.Equal(Small, OsaWriter.CheckProtection(new OsaWriterOptions { Password = Password, Kdf = Small }));
    }

    [Fact]
    public void SameInputsGiveTheSameBytes()
    {
        using var t = new TempDir();
        OsaWriterOptions Fixed() => new()
        {
            Password = Password, Kdf = Small, Creator = "Osmium test", TestFixedArchiveId = Id, TestFixedSalt = Salt, TestFixedSaltAcknowledged = true,
            SpareCopies = SpareCopyPolicy.Always,
        };
        var first = File.ReadAllBytes(Write(t.Sub("a.osa"), Fixed()));
        var second = File.ReadAllBytes(Write(t.Sub("b.osa"), Fixed()));
        Assert.Equal(first, second);
    }

    [Fact]
    public void TheWriterZeroesItsKeyAndItsOptionsNeverShowThePassword()
    {
        using var t = new TempDir();
        var options = new OsaWriterOptions { Password = Password, Kdf = Small };
        var w = new OsaWriter(t.Sub("a.osa"), options);
        var key = (ArchiveKey)typeof(OsaWriter).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!;
        Assert.False(key.IsDisposed);
        w.Dispose();
        Assert.True(key.IsDisposed);
        Assert.DoesNotContain(Password, options.ToString());
        Assert.Contains("Encrypted = True", options.ToString());
    }
}

