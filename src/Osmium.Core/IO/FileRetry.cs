namespace Osmium.Core.IO;

/// <summary>
/// Antivirus scanners and indexers (e.g. Windows Defender) briefly lock files that were just written, which makes
/// an immediate rename fail with a sharing violation. Retry for up to ~3 seconds before giving up.
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
            catch (IOException) when (attempt < 30 && File.Exists(source) && !(File.Exists(destination) && !overwrite))
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 30 && File.Exists(source))
            {
                Thread.Sleep(100);
            }
        }
    }
}
