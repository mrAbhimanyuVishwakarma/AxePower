using System;
using System.Globalization;
using System.Windows.Data;

namespace AxePower.App.Converters;

/// <summary>
/// Two-way glue between a string VM property and a RadioButton's IsChecked, so tabs/chips
/// stay in sync when the value is changed in code (e.g. a deep-link into a specific tab).
/// ConvertBack returns Binding.DoNothing on uncheck so the sibling that just checked wins.
/// </summary>
public class StringEqualsToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? (parameter?.ToString() ?? Binding.DoNothing) : Binding.DoNothing;
}
