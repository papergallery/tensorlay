using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TensorLay.Converters;

/// <summary>
/// Returns Visible when the bound string equals TargetValue, otherwise Collapsed.
/// Used for page navigation: Visibility="{Binding SelectedPage, Converter={...}}"
/// where TargetValue is set inline in XAML.
/// </summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public class StringEqualityToVisibilityConverter : IValueConverter
{
    public string TargetValue { get; set; } = "";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string? actual = value as string;
        string expected = parameter as string ?? TargetValue;
        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
