using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AxePower.App.Converters;

public class BatteryTrendConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<double> points || !points.Any())
            return new PointCollection();

        var pointList = points.ToList();
        var collection = new PointCollection();

        // Map values to a 200x50 area
        double width = 200;
        double height = 50;
        bool isPolygon = parameter?.ToString()?.Equals("Polygon", StringComparison.OrdinalIgnoreCase) == true;

        if (pointList.Count == 1)
        {
            double y = height - (Math.Clamp(pointList[0], 0, 100) / 100.0 * height);
            collection.Add(new Point(0, y));
            collection.Add(new Point(width, y));
            if (isPolygon)
            {
                collection.Add(new Point(width, height));
                collection.Add(new Point(0, height));
            }
            return collection;
        }

        double stepX = width / (pointList.Count - 1);
        for (int i = 0; i < pointList.Count; i++)
        {
            double x = i * stepX;
            // Value is 0-100. Y is inverted in screen coordinates (0 is top)
            double y = height - (Math.Clamp(pointList[i], 0, 100) / 100.0 * height);
            collection.Add(new Point(x, y));
        }

        if (isPolygon)
        {
            collection.Add(new Point(width, height));
            collection.Add(new Point(0, height));
        }

        return collection;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BatteryPercentageToYConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double percent = 100;
        if (value is int intVal) percent = intVal;
        else if (value is double dblVal) percent = dblVal;
        else if (value is string strVal)
        {
            strVal = strVal.TrimEnd('%').Trim();
            if (double.TryParse(strVal, out var parsed)) percent = parsed;
        }

        percent = Math.Clamp(percent, 0, 100);
        double height = 50;
        if (parameter != null && double.TryParse(parameter.ToString(), out var hParam))
        {
            height = hParam;
        }

        // Screen Y coordinate (0 at top, height at bottom) - subtract 4px to center 8px dot
        return Math.Max(0, (height - (percent / 100.0 * height)) - 4);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
