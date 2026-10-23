using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TensorLay.Models;

namespace TensorLay.Services;

// The "super-bridge" — a local HTTP server that lets a paired VPS agent run
// commands, move files and drive services on THIS PC. Modelled on the
// thechest Unity MCP bridge (an HTTP server on 127.0.0.1:6510 that executes
// code on the author's machine), but built into TensorLay so it rides the
// SSH tunnel the app already maintains instead of a hand-run `ssh -R`.
//
// Trust model — this is remote code execution on the user's machine, on
// purpose, so every layer is deliberately conservative:
//   * Off by default (AppSettings.AllowRemoteCommandBridge). The listener is
//     only started while the toggle is on; flipping it off Stop()s it.
//   * Binds 127.0.0.1 only. Nothing reaches it except processes on this PC
//     and whatever the SSH tunnel forwards from the VPS (the relay's
//     permitlisten pins 127.0.0.1:<port>, so only the VPS loopback).
//   * Every request must carry `Authorization: Bearer <token>` matching the
//     DPAPI-stored RemoteCommandBridgeToken, compared in constant time.
//   * Only runs while the app window is open — closing TensorLay disposes it.
//   * Every request is surfaced on the activity log via the Log event.
//
// Long commands are the thechest bridge's sharpest edge (its execute_code
// silently returns null past ~30s and deadlocks on piped stdout). We dodge
// both: output is drained asynchronously (OutputDataReceived), the process
// tree is killed on timeout, and `spawn` exists for detached long-lived
// processes that must not be waited on.
public class RemoteBridgeService : IDisposable
{
    private readonly SettingsService _settingsService;

    // Injected by MainViewModel so service control reuses the exact same
    // ServiceViewModel command path the UI uses (state machine, health
    // checks, logging) rather than poking ProcessManager directly.
    private readonly Func<IReadOnlyList<BridgeServiceInfo>> _listServices;
    private readonly Func<string, Task<bool>> _startService;
    private readonly Func<string, Task<bool>> _stopService;
    private readonly Func<GpuInfo?> _getGpu;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly object _lifecycleLock = new();
    private int _boundPort;
    private bool _disposed;

    // Processes started via /bridge/spawn (detached, not awaited). Kept so
    // /bridge/processes and /bridge/kill can see and stop them. exec's own
    // short-lived children are not tracked here — they're awaited inline.
    private readonly ConcurrentDictionary<int, SpawnedProcess> _spawned = new();

    // Cap captured output so a chatty command can't balloon memory. 2 MB each
    // for stdout/stderr; past that we set `truncated` and keep the tail.
    private const int MaxCapturedBytes = 2 * 1024 * 1024;

    public event Action<string>? Log;

    public bool IsRunning
    {
        get { lock (_lifecycleLock) return _listener?.IsListening == true; }
    }

    public int BoundPort
    {
        get { lock (_lifecycleLock) return _boundPort; }
    }

    public RemoteBridgeService(
        SettingsService settingsService,
        Func<IReadOnlyList<BridgeServiceInfo>> listServices,
        Func<string, Task<bool>> startService,
        Func<string, Task<bool>> stopService,
        Func<GpuInfo?> getGpu)
    {
        _settingsService = settingsService;
        _listServices = listServices;
        _startService = startService;
        _stopService = stopService;
        _getGpu = getGpu;
    }

    /// <summary>
    /// Start the listener if the feature is enabled and a token exists. Safe
    /// to call repeatedly — a no-op when already running on the same port,
    /// and a restart when the configured port changed.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            var settings = _settingsService.Load();
            if (!settings.AllowRemoteCommandBridge)
            {
                Log?.Invoke("Not starting — the command bridge is off in Settings.");
                return;
            }
            if (string.IsNullOrEmpty(settings.RemoteCommandBridgeToken))
            {
                Log?.Invoke("Not starting — no bridge token issued yet (enable the toggle in Settings to generate one).");
                return;
            }

            int port = settings.RemoteCommandBridgePort;
            if (_listener?.IsListening == true)
            {
                if (_boundPort == port) return; // already up on the right port
                StopInternal();                 // port changed — restart below
            }

