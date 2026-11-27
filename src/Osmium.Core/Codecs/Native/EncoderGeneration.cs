namespace Osmium.Core.Codecs.Native;

/// <summary>
/// A fixed set of LZMA and Zstandard encoder implementations and settings. A block's method and parameters define
/// its stream, so archives from every generation read the same way; but each generation writes its own bytes, and
/// its golden vectors (tests/Osmium.Core.Tests/Native/Vectors/&lt;id&gt;.txt) pin them. Any change to a shipped
/// encoder is a new generation. <see cref="Shipped"/> is set in the release that first ships a generation; from then
/// on its vectors are never regenerated.
/// </summary>
public sealed record EncoderGeneration(string Id, string Description, bool Shipped)
{
    /// <summary>The built-in encoders: LZMA SDK for C# 22.1.1 and ZstdSharp 0.8.8 (streaming, 1 MiB writes, no
    /// pledged size). The encoders of Osmium 0.1.1.</summary>
    public static readonly EncoderGeneration Managed1 = new("managed-1",
        "Built-in encoders: LZMA SDK for C# 22.1.1 and ZstdSharp 0.8.8", Shipped: true);

    /// <summary>The osmium_native library: LZMA SDK 26.06 (LzmaEnc) and Zstandard 1.5.7 (lib/compress) with the
    /// settings of native/src/osmium_native.c.</summary>
    public static readonly EncoderGeneration Native1 = new("native-1",
        "Native encoders: LZMA SDK 26.06 and Zstandard 1.5.7 (osmium_native)", Shipped: false);

    public static IReadOnlyList<EncoderGeneration> All { get; } = [Managed1, Native1];

    /// <summary>The generation this process encodes with. Reading it decides the encoders (and may load the native
    /// library) if nothing has yet; see <see cref="NativeEncoders"/>.</summary>
    public static EncoderGeneration Active => NativeEncoders.ActiveGeneration;

    public static EncoderGeneration? Find(string id) => All.FirstOrDefault(g => g.Id == id);

    public override string ToString() => Id;
}
