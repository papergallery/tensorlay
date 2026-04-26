using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Windows;
using TensorLay.Services;
using TensorLay.ViewModels;
using TensorLay.Views;
namespace TensorLay;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Global\\TensorLay-SingleInstance-9F8E";
    private const string UrlSchemePipeName = "TensorLay-UrlScheme-9F8E";
    private const string UrlScheme = "tensorlay";

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _pipeCts;

    private ProcessManager? _processManager;
    private HealthCheckService? _healthCheckService;
    private InstallerService? _installerService;
    private ModelDownloader? _modelDownloader;
    private GpuMonitor? _gpuMonitor;
    private SshTunnelService? _sshTunnelService;
    private AccountService? _accountService;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // One-time migration of legacy %APPDATA%\GpuHub → %APPDATA%\TensorLay
        // (pre-rename installs stored settings under the old name).
        MigrateLegacyAppData();

        // ── Single-instance: forward URL to existing instance and exit ──
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            string? incomingUrl = FindUrlArg(e.Args);
            if (incomingUrl != null) TryForwardUrl(incomingUrl);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var settingsService = new SettingsService();
        _accountService = new AccountService();
        _accountService.LoadCached();
        _gpuMonitor = new GpuMonitor();
        _processManager = new ProcessManager();
        _healthCheckService = new HealthCheckService();
        _sshTunnelService = new SshTunnelService();
        _installerService = new InstallerService();
        _modelDownloader = new ModelDownloader(settingsService);

        var pairingService = new PairingService();
        var sshKeyService = new SshKeyService();

        var mainVm = new MainViewModel(
            _processManager,
            _healthCheckService,
            _installerService,
            _modelDownloader,
            settingsService,
            _gpuMonitor,
            _sshTunnelService,
            pairingService,
            sshKeyService);

        mainVm.Initialize();

        var settings = settingsService.Load();
        if (settings.AutoconnectTunnel)
            _ = mainVm.ConnectTunnelCommand.ExecuteAsync(null);

        // Show main window
        _mainWindow = new MainWindow { DataContext = mainVm };
        _mainWindow.Show();

        // Start URL pipe server for forwarded sign-in URLs
        _pipeCts = new CancellationTokenSource();
        _ = Task.Run(() => RunUrlPipeServerAsync(_pipeCts.Token));

        // Process the URL the OS launched us with, if any
        string? initialUrl = FindUrlArg(e.Args);
        if (initialUrl != null) _ = HandleUrlAsync(initialUrl);

        // Check for updates
        try
        {
            var updater = new AutoUpdater();
            _mainWindow.Title = $"TensorLay v{updater.CurrentVersion}";
            updater.UpdateLog += msg => System.Diagnostics.Debug.WriteLine($"[updater] {msg}");
            var (available, newVersion, changelog) = await updater.CheckForUpdate();

            if (available && newVersion != null)
            {
                var updateWindow = new UpdateWindow(updater, newVersion, changelog);
                updateWindow.Owner = _mainWindow;
                updateWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                var accepted = updateWindow.ShowDialog() == true && updateWindow.UserAccepted;
                if (accepted)
                {
                    Shutdown();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[updater] update error: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _pipeCts?.Cancel(); } catch { /* ignore */ }
        _processManager?.Dispose();
        _healthCheckService?.Dispose();
        _modelDownloader?.Dispose();
        _gpuMonitor?.Dispose();
        _sshTunnelService?.Dispose();

        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { /* not owned */ }
            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    // ── URL scheme plumbing ────────────────────────────────────────────────

    private static string? FindUrlArg(string[] args)
    {
        return args.FirstOrDefault(a =>
            a.StartsWith(UrlScheme + "://", StringComparison.OrdinalIgnoreCase));
    }

    private static void TryForwardUrl(string url)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", UrlSchemePipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var sw = new StreamWriter(pipe);
            sw.WriteLine(url);
            sw.Flush();
        }
        catch
        {
            // Best-effort — if the pipe is gone the other instance is likely shutting down.
        }
    }

    private async Task RunUrlPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    UrlSchemePipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                using var sr = new StreamReader(pipe);
                string? url = await sr.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    string captured = url;
                    _ = Dispatcher.InvokeAsync(() => HandleUrlAsync(captured));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(500, ct);
            }
        }
    }

    private async Task HandleUrlAsync(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)) return;
        if (!string.Equals(uri.Scheme, UrlScheme, StringComparison.OrdinalIgnoreCase)) return;

        BringToForeground();

        if (!string.Equals(uri.Host, "auth", StringComparison.OrdinalIgnoreCase)) return;

        string? token = ParseQueryParam(uri.Query, "token");
        if (string.IsNullOrWhiteSpace(token))
        {
            MessageBox.Show(_mainWindow,
                "Sign-in URL is missing a token.",
                "TensorLay",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_accountService == null) return;

        try
        {
            var session = await _accountService.SignInWithTokenAsync(token);
            MessageBox.Show(_mainWindow,
                $"Signed in as {session.Username} ({session.Email}).",
                "TensorLay",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(_mainWindow,
                $"Sign-in failed: {ex.Message}",
                "TensorLay",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static void MigrateLegacyAppData()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string oldDir = Path.Combine(appData, "GpuHub");
        string newDir = Path.Combine(appData, "TensorLay");
        if (Directory.Exists(newDir)) return;
        if (!Directory.Exists(oldDir)) return;
        try
        {
            Directory.Move(oldDir, newDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[migration] could not move {oldDir} → {newDir}: {ex.Message}");
        }
    }

    private static string? ParseQueryParam(string query, string name)
    {
        if (string.IsNullOrEmpty(query)) return null;
        if (query.StartsWith("?")) query = query.Substring(1);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq >= 0 ? pair.Substring(0, eq) : pair;
            string value = eq >= 0 ? pair.Substring(eq + 1) : "";
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(value);
        }
        return null;
    }

    private void BringToForeground()
    {
        if (_mainWindow == null) return;
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }
}
