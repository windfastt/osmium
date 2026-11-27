namespace Osmium.Core.Codecs.Native;

/// <summary>Which LZMA and Zstandard encoders a process uses.</summary>
public enum EncoderMode
{
    /// <summary>The native encoders if the library loads, otherwise the built-in ones (the default).</summary>
    Auto,
    /// <summary>The native encoders are required: if the library does not load, LZMA and Zstandard compression fail
    /// with the reason.</summary>
    Native,
    /// <summary>The built-in encoders; the library is never loaded.</summary>
    Managed,
}

/// <summary>
/// Decides, once per process, which encoder generation compresses: at the first LZMA or Zstandard encode, or at the
/// first read of <see cref="ActiveGeneration"/> or <see cref="Status"/>, whichever comes first. The decision never
/// changes afterwards, so one archive never mixes generations. The mode comes from <see cref="Configure"/> (the
/// app's setting, the command line's option), otherwise from the environment variable OSMIUM_ENCODERS
/// (auto, native, managed; for CI, tests and support), otherwise <see cref="EncoderMode.Auto"/>.
/// </summary>
public static class NativeEncoders
{
    public const string EnvironmentVariable = "OSMIUM_ENCODERS";

    private static readonly object Gate = new();
    private static EncoderMode? s_configured;
    private static string? s_configuredReason;
    private static volatile Decision? s_decision;

    private sealed record Decision(EncoderMode Mode, NativeStatus Status, string Message, NativeApi? Api, EncoderGeneration Generation);

    /// <summary>Chooses the mode. Valid only before the first use; afterwards it throws, so a caller can never believe
    /// a setting applied when it did not. <paramref name="reason"/> is shown with the status (for example why the
    /// built-in encoders were chosen).</summary>
    public static void Configure(EncoderMode mode, string? reason = null)
    {
        lock (Gate)
        {
            if (s_decision != null)
                throw new InvalidOperationException(
                    $"The encoders were already chosen for this process ({s_decision.Generation.Id}); the setting applies from the next start.");
            s_configured = mode;
            s_configuredReason = reason;
        }
    }

    /// <summary>True once the generation is decided (nothing here decides it).</summary>
    public static bool IsDecided => s_decision != null;

    public static EncoderMode Mode => Decide().Mode;
    public static NativeStatus Status => Decide().Status;
    public static string StatusMessage => Decide().Message;
    public static EncoderGeneration ActiveGeneration => Decide().Generation;

    /// <summary>One line for "osmium version", About and the Log, e.g. "native (LZMA SDK 26.06, Zstandard 1.5.7)" or
    /// "built in (the native library is not in the program folder)".</summary>
    public static string Describe()
    {
        var d = Decide();
        return d.Status == NativeStatus.Active
            ? $"native (LZMA SDK {NativeExpected.LzmaSdkVersion}, Zstandard {NativeExpected.ZstdVersion})"
            : $"built in ({d.Message})";
    }

    /// <summary>The library to encode with, or null for the built-in encoders. In <see cref="EncoderMode.Native"/>
    /// mode without a library it throws with the reason.</summary>
    internal static NativeApi? ForEncode()
    {
        var d = Decide();
        if (d.Api != null) return d.Api;
        if (d.Mode == EncoderMode.Native)
            throw new InvalidOperationException("The native encoders are required but cannot be used: " + d.Message);
        return null;
    }

    private static Decision Decide()
    {
        var d = s_decision;
        if (d != null) return d;
        lock (Gate)
        {
            return s_decision ??= Make();
        }
    }

    private static Decision Make()
    {
        EncoderMode mode;
        string? note = null;
        if (s_configured is { } m) mode = m;
        else
        {
            string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(env)) mode = EncoderMode.Auto;
            else if (!TryParse(env, out mode))
            {
                mode = EncoderMode.Auto;
                note = $"{EnvironmentVariable} has the unknown value '{env}'";
            }
        }
        if (mode == EncoderMode.Managed)
        {
            string why = s_configuredReason ?? "the built-in encoders were chosen";
            return new(mode, NativeStatus.Disabled, why, null, EncoderGeneration.Managed1);
        }
        LoadResult r;
        try { r = NativeLibraryLoader.TryLoad(NativeLibraryLoader.Defaults()); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            r = new(NativeStatus.LoadFailed, "the native library could not be loaded: " + e.Message, null, null);
        }
        string message = note == null ? r.Message : $"{r.Message}; {note}";
        return r.Status == NativeStatus.Active && r.Api != null
            ? new(mode, r.Status, message, r.Api, EncoderGeneration.Native1)
            : new(mode, r.Status, message, null, EncoderGeneration.Managed1);
    }

    /// <summary>Parses auto, native or managed (any case).</summary>
    public static bool TryParse(string? text, out EncoderMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "auto": mode = EncoderMode.Auto; return true;
            case "native": mode = EncoderMode.Native; return true;
            case "managed": mode = EncoderMode.Managed; return true;
            default: mode = EncoderMode.Auto; return false;
        }
    }
}
