using System.Diagnostics;
using System.IO;
using System.Net.Http;
using TensorLay.Models;

namespace TensorLay.Services;

public class InstallerService : IDisposable
{
    private const string OllamaInstallerUrl = "https://ollama.com/download/OllamaSetup.exe";

    private bool _disposed;

    public event Action<string, string>? InstallLog;
    public event Action<string, double>? InstallProgress;

    public async Task Install(ServiceDefinition service, string installDir)
    {
        try
        {
            if (service.UseSystemInstaller)
            {
                await InstallOllama(service.Id);
                return;
            }

            if (string.IsNullOrEmpty(service.GitRepoUrl))
            {
                if (service.Id == "musicgen")
                {
                    throw new NotImplementedException(
                        "MusicGen install is not yet supported. Install manually from https://github.com/facebookresearch/audiocraft.");
                }
                return;
            }

            string targetPath = string.IsNullOrEmpty(service.RelativeInstallPath)
                ? installDir
                : Path.Combine(installDir, service.RelativeInstallPath);

            EnsureOnPath("git", "Git", "https://git-scm.com/download/win");

            Directory.CreateDirectory(installDir);

            InstallLog?.Invoke(service.Id, $"Cloning {service.GitRepoUrl}...");
            InstallProgress?.Invoke(service.Id, 0.1);

            await RunProcess("git", $"clone {service.GitRepoUrl} \"{targetPath}\"", installDir, service.Id);

            InstallProgress?.Invoke(service.Id, 0.6);

            // SD Forge ships only `requirements_versions.txt`; other forks may
            // use either name. Fall back so we don't silently skip the pip step.
            string? requirementsFile = null;
            foreach (var candidate in new[] { "requirements.txt", "requirements_versions.txt" })
            {
                if (File.Exists(Path.Combine(targetPath, candidate)))
                {
                    requirementsFile = candidate;
                    break;
                }
            }
            if (requirementsFile is not null)
            {
                // For sd-forge etc. service.PythonExecutable is "py" with
                // PythonInterpreterArgs="-3.10" — must match the runtime
                // launcher so pip installs into the same Python that runs
                // the service.
                EnsureOnPath(service.PythonExecutable, "Python 3", "https://www.python.org/downloads/windows/");

                // The new Windows Python Launcher (bundled with Python 3.13+)
                // can fetch missing runtimes on demand via `py install 3.10`.
                // Idempotent on the new launcher; will fail loudly on older
                // launchers — we swallow that and let the actual pip step
                // produce the real error if the runtime is still missing.
                if (service.PythonExecutable == "py" && service.PythonInterpreterArgs.StartsWith("-3."))
                {
                    string version = service.PythonInterpreterArgs.Split(' ')[0].TrimStart('-');
                    InstallLog?.Invoke(service.Id, $"Ensuring Python {version} runtime is installed (one-time, ~30 MB)...");
                    try
                    {
                        await RunProcess("py", $"install {version}", targetPath, service.Id);
                    }
                    catch (Exception ex)
                    {
                        InstallLog?.Invoke(service.Id, $"py install warning (continuing): {ex.Message}");
                    }
                }

                InstallLog?.Invoke(service.Id, $"Installing pip requirements ({requirementsFile})...");
                string pipArgs = string.IsNullOrEmpty(service.PythonInterpreterArgs)
                    ? $"-m pip install -r {requirementsFile}"
                    : $"{service.PythonInterpreterArgs} -m pip install -r {requirementsFile}";
                await RunProcess(service.PythonExecutable, pipArgs, targetPath, service.Id);
            }

            InstallProgress?.Invoke(service.Id, 1.0);
            InstallLog?.Invoke(service.Id, "Installation complete.");
        }
        catch (Exception ex)
        {
            InstallLog?.Invoke(service.Id, $"ERROR: {ex.Message}");
            throw;
        }
    }

    private async Task InstallOllama(string serviceId)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");

