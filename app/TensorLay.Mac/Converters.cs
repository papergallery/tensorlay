using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TensorLay.Models;

namespace TensorLay.Mac;

// Avalonia has no DataTriggers, so the WPF app's state-driven styling
// (dot colour, status badge colours, which action buttons show) is expressed
// as converters over ServiceState. Colours match the WPF converters/triggers.

public sealed class StateToDotBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(Color.Parse(value switch
        {
            ServiceState.Installing or ServiceState.Starting or ServiceState.Stopping => "#6C5CE7",
            ServiceState.Stopped => "#9B9B9B",
            ServiceState.Running => "#2ECC71",
            ServiceState.Error => "#E74C3C",
            _ => "#C5C5C5",
        }));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StateToBadgeBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(Color.Parse(value switch
        {
            ServiceState.Running => "#E8F8F0",
            ServiceState.Error => "#FEF2F2",
            ServiceState.Installing => "#F0EBFF",
            _ => "#F7F6F3",
        }));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StateToBadgeForegroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(Color.Parse(value switch
        {
            ServiceState.Running => "#2ECC71",
            ServiceState.Error => "#E74C3C",
            ServiceState.Starting or ServiceState.Installing => "#6C5CE7",
            _ => "#9B9B9B",
        }));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// True when the state is one of the comma-separated names in the parameter:
// IsVisible="{Binding State, Converter={StaticResource StateIs}, ConverterParameter='Running,Starting'}"
public sealed class StateIsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ServiceState state || parameter is not string names) return false;
        foreach (var name in names.Split(',', StringSplitOptions.TrimEntries))
            if (Enum.TryParse<ServiceState>(name, out var parsed) && parsed == state)
                return true;
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Page switching: IsVisible="{Binding SelectedPage, Converter={StaticResource StringEquals}, ConverterParameter=Models}"
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
