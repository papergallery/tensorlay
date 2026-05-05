using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
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

        // System-installer services (Ollama on Windows) install themselves
        // as a tray app that auto-binds its port at user login. By the time
        // the user clicks Start the port is already taken — launching our
        // own `ollama serve` produces:
        //   "listen tcp 127.0.0.1:11434: bind: Only one usage of each
        //    socket address (...) is normally permitted."
        // Treat a bound port as "already running, externally managed":
        // skip launch, log a note, and let HealthCheckService report the
        // real state. Does mean Stop won't kill a tray-launched Ollama —
        // user has to Quit it from the tray icon. Acceptable trade-off.
        if (service.UseSystemInstaller && IsPortBound(service.Port))
        {
            OutputReceived?.Invoke(service.Id,
                $"Port {service.Port} already in use — assuming {service.DisplayName} is " +
                "already running (e.g. autostarted from system tray). Skipping launch.");
            return;
        }

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

    // True iff something on the local machine is already listening on `port`.
    // Probe via TcpListener bind — if it throws SocketException with
    // AddressAlreadyInUse, the port is taken. Stop the listener immediately
    // either way (a successful bind would otherwise hold the port for ~30s
    // in TIME_WAIT and prevent the legitimate service from starting).
    private static bool IsPortBound(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
        finally
        {
            try { listener?.Stop(); } catch { /* ignore */ }
        }
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
