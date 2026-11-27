using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using Osmium.Core.Crypto;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Container;

/// <summary>Constants and low-level records of the .osa format (see docs/FORMAT.md).</summary>
public static class Osa
{
    public static ReadOnlySpan<byte> Magic => [0x4F, 0x53, 0x4D, 0x41, 0x1A, 0x0D, 0x0A, 0x00];
    public static ReadOnlySpan<byte> FrameMagic => "OBLK"u8;
    public static ReadOnlySpan<byte> FooterMagic => "OSAE"u8;
    /// <summary>The container version this reader understands and this writer writes. Readers accept every minor of
    /// <see cref="Major"/>: minor versions only add what older readers can ignore.</summary>
    public const ushort Major = 1, Minor = 1;
    public const int FrameHeaderSize = 28;
    public const int FooterSize = 32;
    public const ulong IndexFrameNumber = ulong.MaxValue;
    public const string Extension = ".osa";

    public const uint FlagEncrypted = 1u << 0;
    public const uint FlagExperimental = 1u << 16;
    public const uint KnownCriticalFlags = FlagEncrypted;

    public const uint FrameFlagEncrypted = 1u << 0;
    public const uint FrameFlagIndex = 1u << 1;

    public static uint Crc(ReadOnlySpan<byte> data) => Crc32.HashToUInt32(data);
}

/// <summary>
/// The header's EncryptionInfo record (FORMAT §1, §5): how the key is derived, the 32-byte salt and the 16-byte
/// password check value. Holds no key.
/// </summary>
public sealed record EncryptionInfo(KdfParameters Kdf, byte[] Salt, byte[] Verifier)
{
    /// <summary>PBKDF2 with <paramref name="iterations"/>, as Osmium 0.1 writes it.</summary>
    public EncryptionInfo(int iterations, byte[] salt, byte[] verifier) : this(KdfParameters.Pbkdf2(iterations), salt, verifier) { }

    /// <summary>A copy (<c>with</c>) serializes its own record again.</summary>
    private EncryptionInfo(EncryptionInfo other)
    {
        Kdf = other.Kdf;
        Salt = other.Salt;
        Verifier = other.Verifier;
    }

    /// <summary>PBKDF2 iterations; 0 for Argon2id.</summary>
    public int Iterations => Kdf.Algorithm == KdfAlgorithm.Pbkdf2Sha512 ? Kdf.Iterations : 0;

    private byte[]? _record;

    /// <summary>The record's value bytes exactly as stored (as read, or as this version writes them): salvage writes
    /// them again verbatim, and the key cache tells archives apart by them.</summary>
    internal byte[] RecordBytes { get => _record ??= EncryptionRecord.Write(this); init => _record = value; }

    /// <summary>Bytes after the verifier in a kdf 1 record as read (readers of kdf 1 ignore them; kdf 2 has none).</summary>
    internal int ExtraBytes { get; init; }

    public override string ToString() => $"EncryptionInfo {{ {Kdf.Describe()} }}";
}

/// <summary>
/// The value of header record 1 (FORMAT §1): <c>u8 kdf</c>, then for kdf 1 <c>varint iterations</c>, for kdf 2
/// <c>varint timeCost · varint memoryKiB · varint lanes</c>, then <c>u8[32] salt · u8 aead · u8[16] verifier</c>.
/// The header reader and salvage share this parser and its checks.
/// </summary>
internal static class EncryptionRecord
{
    public const string InvalidMessage = "The encryption settings in this archive's header are invalid.";
    public const int SaltBytes = 32, VerifierBytes = 16;
    public const byte Aes256Gcm = 1;

