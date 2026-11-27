using System.Security.Cryptography;
using Osmium.Core.Container;
using Osmium.Core.Crypto;

namespace Osmium.Core.Tests.Crypto;

/// <summary>
/// Pins a small Argon2id archive byte for byte: fixed salt, archive id, creator, times and contents, stored blocks and a
/// stored (uncompressed) file list, so that nothing depends on a compressor's version. The header pins the record of
/// kdf 2 and the flags; the whole file pins the key derivation, the frame layout, the nonces and AAD, and the padding
/// of the file list. A change here is a format change.
/// </summary>
public class KdfGoldenTests
{
    private static readonly byte[] Id = [.. Enumerable.Range(0x10, 16).Select(i => (byte)i)];
    private static readonly byte[] Salt = [.. Enumerable.Range(0x40, 32).Select(i => (byte)i)];
    private const string Password = "golden";

    private static string Write(TempDir t, KdfParameters kdf, SpareCopyPolicy spares) =>
        EncryptedArchives.WriteWithFolder(t.Sub($"golden-{spares}.osa"), new OsaWriterOptions
        {
            Password = Password, Kdf = kdf, Creator = "Osmium golden", IndexPipeline = "store", SpareCopies = spares,
            TestFixedArchiveId = Id, TestFixedSalt = Salt, TestFixedSaltAcknowledged = true,
        }, 3000, 3000);

    // Magic, version 1.1, flags 0x0001, the archive id, extLen 55; record 1 (53 bytes): kdf 2, t 1, m 64 KiB, p 1, the
    // salt, cipher 1, the verifier; no Creator record (the creator is in the encrypted file list); the CRC.
    private const string Header = "4F534D411A0D0A000100010001000000101112131415161718191A1B1C1D1E1F370000000135020140014041424344454647"
        + "48494A4B4C4D4E4F505152535455565758595A5B5C5D5E5F016962D33CA3E17C13AFB58568837C8F892195D4BB";

    [Theory]
    [InlineData(SpareCopyPolicy.Never, 6589, "DCE22E95F7016A5715F17E6EABA676EA76A9A23AF7453B0E5A822F0BC4906D80")]
    [InlineData(SpareCopyPolicy.Always, 11017, "F302544F348F6B9AD59BE998CB2BCDD204611DDE1AF3CCA4716C57A4587B30E1")]
    public void ASmallArgon2idArchiveIsPinned(SpareCopyPolicy spares, int length, string sha256)
    {
        using var t = new TempDir();
        string path = Write(t, KdfParameters.Argon2id(1, 64, 1), spares);
        var b = File.ReadAllBytes(path);
        using var r = OsaReader.Open(path, () => Password);
        Assert.Equal(Header, Convert.ToHexString(r.Header.Raw));
        Assert.Equal(Osa.FlagEncrypted, r.Header.Flags);
        Assert.Equal(length, b.Length);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(b)));
        Assert.Equal((3, 0), (Extractor.Test(r, null, TestContext.Current.CancellationToken).FilesOk, 0));
    }
}
