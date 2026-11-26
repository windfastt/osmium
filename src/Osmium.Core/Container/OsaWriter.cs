using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Container;

public sealed record OsaWriterOptions
{
    public string? Password { get; init; }
    public int KdfIterations { get; init; } = ArchiveKey.DefaultIterations;
    public bool Experimental { get; init; }
    public string Creator { get; init; } = "Osmium " + (typeof(OsaWriter).Assembly.GetName().Version?.ToString(3) ?? "0");
    /// <summary>Pipeline used for the index frame.</summary>
    public string IndexPipeline { get; init; } = "zstd(level=19)";
    /// <summary>Block frames totalling at least this many bytes get spare copies of the index and header (tests lower it).</summary>
    internal long SpareThreshold { get; init; } = SpareCopies.Threshold;
    /// <summary>A fixed archive id instead of a random one (tests: 16 bytes).</summary>
    internal byte[]? ArchiveId { get; init; }
    /// <summary>A fixed key-derivation salt instead of a random one (tests: 32 bytes).</summary>
    internal byte[]? Salt { get; init; }
    /// <summary>The minor version written in the header (tests write 0 to make archives as Osmium 0.1.x did).</summary>
    internal ushort HeaderMinor { get; init; } = Osa.Minor;
}

/// <summary>
/// Streams an archive to a temporary file next to the destination. Blocks may be added from several threads
/// in any order (frame numbers are assigned in write order). Nothing appears at the destination until
/// <see cref="Commit"/> renames the verified temp file into place.
/// </summary>
public sealed class OsaWriter : IDisposable
{
    private readonly object _lock = new();
    private readonly FileStream _fs;
    private readonly ArchiveHeader _header;
    private readonly ArchiveKey? _key;
    private readonly OsaWriterOptions _options;
    private readonly List<BlockInfo> _blocks = [];
    private bool _finished, _committed;

    public string FinalPath { get; }
    public string TempPath { get; }
    public long BytesWritten { get { lock (_lock) return _fs.Position; } }

    public OsaWriter(string finalPath, OsaWriterOptions? options = null)
    {
        _options = options ?? new OsaWriterOptions();
        FinalPath = Path.GetFullPath(finalPath);
        TempPath = FinalPath + ".partial-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        if (_options.ArchiveId is { Length: not 16 }) throw new ArgumentException("An archive id is 16 bytes.", nameof(options));
        if (_options.Salt is { Length: not 32 }) throw new ArgumentException("A salt is 32 bytes.", nameof(options));
        EncryptionInfo? enc = null;
        if (!string.IsNullOrEmpty(_options.Password))
        {
            var salt = _options.Salt?.ToArray() ?? RandomNumberGenerator.GetBytes(32);
            _key = ArchiveKey.Derive(_options.Password, salt, _options.KdfIterations);
            enc = new EncryptionInfo(_options.KdfIterations, salt, _key.Verifier);
        }
        _header = new ArchiveHeader
        {
            Minor = _options.HeaderMinor,
            Flags = (enc is not null ? Osa.FlagEncrypted : 0) | (_options.Experimental ? Osa.FlagExperimental : 0),
            ArchiveId = _options.ArchiveId?.ToArray() ?? RandomNumberGenerator.GetBytes(16),
            Encryption = enc,
            Creator = _options.Creator,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FinalPath)!);
        _fs = new FileStream(TempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        _fs.Write(_header.Serialize());
    }

    public bool Encrypted => _key is not null;

    /// <summary>True once <see cref="Finish"/> wrote spare copies of the index frame and the header (block frames of
    /// at least 1 MiB in total; FORMAT §4.1).</summary>
    public bool HasSpareCopies { get; private set; }

    /// <summary>Appends one compressed block; returns its frame number. Thread-safe.</summary>
    public int WriteBlock(BlockDescriptor descriptor, UInt128 rawHash, byte[] payload, long decodeMemory)
    {
        var body = new BlockBody(descriptor, rawHash, payload);
        var prefix = body.SerializePrefix();
        lock (_lock)
        {
            if (_finished) throw new InvalidOperationException("Archive already finished.");
            int frame = _blocks.Count;
            long offset = _fs.Position;
            long bodyLen = WriteFrame((ulong)frame, 0, prefix, payload);
            _blocks.Add(new BlockInfo(frame, offset, Osa.FrameHeaderSize + bodyLen, descriptor.RawSize, decodeMemory, descriptor));
            return frame;
        }
    }

    private long WriteFrame(ulong frameNumber, uint extraFlags, byte[] prefix, byte[] payload)
    {
        var (fh, sealedBody, bodyLen) = BuildFrame(frameNumber, extraFlags, prefix, payload);
        _fs.Write(fh);
        if (sealedBody is not null) _fs.Write(sealedBody);
        else { _fs.Write(prefix); _fs.Write(payload); }
        return bodyLen;
    }

