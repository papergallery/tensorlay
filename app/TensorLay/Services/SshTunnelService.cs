using System.IO;
using TensorLay.Models;
using Renci.SshNet;

namespace TensorLay.Services;

public class SshTunnelService : IDisposable
{
    private SshClient? _client;
    private readonly List<ForwardedPortRemote> _ports = new();
    private CancellationTokenSource? _reconnectCts;
    private bool _disposed;

    public bool IsConnected => _client?.IsConnected == true;

    public event Action<bool>? ConnectionStatusChanged;
    public event Action<string>? TunnelLog;

    public async Task Connect(AppSettings settings, Func<IReadOnlyList<int>> portsProvider)
    {
        await DisconnectInternal();

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

            TunnelLog?.Invoke($"Connecting to {settings.VpsUser}@{settings.VpsHost}:{settings.SshPort}...");
            await Task.Run(() => _client.Connect());

            // Re-evaluate the port list at connect time so that services started
            // after the initial connect get tunnelled on the next (re)connect.
            var ports = portsProvider();
            foreach (int port in ports)
            {
                var forwardedPort = new ForwardedPortRemote((uint)port, "127.0.0.1", (uint)port);
                _client.AddForwardedPort(forwardedPort);
                forwardedPort.Start();
                _ports.Add(forwardedPort);
                TunnelLog?.Invoke($"Tunnel opened: remote:{port} -> local:{port}");
            }

            TunnelLog?.Invoke("Connected.");
            ConnectionStatusChanged?.Invoke(true);
        }
        catch (Exception ex)
        {
            TunnelLog?.Invoke($"Connection failed: {ex.Message}");
            ConnectionStatusChanged?.Invoke(false);
        }
    }

    public async Task Disconnect()
    {
        _reconnectCts?.Cancel();
        await DisconnectInternal();
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

                    await Connect(settings, portsProvider).ConfigureAwait(false);

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
    }
}
