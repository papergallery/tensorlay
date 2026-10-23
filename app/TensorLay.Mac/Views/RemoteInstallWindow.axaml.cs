using Avalonia.Controls;
using Avalonia.Threading;
using TensorLay.ViewModels;

namespace TensorLay.Mac.Views;

public partial class RemoteInstallWindow : Window
{
    private readonly RemoteInstallViewModel? _vm;
    private bool _closingFromDecision;

    public RemoteInstallWindow() => InitializeComponent();

    public RemoteInstallWindow(RemoteInstallViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        // Close once a decision is final (reject posted, or the approved
        // download finished/failed/was cancelled). Same rule as WPF.
        vm.DecisionMade += OnDecisionMade;
        Closing += OnClosing;
    }

    private void OnDecisionMade() => Dispatcher.UIThread.Post(() =>
    {
        if (_vm is { } vm && (vm.IsFinished || !vm.IsDownloading))
        {
            _closingFromDecision = true;
            Close();
        }
    });

    // The red close button acts as Reject; while a download runs it's
    // ignored — the user has to use Cancel Download.
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingFromDecision || _vm is null) return;
        // App quitting / owner closing: never block shutdown.
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OwnerWindowClosing) return;
        e.Cancel = true;
        if (_vm.IsDownloading && !_vm.IsFinished) return;
        if (_vm.RejectCommand.CanExecute(null))
            _vm.RejectCommand.Execute(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_vm is not null) _vm.DecisionMade -= OnDecisionMade;
        base.OnClosed(e);
    }
}
