using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using TensorLay.Models;

namespace TensorLay.Services;

public class ProcessManager : IDisposable
{
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    private bool _disposed;

    public event Action<string, string>? OutputReceived;
    public event Action<string, int>? ProcessExited;

    public void StartService(ServiceDefinition service, string installDir)
    {
        if (_processes.TryGetValue(service.Id, out var existing) && IsAlive(existing))
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
            // Value-equality TryRemove: a fresh StartService for the same
            // serviceId may have already replaced this entry with a new
            // Process object. Without value-check we'd yank the new entry
            // out of the dict and orphan a running process — IsRunning then
            // returns false, the user clicks Start again, another instance
            // launches, and so on.
            _processes.TryRemove(new KeyValuePair<string, Process>(serviceId, process));
            // Release process handles — dispose only happens after Exited
            // because reading ExitCode requires the underlying handle.
            try { process.Dispose(); } catch { }
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token);
        }
        catch
        {
            // Process already exited or kill failed
        }
        finally
        {
            _processes.TryRemove(serviceId, out _);
        }
    }

    public bool IsRunning(string serviceId)
    {
        return _processes.TryGetValue(serviceId, out var process) && IsAlive(process);
    }

    // HasExited throws InvalidOperationException if the Process was already
    // disposed (e.g. by the Exited handler racing with this check).
    private static bool IsAlive(Process p)
    {
        try { return !p.HasExited; }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (_, process) in _processes.ToArray())
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        _processes.Clear();
    }
}
