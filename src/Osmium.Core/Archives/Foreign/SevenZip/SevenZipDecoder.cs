using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Osmium.Core.Archives.Foreign.Compression;
using Osmium.Core.Archives.Foreign.Filters;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Deflate64;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.PPMd;

namespace Osmium.Core.Archives.Foreign.SevenZip;

/// <summary>
/// Decodes one 7z folder: the graph of coders (LZMA, LZMA2, PPMd, BZip2, Deflate, Copy, AES-256 and the BCJ, BCJ2,
/// ARM, ARM Thumb, ARM64, PowerPC, SPARC and Delta filters) from its packed streams to its result, as one forward
/// stream read once from the start. Every coder's memory is checked against the budget before it is constructed.
/// </summary>
internal static class SevenZipDecoder
{
    public static string MethodName(SzCoder c) => c.Id switch
    {
        0x00 => "Copy",
        0x21 => "LZMA2",
        0x030101 => "LZMA",
        0x030401 => "PPMd",
        0x040202 => "BZip2",
        0x040108 => "Deflate",
        0x040109 => "Deflate (64 KiB window)",
        0x03 => "Delta",
        0x03030103 => "BCJ",
        0x0303011B => "BCJ2",
        0x03030205 => "PPC",
        0x03030501 => "ARM",
        0x03030701 => "ARMT",
        0x03030805 => "SPARC",
        0x0A => "ARM64",
        SevenZipHeader.AesId => "AES-256",
        _ => $"method {Convert.ToHexString(c.MethodId)}",
    };

    /// <summary>The folder's methods for display in their stored order ("LZMA2, BCJ"), encryption left out.</summary>
    public static string Methods(SzFolder f) =>
        string.Join(", ", f.Coders.Where(c => c.Id != SevenZipHeader.AesId).Select(MethodName));

