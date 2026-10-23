using TensorLay.ViewModels;

namespace TensorLay.Platform;

public enum MessageKind { Info, Warning, Error }

// A window the view models keep a handle on (the single open model-deletion
// window) so they can bring it to front instead of stacking a second one.
public interface IUiWindow
{
    void Activate();
}

// Everything the shared view models need from the UI toolkit: the UI thread,
// message boxes, the app's own dialogs, file pickers and the clipboard. WPF
// (Windows) and Avalonia (macOS) each implement it; view models never touch
// toolkit types directly.
public interface IUiHost
{
    // Run on the UI thread, blocking the caller until done (no-op hop when
    // already on it).
    void Invoke(Action action);
    T Invoke<T>(Func<T> func);
    Task<T> InvokeAsync<T>(Func<Task<T>> func);

    void ShowMessage(string title, string message, MessageKind kind);

    Task<(bool Confirmed, bool KeepModels)> AskUninstallAsync(
        string serviceName, string installPath, bool offerKeepModels);

    // Modal pairing dialog; true once /pair succeeded and settings were saved.
    Task<bool> ShowPairingAsync();

    // Modal settings dialog; true when the user saved. `repair` runs the
    // Re-pair… flow from inside the dialog.
    Task<bool> ShowSettingsAsync(Func<Task<bool>> repair);

    // Non-modal approval window for an agent's install request.
    void ShowRemoteInstall(RemoteInstallViewModel vm, Action onClosed);

    // Model deletion window: non-modal for agent requests, modal for a local
    // Delete click (the returned task completes when it closes).
    IUiWindow ShowModelDeletion(ModelDeletionViewModel vm, Action onClosed);
    Task ShowModelDeletionModalAsync(ModelDeletionViewModel vm, Action<IUiWindow> opened);

    Task<string?> PickFolderAsync(string title, string? initialPath);
    Task<string?> PickFileAsync(string title);

    void SetClipboard(string text);
}

// Set once at startup by the front end, before any view model is built.
public static class UiHost
{
    private static IUiHost? _current;

    public static IUiHost Current
    {
        get => _current ?? throw new InvalidOperationException("UiHost.Current was not set at startup.");
        set => _current = value;
    }
}