    public static byte[] Write(EncryptionInfo e)
    {
        var w = new BlobWriter().B((byte)e.Kdf.Algorithm);
        switch (e.Kdf.Algorithm)
        {
            case KdfAlgorithm.Pbkdf2Sha512: w.U((ulong)e.Kdf.Iterations); break;
            case KdfAlgorithm.Argon2id: w.U((ulong)e.Kdf.TimeCost).U((ulong)e.Kdf.MemoryKiB).U((ulong)e.Kdf.Lanes); break;
            default: throw new ArgumentException($"Key derivation function {(byte)e.Kdf.Algorithm} cannot be written.");
        }
        if (e.Salt.Length != SaltBytes || e.Verifier.Length != VerifierBytes) throw new ArgumentException("The salt is 32 bytes and the verifier 16.");
        return w.Raw(e.Salt).B(Aes256Gcm).Raw(e.Verifier).ToArray();
    }

    /// <summary>
    /// Record 1 in its reading order (FORMAT §5): the kdf id (unknown: <see cref="NewerVersionException"/>), the fields
    /// of its layout (missing or malformed: <see cref="CorruptDataException"/>), the cipher id (unknown:
    /// <see cref="NewerVersionException"/>), for kdf 2 nothing after the verifier, then the hard limits. Allocates only
    /// the salt and the verifier.
    /// </summary>
    public static EncryptionInfo Read(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) throw new CorruptDataException(InvalidMessage);
        byte kdf = value[0];
        if (kdf is not ((byte)KdfAlgorithm.Pbkdf2Sha512 or (byte)KdfAlgorithm.Argon2id)) throw new NewerVersionException($"key derivation function {kdf}");
        var info = ReadFields(value[1..], (KdfAlgorithm)kdf, checkCipher: true, out int consumed);
        int extra = value.Length - 1 - consumed;
        if (kdf == (byte)KdfAlgorithm.Argon2id && extra != 0) throw new CorruptDataException(InvalidMessage);
        if (info.Kdf.Check() is { Kind: KdfVerdictKind.Invalid } v) throw new CorruptDataException(v.Reason!);
        return info with { RecordBytes = value.ToArray(), ExtraBytes = extra };
    }

    /// <summary>
    /// The fields after the kdf byte, read in the layout of <paramref name="kdf"/>; <paramref name="consumed"/> is how
    /// many bytes they took. Salvage reads them where they lie in a damaged header and does not check the cipher id
    /// (there is one). Missing bytes or a malformed varint: <see cref="CorruptDataException"/>. Values beyond an int are
    /// kept as int.MaxValue, which the limits refuse.
    /// </summary>
    public static EncryptionInfo ReadFields(ReadOnlySpan<byte> fields, KdfAlgorithm kdf, bool checkCipher, out int consumed)
    {
        try
        {
            var r = new BlobReader(fields);
            KdfParameters p;
            if (kdf == KdfAlgorithm.Pbkdf2Sha512) p = KdfParameters.Pbkdf2(Clamp(r.U()));
            else
            {
                int t = Clamp(r.U()), m = Clamp(r.U()), lanes = Clamp(r.U());
                p = KdfParameters.Argon2id(t, m, lanes);
            }
            var salt = r.Raw(SaltBytes).ToArray();
            byte cipher = r.B();
            if (checkCipher && cipher != Aes256Gcm) throw new NewerVersionException($"cipher {cipher}");
            var verifier = r.Raw(VerifierBytes).ToArray();
            consumed = r.Position;
            return new EncryptionInfo(p, salt, verifier);
        }
        catch (InvalidDataException)
        {
            throw new CorruptDataException(InvalidMessage);
        }

        static int Clamp(ulong v) => v > int.MaxValue ? int.MaxValue : (int)v;
    }
}

