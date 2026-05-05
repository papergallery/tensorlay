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
                _client.HostKeyReceived += OnHostKeyReceived;

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
                ConnectionStatusChanged?.Invoke(false);
                throw;
            }
            catch (Exception ex)
            {
                TunnelLog?.Invoke($"Connection failed: {ex.Message}");
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

    private Task DisconnectInternal()
    {
        return Task.Run(() =>
        {
            foreach (var port in _ports)
            {
                try { port.Stop(); } catch { }
            }
            _ports.Clear();

            try { _client?.Disconnect(); } catch { }
            _client?.Dispose();
            _client = null;
        });
    }

    /// <summary>
    /// Starts a background loop that monitors connection and reconnects on drop.
    /// Uses exponential backoff: 1s, 2s, 4s, ... up to 30s.
    /// The portsProvider is re-evaluated on every reconnect attempt so that
    /// services started after the initial connect are picked up.
    /// </summary>
    public void AutoReconnectLoop(AppSettings settings, Func<IReadOnlyList<int>> portsProvider)
    {
        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;

        Task.Run(async () =>
        {
            int delaySeconds = 1;

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

                if (!IsConnected)
                {
                    TunnelLog?.Invoke($"Connection lost. Reconnecting in {delaySeconds}s...");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);

                    try
                    {
                        await Connect(settings, portsProvider, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (IsConnected)
                        delaySeconds = 1;
                    else
                        delaySeconds = Math.Min(delaySeconds * 2, 30);
                }
                else
                {
                    delaySeconds = 1;
                }
            }
        }, ct);
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
            $"If you intentionally rotated SSH host keys on the VPS, re-pair from Settings.");
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
