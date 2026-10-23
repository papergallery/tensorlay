using System.IO;

namespace TensorLay.Platform;

// Per-user data locations. Windows keeps the historical %APPDATA%\TensorLay
// (settings, SSH keys, logs, crash.log all live there and existing installs
// must keep finding them). macOS uses ~/Library/Application Support/TensorLay
// — .NET maps SpecialFolder.ApplicationData to ~/.config on macOS, which is
// not where Mac apps keep their state.
public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();

    public static string LogsDir => Path.Combine(DataDir, "logs");

    public static string CrashLogPath => Path.Combine(DataDir, "crash.log");

    // Helper binaries TensorLay downloads for itself on macOS (uv, ollama).
    // Prepended to PATH at startup by PlatformEnv so services resolve them
    // like any system tool.
    public static string ToolsDir => Path.Combine(DataDir, "tools");

    private static string ResolveDataDir()
    {
        if (OperatingSystem.IsMacOS())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "TensorLay");
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TensorLay");
    }

    // Create the data dir; on Unix lock it to the owner (0700) since it holds
    // the SSH private key and bearer tokens in plain files.
    public static void EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(DataDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { /* best-effort */ }
        }
    }

    // chmod 600 on Unix; no-op on Windows (DPAPI covers secrets there).
    public static void RestrictToOwner(string filePath)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* best-effort */ }
    }
}
