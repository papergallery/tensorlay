using System.IO;

namespace TensorLay.Platform;

// Process-environment and filesystem-layout differences between Windows and
// macOS that the installer and process manager need.
public static class PlatformEnv
{
    // Directories TensorLay puts on PATH for itself on macOS: our own tool
    // downloads (uv, ollama) first, then the usual Homebrew/user locations and
    // a user-installed Ollama.app. A Mac app launched from Finder inherits only
    // /usr/bin:/bin:/usr/sbin:/sbin, so without this neither our downloads nor
    // Homebrew tools would resolve for services or the command bridge.
    public static string ToolsBinDir => Path.Combine(AppPaths.ToolsDir, "bin");
    public static string OllamaDir => Path.Combine(AppPaths.ToolsDir, "ollama");

    public static void Initialize()
    {
        if (!OperatingSystem.IsMacOS()) return;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var front = new[] { ToolsBinDir, OllamaDir };
        var back = new[]
        {
            "/opt/homebrew/bin",
            "/usr/local/bin",
            Path.Combine(home, ".local", "bin"),
            "/Applications/Ollama.app/Contents/Resources",
            Path.Combine(home, "Applications", "Ollama.app", "Contents", "Resources"),
        };

        var current = (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin:/usr/sbin:/sbin")
            .Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        var result = new List<string>(front);
        result.AddRange(current.Where(p => !front.Contains(p)));
        foreach (var dir in back)
            if (!result.Contains(dir)) result.Add(dir);
        Environment.SetEnvironmentVariable("PATH", string.Join(':', result));

        // Finder-launched apps get no LANG; Python then guesses the C locale
        // for subprocess text and some tools print warnings. Pin UTF-8.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANG")))
            Environment.SetEnvironmentVariable("LANG", "en_US.UTF-8");
    }

    // Interpreter inside a per-service venv: Scripts\python.exe on Windows,
    // bin/python everywhere else.
    public static string VenvPython(string venvDir) => OperatingSystem.IsWindows()
        ? Path.Combine(venvDir, "Scripts", "python.exe")
        : Path.Combine(venvDir, "bin", "python");

    public static string VenvBinDir(string venvDir) => OperatingSystem.IsWindows()
        ? Path.Combine(venvDir, "Scripts")
        : Path.Combine(venvDir, "bin");
}
