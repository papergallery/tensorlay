using System.Windows;
using TensorLay.Services;
using TensorLay.ViewModels;

namespace TensorLay.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        DataContext = new SettingsViewModel(settingsService);
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
