using Osmium.Core.Codecs.Native;
using Osmium.Core.Distributed;

namespace Osmium.Core.Tests.Native;

/// <summary>The encoder generation is part of the cluster's engine fingerprint, so nodes encoding with different
/// generations refuse each other (trial sizes would differ). The handshake between processes of different
/// generations runs in child processes (NativeProcessTests).</summary>
[Collection("Native encoders")]
public sealed class GenerationClusterTests
{
    [Fact]
    public void TheTwoGenerationsHaveDifferentFingerprints()
    {
        var managed = Protocol.EngineFingerprint(EncoderGeneration.Managed1.Id);
        var native = Protocol.EngineFingerprint(EncoderGeneration.Native1.Id);
        Assert.Equal(16, managed.Length);
        Assert.NotEqual(Convert.ToHexString(managed), Convert.ToHexString(native));
    }

    [Fact]
    public void TheFingerprintIsTheActiveGenerations()
    {
        Assert.Equal(Convert.ToHexString(Protocol.EngineFingerprint(EncoderGeneration.Active.Id)),
            Convert.ToHexString(Protocol.EngineFingerprint()));
        Assert.True(NativeEncoders.IsDecided);
    }
}
