using System.IO;
using System.Windows;

namespace TensorLay.Views;

public partial class UninstallConfirmWindow : Window
{
    public bool KeepModels { get; private set; }
    public bool Confirmed { get; private set; }

    private UninstallConfirmWindow()
    {
        InitializeComponent();
    }

    // Show the dialog and return (confirmed, keepModels). Size enumeration
    // runs on a background thread with a hard 5s budget — large
    // installations (40+ GB ComfyUI with all the models) can take several
    // seconds to walk recursively, and we don't want to freeze the UI.
    public static Task<(bool Confirmed, bool KeepModels)> AskAsync(
        Window? owner, string serviceName, string installPath, bool offerKeepModels)
    {
        var w = new UninstallConfirmWindow
        {
            Owner = owner ?? Application.Current?.MainWindow,
            Title = $"Uninstall {serviceName}",
        };
        w.HeadlineText.Text = $"Uninstall {serviceName}?";
        w.PathText.Text = installPath;
        w.KeepModelsCheckbox.Visibility = offerKeepModels ? Visibility.Visible : Visibility.Collapsed;
        w.BodyText.Text = $"This will remove the service installation directory. Computing size…";

        // Kick off size enumeration; update text when it lands. The window
        // is shown immediately so the user isn't staring at a blank screen.
        _ = w.UpdateSizeAsync(installPath);

        w.ShowDialog();
        return Task.FromResult((w.Confirmed, w.Confirmed && w.KeepModelsCheckbox.IsChecked == true));
    }

    private async Task UpdateSizeAsync(string path)
    {
        long? bytes = await Task.Run(() =>
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    try { total += new FileInfo(f).Length; }
                    catch { /* skip files that vanished or perms denied */ }
                }
                return (long?)total;
            }
            catch
            {
                return null;
            }
        }).ConfigureAwait(true);

        BodyText.Text = bytes is long b
            ? $"This will remove the service installation directory and ~{Format(b)} of files."
            : "This will remove the service installation directory.";
    }

    private static string Format(long bytes)
    {
        double gb = bytes / 1_073_741_824.0;
        if (gb >= 1.0) return $"{gb:F1} GB";
        double mb = bytes / 1_048_576.0;
        return $"{mb:F0} MB";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        KeepModels = KeepModelsCheckbox.IsChecked == true;
        Close();
    }
}