    /// <summary>A frame's header and, for an encrypted archive, its sealed body (null: the body is prefix ‖ payload).</summary>
    private (byte[] Header, byte[]? SealedBody, long BodyLength) BuildFrame(ulong frameNumber, uint extraFlags, byte[] prefix, byte[] payload)
    {
        byte[]? sealedBody = null;
        long bodyLen = prefix.Length + payload.Length;
        if (_key is not null)
        {
            var plain = new byte[bodyLen];
            prefix.CopyTo(plain, 0);
            payload.CopyTo(plain, prefix.Length);
            sealedBody = _key.Seal(_header.ArchiveId, frameNumber, plain,
                frameNumber == Osa.IndexFrameNumber ? _header.Raw : ReadOnlySpan<byte>.Empty);
            bodyLen = sealedBody.Length;
        }
        var fh = new byte[Osa.FrameHeaderSize];
        Osa.FrameMagic.CopyTo(fh);
        BinaryPrimitives.WriteUInt32LittleEndian(fh.AsSpan(4), (_key is not null ? Osa.FrameFlagEncrypted : 0) | extraFlags);
        BinaryPrimitives.WriteUInt64LittleEndian(fh.AsSpan(8), frameNumber);
        BinaryPrimitives.WriteInt64LittleEndian(fh.AsSpan(16), bodyLen);
        BinaryPrimitives.WriteUInt32LittleEndian(fh.AsSpan(24), Osa.Crc(fh.AsSpan(0, 24)));
        return (fh, sealedBody, bodyLen);
    }

    /// <summary>
    /// Writes the index frame and footer and flushes to disk. When the block frames total at least 1 MiB, the index
    /// frame is written twice, with the header copy between the two (FORMAT §4.1): the first copy's offset is in the
    /// index (<see cref="ArchiveIndex.SparesKey"/>), so the index is sealed once and both copies are the same bytes.
    /// </summary>
    public void Finish(IEnumerable<ArchiveEntry> entries, IReadOnlyDictionary<string, byte[]>? metadata = null, MethodRegistry? registry = null)
    {
        registry ??= MethodRegistry.Default;
        lock (_lock)
        {
            if (_finished) throw new InvalidOperationException("Archive already finished.");
            var index = new ArchiveIndex();
            index.Blocks.AddRange(_blocks);
            index.Entries.AddRange(entries);
            foreach (var e in index.Entries)
                foreach (var s in e.Segments)
                    if (s.Frame < 0 || s.Frame >= _blocks.Count || s.Offset + s.Length > _blocks[s.Frame].RawSize)
                        throw new InvalidOperationException($"Entry '{e.Path}' references an invalid block region.");
            if (metadata is not null) foreach (var kv in metadata) index.Metadata[kv.Key] = kv.Value;
            // The writer owns the spares key: it says where this archive's spare copy is, or that it has none.
            long spareOffset = _fs.Position;
            bool spares = _options.SpareThreshold >= 0 && _blocks.Sum(b => b.FrameLength) >= _options.SpareThreshold;
            if (spares) index.Metadata[ArchiveIndex.SparesKey] = SpareCopies.OffsetValue(spareOffset);
            else index.Metadata.Remove(ArchiveIndex.SparesKey);
            var raw = index.Serialize();
            var spec = PipelineSpec.Parse(_options.IndexPipeline, registry);
            var (desc, payload) = PipelineExecutor.Encode(spec, raw, CodecContext.None);
            var prefix = new BlockBody(desc, BlockBody.Hash128(raw), payload).SerializePrefix();
            // Sealed once: the spare copy repeats the same ciphertext, so no plaintext is encrypted twice.
            var (fh, sealedBody, bodyLen) = BuildFrame(Osa.IndexFrameNumber, Osa.FrameFlagIndex, prefix, payload);
            void WriteIndexFrame()
            {
                _fs.Write(fh);
                if (sealedBody is not null) _fs.Write(sealedBody);
                else { _fs.Write(prefix); _fs.Write(payload); }
            }
            if (spares)
            {
                WriteIndexFrame();                                // B
                _fs.Write(SpareCopies.HeaderCopy(_header.Raw));   // H
            }
            long offset = _fs.Position;
            WriteIndexFrame();                                    // A
            HasSpareCopies = spares;
            var footer = new ArchiveFooter(offset, Osa.FrameHeaderSize + bodyLen, BinaryPrimitives.ReadUInt64LittleEndian(_header.ArchiveId));
            _fs.Write(footer.Serialize());
            _fs.Flush(flushToDisk: true);
            _fs.Dispose(); // closed so the archive can be re-opened for verification before Commit()
            _finished = true;
        }
    }

    /// <summary>Atomically moves the finished temp file to the destination.</summary>
    public void Commit(bool overwrite)
    {
        if (!_finished) throw new InvalidOperationException("Finish() must be called before Commit().");
        _fs.Dispose();
        IO.FileRetry.Move(TempPath, FinalPath, overwrite);
        _committed = true;
    }

    public void Dispose()
    {
        _fs.Dispose();
        if (!_committed)
        {
            try { File.Delete(TempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
