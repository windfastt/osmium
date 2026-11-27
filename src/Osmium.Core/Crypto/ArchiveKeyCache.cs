using System.Diagnostics.CodeAnalysis;
using Osmium.Core.Container;

namespace Osmium.Core.Crypto;

/// <summary>
/// Derived archive keys, so that opening an archive again (to extract after listing, to read back what was just
/// written) costs no second key derivation. Keys are found by the exact EncryptionInfo record of the header (kdf id,
/// parameters, salt and verifier); passwords are never kept. A key from the cache is still checked against the
/// header's verifier on every open. The cache keeps its own copies: what goes in and what comes out are the caller's to
/// dispose, and <see cref="Dispose"/> zeroes the copies held here. Thread-safe.
/// </summary>
public sealed class ArchiveKeyCache : IDisposable
{
    private readonly Dictionary<string, ArchiveKey> _keys = new(StringComparer.Ordinal);
    private bool _disposed;

    private static string Id(EncryptionInfo info) => Convert.ToHexString(info.RecordBytes);

    /// <summary>A copy of the key stored for <paramref name="info"/>; the caller disposes it.</summary>
    public bool TryGet(EncryptionInfo info, [MaybeNullWhen(false)] out ArchiveKey key)
    {
        ArgumentNullException.ThrowIfNull(info);
        lock (_keys)
        {
            key = !_disposed && _keys.TryGetValue(Id(info), out var k) ? k.Copy() : null;
            return key is not null;
        }
    }

    /// <summary>Stores a copy of <paramref name="key"/> for <paramref name="info"/> (replacing one stored before).</summary>
    public void Add(EncryptionInfo info, ArchiveKey key)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(key);
        var copy = key.Copy();
        lock (_keys)
        {
            if (_disposed)
            {
                copy.Dispose();
                throw new ObjectDisposedException(nameof(ArchiveKeyCache));
            }
            if (_keys.Remove(Id(info), out var old)) old.Dispose();
            _keys[Id(info)] = copy;
        }
    }

    public int Count { get { lock (_keys) return _keys.Count; } }

    /// <summary>Zeroes every key held here.</summary>
    public void Dispose()
    {
        lock (_keys)
        {
            _disposed = true;
            foreach (var k in _keys.Values) k.Dispose();
            _keys.Clear();
        }
    }
}