/// <summary>Archive header (FORMAT §1).</summary>
public sealed class ArchiveHeader
{
    /// <summary><see cref="KdfLimits.MaxPbkdf2Iterations"/>.</summary>
    public const int MaxKdfIterations = KdfLimits.MaxPbkdf2Iterations;
    /// <summary>The exact serialized header bytes (bound into the index's AEAD data).</summary>
    public byte[] Raw { get; private set; } = [];
    /// <summary>The archive's own minor version (offset 10): 0 for archives of Osmium 0.1.x, 1 since 0.2.0.</summary>
    public ushort Minor { get; init; } = Osa.Minor;
    public uint Flags { get; init; }
    public byte[] ArchiveId { get; init; } = RandomNumberGenerator.GetBytes(16);
    public EncryptionInfo? Encryption { get; init; }
    public string? Creator { get; init; }
    public int Length { get; private set; }

    public bool Encrypted => (Flags & Osa.FlagEncrypted) != 0;
    public bool Experimental => (Flags & Osa.FlagExperimental) != 0;

    public byte[] Serialize()
    {
        var ext = new BlobWriter();
        if (Encryption is { } e) ext.U(1).Bytes(e.RecordBytes);
        if (Creator is not null) ext.U(2).Str(Creator);
        var extBytes = ext.ToArray();
        var buf = new byte[36 + extBytes.Length + 4];
        Osa.Magic.CopyTo(buf);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), Osa.Major);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), Minor);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), Flags);
        ArchiveId.CopyTo(buf, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32), (uint)extBytes.Length);
        extBytes.CopyTo(buf, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(36 + extBytes.Length), Osa.Crc(buf.AsSpan(0, 36 + extBytes.Length)));
        Length = buf.Length;
        Raw = buf;
        return buf;
    }

    public static ArchiveHeader Read(Stream s)
    {
        var fixedPart = new byte[36];
        s.Position = 0;
        s.ReadExactly(fixedPart);
        if (!fixedPart.AsSpan(0, 8).SequenceEqual(Osa.Magic)) throw new CorruptDataException("Not an Osmium archive (bad magic).");
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(8));
        if (major != Osa.Major) throw new CorruptDataException($"Unsupported archive format version {major}.x.");
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(10));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(12));
        uint extLen = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(32));
        if (extLen > 1 << 20) throw new CorruptDataException("Header extension area too large.");
        var rest = new byte[extLen + 4];
        s.ReadExactly(rest);
        var all = new byte[36 + extLen];
        fixedPart.CopyTo(all, 0);
        rest.AsSpan(0, (int)extLen).CopyTo(all.AsSpan(36));
        if (Osa.Crc(all) != BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan((int)extLen)))
            throw new CorruptDataException("Archive header is corrupt (CRC mismatch).");
        // Checked after the CRC: an unknown critical flag in an intact header was set by a newer version.
        if ((flags & 0xFFFF & ~Osa.KnownCriticalFlags) is var unknown and not 0)
            throw new NewerVersionException($"archive flag 0x{unknown:X4}");

        EncryptionInfo? enc = null;
        string? creator = null;
        var r = new BlobReader(rest.AsSpan(0, (int)extLen));
        while (!r.End)
        {
            ulong type = r.U();
            var val = r.Bytes(1 << 20);
            switch (type)
            {
                case 1:
                    // At most one: a later record would otherwise replace an earlier one that salvage reads.
                    if (enc is not null) throw new CorruptDataException(EncryptionRecord.InvalidMessage);
                    enc = EncryptionRecord.Read(val);
                    break;
                case 2:
                    creator = Encoding.UTF8.GetString(val);
                    break;
                default:
                    if ((type & 1) == 1) throw new NewerVersionException($"archive feature #{type}");
                    break;
            }
        }
        if (((flags & Osa.FlagEncrypted) != 0) != (enc is not null)) throw new CorruptDataException("Encryption flag and header disagree.");
        var raw = new byte[36 + extLen + 4];
        all.CopyTo(raw, 0);
        rest.AsSpan((int)extLen).CopyTo(raw.AsSpan(36 + (int)extLen));
        return new ArchiveHeader { Minor = minor, Flags = flags, ArchiveId = fixedPart.AsSpan(16, 16).ToArray(), Encryption = enc, Creator = creator, Length = raw.Length, Raw = raw };
    }
}

