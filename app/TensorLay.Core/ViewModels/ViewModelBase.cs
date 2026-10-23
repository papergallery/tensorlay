using CommunityToolkit.Mvvm.ComponentModel;
using TensorLay.Platform;

namespace TensorLay.ViewModels;

public partial class ViewModelBase : ObservableObject
{
    protected static IUiHost Ui => UiHost.Current;

    protected void RunOnUI(Action action) => Ui.Invoke(action);
}
