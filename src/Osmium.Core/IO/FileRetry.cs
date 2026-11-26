namespace Osmium.Core.IO;

/// <summary>
/// Antivirus scanners and indexers (e.g. Windows Defender) briefly lock files that were just written, which makes
/// an immediate rename fail with a sharing violation. Retry for up to ~3 seconds before giving up. Only those
/// passing errors are retried; every other error (a name too long or not valid, a folder that is missing) fails at once.
/// </summary>
public static class FileRetry
{
    public static void Move(string source, string destination, bool overwrite)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && Passing(e)
                && attempt < 30 && File.Exists(source) && !(File.Exists(destination) && !overwrite))
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Whether the error is one that goes away by itself: on Windows a sharing or lock violation (another program has
    /// the file open), or access denied, which is what a scanner holding the file being replaced gives. An allow-list:
    /// anything not named here is permanent.
    /// </summary>
    private static bool Passing(Exception e)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (e is UnauthorizedAccessException) return true;
        const int SharingViolation = 32, LockViolation = 33;
        uint hr = unchecked((uint)e.HResult);
        return (hr & 0xFFFF0000u) == 0x80070000u && (hr & 0xFFFF) is SharingViolation or LockViolation;
    }
}
