using System;
using System.Globalization;
using System.Windows.Data;

namespace AxePower.App.Converters;

/// <summary>
/// Returns true when a numeric binding value is below the numeric converter parameter.
/// Used by responsive WPF layouts that need to move controls when space is limited.
/// </summary>
public sealed class LessThanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var current)
            && double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            && current < threshold;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
