using Avalonia.Controls;
using Avalonia.Interactivity;
using TensorLay.Services;
using TensorLay.ViewModels;

namespace TensorLay.Mac.Views;

public partial class SettingsWindow : Window
{
    private readonly Func<Task<bool>>? _repair;
    private bool _repairing;

    public SettingsWindow() : this(new SettingsService(), null) { }

    public SettingsWindow(SettingsService settingsService, Func<Task<bool>>? repair)
    {
        InitializeComponent();
        _repair = repair;
        DataContext = new SettingsViewModel(settingsService);
    }

    private async void OnRepairClicked(object? sender, RoutedEventArgs e)
    {
        if (_repair is null || _repairing) return;
        _repairing = true;
        try
        {
            // /pair rewrote host/port/key/pinned keys on disk — refresh those
            // fields so a later Save doesn't write the stale ones back.
            if (await _repair() && DataContext is SettingsViewModel vm)
                vm.ReloadAfterPairing();
        }
        finally
        {
            _repairing = false;
        }
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.SaveCommand.Execute(null);
        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}
