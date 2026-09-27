using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AxePower.App.Converters;

/// <summary>Visible when a string VM property equals the ConverterParameter (e.g. the selected Settings category).</summary>
public class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
