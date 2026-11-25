using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Distributed;

/// <summary>
/// Cluster pairing key: 32 random bytes shown as <c>OSM1-XXXXX-…</c> (base32). Passphrase-derived keys are
/// deliberately not supported: a LAN eavesdropper could brute-force them offline from the handshake.
/// </summary>
public sealed class ClusterKey
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0, 1
    private const string Prefix = "OSM1";
    /// <summary>32 key bytes + 2 checksum bytes = 272 bits = 55 base32 characters (the last one has 3 zero bits).</summary>
    private const int BodyLength = 55;
    public byte[] Master { get; }
    public byte[] AuthKey { get; }
    public byte[] DiscoveryKey { get; }
    public byte[] Tag { get; }

    private ClusterKey(byte[] master)
    {
        Master = master;
        AuthKey = HKDF.Expand(HashAlgorithmName.SHA256, master, 32, "osmium/cluster/auth/v1"u8.ToArray());
        DiscoveryKey = HKDF.Expand(HashAlgorithmName.SHA256, master, 32, "osmium/cluster/disc/v1"u8.ToArray());
        Tag = HMACSHA256.HashData(master, "osmium/cluster-id"u8)[..8];
    }

    public static ClusterKey Generate() => new(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Parses a key the way people paste it: letter case, spaces, line breaks, every kind of dash and invisible
    /// characters (zero-width spaces, byte-order marks, soft hyphens, direction marks) are ignored. Errors are
    /// <see cref="FormatException"/>s with a message that can be shown to the user as is.
    /// </summary>
    public static ClusterKey Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string s = Normalize(text);
        if (s.Length == 0) throw new FormatException("The cluster key is empty.");
        if (!s.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException("This is not an Osmium cluster key: cluster keys start with OSM1-.");
        // The prefix contains '1', so the check for excluded characters starts after it.
        string body = s[Prefix.Length..];
        foreach (char c in body)
        {
            if (Alphabet.Contains(c)) continue;
            if (c is 'I' or 'O' or '0' or '1')
                throw new FormatException($"The cluster key contains '{c}', which cluster keys never use (they have no I, O, 0 or 1). Check the key for a typo.");
            throw new FormatException($"The cluster key contains '{c}' (U+{(int)c:X4}), which is not a cluster key character.");
        }
        if (body.Length < BodyLength)
            throw new FormatException($"The cluster key is too short: {body.Length} of {BodyLength} characters after OSM1-. Copy the whole key.");

        var bytes = new byte[34];
        int buffer = 0, bits = 0, n = 0;
        foreach (char c in body.AsSpan(0, BodyLength))
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8) { bytes[n++] = (byte)(buffer >> (bits - 8)); bits -= 8; }
            buffer &= (1 << bits) - 1;
        }
        var master = bytes[..32];
        bool checksumOk = SHA256.HashData(master).AsSpan(0, 2).SequenceEqual(bytes.AsSpan(32, 2));
        // Version 0.1.1 ignored extra trailing characters and the 3 pad bits, so a saved key that has either keeps
        // working when its first 34 bytes verify (it derives the same master key); anything that does not verify is a typo.
        if (body.Length > BodyLength && !checksumOk)
            throw new FormatException($"The cluster key is too long: {body.Length} of {BodyLength} characters after OSM1-.");
        if (!checksumOk)
            throw new FormatException("The cluster key does not match its checksum. Check it for a typo, or copy it again.");
        return new ClusterKey(master);
    }

    /// <summary>Upper-cases and drops everything that is not part of the key text itself.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (!IsIgnorable(c)) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// Whitespace (including line breaks and no-break spaces), control characters, dashes (U+002D, U+2010–U+2015,
    /// U+2212 minus, U+00AD soft hyphen, U+FE63, U+FF0D and any other dash punctuation) and invisible format
    /// characters (U+200B–U+200D, U+2060 word joiner, U+FEFF byte-order mark, direction marks).
    /// </summary>
    private static bool IsIgnorable(char c)
    {
        if (char.IsWhiteSpace(c) || char.IsControl(c)) return true;
        if (c is '-' or '\u2212' or '\u00AD' or '\uFE63' or '\uFF0D' or (>= '\u2010' and <= '\u2015')) return true;
        if (c is (>= '\u200B' and <= '\u200D') or '\u2060' or '\uFEFF') return true;
        var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is System.Globalization.UnicodeCategory.DashPunctuation or System.Globalization.UnicodeCategory.Format;
    }

    public override string ToString()
    {
        var data = Master.Concat(SHA256.HashData(Master).Take(2)).ToArray();
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        var groups = Enumerable.Range(0, (sb.Length + 4) / 5).Select(i => sb.ToString(i * 5, Math.Min(5, sb.Length - i * 5)));
        return "OSM1-" + string.Join('-', groups);
    }
}

