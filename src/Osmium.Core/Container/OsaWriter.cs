using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Osmium.Core.Crypto;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Container;

public sealed record OsaWriterOptions
{
    /// <summary>Null: not encrypted. Empty, or text that cannot be stored (an unpaired surrogate): refused.</summary>
    public string? Password { get; init; }
    /// <summary>How the key is derived; null: PBKDF2 with <see cref="KdfIterations"/> when that is set, else the default
    /// protection (<see cref="KdfPresets.Default"/>).</summary>
    public KdfParameters? Kdf { get; init; }
    /// <summary>PBKDF2 iterations, for callers that still choose them (see <see cref="Kdf"/>).</summary>
    public int? KdfIterations { get; init; }
    /// <summary>The most memory the key derivation may use (the build's memory limit).</summary>
    public long KdfMemoryLimit { get; init; } = MemoryBudgets.Default();
    /// <summary>Cancels the key derivation (the build's token).</summary>
    public CancellationToken Cancellation { get; init; }
    /// <summary>Progress of the key derivation, 0 to 1 ("Preparing encryption").</summary>
    public IProgress<double>? KdfProgress { get; init; }
    public bool Experimental { get; init; }
    public string Creator { get; init; } = "Osmium " + (typeof(OsaWriter).Assembly.GetName().Version?.ToString(3) ?? "0");
    /// <summary>Pipeline used for the index frame.</summary>
    public string IndexPipeline { get; init; } = "zstd(level=19)";
    /// <summary>When the archive gets spare copies of its index frame and header (FORMAT §4.1).</summary>
    public SpareCopyPolicy SpareCopies { get; init; } = SpareCopyPolicy.Auto;
    /// <summary><see cref="SpareCopyPolicy.Auto"/>: the block frames' total from which spare copies are written (tests
    /// change it).</summary>
    internal long SpareThreshold { get; init; } = global::Osmium.Core.Container.SpareCopies.Threshold;
    /// <summary>Tests only: a fixed archive id (16 bytes) instead of a random one, for tests that pin an archive's
    /// layout. Real archives always get a random id.</summary>
    internal byte[]? TestFixedArchiveId { get; init; }
    /// <summary>
    /// Tests only, never anywhere else: a fixed key-derivation salt (32 bytes) instead of a random one. The key comes
    /// from the password and the salt, and a frame's nonce is its frame number, so two archives with the same password
    /// and salt would use every nonce twice under one key, which breaks both the secrecy and the integrity of
    /// AES-GCM. With a password it is refused unless <see cref="TestFixedSaltAcknowledged"/> is set too.
    /// </summary>
    internal byte[]? TestFixedSalt { get; init; }
    /// <summary>Tests only: the caller knows that <see cref="TestFixedSalt"/> reuses nonces across archives.</summary>
    internal bool TestFixedSaltAcknowledged { get; init; }
    /// <summary>The minor version written in the header (tests write 0 to make archives as Osmium 0.1.x did).</summary>
    internal ushort HeaderMinor { get; init; } = Osa.Minor;
    /// <summary>Tests: called with the number of earlier writes before each write to the file (a test makes one fail).</summary>
    internal Action<int>? TestBeforeWrite { get; init; }
    /// <summary>The largest index frame this writer writes (<see cref="Osa.MaxIndexFrameBytes"/>; tests lower it).</summary>
    internal long MaxIndexFrame { get; init; } = Osa.MaxIndexFrameBytes;
    /// <summary>Tests only: header records after the ones Osmium writes, as other software might add them (the index is
    /// sealed over them, so only the reader's own rules can refuse them).</summary>
    internal IReadOnlyList<(ulong Type, byte[] Value)>? TestExtraHeaderRecords { get; init; }
    /// <summary>Tests only: bytes after the verifier in the EncryptionInfo record, as other software might write them.</summary>
    internal byte[]? TestEncryptionRecordSuffix { get; init; }

    /// <summary>The key derivation these options ask for (null without a password).</summary>
    internal KdfParameters? ResolvedKdf => Password is null ? null : Kdf ?? (KdfIterations is { } n ? KdfParameters.Pbkdf2(n) : KdfPresets.Default);

    /// <summary>Never prints the password.</summary>
    private bool PrintMembers(StringBuilder b)
    {
        b.Append($"Encrypted = {Password is not null}, Kdf = {ResolvedKdf?.Describe() ?? "none"}, Experimental = {Experimental}, ");
        b.Append($"Creator = {Creator}, IndexPipeline = {IndexPipeline}, SpareCopies = {SpareCopies}");
        return true;
    }
}

