using System.Windows;
using TensorLay.ViewModels;

namespace TensorLay.Views;

public partial class RemoteInstallApprovalWindow : Window
{
    private readonly RemoteInstallViewModel _vm;

    public RemoteInstallApprovalWindow(RemoteInstallViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        // Close once Approve has finished (download done/failed/cancelled),
        // or as soon as Reject/AlwaysReject posts. Subscribed before any
        // command can fire so we never miss the signal.
        vm.DecisionMade += OnDecisionMade;
    }

    private void OnDecisionMade()
    {
        // VM may raise on background thread (download completed handler).
        Dispatcher.Invoke(() =>
        {
            // If the user already approved and a download is running, give
            // it a moment to flush its final status post before tearing
            // the window down. IsFinished gates that.
            if (_vm.IsFinished || !_vm.IsDownloading)
                Close();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.DecisionMade -= OnDecisionMade;
        base.OnClosed(e);
    }

    private void OnRejectClicked(object sender, RoutedEventArgs e)
    {
        // Title-bar X — same as Reject button. If a download is in flight
        // the close is suppressed; user must use the Cancel button.
        if (_vm.IsDownloading && !_vm.IsFinished) return;
        if (_vm.RejectCommand.CanExecute(null))
            _vm.RejectCommand.Execute(null);
    }

    private void OnAlwaysRejectClicked(object sender, RoutedEventArgs e)
    {
        if (_vm.AlwaysRejectCommand.CanExecute(null))
            _vm.AlwaysRejectCommand.Execute(null);
        e.Handled = true;
    }
}