public static class Protocol
{
    public const ushort Version = 1;
    public const int DefaultPort = 46510;
    public const int DiscoveryPort = 46511;
    public const int MaxFrame = (1 << 20) + 1024;
    public const int Chunk = 1 << 20;

    public const byte Caps = 0x10, Heartbeat = 0x11, Reject = 0x12;
    public const byte TaskBegin = 0x20, TaskData = 0x21, TaskEnd = 0x22, TaskCancel = 0x23;
    public const byte ResultBegin = 0x30, ResultData = 0x31, ResultEnd = 0x32;
    public const byte Bye = 0x41;

    public const byte KindTrials = 1, KindCompress = 2;

    /// <summary>Identifies the exact engine build: workers with a different fingerprint are refused, because version
    /// skew would make honest workers fail verification.</summary>
    public static byte[] EngineFingerprint()
    {
        var sb = new StringBuilder($"osa{Container.Osa.Major}.{Container.Osa.Minor};");
        foreach (var m in MethodRegistry.Default.All)
        {
            sb.Append(m.Id).Append(':').Append(m.Name).Append('(');
            foreach (var p in m.Params) sb.Append(p.Name).Append('=').Append(p.Default).Append(',');
            sb.Append(");");
        }
        sb.Append(typeof(Protocol).Assembly.GetName().Version);
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))[..16];
    }
}

/// <summary>
/// Authenticated, encrypted, framed duplex channel.
/// Handshake: Hello(nonce, ephemeral P-256 key, cluster tag, fingerprint) → Challenge(nonce, key, caps) →
/// Auth = HMAC(K_auth, "c-auth" ‖ TH) → AuthOk = HMAC(K_auth, "s-auth" ‖ TH), TH = SHA-256(Hello ‖ Challenge).
/// Session keys = HKDF(K_master ‖ ECDH secret, salt TH): one AES-256-GCM key + IV per direction, nonce = IV ⊕ seq.
/// The strict per-direction sequence rejects replayed or reordered frames.
/// </summary>
public sealed class SecureChannel : IAsyncDisposable
{
    private readonly Stream _s;
    private readonly AesGcm _send, _recv;
    private readonly byte[] _sendIv, _recvIv;
    private ulong _sendSeq, _recvSeq;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string PeerName { get; }
    public int PeerSlots { get; }
    public long PeerMemory { get; }

    private SecureChannel(Stream s, byte[] kSend, byte[] ivSend, byte[] kRecv, byte[] ivRecv, string peer, int slots, long mem)
    {
        _s = s;
        _send = new AesGcm(kSend, 16);
        _recv = new AesGcm(kRecv, 16);
        _sendIv = ivSend;
        _recvIv = ivRecv;
        PeerName = peer; PeerSlots = slots; PeerMemory = mem;
    }

    // ------------------------------------------------------------------ plaintext handshake framing

