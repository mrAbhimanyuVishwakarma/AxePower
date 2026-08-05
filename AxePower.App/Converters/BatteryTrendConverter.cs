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

        if (pointList.Count == 1)
        {
            collection.Add(new Point(0, height - (pointList[0] / 100.0 * height)));
            collection.Add(new Point(width, height - (pointList[0] / 100.0 * height)));
            return collection;
        }

        double stepX = width / (pointList.Count - 1);
        for (int i = 0; i < pointList.Count; i++)
        {
            double x = i * stepX;
            // Value is 0-100. Y is inverted in screen coordinates (0 is top)
            double y = height - (pointList[i] / 100.0 * height);
            collection.Add(new Point(x, y));
        }

        return collection;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