        InstallLog?.Invoke(serviceId, "Downloading OllamaSetup.exe...");
        InstallProgress?.Invoke(serviceId, 0.0);

        // Default HttpClient.Timeout (100s) cannot finish ~700 MB on a typical
        // residential link — must be raised. Stream into a FileStream instead
        // of GetByteArrayAsync so the whole installer doesn't sit in RAM and so
        // we can report progress.
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using (var response = await httpClient.GetAsync(OllamaInstallerUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tempPath);

            var buffer = new byte[81920];
            long copied = 0;
            int read;
            int reportTick = 0;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                copied += read;
                // 0.0..0.45 = download phase, 0.5+ = installer running phase.
                // Throttle progress to ~every 64 reads (~5 MB) to avoid UI churn.
                if (total is > 0 && (++reportTick & 0x3F) == 0)
                {
                    InstallProgress?.Invoke(serviceId, 0.45 * ((double)copied / total.Value));
                }
            }
        }

        InstallProgress?.Invoke(serviceId, 0.5);
        InstallLog?.Invoke(serviceId, "Running Ollama installer...");

        var psi = new ProcessStartInfo(tempPath) { UseShellExecute = true };
        var process = Process.Start(psi);
        if (process is not null)
            await process.WaitForExitAsync();

        InstallProgress?.Invoke(serviceId, 1.0);
        InstallLog?.Invoke(serviceId, "Ollama installation complete.");
    }

    public async Task Uninstall(ServiceDefinition service, string installDir)
    {
        if (service.UseSystemInstaller)
        {
            // Ollama (the only system-installer service today) is installed via
            // its own MSI/setup into Program Files — we have no clean way to
            // call its uninstaller, and silently doing nothing while the UI
            // says "Uninstalled" leaves the user confused on next launch.
            throw new NotSupportedException(
                $"{service.DisplayName} was installed by its own setup. " +
                "Remove it via Windows Settings → Apps & Features.");
        }

        string targetPath = string.IsNullOrEmpty(service.RelativeInstallPath)
            ? installDir
            : Path.Combine(installDir, service.RelativeInstallPath);

        if (Directory.Exists(targetPath))
        {
            InstallLog?.Invoke(service.Id, $"Removing {targetPath}...");
            await Task.Run(() => ForceDeleteDirectory(targetPath));
            InstallLog?.Invoke(service.Id, "Uninstalled.");
        }
    }

    // git on Windows marks pack files (.git/objects/pack/*.idx etc.) as
    // read-only, which makes Directory.Delete throw UnauthorizedAccessException
    // halfway through. Walk the tree and clear the attribute first.
    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
            catch { /* best-effort — Delete will report the real error if it fails */ }
        }

        Directory.Delete(path, recursive: true);
    }

    public bool IsInstalled(ServiceDefinition service, string installDir)
    {
        if (service.UseSystemInstaller)
        {
            // Only Ollama uses UseSystemInstaller today; check whether ollama is on PATH.
            return IsOnPath("ollama");
        }

        string targetPath = string.IsNullOrEmpty(service.RelativeInstallPath)
            ? installDir
            : Path.Combine(installDir, service.RelativeInstallPath);

        return Directory.Exists(targetPath);
    }

    private static void EnsureOnPath(string exe, string friendlyName, string installUrl)
    {
        if (!IsOnPath(exe))
        {
            throw new InvalidOperationException(
                $"{friendlyName} was not found in PATH. Install it from {installUrl}, then restart TensorLay.");
        }
    }

    private static bool IsOnPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                if (File.Exists(Path.Combine(dir, exe + ".exe")) || File.Exists(Path.Combine(dir, exe)))
                    return true;
            }
            catch { /* skip bad path */ }
        }
        return false;
    }

    private async Task RunProcess(string exe, string args, string workingDir, string serviceId)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) InstallLog?.Invoke(serviceId, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) InstallLog?.Invoke(serviceId, e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{exe} exited with code {process.ExitCode}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
