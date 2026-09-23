using System;
using System.Globalization;
using System.Windows.Data;

namespace AxePower.App.Converters;

/// <summary>
/// Binds a group of radio buttons to a single enum property: each button passes the member it
/// stands for as <c>ConverterParameter</c> and is checked when the property equals it.
/// </summary>
public class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter is not string name) return false;

        return string.Equals(value.ToString(), name, StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Unchecking is what the newly checked button reports; only the check should write back.
        if (value is not true || parameter is not string name) return Binding.DoNothing;

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return enumType.IsEnum && Enum.TryParse(enumType, name, out var parsed)
            ? parsed
            : Binding.DoNothing;
    }
}