/// <summary>
/// Streams an archive to a temporary file next to the destination. Blocks may be added from several threads
/// in any order (frame numbers are assigned in write order). Nothing appears at the destination until
/// <see cref="Commit"/> renames the verified temp file into place.
/// </summary>
/// <remarks>
/// A frame number is sealed at most once under the archive's key (AES-GCM's nonce is the frame number): once
/// <see cref="WriteBlock"/> or <see cref="Finish"/> has thrown, every later call throws too, instead of sealing a number
/// that may already be in the file over other data.
/// </remarks>
public sealed class OsaWriter : IDisposable
{
    internal const string FailedEarlier = "This archive writer failed earlier and cannot continue.";

    private readonly object _lock = new();
    private readonly FileStream _fs;
    private readonly ArchiveHeader _header;
    private readonly ArchiveKey? _key;
    private readonly OsaWriterOptions _options;
    private readonly List<BlockInfo> _blocks = [];
    private bool _finished, _committed, _failed;
    private int _seals;
#if DEBUG
    private readonly HashSet<ulong> _sealed = [];
#endif

    public string FinalPath { get; }
    public string TempPath { get; }
    public long BytesWritten { get { lock (_lock) return _fs.Position; } }

    /// <summary>
    /// Checks the options (an empty password or text that cannot be stored: <see cref="ArgumentException"/>; key
    /// derivation outside the limits or below the minimum, or needing more memory than this PC has or than
    /// <see cref="OsaWriterOptions.KdfMemoryLimit"/>: refused), derives the key, and only then creates the temporary file.
    /// </summary>
    public OsaWriter(string finalPath, OsaWriterOptions? options = null)
    {
        _options = options ?? new OsaWriterOptions();
        FinalPath = Path.GetFullPath(finalPath);
        TempPath = FinalPath + ".partial-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        if (_options.TestFixedArchiveId is { Length: not 16 }) throw new ArgumentException("An archive id is 16 bytes.", nameof(options));
        if (_options.TestFixedSalt is { Length: not 32 }) throw new ArgumentException("A salt is 32 bytes.", nameof(options));
        if (_options.TestFixedSalt is not null && !string.IsNullOrEmpty(_options.Password) && !_options.TestFixedSaltAcknowledged)
            throw new InvalidOperationException("A fixed salt makes archives with the same password reuse their nonces; it is for tests only.");
        var kdf = CheckProtection(_options);
        EncryptionInfo? enc = null;
        if (kdf is not null)
        {
            var salt = _options.TestFixedSalt?.ToArray() ?? RandomNumberGenerator.GetBytes(EncryptionRecord.SaltBytes);
            var run = new KdfRun { MemoryLimitBytes = _options.KdfMemoryLimit, Cancellation = _options.Cancellation, Progress = _options.KdfProgress };
            // The work area is wiped and freed when this returns: compression never runs next to it.
            _key = ArchiveKey.Derive(_options.Password!, new EncryptionInfo(kdf, salt, new byte[EncryptionRecord.VerifierBytes]), run, creating: true);
            enc = new EncryptionInfo(kdf, salt, _key.Verifier);
            if (_options.TestEncryptionRecordSuffix is { } suffix) enc = enc with { RecordBytes = [.. enc.RecordBytes, .. suffix] };
        }
        _header = new ArchiveHeader
        {
            Minor = _options.HeaderMinor,
            Flags = (enc is not null ? Osa.FlagEncrypted : 0) | (_options.Experimental ? Osa.FlagExperimental : 0),
            ArchiveId = _options.TestFixedArchiveId?.ToArray() ?? RandomNumberGenerator.GetBytes(16),
            Encryption = enc,
            // An encrypted archive does not say in the clear which release made it: the creator is in its file list only.
            Creator = enc is null ? _options.Creator : null,
            ExtraRecords = _options.TestExtraHeaderRecords,
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FinalPath)!);
            _fs = new FileStream(TempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            _fs.Write(_header.Serialize());
        }
        catch
        {
            _key?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The writer's checks of the password and its protection, without deriving or writing anything: what the
    /// constructor refuses, this refuses (callers check before they scan or plan). Returns the key derivation, or null
    /// when the options ask for no encryption.
    /// </summary>
    public static KdfParameters? CheckProtection(OsaWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Password is not { } password) return null;
        PasswordKdf.CheckPasswordText(password, creating: true);
        var kdf = options.ResolvedKdf!;
        PasswordKdf.AdmitForWriting(kdf, options.KdfMemoryLimit);
        return kdf;
    }

    public bool Encrypted => _key is not null;

