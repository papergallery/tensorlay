using System.Windows;
using System.Windows.Controls;

namespace TensorLay.Views;

public partial class ServiceCard : UserControl
{
    public ServiceCard()
    {
        InitializeComponent();
    }

    // Drop the overflow ContextMenu on plain left-click — same pattern as
    // MainWindow's home-row overflow.
    private void OverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }
}