    private static async Task WritePlainAsync(Stream s, byte[] payload, CancellationToken ct)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
        await s.WriteAsync(len, ct);
        await s.WriteAsync(payload, ct);
        await s.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadPlainAsync(Stream s, CancellationToken ct)
    {
        var len = new byte[4];
        await s.ReadExactlyAsync(len, ct);
        int n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n is < 1 or > 65536) throw new InvalidDataException("Invalid handshake frame.");
        var b = new byte[n];
        await s.ReadExactlyAsync(b, ct);
        return b;
    }

    private static byte[] HelloBytes(byte[] nonce, byte[] pub, byte[] tag, string name, int slots, long mem)
    {
        var w = new IO.BlobWriter().Raw("OSM1"u8).U(Protocol.Version).Raw(Protocol.EngineFingerprint()).Raw(nonce)
            .Bytes(pub).Raw(tag).Str(name).U((ulong)slots).U((ulong)mem);
        return w.ToArray();
    }

    private sealed record Hello(byte[] Fingerprint, byte[] Nonce, byte[] Pub, byte[] Tag, string Name, int Slots, long Mem);

    private static Hello ParseHello(byte[] b)
    {
        var r = new IO.BlobReader(b);
        if (!r.Raw(4).SequenceEqual("OSM1"u8)) throw new InvalidDataException("Not an Osmium peer.");
        int ver = r.Int(65535);
        if (ver != Protocol.Version) throw new InvalidDataException($"Protocol version {ver} is not supported (this node speaks {Protocol.Version}).");
        return new Hello(r.Raw(16).ToArray(), r.Raw(32).ToArray(), r.Bytes(256).ToArray(), r.Raw(8).ToArray(), r.Str(128), r.Int(4096), (long)r.U());
    }

    private static SecureChannel Derive(Stream s, ClusterKey key, ECDiffieHellman mine, byte[] peerPub, byte[] th, bool isClient, Hello peer)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(peerPub, out _);
        var secret = mine.DeriveRawSecretAgreement(other.PublicKey);
        var ikm = key.Master.Concat(secret).ToArray();
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 88, th, "osmium/session/v1"u8.ToArray());
        CryptographicOperations.ZeroMemory(secret);
        CryptographicOperations.ZeroMemory(ikm);
        byte[] kc2s = okm[..32], ks2c = okm[32..64], ivc2s = okm[64..76], ivs2c = okm[76..88];
        return isClient
            ? new SecureChannel(s, kc2s, ivc2s, ks2c, ivs2c, peer.Name, peer.Slots, peer.Mem)
            : new SecureChannel(s, ks2c, ivs2c, kc2s, ivc2s, peer.Name, peer.Slots, peer.Mem);
    }

    public static async Task<SecureChannel> ConnectAsync(Stream s, ClusterKey key, string myName, CancellationToken ct)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = HelloBytes(RandomNumberGenerator.GetBytes(32), ecdh.ExportSubjectPublicKeyInfo(), key.Tag, myName, 0, 0);
        await WritePlainAsync(s, hello, ct);
        var chBytes = await ReadPlainAsync(s, ct);
        if (chBytes.Length > 0 && chBytes[0] == Protocol.Reject)
            throw new InvalidDataException("Worker refused: " + Encoding.UTF8.GetString(chBytes, 1, chBytes.Length - 1));
        var ch = ParseHello(chBytes);
        if (!ch.Fingerprint.AsSpan().SequenceEqual(Protocol.EngineFingerprint()))
            throw new InvalidDataException("Worker runs a different Osmium engine version.");
        var th = SHA256.HashData(hello.Concat(chBytes).ToArray());
        await WritePlainAsync(s, HMACSHA256.HashData(key.AuthKey, "osmium c-auth"u8.ToArray().Concat(th).ToArray()), ct);
        var ok = await ReadPlainAsync(s, ct);
        var expect = HMACSHA256.HashData(key.AuthKey, "osmium s-auth"u8.ToArray().Concat(th).ToArray());
        if (!CryptographicOperations.FixedTimeEquals(ok, expect)) throw new InvalidDataException("Worker failed authentication (wrong cluster key?).");
        return Derive(s, key, ecdh, ch.Pub, th, isClient: true, ch);
    }

    public static async Task<SecureChannel> AcceptAsync(Stream s, ClusterKey key, string myName, int slots, long mem, CancellationToken ct)
    {
        var helloBytes = await ReadPlainAsync(s, ct);
        var hello = ParseHello(helloBytes);
        if (!CryptographicOperations.FixedTimeEquals(hello.Tag, key.Tag))
        {
            await WritePlainAsync(s, [Protocol.Reject, .. "different cluster"u8], ct);
            throw new InvalidDataException("Client belongs to a different cluster.");
        }
        if (!hello.Fingerprint.AsSpan().SequenceEqual(Protocol.EngineFingerprint()))
        {
            await WritePlainAsync(s, [Protocol.Reject, .. "engine version mismatch"u8], ct);
            throw new InvalidDataException("Client runs a different Osmium engine version.");
        }
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ch = HelloBytes(RandomNumberGenerator.GetBytes(32), ecdh.ExportSubjectPublicKeyInfo(), key.Tag, myName, slots, mem);
        await WritePlainAsync(s, ch, ct);
        var th = SHA256.HashData(helloBytes.Concat(ch).ToArray());
        var auth = await ReadPlainAsync(s, ct);
        var expect = HMACSHA256.HashData(key.AuthKey, "osmium c-auth"u8.ToArray().Concat(th).ToArray());
        if (!CryptographicOperations.FixedTimeEquals(auth, expect)) throw new InvalidDataException("Client failed authentication.");
        await WritePlainAsync(s, HMACSHA256.HashData(key.AuthKey, "osmium s-auth"u8.ToArray().Concat(th).ToArray()), ct);
        return Derive(s, key, ecdh, hello.Pub, th, isClient: false, hello);
    }

    // ------------------------------------------------------------------ encrypted frames

    private static void Nonce(Span<byte> nonce, byte[] iv, ulong seq)
    {
        iv.CopyTo(nonce);
        ulong x = BinaryPrimitives.ReadUInt64BigEndian(nonce[4..]) ^ seq;
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], x);
    }

    public async Task SendAsync(byte type, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (body.Length + 1 > Protocol.MaxFrame) throw new ArgumentException("Frame too large; send in chunks.");
        await _sendLock.WaitAsync(ct);
        try
        {
            int plainLen = body.Length + 1;
            var frame = new byte[4 + plainLen + 16];
            BinaryPrimitives.WriteInt32LittleEndian(frame, plainLen + 16);
            var plain = new byte[plainLen];
            plain[0] = type;
            body.Span.CopyTo(plain.AsSpan(1));
            var nonce = new byte[12];
            Nonce(nonce, _sendIv, _sendSeq++);
            _send.Encrypt(nonce, plain, frame.AsSpan(4, plainLen), frame.AsSpan(4 + plainLen, 16), frame.AsSpan(0, 4));
            await _s.WriteAsync(frame, ct);
            await _s.FlushAsync(ct);
        }
        finally { _sendLock.Release(); }
    }

    public async Task<(byte Type, byte[] Body)> ReceiveAsync(CancellationToken ct)
    {
        var len = new byte[4];
        await _s.ReadExactlyAsync(len, ct);
        int n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n < 17 || n > Protocol.MaxFrame + 16) throw new InvalidDataException("Invalid frame length.");
        var data = new byte[n];
        await _s.ReadExactlyAsync(data, ct);
        var plain = new byte[n - 16];
        var nonce = new byte[12];
        Nonce(nonce, _recvIv, _recvSeq++);
        try { _recv.Decrypt(nonce, data.AsSpan(0, n - 16), data.AsSpan(n - 16), plain, len); }
        catch (AuthenticationTagMismatchException) { throw new InvalidDataException("Frame failed authentication (tampering or replay)."); }
        return (plain[0], plain[1..]);
    }

    /// <summary>Sends a large payload as Begin header + 1 MiB data chunks + End.</summary>
    public async Task SendLargeAsync(byte begin, byte[] header, byte data, byte end, ulong taskId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await SendAsync(begin, header, ct);
        for (int off = 0; off < payload.Length; off += Protocol.Chunk)
        {
            int n = Math.Min(Protocol.Chunk, payload.Length - off);
            var b = new byte[8 + n];
            BinaryPrimitives.WriteUInt64LittleEndian(b, taskId);
            payload.Span.Slice(off, n).CopyTo(b.AsSpan(8));
            await SendAsync(data, b, ct);
        }
        var e = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(e, taskId);
        await SendAsync(end, e, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _s.DisposeAsync(); } catch (IOException) { } catch (SocketException) { }
        _send.Dispose();
        _recv.Dispose();
        _sendLock.Dispose();
    }
}
