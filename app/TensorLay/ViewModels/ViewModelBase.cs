using CommunityToolkit.Mvvm.ComponentModel;

namespace TensorLay.ViewModels;

public partial class ViewModelBase : ObservableObject
{
    protected void RunOnUI(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(action);
        else
            action();
    }
}
