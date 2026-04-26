using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TensorLay.Models;

namespace TensorLay.Converters;

[ValueConversion(typeof(ServiceState), typeof(SolidColorBrush))]
public class StateToColorConverter : IValueConverter
{
    // Light-theme Wispr Flow palette
    private static readonly SolidColorBrush BrushNotInstalled = new(Color.FromRgb(0xC5, 0xC5, 0xC5));
    private static readonly SolidColorBrush BrushInstalling   = new(Color.FromRgb(0x6C, 0x5C, 0xE7));
    private static readonly SolidColorBrush BrushStopped      = new(Color.FromRgb(0x9B, 0x9B, 0x9B));
    private static readonly SolidColorBrush BrushStarting     = new(Color.FromRgb(0x6C, 0x5C, 0xE7));
    private static readonly SolidColorBrush BrushRunning      = new(Color.FromRgb(0x2E, 0xCC, 0x71));
    private static readonly SolidColorBrush BrushError        = new(Color.FromRgb(0xE7, 0x4C, 0x3C));

    static StateToColorConverter()
    {
        BrushNotInstalled.Freeze();
        BrushInstalling.Freeze();
        BrushStopped.Freeze();
        BrushStarting.Freeze();
        BrushRunning.Freeze();
        BrushError.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ServiceState state)
        {
            return state switch
            {
                ServiceState.NotInstalled => BrushNotInstalled,
                ServiceState.Installing   => BrushInstalling,
                ServiceState.Stopped      => BrushStopped,
                ServiceState.Starting     => BrushStarting,
                ServiceState.Running      => BrushRunning,
                ServiceState.Stopping     => BrushStarting,
                ServiceState.Error        => BrushError,
                _                         => BrushNotInstalled
            };
        }

        return BrushNotInstalled;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