    /// <summary>The header as written (its EncryptionInfo, archive id and bytes).</summary>
    internal ArchiveHeader Header => _header;

    /// <summary>Frames sealed so far (tests).</summary>
    internal int Seals => Volatile.Read(ref _seals);

    /// <summary>The archive's key (the writer disposes it), for reading the archive again without a second derivation:
    /// <see cref="ArchiveKeyCache.Add"/> keeps a copy of its own.</summary>
    internal ArchiveKey? Key => _key;

    /// <summary>True once <see cref="Finish"/> wrote spare copies of the index frame and the header
    /// (<see cref="OsaWriterOptions.SpareCopies"/>; FORMAT §4.1).</summary>
    public bool HasSpareCopies { get; private set; }

    /// <summary>Appends one compressed block; returns its frame number. Thread-safe.</summary>
    public int WriteBlock(BlockDescriptor descriptor, UInt128 rawHash, byte[] payload, long decodeMemory)
    {
        var body = new BlockBody(descriptor, rawHash, payload);
        var prefix = body.SerializePrefix();
        lock (_lock)
        {
            if (_failed) throw new InvalidOperationException(FailedEarlier);
            if (_finished) throw new InvalidOperationException("Archive already finished.");
            try
            {
                int frame = _blocks.Count;
                long offset = _fs.Position;
                long bodyLen = WriteFrame((ulong)frame, 0, prefix, payload);
                _blocks.Add(new BlockInfo(frame, offset, Osa.FrameHeaderSize + bodyLen, descriptor.RawSize, decodeMemory, descriptor));
                return frame;
            }
            catch
            {
                _failed = true;
                throw;
            }
        }
    }

    private int _writes;

    private void Write(ReadOnlySpan<byte> bytes)
    {
        _options.TestBeforeWrite?.Invoke(_writes++);
        _fs.Write(bytes);
    }

    private long WriteFrame(ulong frameNumber, uint extraFlags, byte[] prefix, byte[] payload)
    {
        var (fh, sealedBody, bodyLen) = BuildFrame(frameNumber, extraFlags, prefix, payload, prefix.Length + payload.Length);
        Write(fh);
        if (sealedBody is not null) Write(sealedBody);
        else { Write(prefix); Write(payload); }
        return bodyLen;
    }

