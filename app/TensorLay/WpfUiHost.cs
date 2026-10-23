using System.Windows;
using TensorLay.Platform;
using TensorLay.Services;
using TensorLay.ViewModels;
using TensorLay.Views;
using WinForms = System.Windows.Forms;

namespace TensorLay;

// IUiHost for the WPF front end: the window/dialog calls the shared view
// models used to make directly, moved here unchanged. Dialog owners follow
// the old behaviour — the main window, or whichever app window is active
// (so Re-pair… from Settings parents the pairing dialog to Settings).
public sealed class WpfUiHost : IUiHost
{
    private readonly SettingsService _settingsService;
    private readonly PairingService _pairingService;
    private readonly SshKeyService _sshKeyService;

    public WpfUiHost(SettingsService settingsService, PairingService pairingService, SshKeyService sshKeyService)
    {
        _settingsService = settingsService;
        _pairingService = pairingService;
        _sshKeyService = sshKeyService;
    }

    private static Window? MainWindow => Application.Current?.MainWindow;

    private static Window? ActiveWindow =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? MainWindow;

    public void Invoke(Action action)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(action);
        else
            action();
    }

    public T Invoke<T>(Func<T> func)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            return d.Invoke(func);
        return func();
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> func) =>
        Application.Current.Dispatcher.InvokeAsync(func).Task.Unwrap();

    public void ShowMessage(string title, string message, MessageKind kind)
    {
        var image = kind switch
        {
            MessageKind.Error => MessageBoxImage.Error,
            MessageKind.Warning => MessageBoxImage.Warning,
            _ => MessageBoxImage.Information,
        };
        if (MainWindow is { } owner)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, image);
    }

    public Task<(bool Confirmed, bool KeepModels)> AskUninstallAsync(
        string serviceName, string installPath, bool offerKeepModels) =>
        UninstallConfirmWindow.AskAsync(MainWindow, serviceName, installPath, offerKeepModels);

    public Task<bool> ShowPairingAsync()
    {
        var window = new PairingWindow(_pairingService, _sshKeyService, _settingsService)
        {
            Owner = ActiveWindow,
        };
        return Task.FromResult(window.ShowDialog() == true);
    }

    public Task<bool> ShowSettingsAsync(Func<Task<bool>> repair)
    {
        var window = new SettingsWindow(_settingsService, _ => repair());
        return Task.FromResult(window.ShowDialog() == true);
    }

    public void ShowRemoteInstall(RemoteInstallViewModel vm, Action onClosed)
    {
        var window = new RemoteInstallApprovalWindow(vm) { Owner = MainWindow };
        window.Closed += (_, _) => onClosed();
        window.Show();
    }

    public IUiWindow ShowModelDeletion(ModelDeletionViewModel vm, Action onClosed)
    {
        var window = new ModelDeletionWindow(vm) { Owner = MainWindow };
        window.Closed += (_, _) => onClosed();
        window.Show();
        return new WindowHandle(window);
    }

    public Task ShowModelDeletionModalAsync(ModelDeletionViewModel vm, Action<IUiWindow> opened)
    {
        var window = new ModelDeletionWindow(vm) { Owner = MainWindow };
        opened(new WindowHandle(window));
        window.ShowDialog();
        return Task.CompletedTask;
    }

    public Task<string?> PickFolderAsync(string title, string? initialPath)
    {
        // WPF .NET 8 has no native folder picker — WinForms FolderBrowserDialog.
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = title,
            SelectedPath = initialPath ?? "",
            UseDescriptionForTitle = true
        };
        return Task.FromResult(dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.SelectedPath : null);
    }

    public Task<string?> PickFileAsync(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "All files (*.*)|*.*|PEM files (*.pem)|*.pem|PPK files (*.ppk)|*.ppk",
            CheckFileExists = true
        };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public void SetClipboard(string text) => Clipboard.SetText(text);

    private sealed class WindowHandle : IUiWindow
    {
        private readonly Window _window;
        public WindowHandle(Window window) => _window = window;
        public void Activate() => _window.Activate();
    }
}
