using Avalonia.Controls;
using Avalonia.Threading;
using TensorLay.ViewModels;

namespace TensorLay.Mac.Views;

public partial class ModelDeletionWindow : Window
{
    private readonly ModelDeletionViewModel? _vm;
    private bool _finished;

    public ModelDeletionWindow() => InitializeComponent();

    public ModelDeletionWindow(ModelDeletionViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        vm.Finished += OnFinished;
        Opened += async (_, _) => await vm.InitializeAsync();
        Closing += OnClosing;
    }

    private void OnFinished(IReadOnlyCollection<string> _) => Dispatcher.UIThread.Post(() =>
    {
        _finished = true;
        Close();
    });

    // Window close button = Reject/Cancel before a decision, Close after;
    // ignored while files are being deleted (same as WPF's title-bar X).
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_finished || _vm is null) return;
        // App quitting / owner closing: never block shutdown.
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OwnerWindowClosing) return;
        e.Cancel = true;
        if (_vm.DismissCommand.CanExecute(null))
            _vm.DismissCommand.Execute(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_vm is not null) _vm.Finished -= OnFinished;
        base.OnClosed(e);
    }
}