    /// <summary>
    /// A frame's header and, for an encrypted archive, its sealed body (null: the body is prefix ‖ payload). An
    /// encrypted body is padded with zero bytes to <paramref name="plainLength"/> before it is sealed.
    /// </summary>
    private (byte[] Header, byte[]? SealedBody, long BodyLength) BuildFrame(ulong frameNumber, uint extraFlags, byte[] prefix, byte[] payload, long plainLength)
    {
        byte[]? sealedBody = null;
        long bodyLen = prefix.Length + payload.Length;
        if (_key is not null)
        {
            var plain = new byte[plainLength];
            prefix.CopyTo(plain, 0);
            payload.CopyTo(plain, prefix.Length);
#if DEBUG
            Debug.Assert(_sealed.Add(frameNumber), $"Frame {frameNumber} sealed twice under one key.");
#endif
            Interlocked.Increment(ref _seals);
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

    /// <summary>Argon2id archives (kdf 2) pad the plaintext of their index frame (FORMAT §5); others never do.</summary>
    private bool PadsIndex => _header.Encryption?.Kdf.Algorithm == KdfAlgorithm.Argon2id;

    /// <summary>
    /// Writes the index frame and footer and flushes to disk. With spare copies (<see cref="OsaWriterOptions.SpareCopies"/>)
    /// the index frame is written twice, with the header copy between the two (FORMAT §4.1): the first copy's offset is
    /// in the index (<see cref="ArchiveIndex.SparesKey"/>), so the index is sealed once and both copies are the same bytes.
    /// </summary>
    public void Finish(IEnumerable<ArchiveEntry> entries, IReadOnlyDictionary<string, byte[]>? metadata = null, MethodRegistry? registry = null)
    {
        registry ??= MethodRegistry.Default;
        lock (_lock)
        {
            if (_failed) throw new InvalidOperationException(FailedEarlier);
            if (_finished) throw new InvalidOperationException("Archive already finished.");
            try
            {
                FinishLocked(entries, metadata, registry);
            }
            catch
            {
                _failed = true;
                throw;
            }
        }
    }

    private void FinishLocked(IEnumerable<ArchiveEntry> entries, IReadOnlyDictionary<string, byte[]>? metadata, MethodRegistry registry)
    {
        var index = new ArchiveIndex();
        index.Blocks.AddRange(_blocks);
        index.Entries.AddRange(entries);
        foreach (var e in index.Entries)
            foreach (var s in e.Segments)
                if (s.Frame < 0 || s.Frame >= _blocks.Count || s.Offset + s.Length > _blocks[s.Frame].RawSize)
                    throw new InvalidOperationException($"Entry '{e.Path}' references an invalid block region.");
        if (metadata is not null) foreach (var kv in metadata) index.Metadata[kv.Key] = kv.Value;
        // The writer owns the creator, as it owns the spares key: one source, in every archive.
        index.Metadata[ArchiveIndex.CreatorKey] = Encoding.UTF8.GetBytes(_options.Creator);
        long spareOffset = _fs.Position, blockFrames = _blocks.Sum(b => b.FrameLength);
        bool spares = _options.SpareCopies switch
        {
            SpareCopyPolicy.Never => false,
            SpareCopyPolicy.Always => _blocks.Count > 0,
            _ => _blocks.Count > 0 && _options.SpareThreshold >= 0 && blockFrames >= _options.SpareThreshold,
        };
        var spec = PipelineSpec.Parse(_options.IndexPipeline, registry);
        // The writer owns the spares key: it says where this archive's spare copy is, or that it has none.
        (byte[] Prefix, byte[] Payload) Encode(bool withSpares)
        {
            if (withSpares) index.Metadata[ArchiveIndex.SparesKey] = SpareCopies.OffsetValue(spareOffset);
            else index.Metadata.Remove(ArchiveIndex.SparesKey);
            var raw = index.Serialize();
            var (desc, payload) = PipelineExecutor.Encode(spec, raw, CodecContext.None);
            return (new BlockBody(desc, BlockBody.Hash128(raw), payload).SerializePrefix(), payload);
        }
        // The plaintext body of the index frame: exact, or padded to the Padmé length in Argon2id archives.
        long PlainLength(byte[] prefix, byte[] payload) =>
            PadsIndex ? BlockBody.PaddedLength(prefix.Length + payload.Length) : prefix.Length + payload.Length;
        var (prefix, payload) = Encode(spares);
        // Auto: only where the copy costs little next to the data. The frame's length is known before it is sealed
        // (the padding is fixed by the length, and the tag adds 16 bytes), so the index is never sealed twice.
        long frameLength = Osa.FrameHeaderSize + PlainLength(prefix, payload) + (_key is null ? 0 : 16);
        if (spares && _options.SpareCopies == SpareCopyPolicy.Auto && frameLength * SpareCopies.MaxIndexShare > blockFrames)
        {
            spares = false;
            (prefix, payload) = Encode(false);
            frameLength = Osa.FrameHeaderSize + PlainLength(prefix, payload) + (_key is null ? 0 : 16);
        }
        // Readers refuse a larger index frame before they read it; nothing is sealed or written then.
        if (frameLength > _options.MaxIndexFrame)
            throw new IOException($"The file list of this archive is too large (more than {SizeText.Format(_options.MaxIndexFrame)} compressed).");
        // Sealed once: the spare copy repeats the same ciphertext, so no plaintext is encrypted twice.
        var (fh, sealedBody, bodyLen) = BuildFrame(Osa.IndexFrameNumber, Osa.FrameFlagIndex, prefix, payload, PlainLength(prefix, payload));
        void WriteIndexFrame()
        {
            Write(fh);
            if (sealedBody is not null) Write(sealedBody);
            else { Write(prefix); Write(payload); }
        }
        if (spares)
        {
            WriteIndexFrame();                                // B
            Write(SpareCopies.HeaderCopy(_header.Raw));   // H
        }
        long offset = _fs.Position;
        WriteIndexFrame();                                    // A
        HasSpareCopies = spares;
        var footer = new ArchiveFooter(offset, Osa.FrameHeaderSize + bodyLen, BinaryPrimitives.ReadUInt64LittleEndian(_header.ArchiveId));
        Write(footer.Serialize());
        _fs.Flush(flushToDisk: true);
        _fs.Dispose(); // closed so the archive can be re-opened for verification before Commit()
        _finished = true;
    }

    /// <summary>Atomically moves the finished temp file to the destination.</summary>
    public void Commit(bool overwrite)
    {
        if (!_finished) throw new InvalidOperationException("Finish() must be called before Commit().");
        _fs.Dispose();
        IO.FileRetry.Move(TempPath, FinalPath, overwrite);
        _committed = true;
    }

    /// <summary>Zeroes the key and, unless committed, deletes the temporary file.</summary>
    public void Dispose()
    {
        _key?.Dispose();
        _fs.Dispose();
        if (!_committed)
        {
            try { File.Delete(TempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
