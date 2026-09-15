using System.Windows;
using TensorLay.ViewModels;

namespace TensorLay.Views;

public partial class ModelDeletionWindow : Window
{
    private readonly ModelDeletionViewModel _vm;

    public ModelDeletionWindow(ModelDeletionViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Finished += OnFinished;
        Loaded += async (_, _) => await vm.InitializeAsync();
    }

    private void OnFinished(IReadOnlyCollection<string> _) => Dispatcher.Invoke(Close);

    protected override void OnClosed(EventArgs e)
    {
        _vm.Finished -= OnFinished;
        base.OnClosed(e);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        // Title-bar X behaves like Reject/Cancel before a decision and like
        // Close afterwards; ignored while files are being deleted.
        if (_vm.DismissCommand.CanExecute(null))
            _vm.DismissCommand.Execute(null);
    }
}
