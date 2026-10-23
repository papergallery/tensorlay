using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TensorLay.Mac.Views;

public partial class UninstallConfirmWindow : Window
{
    private bool _confirmed;

    public UninstallConfirmWindow() => InitializeComponent();

    // (confirmed, keepModels). Size is computed in the background with a 5 s
    // budget so a 40 GB ComfyUI tree doesn't freeze the dialog.
    public static async Task<(bool Confirmed, bool KeepModels)> AskAsync(
        Window owner, string serviceName, string installPath, bool offerKeepModels)
    {
        var w = new UninstallConfirmWindow { Title = $"Uninstall {serviceName}" };
        w.HeadlineText.Text = $"Uninstall {serviceName}?";
        w.PathText.Text = installPath;
        w.KeepModelsCheckbox.IsVisible = offerKeepModels;
        w.BodyText.Text = "This moves the service installation directory to the Trash. Computing size…";
        _ = w.UpdateSizeAsync(installPath);

        await w.ShowDialog(owner);
        return (w._confirmed, w._confirmed && offerKeepModels && w.KeepModelsCheckbox.IsChecked == true);
    }

    private async Task UpdateSizeAsync(string path)
    {
        long? bytes = await Task.Run(() =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    try { total += new FileInfo(f).Length; } catch { /* vanished / no access */ }
                }
                return (long?)total;
            }
            catch
            {
                return null;
            }
        });
        BodyText.Text = bytes is long b
            ? $"This moves the service installation directory and ~{Format(b)} of files to the Trash."
            : "This moves the service installation directory to the Trash.";
    }

    private static string Format(long bytes)
    {
        double gb = bytes / 1_073_741_824.0;
        return gb >= 1.0 ? $"{gb:F1} GB" : $"{bytes / 1_048_576.0:F0} MB";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnUninstallClick(object? sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }
}