/// <summary>Footer (FORMAT §4).</summary>
public readonly record struct ArchiveFooter(long IndexOffset, long IndexLength, ulong IdPrefix)
{
    public byte[] Serialize()
    {
        var b = new byte[Osa.FooterSize];
        BinaryPrimitives.WriteInt64LittleEndian(b, IndexOffset);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), IndexLength);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), IdPrefix);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), Osa.Crc(b.AsSpan(0, 24)));
        Osa.FooterMagic.CopyTo(b.AsSpan(28));
        return b;
    }

    public static ArchiveFooter Read(Stream s)
    {
        if (s.Length < Osa.FooterSize + 36) throw new CorruptDataException("File is too small to be an Osmium archive.");
        var b = new byte[Osa.FooterSize];
        s.Position = s.Length - Osa.FooterSize;
        s.ReadExactly(b);
        if (!b.AsSpan(28, 4).SequenceEqual(Osa.FooterMagic)) throw new CorruptDataException("Archive footer missing — the file is truncated or incomplete.");
        if (Osa.Crc(b.AsSpan(0, 24)) != BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24)))
            throw new CorruptDataException("Archive footer is corrupt.");
        return new ArchiveFooter(BinaryPrimitives.ReadInt64LittleEndian(b), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16)));
    }
}

/// <summary>
/// Password-derived keys and AES-256-GCM sealing of frame bodies (FORMAT §5). The encryption key lives in a pinned
/// array that <see cref="Dispose"/> zeroes; after that, sealing and opening throw.
/// </summary>
public sealed class ArchiveKey : IDisposable
{
    /// <summary>PBKDF2 iterations of Compatible protection (<see cref="KdfPresets.CompatibleIterations"/>).</summary>
    public const int DefaultIterations = KdfPresets.CompatibleIterations;
    private readonly byte[] _kenc;
    private volatile bool _disposed;
    /// <summary>The password check value derived with the key (16 bytes; the header stores it).</summary>
    public byte[] Verifier { get; }

    private ArchiveKey(byte[] kenc, byte[] verifier) { _kenc = kenc; Verifier = verifier; }

