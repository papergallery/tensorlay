using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TensorLay.Models;

namespace TensorLay.Converters;

/// <summary>
/// Converts ServiceState to Visibility.
/// Parameter: one or more comma-separated state names that should be Visible.
/// Example: "{Binding State, Converter={...}, ConverterParameter=Running}"
/// </summary>
[ValueConversion(typeof(ServiceState), typeof(Visibility))]
public class StateToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ServiceState state || parameter is not string param)
            return Visibility.Collapsed;

        string[] visibleStates = param.Split(',', StringSplitOptions.TrimEntries);
        foreach (string name in visibleStates)
        {
            if (Enum.TryParse<ServiceState>(name, out var parsed) && parsed == state)
                return Visibility.Visible;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
