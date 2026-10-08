using System.IO;
using System.Security.Cryptography;
using TensorLay.Models;
using Renci.SshNet;

namespace TensorLay.Services;

public class SshTunnelService : IDisposable
{
    // Hard cap on the underlying SSH library's blocking connect — without
    // this, a routing black-hole leaves the UI command stuck for ~30s.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    // Below the relay's ClientAliveInterval (30 s).
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    // SSH.NET's Disconnect() can block indefinitely on a socket whose network
    // vanished (it waits for the message listener). Tearing down the old session
    // must never hold _connectLock forever — past this we abandon it.
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LivenessCheckInterval = TimeSpan.FromSeconds(5);
    private const int MaxReconnectDelaySeconds = 30;

    private readonly SettingsService _settingsService;
    private SshClient? _client;
    private readonly List<ForwardedPortRemote> _ports = new();
    // Serializes Connect / Disconnect so the auto-reconnect loop and a UI
    // click cannot mutate _client / _ports simultaneously.
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private CancellationTokenSource? _reconnectCts;
    private bool _disposed;

    public SshTunnelService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public bool IsConnected => _client?.IsConnected == true;

    public event Action<bool>? ConnectionStatusChanged;
    public event Action<string>? TunnelLog;

    public async Task Connect(AppSettings settings, Func<IReadOnlyList<int>> portsProvider, CancellationToken ct = default)
    {
        if (_disposed) return;
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisconnectInternal().ConfigureAwait(false);

            if (string.IsNullOrEmpty(settings.SshKeyPath) || !File.Exists(settings.SshKeyPath))
            {
                TunnelLog?.Invoke($"SSH key not found at: {settings.SshKeyPath}");
                ConnectionStatusChanged?.Invoke(false);
                return;
            }

            try
            {
                var keyFile = new PrivateKeyFile(settings.SshKeyPath);
                _client = new SshClient(settings.VpsHost, settings.SshPort, settings.VpsUser, keyFile);
                _client.ConnectionInfo.Timeout = ConnectTimeout;
                // SSH.NET never replies to the server's keepalive@openssh.com probes, so an sshd with
                // ClientAliveInterval counts every idle window as unanswered and drops the session
                // ("Timeout, client not responding" — every session on the relay since 2026-09-30,
                // 14–24 min each). Our own keepalive keeps sshd from ever seeing an idle window, and
                // a failed send flips IsConnected so AutoReconnectLoop notices a dead link in seconds.
                _client.KeepAliveInterval = KeepAliveInterval;
                _client.HostKeyReceived += OnHostKeyReceived;
                _client.ErrorOccurred += OnClientError;

                TunnelLog?.Invoke($"Connecting to {settings.VpsUser}@{settings.VpsHost}:{settings.SshPort}...");
                await _client.ConnectAsync(ct).ConfigureAwait(false);

                // Re-evaluate the port list at connect time so that services started
                // after the initial connect get tunnelled on the next (re)connect.
                var ports = portsProvider();
                foreach (int port in ports)
                {
                    // Explicit "127.0.0.1" boundHost — relay's authorized_keys
                    // pins permitlisten="127.0.0.1:<port>"; the 3-arg overload
                    // sends bind_address="" and sshd refuses on strict match.
                    var forwardedPort = new ForwardedPortRemote("127.0.0.1", (uint)port, "127.0.0.1", (uint)port);
                    _client.AddForwardedPort(forwardedPort);
                    forwardedPort.Start();
                    _ports.Add(forwardedPort);
                    TunnelLog?.Invoke($"Tunnel opened: remote:{port} -> local:{port}");
                }

                TunnelLog?.Invoke("Connected.");
                ConnectionStatusChanged?.Invoke(true);
            }
            catch (OperationCanceledException)
            {
                TunnelLog?.Invoke("Connection cancelled.");
                await DisconnectInternal().ConfigureAwait(false);
                ConnectionStatusChanged?.Invoke(false);
                throw;
            }
            catch (Exception ex)
            {
                // Also a half-built session: SSH connected but a remote forward was
                // refused (e.g. sshd still holds the port for the previous, dead
                // session). Drop it so IsConnected is false and the reconnect loop
                // retries, instead of a "connected" tunnel that forwards nothing.
                TunnelLog?.Invoke($"Connection failed: {ex.Message}");
                await DisconnectInternal().ConfigureAwait(false);
                ConnectionStatusChanged?.Invoke(false);
            }
        }
        finally
        {
            // Catch ObjectDisposedException — the user can hit close (which
            // calls Dispose() and disposes the semaphore) while we're inside
            // ConnectAsync's ~10s window. Without this catch, Release on a
            // disposed semaphore throws on a threadpool thread → process
            // crash. The semaphore is gone, the lock is gone, nothing to
            // release — accept the race and exit quietly.
            try { _connectLock.Release(); }
            catch (ObjectDisposedException) { /* SshTunnelService disposed mid-connect */ }
        }
    }

    public async Task Disconnect()
    {
        _reconnectCts?.Cancel();
        if (_disposed) return;
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectInternal().ConfigureAwait(false);
        }
        finally
        {
            try { _connectLock.Release(); }
            catch (ObjectDisposedException) { /* SshTunnelService disposed mid-disconnect */ }
        }
        TunnelLog?.Invoke("Disconnected.");
        ConnectionStatusChanged?.Invoke(false);
    }

    /// <summary>
    /// Stops the forwards and closes the session. Never throws and never takes
    /// longer than TeardownTimeout: a session whose network is gone may block in
    /// SSH.NET's Disconnect(), and Connect calls this under _connectLock.
    /// </summary>
    private async Task DisconnectInternal()
    {
        var client = _client;
        var ports = _ports.ToList();
        _client = null;
        _ports.Clear();
        if (client == null && ports.Count == 0) return;

        if (client != null)
        {
            client.ErrorOccurred -= OnClientError;
            client.HostKeyReceived -= OnHostKeyReceived;
        }

        var teardown = Task.Run(() =>
        {
            foreach (var port in ports)
            {
                try { port.Stop(); } catch { }
            }
            try { client?.Disconnect(); } catch { }
            try { client?.Dispose(); } catch { }
        });

        try
        {
            await teardown.WaitAsync(TeardownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TunnelLog?.Invoke($"Old SSH session did not close in {TeardownTimeout.TotalSeconds:0}s - abandoned.");
        }
    }

    private void OnClientError(object? sender, Renci.SshNet.Common.ExceptionEventArgs e)
    {
        // Raised on SSH.NET's own threads when the session breaks (network gone,
        // keepalive send failed, server closed). The reconnect loop notices via
        // IsConnected; this only makes the reason visible in the log.
        TunnelLog?.Invoke($"Tunnel error: {e.Exception.Message}");
    }

    /// <summary>
    /// Keeps the tunnel up until Disconnect() or Dispose(): checks the session
    /// every few seconds and reconnects on drop with exponential backoff
    /// (1s, 2s, 4s, ... up to 30s), forever — a PC that lost its network for
    /// hours must come back on its own once the network does.
    /// Settings are re-read and the portsProvider re-evaluated on every attempt,
    /// so a re-pair or services started after the first connect are picked up.
    /// Calling it again replaces the previous loop.
    /// </summary>
    public void AutoReconnectLoop(Func<IReadOnlyList<int>> portsProvider)
    {
        if (_disposed) return;
        // Cancel, not dispose: Disconnect() may still call Cancel() on it.
        _reconnectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        var ct = cts.Token;

        _ = Task.Run(async () =>
        {
            int delaySeconds = 1;
            bool wasUp = IsConnected;

            while (!ct.IsCancellationRequested)
            {
                // Any failure of one attempt is logged and retried — an exception
                // escaping this loop would end reconnects for good, silently.
                try
                {
                    await Task.Delay(LivenessCheckInterval, ct).ConfigureAwait(false);

                    if (IsConnected)
                    {
                        wasUp = true;
                        delaySeconds = 1;
                        continue;
                    }

                    if (wasUp)
                    {
                        // The session died on its own: nothing else reports that,
                        // so the UI would keep showing "Connected".
                        wasUp = false;
                        TunnelLog?.Invoke("Connection lost.");
                        ConnectionStatusChanged?.Invoke(false);
                    }

                    TunnelLog?.Invoke($"Reconnecting in {delaySeconds}s...");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
                    await Connect(_settingsService.Load(), portsProvider, ct).ConfigureAwait(false);

                    if (IsConnected)
                    {
                        wasUp = true;
                        delaySeconds = 1;
                    }
                    else
                    {
                        delaySeconds = Math.Min(delaySeconds * 2, MaxReconnectDelaySeconds);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    TunnelLog?.Invoke($"Reconnect attempt failed: {ex.Message}");
                    delaySeconds = Math.Min(delaySeconds * 2, MaxReconnectDelaySeconds);
                }
            }
        });
    }

    private void OnHostKeyReceived(object? sender, Renci.SshNet.Common.HostKeyEventArgs e)
    {
        // Format matches `ssh-keygen -lf` and what relay.py /pair returns:
        //   SHA256:<base64 of SHA-256 over the SSH wire-format host key>
        // (base64 padding stripped — that's what OpenSSH does too).
        var sha = SHA256.HashData(e.HostKey);
        var observed = "SHA256:" + Convert.ToBase64String(sha).TrimEnd('=');

        // Re-read settings here — the user may have re-paired since Connect
        // was invoked, picking up new fingerprints in another window.
        var pinned = _settingsService.Load().SshHostKeyFingerprints;

        if (pinned.Count == 0)
        {
            // TOFU: legacy paired client (upgraded from < 0.8.0 without
            // re-pairing) has nothing pinned yet. Trust the first server
            // we see and persist it so subsequent connects are verified.
            // The first connect after upgrade is the one-shot trust window.
            TunnelLog?.Invoke($"TOFU: pinning host key {observed}");
            e.CanTrust = true;

            // Persist asynchronously — Save() takes the SettingsService lock
            // and writes disk. Doing that synchronously inside Renci's host
            // key event handler runs while _connectLock is held; if Save
            // throws (disk full, antivirus locking the .tmp file) the user
            // sees a confusing "host key error" instead of a disk error.
            // Trusting the connection first matches the actual TOFU semantics:
            // we already decided to trust, persist is just bookkeeping.
            _ = Task.Run(() =>
            {
                try
                {
                    var fresh = _settingsService.Load();
                    if (!fresh.SshHostKeyFingerprints.Contains(observed))
                    {
                        fresh.SshHostKeyFingerprints.Add(observed);
                        _settingsService.Save(fresh);
                    }
                }
                catch (Exception ex)
                {
                    TunnelLog?.Invoke($"TOFU persist failed (host still trusted): {ex.Message}");
                }
            });
            return;
        }

        if (pinned.Contains(observed))
        {
            e.CanTrust = true;
            return;
        }

        TunnelLog?.Invoke(
            $"HOST KEY MISMATCH — refusing to connect. " +
            $"Server presented {observed}, pinned set: [{string.Join(", ", pinned)}]. " +
            $"If you moved the relay to a new VPS or rotated its SSH host keys, open Settings → Re-pair…");
        e.CanTrust = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();

        foreach (var port in _ports)
        {
            try { port.Stop(); port.Dispose(); } catch { }
        }
        _client?.Dispose();
        _connectLock.Dispose();
    }
}