    /// <summary>
    /// Derives the keys for <paramref name="info"/> through <see cref="PasswordKdf"/>: the reader's checks (or, with
    /// <paramref name="creating"/>, the writer's) come first, then the password text, then one derivation; the master
    /// key is wiped.
    /// </summary>
    public static ArchiveKey Derive(string password, EncryptionInfo info, KdfRun? run = null, bool creating = false)
    {
        ArgumentNullException.ThrowIfNull(info);
        var master = PasswordKdf.DeriveMaster(password, info.Salt, info.Kdf, run ?? new KdfRun(), creating);
        try { return FromMaster(master); }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    /// <summary>PBKDF2 with a bare salt and iteration count, without the reader's minimum (for callers that hold no
    /// header record).</summary>
    public static ArchiveKey Derive(string password, byte[] salt, int iterations) =>
        Derive(password, new EncryptionInfo(iterations, salt, new byte[EncryptionRecord.VerifierBytes]), new KdfRun { Policy = ProtectionPolicy.AllowWeak });

    /// <summary>Kenc = HKDF-Expand(SHA-256, master, 32, "osmium/v1/aead"); verifier = HKDF-Expand(SHA-256, master, 16,
    /// "osmium/v1/verify").</summary>
    internal static ArchiveKey FromMaster(ReadOnlySpan<byte> master)
    {
        var kenc = GC.AllocateArray<byte>(32, pinned: true);
        var verifier = new byte[EncryptionRecord.VerifierBytes];
        HKDF.Expand(HashAlgorithmName.SHA256, master, kenc, "osmium/v1/aead"u8);
        HKDF.Expand(HashAlgorithmName.SHA256, master, verifier, "osmium/v1/verify"u8);
        return new ArchiveKey(kenc, verifier);
    }

    /// <summary>Compares in constant time.</summary>
    public bool Matches(byte[] verifier) => CryptographicOperations.FixedTimeEquals(Verifier, verifier);

    public bool IsDisposed => _disposed;

    /// <summary>Zeroes the encryption key.</summary>
    public void Dispose()
    {
        _disposed = true;
        CryptographicOperations.ZeroMemory(_kenc);
    }

    private byte[] Kenc
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _kenc;
        }
    }

    private static void Nonce(Span<byte> nonce, ulong frame)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, frame);
    }

    /// <summary>AAD = archiveId ‖ frame ‖ (for the index frame) the full header bytes, which authenticates
    /// the header's flags and KDF parameters.</summary>
    private static byte[] Aad(byte[] archiveId, ulong frame, ReadOnlySpan<byte> headerBytes)
    {
        var aad = new byte[24 + headerBytes.Length];
        archiveId.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(aad.AsSpan(16), frame);
        headerBytes.CopyTo(aad.AsSpan(24));
        return aad;
    }

    public byte[] Seal(byte[] archiveId, ulong frame, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> headerBytes = default)
    {
        var output = new byte[plaintext.Length + 16];
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, frame);
        using var gcm = new AesGcm(Kenc, 16);
        gcm.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), Aad(archiveId, frame, headerBytes));
        return output;
    }

    public byte[] Open(byte[] archiveId, ulong frame, ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> headerBytes = default)
    {
        if (sealedData.Length < 16) throw new CorruptDataException("Encrypted frame too short.");
        var output = new byte[sealedData.Length - 16];
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, frame);
        using var gcm = new AesGcm(Kenc, 16);
        try
        {
            gcm.Decrypt(nonce, sealedData[..^16], sealedData[^16..], output, Aad(archiveId, frame, headerBytes));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CorruptDataException($"Encrypted frame {frame} failed authentication (corrupted or tampered).");
        }
        return output;
    }
}

/// <summary>Plaintext block body (FORMAT §2.1).</summary>
public sealed record BlockBody(BlockDescriptor Descriptor, UInt128 RawHash, ReadOnlyMemory<byte> Payload)
{
    public static UInt128 Hash128(ReadOnlySpan<byte> data) => XxHash128.HashToUInt128(data);

    public byte[] SerializePrefix()
    {
        var w = new BlobWriter().Bytes(Descriptor.Serialize());
        Span<byte> h = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(h, RawHash);
        w.Raw(h);
        w.U64(XxHash3.HashToUInt64(Payload.Span));
        w.U((ulong)Payload.Length);
        var prefix = w.ToArray();
        var full = new byte[prefix.Length + 4];
        prefix.CopyTo(full, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(full.AsSpan(prefix.Length), Osa.Crc(prefix));
        return full;
    }

    /// <param name="location">"block N" or "the index", for errors about a pipeline that needs a newer version.</param>
    public static BlockBody Parse(ReadOnlyMemory<byte> body, MethodRegistry registry, string? location = null)
    {
        var span = body.Span;
        var r = new BlobReader(span);
        var desc = r.Bytes(1 << 16).ToArray();
        var hash = BinaryPrimitives.ReadUInt128LittleEndian(r.Raw(16));
        ulong payloadHash = r.U64();
        long payloadLen = (long)r.U();
        int prefixLen = r.Position;
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(r.Raw(4));
        if (Osa.Crc(span[..prefixLen]) != crc) throw new CorruptDataException("Block header is corrupt (CRC mismatch).");
        if (payloadLen != span.Length - r.Position) throw new CorruptDataException("Block payload length mismatch.");
        var payload = body[r.Position..];
        if (XxHash3.HashToUInt64(payload.Span) != payloadHash) throw new CorruptDataException("Block payload is corrupt (hash mismatch).");
        return new BlockBody(BlockDescriptor.Deserialize(desc, registry, location), hash, payload);
    }
}
