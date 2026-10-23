using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TensorLay.Platform;

namespace TensorLay.Services;

// macOS-only prerequisites, fetched by the app itself so the user never has
// to open a terminal (the Windows build leans on py.exe / OllamaSetup.exe for
// the same jobs):
//   * uv     — creates each service's venv with the exact Python version the
//              registry asks for, downloading that interpreter on demand.
//              Pinned release + sha256, extracted into AppPaths.ToolsDir/bin.
//   * Command Line Tools — provide git (and clang for the occasional sdist).
//              /usr/bin/git is only a stub until they are installed; we
//              trigger Apple's own installer dialog when they are missing.
//   * Ollama — the standalone darwin build (ollama-darwin.tgz), unpacked
//              into AppPaths.ToolsDir/ollama so Start/Stop manage our own
//              `ollama serve` rather than a menu-bar app we can't control.
public static class MacToolchain
{
    private const string UvVersion = "0.12.23";
    private static readonly Dictionary<Architecture, (string Asset, string Sha256)> UvBuilds = new()
    {
        [Architecture.Arm64] = ("uv-aarch64-apple-darwin", "50487ae565ccd96e499056b4674d438f4c53170202617b4c759defe0c6a1b544"),
        [Architecture.X64] = ("uv-x86_64-apple-darwin", "960da44cb4b73685206ddd250b19e0a117fa41095710c1038f081f5cb613efb4"),
    };

    private const string OllamaUrl = "https://github.com/ollama/ollama/releases/latest/download/ollama-darwin.tgz";

    private static readonly TimeSpan IdleReadTimeout = TimeSpan.FromSeconds(60);

    public static string UvPath => Path.Combine(PlatformEnv.ToolsBinDir, "uv");

    public static async Task<string> EnsureUvAsync(Action<string> log)
    {
        if (File.Exists(UvPath)) return UvPath;

        if (!UvBuilds.TryGetValue(RuntimeInformation.OSArchitecture, out var build))
            throw new PlatformNotSupportedException($"No uv build for {RuntimeInformation.OSArchitecture}.");

        log($"Downloading uv {UvVersion} (Python manager, one-time, ~15 MB)...");
        string url = $"https://github.com/astral-sh/uv/releases/download/{UvVersion}/{build.Asset}.tar.gz";
        string tmp = Path.Combine(Path.GetTempPath(), $"{build.Asset}.tar.gz");
        await DownloadAsync(url, tmp, null);

        string actual = await Sha256Async(tmp);
        if (!string.Equals(actual, build.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmp);
            throw new InvalidOperationException($"uv download failed its integrity check (sha256 {actual[..16]}…).");
        }

        string stage = Path.Combine(Path.GetTempPath(), $"uv-stage-{Environment.ProcessId}");
        Directory.CreateDirectory(stage);
        await RunAsync("/usr/bin/tar", new[] { "-xzf", tmp, "-C", stage });
        Directory.CreateDirectory(PlatformEnv.ToolsBinDir);
        foreach (var name in new[] { "uv", "uvx" })
        {
            string src = Path.Combine(stage, build.Asset, name);
            if (File.Exists(src)) File.Move(src, Path.Combine(PlatformEnv.ToolsBinDir, name), overwrite: true);
        }
        try { Directory.Delete(stage, recursive: true); File.Delete(tmp); } catch { /* best-effort */ }

        if (!File.Exists(UvPath))
            throw new InvalidOperationException("uv archive did not contain the uv binary.");
        log($"uv installed at {UvPath}");
        return UvPath;
    }

    // git ships with Apple's Command Line Tools. `xcode-select -p` fails when
    // they are absent; `xcode-select --install` then opens Apple's installer
    // dialog. We can't wait for it from here (it takes minutes and is the
    // user's to approve), so we start it and stop the install with a message.
    public static async Task EnsureCommandLineToolsAsync()
    {
        int code = await RunAsync("/usr/bin/xcode-select", new[] { "-p" }, throwOnError: false);
        if (code == 0) return;

        try { await RunAsync("/usr/bin/xcode-select", new[] { "--install" }, throwOnError: false); }
        catch { /* the dialog is best-effort; the message below still applies */ }
        throw new InvalidOperationException(
            "Apple's Command Line Tools (which provide git) are not installed. " +
            "macOS just opened an installer for them — finish it, then click Install again.");
    }

    public static string OllamaBinary => Path.Combine(PlatformEnv.OllamaDir, "ollama");

    public static async Task InstallOllamaAsync(Action<string> log, Action<double> progress)
    {
        log("Downloading Ollama for macOS (~180 MB)...");
        string tmp = Path.Combine(Path.GetTempPath(), "ollama-darwin.tgz");
        await DownloadAsync(OllamaUrl, tmp, f => progress(0.9 * f));

        log("Unpacking Ollama...");
        Directory.CreateDirectory(PlatformEnv.OllamaDir);
        await RunAsync("/usr/bin/tar", new[] { "-xzf", tmp, "-C", PlatformEnv.OllamaDir });
        try { File.Delete(tmp); } catch { /* best-effort */ }

        if (!File.Exists(OllamaBinary))
        {
            // Layout changed upstream (e.g. bin/ollama) — link whatever we got.
            string? found = Directory.EnumerateFiles(PlatformEnv.OllamaDir, "ollama", SearchOption.AllDirectories).FirstOrDefault();
            if (found is null)
                throw new InvalidOperationException("The Ollama archive did not contain an ollama binary.");
            File.CreateSymbolicLink(OllamaBinary, found);
        }
        progress(1.0);
        log($"Ollama installed at {OllamaBinary}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task DownloadAsync(string url, string target, Action<double>? progress)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;

        await using var src = await response.Content.ReadAsStreamAsync();
        await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write);
        var buffer = new byte[81920];
        long copied = 0;
        int tick = 0;
        while (true)
        {
            using var idle = new CancellationTokenSource(IdleReadTimeout);
            int read;
            try { read = await src.ReadAsync(buffer.AsMemory(), idle.Token); }
            catch (OperationCanceledException) when (idle.IsCancellationRequested)
            {
                throw new IOException($"Download stalled — no data in {IdleReadTimeout.TotalSeconds:0}s ({url}).");
            }
            if (read == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, read));
            copied += read;
            if (progress is not null && total > 0 && (++tick & 0x3F) == 0)
                progress((double)copied / total);
        }
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var fs = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<int> RunAsync(string exe, string[] args, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var stderr = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (throwOnError && p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} exited with code {p.ExitCode}: {(await stderr).Trim()}");
        return p.ExitCode;
    }
}