    /// <summary>The memory the folder's coders need (dictionaries sized to the output they produce).</summary>
    public static long Memory(SzFolder f)
    {
        long sum = 1 << 20;
        int outIndex = 0;
        foreach (var c in f.Coders)
        {
            long outSize = outIndex < f.UnpackSizes.Count ? f.UnpackSizes[outIndex] : -1;
            outIndex += c.NumOut;
            sum = LimitGuard.Add(sum, c.Id switch
            {
                0x21 when c.Props.Length == 1 && c.Props[0] <= 40 => Math.Min(XzDecoder.Lzma2DictSize(c.Props[0]), Math.Max(4096, outSize)),
                0x030101 when c.Props.Length >= 5 => Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(c.Props.AsSpan(1)), Math.Max(4096, outSize)),
                0x030401 when c.Props.Length >= 5 => BinaryPrimitives.ReadUInt32LittleEndian(c.Props.AsSpan(1)),
                0x040202 => 8L << 20,
                _ => 1L << 16,
            });
        }
        return sum;
    }

    /// <summary>Opens the folder's result stream. <paramref name="key"/> derives the AES key from the coder's
    /// properties (asking for the password the first time); <paramref name="aes"/>, when given, receives the folder's
    /// decrypting streams.</summary>
    public static Stream Open(SafeFileHandle handle, SzDatabase db, SzFolder f, ImportLimits limits, string name,
        Func<byte[], byte[]>? key, Action<long>? onRead, List<SevenZipAesStream>? aes = null)
    {
        LimitGuard.CheckMemory(name, Memory(f), limits);
        var packed = new Stream[f.PackedStreams.Count];
        for (int j = 0; j < packed.Length; j++)
        {
            int index = f.FirstPackStream + j;
            if (index >= db.PackSizes.Count) throw SevenZipHeader.Corrupt("damaged header");
            long offset = db.PackOffset(index), size = db.PackSizes[index];
            if (offset < 32 || size < 0 || offset > RandomAccess.GetLength(handle) - size) throw SevenZipHeader.Corrupt("the archive is truncated");
            packed[j] = new FileSlice(handle, offset, size) { OnRead = onRead };
        }
        var building = new HashSet<int>();
        try { return Output(f.MainOut()); }
        catch (OutOfMemoryException) { throw LimitGuard.MemoryRefusal(name, Memory(f)); }

        Stream Output(int outIndex)
        {
            if (!building.Add(outIndex)) throw SevenZipHeader.Corrupt("a folder whose coders form a loop");
            int coderIndex = 0, firstOut = 0, firstIn = 0;
            for (; coderIndex < f.Coders.Count; coderIndex++)
            {
                var c0 = f.Coders[coderIndex];
                if (outIndex < firstOut + c0.NumOut) break;
                firstOut += c0.NumOut;
                firstIn += c0.NumIn;
            }
            if (coderIndex >= f.Coders.Count) throw SevenZipHeader.Corrupt("damaged header");
            var c = f.Coders[coderIndex];
            if (c.NumOut != 1) throw new NotSupportedException($"{name} uses a coder with several outputs, which Osmium cannot read.");
            var inputs = new Stream[c.NumIn];
            for (int k = 0; k < c.NumIn; k++)
            {
                int inIndex = firstIn + k;
                int bound = f.BindPairs.FindIndex(b => b.In == inIndex);
                if (bound >= 0) inputs[k] = Output(f.BindPairs[bound].Out);
                else
                {
                    int p = f.PackedStreams.IndexOf(inIndex);
                    if (p < 0) throw SevenZipHeader.Corrupt("damaged header");
                    inputs[k] = packed[p];
                }
            }
            return Coder(c, inputs, f.UnpackSizes[outIndex], limits, name, key, aes);
        }
    }

    private static Stream Coder(SzCoder c, Stream[] inputs, long outSize, ImportLimits limits, string name, Func<byte[], byte[]>? key,
        List<SevenZipAesStream>? aes)
    {
        if (c.Id != 0x0303011B && inputs.Length != 1) throw new NotSupportedException($"{name} uses a coder layout Osmium cannot read.");
        // Decoders get their input through a guard that stops them reading on and on past its end.
        Stream input = c.Id is 0x00 or SevenZipHeader.AesId ? inputs[0]
            : new EndGuardStream(inputs[0], c.Id is 0x21 or 0x030101 ? 0 : 8);
        Stream Exact(Stream s) => new ExactStream(s, outSize, "a 7z coder");
        switch (c.Id)
        {
            case 0x00: return Exact(input);
            case 0x21:
            {
                if (c.Props.Length != 1 || c.Props[0] > 40) throw SevenZipHeader.Corrupt("LZMA2 properties");
                return Exact(new LzmaStream([XzDecoder.ShrinkLzma2Prop(c.Props[0], outSize)], input, -1, outSize, null, isLzma2: true));
            }
            case 0x030101:
            {
                if (c.Props.Length != 5) throw SevenZipHeader.Corrupt("LZMA properties");
                var props = (byte[])c.Props.Clone();
                long dict = BinaryPrimitives.ReadUInt32LittleEndian(props.AsSpan(1));
                BinaryPrimitives.WriteUInt32LittleEndian(props.AsSpan(1), (uint)Math.Min(dict, Math.Max(4096, outSize)));
                return Exact(new LzmaStream(props, input, -1, outSize));
            }
            case 0x030401:
            {
                if (c.Props.Length != 5) throw SevenZipHeader.Corrupt("PPMd properties");
                uint memory = BinaryPrimitives.ReadUInt32LittleEndian(c.Props.AsSpan(1));
                if (c.Props[0] is < 2 or > 32 || memory < (1 << 11) || memory > 0xFFFFFFFF - 12 * 3) throw SevenZipHeader.Corrupt("PPMd properties");
                LimitGuard.CheckMemory(name, memory, limits);
                return Exact(new PpmdStream(new PpmdProperties(c.Props), input, false));
            }
            case 0x040202: return Exact(new BZip2Stream(input, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: false));
            case 0x040108: return Exact(new DeflateStream(input, CompressionMode.Decompress));
            case 0x040109: return Exact(new Deflate64Stream(input, SharpCompress.Compressors.CompressionMode.Decompress));
            case 0x03:
                if (c.Props.Length != 1) throw SevenZipHeader.Corrupt("Delta properties");
                return Exact(new DeltaFilterStream(input, c.Props[0] + 1));
            case 0x03030103: return Exact(new BranchFilterStream(input, BranchConverter.X86()));
            case 0x03030205: return Exact(new BranchFilterStream(input, BranchConverter.PowerPc()));
            case 0x03030501: return Exact(new BranchFilterStream(input, BranchConverter.Arm()));
            case 0x03030701: return Exact(new BranchFilterStream(input, BranchConverter.ArmThumb()));
            case 0x03030805: return Exact(new BranchFilterStream(input, BranchConverter.Sparc()));
            case 0x0A: return Exact(new BranchFilterStream(input, BranchConverter.Arm64(), c.Props.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(c.Props) : 0));
            case 0x0303011B:
                if (inputs.Length != 4) throw SevenZipHeader.Corrupt("BCJ2 layout");
                return Exact(new Bcj2DecoderStream(inputs[0], inputs[1], inputs[2], inputs[3], outSize));
            case SevenZipHeader.AesId:
            {
                if (key is null) throw new PasswordRequiredException();
                var decrypting = new SevenZipAesStream(input, key(c.Props), SevenZipAes.Iv(c.Props));
                aes?.Add(decrypting);
                return Exact(decrypting);
            }
            default:
                throw new NotSupportedException($"{Osmium.Core.Text.DisplayText.Escape(name)} uses a compression method Osmium cannot read ({MethodName(c)}).");
        }
    }
}

