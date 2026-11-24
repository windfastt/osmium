using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Container;

/// <summary>Constants and low-level records of the .osa format (see docs/FORMAT.md).</summary>
public static class Osa
{
    public static ReadOnlySpan<byte> Magic => [0x4F, 0x53, 0x4D, 0x41, 0x1A, 0x0D, 0x0A, 0x00];
    public static ReadOnlySpan<byte> FrameMagic => "OBLK"u8;
    public static ReadOnlySpan<byte> FooterMagic => "OSAE"u8;
    public const ushort Major = 1, Minor = 0;
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

public sealed record EncryptionInfo(int Iterations, byte[] Salt, byte[] Verifier);

/// <summary>Archive header (FORMAT §1).</summary>
public sealed class ArchiveHeader
{
    public const int MaxKdfIterations = 20_000_000;
    /// <summary>The exact serialized header bytes (bound into the index's AEAD data).</summary>
    public byte[] Raw { get; private set; } = [];
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
        if (Encryption is { } e)
        {
            var v = new BlobWriter().B(1).U((ulong)e.Iterations).Raw(e.Salt).B(1).Raw(e.Verifier).ToArray();
            ext.U(1).Bytes(v);
        }
        if (Creator is not null) ext.U(2).Str(Creator);
        var extBytes = ext.ToArray();
        var buf = new byte[36 + extBytes.Length + 4];
        Osa.Magic.CopyTo(buf);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), Osa.Major);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), Osa.Minor);
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
                    var er = new BlobReader(val);
                    if (er.B() is var kdf and not 1) throw new NewerVersionException($"key derivation function {kdf}");
                    int iters = er.Int(100_000_000);
                    if (iters > MaxKdfIterations) throw new CorruptDataException($"Key derivation cost {iters} exceeds the safety limit.");
                    var salt = er.Raw(32).ToArray();
                    if (er.B() is var aead and not 1) throw new NewerVersionException($"cipher {aead}");
                    var verifier = er.Raw(16).ToArray();
                    enc = new EncryptionInfo(iters, salt, verifier);
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
        return new ArchiveHeader { Flags = flags, ArchiveId = fixedPart.AsSpan(16, 16).ToArray(), Encryption = enc, Creator = creator, Length = raw.Length, Raw = raw };
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

/// <summary>Password-derived keys and AES-256-GCM sealing of frame bodies (FORMAT §5).</summary>
public sealed class ArchiveKey
{
    public const int DefaultIterations = 600_000;
    private readonly byte[] _kenc;
    public byte[] Verifier { get; }

    private ArchiveKey(byte[] kenc, byte[] verifier) { _kenc = kenc; Verifier = verifier; }

    public static ArchiveKey Derive(string password, byte[] salt, int iterations)
    {
        var pw = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        var master = Rfc2898DeriveBytes.Pbkdf2(pw, salt, iterations, HashAlgorithmName.SHA512, 32);
        var kenc = HKDF.Expand(HashAlgorithmName.SHA256, master, 32, "osmium/v1/aead"u8.ToArray());
        var ver = HKDF.Expand(HashAlgorithmName.SHA256, master, 16, "osmium/v1/verify"u8.ToArray());
        CryptographicOperations.ZeroMemory(master);
        CryptographicOperations.ZeroMemory(pw);
        return new ArchiveKey(kenc, ver);
    }

    public bool Matches(byte[] verifier) => CryptographicOperations.FixedTimeEquals(Verifier, verifier);

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
        using var gcm = new AesGcm(_kenc, 16);
        gcm.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), Aad(archiveId, frame, headerBytes));
        return output;
    }

    public byte[] Open(byte[] archiveId, ulong frame, ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> headerBytes = default)
    {
        if (sealedData.Length < 16) throw new CorruptDataException("Encrypted frame too short.");
        var output = new byte[sealedData.Length - 16];
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, frame);
        using var gcm = new AesGcm(_kenc, 16);
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
