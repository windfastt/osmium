using Osmium.Core.Distributed;

namespace Osmium.Desktop;

/// <summary>What the About dialog shows.</summary>
public static class AboutInfo
{
    /// <summary>
    /// The informational version for people: "0.1.2 (commit 9a13971)" for "0.1.2+9a13971d16…" (the SDK appends the
    /// commit). Anything else is returned unchanged.
    /// </summary>
    public static string DisplayVersion(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);
        int plus = informationalVersion.IndexOf('+');
        if (plus <= 0) return informationalVersion;
        string version = informationalVersion[..plus], build = informationalVersion[(plus + 1)..];
        return build.Length >= 7 && build.All(Uri.IsHexDigit) ? $"{version} (commit {build[..7].ToLowerInvariant()})" : informationalVersion;
    }

    /// <summary>
    /// The first 8 hex digits of the engine fingerprint, as logged at startup. Cluster workers only work together
    /// when their fingerprints are equal.
    /// </summary>
    public static string EngineFingerprint() => Convert.ToHexString(Protocol.EngineFingerprint())[..8];
}
