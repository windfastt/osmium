using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Container;

public sealed class PasswordRequiredException() : Exception("This archive is encrypted. A password is required.");
public sealed class WrongPasswordException() : Exception("Wrong password.");

/// <summary>Random-access reader. Thread-safe for concurrent block reads.</summary>
public sealed class OsaReader : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly ArchiveKey? _key;
    private readonly MethodRegistry _registry;

    public string Path { get; }
    public ArchiveHeader Header { get; }
    public ArchiveIndex Index { get; }
    public long FileLength { get; }
    public IReadOnlyList<ArchiveEntry> Entries => Index.Entries;
    public IReadOnlyList<BlockInfo> Blocks => Index.Blocks;

    private OsaReader(string path, SafeFileHandle handle, ArchiveHeader header, ArchiveKey? key, ArchiveIndex index, long length, MethodRegistry reg)
    {
        Path = path; _handle = handle; Header = header; _key = key; Index = index; FileLength = length; _registry = reg;
    }

    /// <param name="password">Called only when the archive is encrypted; return null to cancel.</param>
    public static OsaReader Open(string path, Func<string?>? password = null, MethodRegistry? registry = null)
    {
        registry ??= MethodRegistry.Default;
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            ArchiveHeader header;
            ArchiveFooter footer;
            long length;
            // A separate stream for header/footer: disposing a FileStream built on 'handle' would close it.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
            {
                header = ArchiveHeader.Read(fs);
                footer = ArchiveFooter.Read(fs);
                length = fs.Length;
            }
            if (footer.IdPrefix != BinaryPrimitives.ReadUInt64LittleEndian(header.ArchiveId))
                throw new CorruptDataException("Footer does not belong to this archive.");
            if (footer.IndexOffset < header.Length || footer.IndexOffset + footer.IndexLength + Osa.FooterSize != length)
                throw new CorruptDataException("Index location is invalid (truncated or appended file?).");

            ArchiveKey? key = null;
            if (header.Encrypted)
            {
                var pw = password?.Invoke() ?? throw new PasswordRequiredException();
                key = ArchiveKey.Derive(pw, header.Encryption!.Salt, header.Encryption.Iterations);
                if (!key.Matches(header.Encryption.Verifier)) throw new WrongPasswordException();
            }
            var body = ReadFrame(handle, footer.IndexOffset, footer.IndexLength, Osa.IndexFrameNumber, header, key, registry, isIndex: true);
            var raw = PipelineExecutor.Decode(body.Descriptor, body.Payload, CodecContext.None);
            if (BlockBody.Hash128(raw) != body.RawHash) throw new CorruptDataException("Archive index is corrupt (hash mismatch).");
            var index = ArchiveIndex.Deserialize(raw, header.Length, registry);
            foreach (var b in index.Blocks)
                if (b.Offset + b.FrameLength > footer.IndexOffset) throw new CorruptDataException("Block table points past the index.");
            return new OsaReader(System.IO.Path.GetFullPath(path), handle, header, key, index, length, registry);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static BlockBody ReadFrame(SafeFileHandle h, long offset, long frameLength, ulong expectedFrame,
        ArchiveHeader header, ArchiveKey? key, MethodRegistry reg, bool isIndex)
    {
        if (frameLength < Osa.FrameHeaderSize || frameLength - Osa.FrameHeaderSize > BlockLimits.MaxRawSize * 2)
            throw new CorruptDataException("Invalid frame length.");
        var fh = new byte[Osa.FrameHeaderSize];
        if (RandomAccess.Read(h, fh, offset) != fh.Length) throw new CorruptDataException("Unexpected end of archive.");
        if (!fh.AsSpan(0, 4).SequenceEqual(Osa.FrameMagic)) throw new CorruptDataException($"Frame {expectedFrame} has no frame marker.");
        if (Osa.Crc(fh.AsSpan(0, 24)) != BinaryPrimitives.ReadUInt32LittleEndian(fh.AsSpan(24)))
            throw new CorruptDataException($"Frame {expectedFrame} header is corrupt.");
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(fh.AsSpan(4));
        ulong number = BinaryPrimitives.ReadUInt64LittleEndian(fh.AsSpan(8));
        long bodyLen = BinaryPrimitives.ReadInt64LittleEndian(fh.AsSpan(16));
        string location = isIndex ? "the index" : $"block {expectedFrame}";
        if (number != expectedFrame) throw new CorruptDataException($"Expected frame {expectedFrame} but found {number}.");
        if (((flags & Osa.FrameFlagEncrypted) != 0) != header.Encrypted) throw new CorruptDataException("Frame encryption flag mismatch.");
        if (((flags & Osa.FrameFlagIndex) != 0) != isIndex) throw new CorruptDataException("Frame type mismatch.");
        // The frame header CRC has been checked: a flag this version does not know was set by a newer one.
        if ((flags & ~(Osa.FrameFlagEncrypted | Osa.FrameFlagIndex)) is var unknown and not 0)
            throw new NewerVersionException($"frame flag 0x{unknown:X}", location);
        if (bodyLen != frameLength - Osa.FrameHeaderSize) throw new CorruptDataException("Frame length mismatch.");
        var body = GC.AllocateUninitializedArray<byte>(checked((int)bodyLen));
        int read = 0;
        while (read < body.Length)
        {
            int n = RandomAccess.Read(h, body.AsSpan(read), offset + Osa.FrameHeaderSize + read);
            if (n <= 0) throw new CorruptDataException("Unexpected end of archive.");
            read += n;
        }
        byte[] plain = key is null ? body : key.Open(header.ArchiveId, expectedFrame, body, isIndex ? header.Raw : ReadOnlySpan<byte>.Empty);
        return BlockBody.Parse(plain, reg, location);
    }

    /// <summary>
    /// The first block this version cannot decode because a newer version of Osmium made it, or null. Such an archive
    /// still opens and lists (those blocks show a stand-in pipeline); reading those blocks throws this exception, and
    /// every other block extracts normally.
    /// </summary>
    public NewerVersionException? NewerVersion => Index.Blocks.Select(b => b.Descriptor.NewerVersion).FirstOrDefault(e => e is not null);

    /// <summary>Reads and structurally verifies one frame (CRCs, tag, payload hash) without decoding it.</summary>
    /// <exception cref="NewerVersionException">The block needs a newer version of Osmium.</exception>
    public BlockBody ReadBody(int frame)
    {
        var b = Blocks[frame];
        if (b.Descriptor.NewerVersion is { } newer) throw newer.Again();
        return ReadFrame(_handle, b.Offset, b.FrameLength, (ulong)frame, Header, _key, _registry, isIndex: false);
    }

    /// <summary>Reads, decodes and hash-verifies one block's raw content.</summary>
    /// <exception cref="NewerVersionException">The block needs a newer version of Osmium.</exception>
    public byte[] ReadBlock(int frame, CodecContext? ctx = null)
    {
        var body = ReadBody(frame);
        if (body.Descriptor.RawSize != Blocks[frame].RawSize) throw new CorruptDataException($"Block {frame} size mismatch.");
        var raw = PipelineExecutor.Decode(body.Descriptor, body.Payload, ctx ?? CodecContext.None);
        if (BlockBody.Hash128(raw) != body.RawHash) throw new CorruptDataException($"Block {frame} is corrupt (content hash mismatch).");
        return raw;
    }

    public long TotalUnpacked => Index.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
    public long TotalStoredRaw => Index.Blocks.Sum(b => b.RawSize);
    /// <summary>Total file bytes ÷ stored raw block bytes. Large values mean heavy dedup — or an expansion bomb.</summary>
    public double ExpansionFactor => TotalStoredRaw == 0 ? 1 : (double)TotalUnpacked / TotalStoredRaw;
    public long MaxBlockDecodeMemory => Index.Blocks.Count == 0 ? 0 : Index.Blocks.Max(b => b.DecodeMemory + b.RawSize);
    public double EstimatedDecodeSeconds => Index.Blocks.Sum(b => b.Descriptor.Spec.EstimateDecodeSeconds(b.RawSize));

    public void Dispose() => _handle.Dispose();
}
