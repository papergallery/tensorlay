using System.Diagnostics;
using System.IO;
using TensorLay.Models;

namespace TensorLay.Services;

public class ProcessManager : IDisposable
{
    private readonly Dictionary<string, Process> _processes = new();
    private bool _disposed;

    public event Action<string, string>? OutputReceived;
    public event Action<string, int>? ProcessExited;

    public void StartService(ServiceDefinition service, string installDir)
    {
        if (_processes.TryGetValue(service.Id, out var existing) && !existing.HasExited)
            return;

        string workingDir = string.IsNullOrEmpty(service.RelativeInstallPath)
            ? installDir
            : Path.Combine(installDir, service.RelativeInstallPath);

        var psi = new ProcessStartInfo
        {
            FileName = service.StartExecutable,
            Arguments = service.StartArguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        string serviceId = service.Id;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                OutputReceived?.Invoke(serviceId, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                OutputReceived?.Invoke(serviceId, e.Data);
        };
        process.Exited += (_, _) =>
        {
            int code = 0;
            try { code = process.ExitCode; } catch { }
            ProcessExited?.Invoke(serviceId, code);
            _processes.Remove(serviceId);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _processes[service.Id] = process;
    }

    public async Task StopService(string serviceId)
    {
        if (!_processes.TryGetValue(serviceId, out var process)) return;

        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch
        {
            // Process already exited or kill failed
        }
        finally
        {
            _processes.Remove(serviceId);
        }
    }

    public bool IsRunning(string serviceId)
    {
        return _processes.TryGetValue(serviceId, out var process) && !process.HasExited;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (_, process) in _processes)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        _processes.Clear();
    }
}