            try
            {
                var listener = new HttpListener();
                // Explicit 127.0.0.1 host: this does NOT need an admin urlacl
                // reservation (only `+`/`*` wildcards do). Loopback-only is
                // the whole point — the tunnel is the only other way in.
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                _listener = listener;
                _boundPort = port;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _ = Task.Run(() => AcceptLoop(listener, token));
                Log?.Invoke($"Command bridge listening on 127.0.0.1:{port}. The VPS agent can now run commands on this PC while this stays on.");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Failed to start on port {port}: {ex.Message}");
                _listener = null;
                _boundPort = 0;
            }
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (_listener?.IsListening == true)
                Log?.Invoke("Command bridge stopped — the VPS agent can no longer run commands here.");
            StopInternal();
        }
    }

    private void StopInternal()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        _cts = null;
        _boundPort = 0;
    }

    private async Task AcceptLoop(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break; // listener stopped — normal shutdown
            }
            catch
            {
                break; // listener disposed under us
            }

            // Handle each request on its own task so a long exec doesn't block
            // the accept loop (and the next request) behind it.
            _ = Task.Run(() => HandleContext(ctx, ct));
        }
    }

    private async Task HandleContext(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        string path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
        if (path.Length == 0) path = "/";

        try
        {
            // Auth on every request except the unauthenticated liveness ping,
            // which returns only a boolean + version so a client can probe the
            // tunnel without the token.
            if (path == "/bridge/ping")
            {
                await WriteJson(ctx, 200, new { ok = true, service = "tensorlay-bridge", version = BridgeVersion });
                return;
            }

            if (!Authorized(req))
            {
                Log?.Invoke($"REJECTED {req.HttpMethod} {path} — bad or missing token.");
                await WriteJson(ctx, 401, new { error = "unauthorized" });
                return;
            }

            switch (path)
            {
                case "/bridge/health":
                    await WriteJson(ctx, 200, new
                    {
                        ok = true,
                        version = BridgeVersion,
                        hostname = Environment.MachineName,
                        user = Environment.UserName,
                        cwd = Directory.GetCurrentDirectory(),
                        // Lets the VPS client pick command syntax: "windows"
                        // (powershell/cmd) or "macos" (zsh/bash/sh).
                        os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
                        shells = OperatingSystem.IsWindows()
                            ? new[] { "powershell", "cmd" }
                            : new[] { "zsh", "bash", "sh" },
                        defaultShell = NormalizeShell(""),
                        home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    });
                    break;
                case "/bridge/exec":
                    await HandleExec(ctx, ct);
                    break;
                case "/bridge/spawn":
                    await HandleSpawn(ctx);
                    break;
                case "/bridge/processes":
                    await WriteProcesses(ctx);
                    break;
                case "/bridge/kill":
                    await HandleKill(ctx);
                    break;
                case "/bridge/put":
                    await HandlePut(ctx);
                    break;
                case "/bridge/get":
                    await HandleGet(ctx);
                    break;
                case "/bridge/services":
                    await WriteJson(ctx, 200, new { services = _listServices() });
                    break;
                case "/bridge/service/start":
                    await HandleServiceControl(ctx, start: true);
                    break;
                case "/bridge/service/stop":
                    await HandleServiceControl(ctx, start: false);
                    break;
                case "/bridge/gpu":
                    await WriteJson(ctx, 200, new { gpu = _getGpu() });
                    break;
                default:
                    await WriteJson(ctx, 404, new { error = "no such endpoint", path });
                    break;
            }
        }
        catch (Exception ex)
        {
            try { await WriteJson(ctx, 500, new { error = ex.Message }); } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private bool Authorized(HttpListenerRequest req)
    {
        var settings = _settingsService.Load();
        // Re-check the toggle on every request — the user can turn it off
        // while the listener is draining an in-flight request.
        if (!settings.AllowRemoteCommandBridge) return false;
        string expected = settings.RemoteCommandBridgeToken;
        if (string.IsNullOrEmpty(expected)) return false;

        string? header = req.Headers["Authorization"];
        if (string.IsNullOrEmpty(header)) return false;
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.Ordinal)) return false;
        string presented = header.Substring(scheme.Length).Trim();

        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ── exec ────────────────────────────────────────────────────────────────
    // Body: { "command": "...", "shell": "powershell"|"cmd" (Windows) or
    //         "zsh"|"bash"|"sh" (macOS), "cwd": "...",
    //         "timeoutSec": 60 }. Runs synchronously, waits up to timeout,
    //         kills the whole process tree on timeout.
    private async Task HandleExec(HttpListenerContext ctx, CancellationToken ct)
    {
        var body = await ReadJson(ctx.Request);
        string command = GetString(body, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            await WriteJson(ctx, 400, new { error = "missing 'command'" });
            return;
        }
        string shell = NormalizeShell(GetString(body, "shell", ""));
        string? cwd = GetString(body, "cwd", "");
        int timeoutSec = GetInt(body, "timeoutSec", 60);
        if (timeoutSec < 1) timeoutSec = 1;
        if (timeoutSec > 3600) timeoutSec = 3600; // hard ceiling: 1h

        Log?.Invoke($"exec [{shell}] ({timeoutSec}s): {Truncate(command, 160)}");

        var psi = BuildShellStartInfo(shell, command, cwd);
        var sw = Stopwatch.StartNew();

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = false };
        var stdout = new BoundedCapture(MaxCapturedBytes);
        var stderr = new BoundedCapture(MaxCapturedBytes);
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.Append(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.Append(e.Data); };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            await WriteJson(ctx, 500, new { error = $"could not start {shell}: {ex.Message}" });
            return;
        }

        bool exited;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                exited = true;
            }
            catch (OperationCanceledException)
            {
                exited = false;
            }
        }

        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { await proc.WaitForExitAsync(new CancellationTokenSource(2000).Token); } catch { }
            sw.Stop();
            Log?.Invoke($"exec timed out after {timeoutSec}s — process tree killed.");
            await WriteJson(ctx, 200, new
            {
                timedOut = true,
                exitCode = (int?)null,
                durationMs = sw.ElapsedMilliseconds,
                stdout = stdout.ToString(),
                stderr = stderr.ToString(),
                truncated = stdout.Truncated || stderr.Truncated,
            });
            return;
        }

        // Give the async readers a beat to flush the final buffered lines
        // after exit before we snapshot the captures.
        await Task.Delay(30).ConfigureAwait(false);
        sw.Stop();
        int exitCode = SafeExitCode(proc);
        await WriteJson(ctx, 200, new
        {
            timedOut = false,
            exitCode,
            durationMs = sw.ElapsedMilliseconds,
            stdout = stdout.ToString(),
            stderr = stderr.ToString(),
            truncated = stdout.Truncated || stderr.Truncated,
        });
    }

    // ── spawn ────────────────────────────────────────────────────────────────
    // Body: { "command": "...", "shell": ..., "cwd": ... }. Launches detached
    // (no stdout capture) so a long-lived server doesn't inherit a pipe we'd
    // block reading — the exact deadlock that froze the thechest editor when
    // an agent spawned llama-server with RedirectStandardOutput. Returns pid.
    private async Task HandleSpawn(HttpListenerContext ctx)
    {
        var body = await ReadJson(ctx.Request);
        string command = GetString(body, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            await WriteJson(ctx, 400, new { error = "missing 'command'" });
            return;
        }
        string shell = NormalizeShell(GetString(body, "shell", ""));
        string? cwd = GetString(body, "cwd", "");

        var psi = BuildShellStartInfo(shell, command, cwd);
        // Detach: no redirected streams, let the child own its own handles.
        psi.RedirectStandardOutput = false;
        psi.RedirectStandardError = false;
        // BuildShellStartInfo sets UTF-8 stream encodings for exec; Process.Start rejects them
        // without redirection ("StandardOutputEncoding is only supported when standard output
        // is redirected") — every spawn failed in 0.9.28.
        psi.StandardOutputEncoding = null;
        psi.StandardErrorEncoding = null;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        try
        {
            var proc = Process.Start(psi);
            if (proc == null)
            {
                await WriteJson(ctx, 500, new { error = "process did not start" });
                return;
            }
            _spawned[proc.Id] = new SpawnedProcess(proc, Truncate(command, 200), DateTime.UtcNow);
            Log?.Invoke($"spawn [{shell}] pid {proc.Id}: {Truncate(command, 160)}");
            await WriteJson(ctx, 200, new { pid = proc.Id });
        }
        catch (Exception ex)
        {
            await WriteJson(ctx, 500, new { error = ex.Message });
        }
    }

    private async Task WriteProcesses(HttpListenerContext ctx)
    {
        var list = new List<object>();
        foreach (var kv in _spawned)
        {
            bool alive;
            int? exit = null;
            try { alive = !kv.Value.Process.HasExited; }
            catch { alive = false; }
            if (!alive) { try { exit = kv.Value.Process.ExitCode; } catch { } }
            list.Add(new
            {
                pid = kv.Key,
                command = kv.Value.Command,
                startedUtc = kv.Value.Started,
                alive,
                exitCode = exit,
            });
        }
        // Drop processes that have exited AND been reported once.
        foreach (var kv in _spawned.ToArray())
        {
            try { if (kv.Value.Process.HasExited) { kv.Value.Process.Dispose(); _spawned.TryRemove(kv.Key, out _); } }
            catch { _spawned.TryRemove(kv.Key, out _); }
        }
        await WriteJson(ctx, 200, new { processes = list });
    }

    private async Task HandleKill(HttpListenerContext ctx)
    {
        var body = await ReadJson(ctx.Request);
        int pid = GetInt(body, "pid", 0);
        bool tree = GetBool(body, "tree", true);
        if (pid <= 0)
        {
            await WriteJson(ctx, 400, new { error = "missing 'pid'" });
            return;
        }
        try
        {
            // Allow killing any pid the user owns, not only ones we spawned —
            // an agent recovering a hung editor/server needs that reach, and
            // the whole bridge is already gated behind the opt-in + token.
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: tree);
            _spawned.TryRemove(pid, out _);
            Log?.Invoke($"kill pid {pid} (tree={tree}).");
            await WriteJson(ctx, 200, new { ok = true });
        }
        catch (ArgumentException)
        {
            await WriteJson(ctx, 404, new { error = $"no process with pid {pid}" });
        }
        catch (Exception ex)
        {
            await WriteJson(ctx, 500, new { error = ex.Message });
        }
    }

    // ── file transfer ────────────────────────────────────────────────────────
    // put: raw request body → file at ?path=. get: file at ?path= → raw body.
    // Streamed both ways, so size is bounded only by disk, not memory — the
    // thechest bridge had to base64-chunk through execute_code's 50 KB limit;
    // here the file rides its own request.
    private async Task HandlePut(HttpListenerContext ctx)
    {
        string? target = ctx.Request.QueryString["path"];
        if (string.IsNullOrWhiteSpace(target))
        {
            await WriteJson(ctx, 400, new { error = "missing ?path=" });
            return;
        }
        try
        {
            string full = Path.GetFullPath(target);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            long written;
            await using (var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await ctx.Request.InputStream.CopyToAsync(fs).ConfigureAwait(false);
                written = fs.Length;
            }
            Log?.Invoke($"put {written} bytes -> {full}");
            await WriteJson(ctx, 200, new { ok = true, path = full, bytes = written });
        }
        catch (Exception ex)
        {
            await WriteJson(ctx, 500, new { error = ex.Message });
        }
    }

    private async Task HandleGet(HttpListenerContext ctx)
    {
        string? source = ctx.Request.QueryString["path"];
        if (string.IsNullOrWhiteSpace(source))
        {
            await WriteJson(ctx, 400, new { error = "missing ?path=" });
            return;
        }
        string full;
        try { full = Path.GetFullPath(source); }
        catch (Exception ex) { await WriteJson(ctx, 400, new { error = ex.Message }); return; }

        if (!File.Exists(full))
        {
            await WriteJson(ctx, 404, new { error = $"no such file: {full}" });
            return;
        }
        try
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            ctx.Response.ContentLength64 = fs.Length;
            await fs.CopyToAsync(ctx.Response.OutputStream).ConfigureAwait(false);
            Log?.Invoke($"get {fs.Length} bytes <- {full}");
        }
        catch (Exception ex)
        {
            // Headers may already be sent — best-effort only.
            try { await WriteJson(ctx, 500, new { error = ex.Message }); } catch { }
        }
    }

    private async Task HandleServiceControl(HttpListenerContext ctx, bool start)
    {
        var body = await ReadJson(ctx.Request);
        string id = GetString(body, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            await WriteJson(ctx, 400, new { error = "missing 'id'" });
            return;
        }
        Log?.Invoke($"service {(start ? "start" : "stop")}: {id}");
        bool ok = start ? await _startService(id) : await _stopService(id);
        if (!ok)
        {
            await WriteJson(ctx, 404, new { error = $"unknown service id '{id}'" });
            return;
        }
        await WriteJson(ctx, 200, new { ok = true, id });
    }

    // ── shell plumbing ────────────────────────────────────────────────────────
    // Map the requested shell onto what this OS has. Windows: powershell
    // (default) or cmd. macOS/Linux: zsh (default), bash or sh — a request for
    // powershell/cmd from an older client that always sent one falls back to
    // the default shell rather than failing.
    private static string NormalizeShell(string requested)
    {
        string s = (requested ?? "").Trim().ToLowerInvariant();
        if (OperatingSystem.IsWindows())
            return s == "cmd" ? "cmd" : "powershell";
        return s is "bash" or "sh" or "zsh" ? s : "zsh";
    }

    private static ProcessStartInfo BuildShellStartInfo(string shell, string command, string? cwd)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            psi.WorkingDirectory = cwd;

        if (!OperatingSystem.IsWindows())
        {
            // Login shell (-l) so the agent sees the same PATH as Terminal
            // (Homebrew's shellenv lives in ~/.zprofile); the command goes in
            // as a single argv entry, so no quoting layer to escape.
            psi.FileName = shell switch
            {
                "bash" => "/bin/bash",
                "sh" => "/bin/sh",
                _ => "/bin/zsh",
            };
            if (shell != "sh") psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        else if (shell == "cmd")
        {
            psi.FileName = "cmd.exe";
            // /c with the command as a single argument; cmd handles its own
            // internal quoting. Callers that need complex quoting should pass
            // powershell with -EncodedCommand instead.
            psi.Arguments = "/d /s /c \"" + command.Replace("\"", "\\\"") + "\"";
        }
        else
        {
            // PowerShell via -EncodedCommand (base64 UTF-16LE): the one form
            // that survives arbitrary quotes, newlines and pipes without a
            // temp file. Force UTF-8 output so non-ASCII (Cyrillic paths) is
            // captured intact.
            string wrapped = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ErrorActionPreference='Continue'; " + command;
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
            psi.FileName = "powershell.exe";
            psi.Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";
        }
        return psi;
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    // ── JSON / request helpers ──────────────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = false };

    private static async Task<JsonElement> ReadJson(HttpListenerRequest req)
    {
        using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
        string raw = await sr.ReadToEndAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return default;
        try { return JsonDocument.Parse(raw).RootElement.Clone(); }
        catch { return default; }
    }

    private static async Task WriteJson(HttpListenerContext ctx, int status, object payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOut);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static string GetString(JsonElement o, string key, string fallback = "")
    {
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? fallback;
        return fallback;
    }

    private static int GetInt(JsonElement o, string key, int fallback)
    {
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
            return n;
        return fallback;
    }

    private static bool GetBool(JsonElement o, string key, bool fallback)
    {
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) &&
            (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
            return v.GetBoolean();
        return fallback;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>
    /// Generate a fresh 256-bit bridge token as URL-safe base64. Caller stores
    /// it in settings (DPAPI-encrypted) and shows it so the user can copy it
    /// to the VPS. Returning a new value here instantly invalidates the old
    /// one once saved — that's the "regenerate = lock out" guarantee.
    /// </summary>
    public static string GenerateToken()
    {
        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public const string BridgeVersion = "1.1.0";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lifecycleLock) StopInternal();
        foreach (var kv in _spawned)
        {
            try { kv.Value.Process.Dispose(); } catch { }
        }
        _spawned.Clear();
    }

    private sealed record SpawnedProcess(Process Process, string Command, DateTime Started);

    // Bounded, thread-safe text accumulator for a process's redirected stream.
    private sealed class BoundedCapture
    {
        private readonly int _max;
        private readonly StringBuilder _sb = new();
        private readonly object _gate = new();
        public bool Truncated { get; private set; }

        public BoundedCapture(int max) => _max = max;

        public void Append(string line)
        {
            lock (_gate)
            {
                if (_sb.Length >= _max) { Truncated = true; return; }
                _sb.Append(line).Append('\n');
                if (_sb.Length > _max)
                {
                    Truncated = true;
                    _sb.Length = _max;
                }
            }
        }

        public override string ToString()
        {
            lock (_gate) return _sb.ToString();
        }
    }
}

// Flat DTO for /bridge/services — a projection of ServiceViewModel the bridge
// can serialize without dragging MVVM types across the wire.
public record BridgeServiceInfo(string Id, string Name, int Port, string Category, string State, bool Installed);
