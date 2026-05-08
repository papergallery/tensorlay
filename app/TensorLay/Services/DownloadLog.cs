using System.IO;

namespace TensorLay.Services;

// Append-only diagnostic log for every model download — populated in real
// time by ModelDownloader and RemoteInstallViewModel. Lives at
// %APPDATA%\TensorLay\logs\downloads-YYYY-MM-DD.log (one file per local day,
// rotation by date — old files stay around so a post-mortem after a phantom
// "completed" status has something to read).
//
// Why this exists: the 2026-05-08 incident — 11 of 13 install-tasks marked
// completed in relay's DB but no file on disk. With no on-disk trace of the
// download flow, root cause was unrecoverable. Going forward every redirect
// hop, byte count, target path, sha256 result, and final state lands here so
// the next phantom-completed report has a concrete artifact.
internal static class DownloadLog
{
    private static readonly object _lock = new();

    private static string LogPath
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string day = DateTime.Now.ToString("yyyy-MM-dd");
            return Path.Combine(appData, "TensorLay", "logs", $"downloads-{day}.log");
        }
    }

    public static void Info(string taskHandle, string message)
        => Write("INFO", taskHandle, message);

    public static void Warn(string taskHandle, string message)
        => Write("WARN", taskHandle, message);

    public static void Error(string taskHandle, string message)
        => Write("ERROR", taskHandle, message);

    // Writes are best-effort — disk full, AV lock, or perms shouldn't break
    // a download. The lock guards against shredded interleaves when multiple
    // downloads run concurrently.
    private static void Write(string level, string taskHandle, string message)
    {
        try
        {
            string path = LogPath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            lock (_lock)
            {
                using var sw = new StreamWriter(path, append: true);
                sw.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{taskHandle}] {message}");
            }
        }
        catch { /* swallow — logging must never break the caller */ }
    }
}
