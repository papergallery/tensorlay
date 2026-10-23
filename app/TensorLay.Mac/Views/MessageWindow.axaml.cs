using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using TensorLay.Platform;

namespace TensorLay.Mac.Views;

// Minimal message box (Avalonia ships none): title, selectable text, OK.
public partial class MessageWindow : Window
{
    public MessageWindow() => InitializeComponent();

    public MessageWindow(string title, string message, MessageKind kind) : this()
    {
        Title = title;
        TitleText.Text = title;
        BodyText.Text = message;
        KindDot.Fill = new SolidColorBrush(Color.Parse(kind switch
        {
            MessageKind.Error => "#E74C3C",
            MessageKind.Warning => "#E67E22",
            _ => "#6C5CE7",
        }));
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
