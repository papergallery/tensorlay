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

        // Apply in-place source patches before launch (idempotent). See
        // ServiceDefinition.SourceReplacements: AllTalk's script.py spawns its
        // TTS engine via a bare `python` subprocess that misses the venv's
        // torch; we rewrite it to sys.executable so the venv interpreter that
        // runs script.py is reused. Done here (start), not only at install, so
        // an already-installed copy is fixed without a reinstall.
        foreach (var (relFile, edits) in service.SourceReplacements)
        {
            try
            {
                string filePath = Path.Combine(workingDir, relFile);
                if (!File.Exists(filePath)) continue;
                string original = File.ReadAllText(filePath);
                string patched = original;
                foreach (var kv in edits)
                    patched = patched.Replace(kv.Key, kv.Value);
                if (patched != original)
                {
                    File.WriteAllText(filePath, patched);
                    OutputReceived?.Invoke(service.Id, $"Patched {relFile} to use the venv interpreter.");
                }
            }
            catch (Exception ex)
            {
                OutputReceived?.Invoke(service.Id, $"Note: could not patch {relFile}: {ex.Message}");
            }
        }

        // Route through the per-service venv when one was provisioned at
        // install time. The presence of .venv/Scripts/python.exe is the
        // ground-truth signal — UsesVenv=true on a 0.9.8-era install that
        // pre-dates venv isolation has no .venv on disk, so we transparently
        // fall back to the legacy py-launcher path with a one-line nudge.
        // The selector-stripped args matter: the launcher form "-3.10 main.py"
        // would be rejected by venv python as an unknown flag.
        string startExe = service.StartExecutable;
        string startArgs = service.StartArguments;
        string? venvDir = null;
        if (service.UsesVenv)
        {
            string venvPython = Path.Combine(workingDir, ".venv", "Scripts", "python.exe");
            if (File.Exists(venvPython))
            {
                startExe = venvPython;
                startArgs = StripVersionSelector(startArgs);
                venvDir = Path.Combine(workingDir, ".venv");
            }
            else
            {
                OutputReceived?.Invoke(service.Id,
                    "Note: this install pre-dates venv isolation. Uninstall + reinstall " +
                    "to migrate dependencies into <install>/.venv/. Falling back to system Python.");
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = startExe,
            Arguments = startArgs,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Force unbuffered stdout/stderr for the child. Python BLOCK-buffers
        // stdout when it's a pipe (which RedirectStandardOutput makes it), so a
        // service that prints progress to stdout — F5-TTS streams "Download
        // Vocos..." then a multi-minute model download, then "Starting app..."
        // — shows NOTHING in our Logs view until it exits or the buffer fills.
        // That reads as a hang/crash when the service is actually working. With
        // PYTHONUNBUFFERED the output streams live. Harmless for non-Python
        // services (they ignore it).
        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

        // Per-service environment variables (added in 0.9.11). Used by:
        //   TripoSR    GRADIO_SERVER_PORT=7862  (gradio_app.py honors it)
        //   MusicGen   GRADIO_SERVER_PORT=7861  (audiocraft demo, same)
        //   AllTalk    COQUI_TOS_AGREED=1       (skip XTTS first-run prompt)
        // Inherits the rest of the user environment so PATH / CUDA_PATH /
        // HF_HOME etc. continue to work — we only add, never replace.
        foreach (var kv in service.EnvironmentVariables)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            psi.EnvironmentVariables[kv.Key] = kv.Value;
        }

        // Activate the venv for the child process. Launching <venv>/Scripts/
        // python.exe directly runs the right interpreter, but it does NOT put
        // the venv on PATH — so any service that shells out to a BARE `python`
        // or `pip` from its own code gets a global interpreter instead. AllTalk
        // is the case in point: script.py spawns its TTS engine via
        // subprocess.Popen(["python", "tts_server.py"]) (and modeldownload.py
        // the same way), so without activation that subprocess dies with
        // "ModuleNotFoundError: No module named 'torch'" even though torch is in
        // the venv — and AllTalk just reports "TTS Subprocess has NOT started up"
        // until it times out. Mirror `activate`: prepend <venv>/Scripts to PATH
        // and set VIRTUAL_ENV (+ clear PYTHONHOME, which would override it).
        if (venvDir is not null)
        {
            string scriptsDir = Path.Combine(venvDir, "Scripts");
            string existingPath = psi.EnvironmentVariables["PATH"] ?? "";
            psi.EnvironmentVariables["PATH"] = scriptsDir + Path.PathSeparator + existingPath;
            psi.EnvironmentVariables["VIRTUAL_ENV"] = venvDir;
            psi.EnvironmentVariables.Remove("PYTHONHOME");
        }

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
            // Surface the exit code in the log. A nonzero code with no Python
            // traceback above usually means a NATIVE crash during import — on
            // Windows e.g. -1073741819 (0xC0000005 access violation) from a
            // bad DLL load, or an OpenMP duplicate-runtime abort. Knowing the
            // code distinguishes "crashed" from "exited cleanly".
            if (code != 0)
                OutputReceived?.Invoke(serviceId, $"Process exited with code {code} (0x{(uint)code:X8}).");
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

    // Mirror of InstallerService.StripVersionSelector — kept private here to
    // avoid a cross-class dependency for one regex. If a third caller
    // appears, hoist to a shared helper.
    private static string StripVersionSelector(string args)
    {
        if (string.IsNullOrEmpty(args)) return args;
        return System.Text.RegularExpressions.Regex.Replace(args, @"^-3\.\d+\s+", "");
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