/// <summary>7z's AES-256: the key is SHA-256 over 2^N rounds of (salt, password as UTF-16LE, round number), and the
/// data is AES-256 in CBC mode with the IV from the coder's properties. N is capped at 24 (as 7-Zip does), so an
/// archive cannot make the key derivation run for hours.</summary>
internal static class SevenZipAes
{
    public const int MaxCyclesPower = 24;

    private static (int Power, byte[] Salt, byte[] Iv) Parse(byte[] props)
    {
        if (props.Length == 0) throw SevenZipHeader.Corrupt("AES properties");
        int b0 = props[0], power = b0 & 0x3F;
        if ((b0 & 0xC0) == 0) return (power, [], new byte[16]);
        if (props.Length < 2) throw SevenZipHeader.Corrupt("AES properties");
        int b1 = props[1];
        int saltSize = ((b0 >> 7) & 1) + (b1 >> 4), ivSize = ((b0 >> 6) & 1) + (b1 & 0x0F);
        if (props.Length != 2 + saltSize + ivSize || ivSize > 16) throw SevenZipHeader.Corrupt("AES properties");
        var iv = new byte[16];
        props.AsSpan(2 + saltSize, ivSize).CopyTo(iv);
        return (power, props.AsSpan(2, saltSize).ToArray(), iv);
    }

    public static byte[] Iv(byte[] props) => Parse(props).Iv;

    /// <summary>What the key depends on (the round count and the salt, never the IV, which differs in every folder):
    /// coders with the same identity share one key.</summary>
    public static string KeyIdentity(byte[] props)
    {
        var (power, salt, _) = Parse(props);
        return power.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + Convert.ToHexString(salt);
    }

    /// <summary>The SHA-256 rounds deriving the key takes (0 for a stored key, or a count Osmium refuses anyway).</summary>
    public static long Rounds(byte[] props)
    {
        int power = Parse(props).Power;
        return power is 0x3F or > MaxCyclesPower ? 0 : 1L << power;
    }

    /// <summary>The key for <paramref name="password"/> under the coder's properties.</summary>
    public static byte[] Key(byte[] props, string password, CancellationToken ct)
    {
        var (power, salt, _) = Parse(props);
        var pw = Encoding.Unicode.GetBytes(password);
        if (power == 0x3F)
        {
            var raw = new byte[32];
            var both = salt.Concat(pw).ToArray();
            both.AsSpan(0, Math.Min(32, both.Length)).CopyTo(raw);
            return raw;
        }
        if (power > MaxCyclesPower) throw new NotSupportedException($"This 7z archive's encryption asks for 2^{power} key rounds, more than the 2^{MaxCyclesPower} Osmium accepts.");
        var round = new byte[salt.Length + pw.Length + 8];
        salt.CopyTo(round, 0);
        pw.CopyTo(round, salt.Length);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long rounds = 1L << power;
        for (long i = 0; i < rounds; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt64LittleEndian(round.AsSpan(salt.Length + pw.Length), i);
            sha.AppendData(round);
        }
        return sha.GetHashAndReset();
    }
}

/// <summary>AES-256-CBC decryption of a stream whose length is a multiple of 16.</summary>
internal sealed class SevenZipAesStream : ReadOnlyStream
{
    private readonly Stream _inner;
    private readonly Aes _aes;
    private readonly ICryptoTransform _decryptor;
    private readonly byte[] _in = new byte[1 << 16], _out = new byte[1 << 16];
    private int _start, _end;
    private bool _eof;

    /// <summary>The decrypted bytes read from this stream so far.</summary>
    public long Delivered { get; private set; }

    public SevenZipAesStream(Stream inner, byte[] key, byte[] iv)
    {
        _inner = inner;
        _aes = Aes.Create();
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.None;
        _decryptor = _aes.CreateDecryptor(key, iv);
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        while (_start == _end)
        {
            if (_eof) return 0;
            int got = 0;
            while (got < _in.Length)
            {
                int n = _inner.Read(_in, got, _in.Length - got);
                if (n <= 0) { _eof = true; break; }
                got += n;
            }
            int whole = got & ~15;
            if (whole == 0) return 0;
            _start = 0;
            _end = _decryptor.TransformBlock(_in, 0, whole, _out, 0);
        }
        int k = Math.Min(buffer.Length, _end - _start);
        _out.AsSpan(_start, k).CopyTo(buffer);
        _start += k;
        Delivered += k;
        return k;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _decryptor.Dispose();
            _aes.Dispose();
        }
        base.Dispose(disposing);
    }
}
