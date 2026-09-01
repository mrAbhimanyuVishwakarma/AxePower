using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AxePower.App.Converters;

public class PercentageToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            return new GridLength(Math.Max(0.001, d), GridUnitType.Star);
        }
        if (value is int i)
        {
            return new GridLength(Math.Max(0.001, i), GridUnitType.Star);
        }
        return new GridLength(1, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
