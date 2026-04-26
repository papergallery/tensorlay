using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TensorLay.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;

        bool invert = parameter is string s &&
                      s.Equals("Invert", StringComparison.OrdinalIgnoreCase);

        if (invert) flag = !flag;

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = parameter is string s &&
                      s.Equals("Invert", StringComparison.OrdinalIgnoreCase);

        bool visible = value is Visibility v && v == Visibility.Visible;
        return invert ? !visible : visible;
    }
}
