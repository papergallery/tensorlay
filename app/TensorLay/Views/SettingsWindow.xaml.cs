using System.Windows;
using TensorLay.Services;
using TensorLay.ViewModels;

namespace TensorLay.Views;

public partial class SettingsWindow : Window
{
    // Supplied by MainViewModel: opens the pairing dialog owned by this
    // window and returns true once /pair succeeded. Null when the host
    // didn't wire re-pairing — the button is then a no-op.
    private readonly Func<Window, Task<bool>>? _repair;
    private bool _repairing;

    public SettingsWindow(SettingsService settingsService, Func<Window, Task<bool>>? repair = null)
    {
        InitializeComponent();
        _repair = repair;
        DataContext = new SettingsViewModel(settingsService);
    }

    private async void OnRepairClicked(object sender, RoutedEventArgs e)
    {
        if (_repair is null || _repairing) return;
        _repairing = true;
        try
        {
            bool paired = await _repair(this);
            // /pair rewrote VpsHost, SshPort, SshKeyPath and the pinned host
            // keys on disk. Refresh those fields so a later Save doesn't push
            // the stale pre-pairing values (e.g. the old SSH port) back over
            // the fresh ones. Unrelated edits in the form are kept.
            if (paired && DataContext is SettingsViewModel vm)
                vm.ReloadAfterPairing();
        }
        finally
        {
            _repairing = false;
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
