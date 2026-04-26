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

            InstallLog?.Invoke(service.Id, $"Cloning {service.GitRepoUrl}...");
            InstallProgress?.Invoke(service.Id, 0.1);

            await RunProcess("git", $"clone {service.GitRepoUrl} \"{targetPath}\"", installDir, service.Id);

            InstallProgress?.Invoke(service.Id, 0.6);

            string requirementsPath = Path.Combine(targetPath, "requirements.txt");
            if (File.Exists(requirementsPath))
            {
                InstallLog?.Invoke(service.Id, "Installing pip requirements...");
                await RunProcess("pip", "install -r requirements.txt", targetPath, service.Id);
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
        InstallProgress?.Invoke(serviceId, 0.1);

        using var httpClient = new HttpClient();
        var bytes = await httpClient.GetByteArrayAsync(OllamaInstallerUrl);
        await File.WriteAllBytesAsync(tempPath, bytes);

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
        if (service.UseSystemInstaller) return;

        string targetPath = string.IsNullOrEmpty(service.RelativeInstallPath)
            ? installDir
            : Path.Combine(installDir, service.RelativeInstallPath);

        if (Directory.Exists(targetPath))
        {
            InstallLog?.Invoke(service.Id, $"Removing {targetPath}...");
            await Task.Run(() => Directory.Delete(targetPath, recursive: true));
            InstallLog?.Invoke(service.Id, "Uninstalled.");
        }
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
