using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TensorLay.Mac.Views;
using TensorLay.Platform;
using TensorLay.Services;
using TensorLay.ViewModels;

namespace TensorLay.Mac;

// IUiHost for the Avalonia front end. Dialogs are owned by the active app
// window (so Re-pair… from Settings parents to Settings), falling back to
// the main window.
public sealed class MacUiHost : IUiHost
{
    private readonly SettingsService _settingsService;
    private readonly PairingService _pairingService;
    private readonly SshKeyService _sshKeyService;

    public MacUiHost(SettingsService settingsService, PairingService pairingService, SshKeyService sshKeyService)
    {
        _settingsService = settingsService;
        _pairingService = pairingService;
        _sshKeyService = sshKeyService;
    }

    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    private static Window MainWindow =>
        Desktop?.MainWindow ?? throw new InvalidOperationException("Main window not created yet.");

    private static Window ActiveWindow =>
        Desktop?.Windows.FirstOrDefault(w => w.IsActive) ?? MainWindow;

    public void Invoke(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Invoke(action);
    }

    public T Invoke<T>(Func<T> func) =>
        Dispatcher.UIThread.CheckAccess() ? func() : Dispatcher.UIThread.Invoke(func);

    public Task<T> InvokeAsync<T>(Func<Task<T>> func) => Dispatcher.UIThread.InvokeAsync(func);

    public void ShowMessage(string title, string message, MessageKind kind) =>
        _ = new MessageWindow(title, message, kind).ShowDialog(ActiveWindow);

    public Task<(bool Confirmed, bool KeepModels)> AskUninstallAsync(
        string serviceName, string installPath, bool offerKeepModels) =>
        UninstallConfirmWindow.AskAsync(ActiveWindow, serviceName, installPath, offerKeepModels);

    public async Task<bool> ShowPairingAsync()
    {
        var window = new PairingWindow(_pairingService, _sshKeyService, _settingsService);
        return await window.ShowDialog<bool>(ActiveWindow);
    }

    public async Task<bool> ShowSettingsAsync(Func<Task<bool>> repair)
    {
        var window = new SettingsWindow(_settingsService, repair);
        bool saved = await window.ShowDialog<bool>(MainWindow);
        if (saved)
            LoginItem.Apply(_settingsService.Load().AutostartWithWindows);
        return saved;
    }

    public void ShowRemoteInstall(RemoteInstallViewModel vm, Action onClosed)
    {
        var window = new RemoteInstallWindow(vm);
        window.Closed += (_, _) => onClosed();
        window.Show(MainWindow);
    }

    public IUiWindow ShowModelDeletion(ModelDeletionViewModel vm, Action onClosed)
    {
        var window = new ModelDeletionWindow(vm);
        window.Closed += (_, _) => onClosed();
        window.Show(MainWindow);
        return new WindowHandle(window);
    }

    public async Task ShowModelDeletionModalAsync(ModelDeletionViewModel vm, Action<IUiWindow> opened)
    {
        var window = new ModelDeletionWindow(vm);
        opened(new WindowHandle(window));
        await window.ShowDialog(MainWindow);
    }

    public async Task<string?> PickFolderAsync(string title, string? initialPath)
    {
        var sp = ActiveWindow.StorageProvider;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
            options.SuggestedStartLocation = await sp.TryGetFolderFromPathAsync(initialPath);
        var result = await sp.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title)
    {
        var sp = ActiveWindow.StorageProvider;
        var result = await sp.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public void SetClipboard(string text) => _ = ActiveWindow.Clipboard?.SetTextAsync(text);

    private sealed class WindowHandle : IUiWindow
    {
        private readonly Window _window;
        public WindowHandle(Window window) => _window = window;
        public void Activate() => _window.Activate();
    }
}
