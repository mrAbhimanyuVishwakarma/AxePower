using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AxePower.App.Converters;

public class SparklinePathConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<double> points || !points.Any())
            return Geometry.Empty;

        var pointList = points.ToList();
        int count = pointList.Count;

        double width = 100;
        double height = 50;
        bool isFill = false;

        if (parameter is string paramStr)
        {
            var parts = paramStr.Split(';');
            var numbers = new List<double>();
            foreach (var part in parts)
            {
                if (part.Equals("Fill", StringComparison.OrdinalIgnoreCase) || part.Equals("Polygon", StringComparison.OrdinalIgnoreCase))
                {
                    isFill = true;
                }
                else if (double.TryParse(part, NumberStyles.Any, CultureInfo.InvariantCulture, out double num))
                {
                    numbers.Add(num);
                }
            }

            if (numbers.Count > 0) width = numbers[0];
            if (numbers.Count > 1) height = numbers[1];
        }

        double stepX = count > 1 ? width / (count - 1) : width;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double firstVal = Math.Clamp(pointList[0], 0, 100);
            double firstY = height - (firstVal / 100.0 * (height - 2)) - 1;

            if (isFill)
            {
                ctx.BeginFigure(new Point(0, height), true, true);
                ctx.LineTo(new Point(0, firstY), true, false);
            }
            else
            {
                ctx.BeginFigure(new Point(0, firstY), false, false);
            }

            for (int i = 1; i < count; i++)
            {
                double x = i * stepX;
                double val = Math.Clamp(pointList[i], 0, 100);
                double y = height - (val / 100.0 * (height - 2)) - 1;
                ctx.LineTo(new Point(x, y), true, false);
            }

            if (isFill)
            {
                ctx.LineTo(new Point(width, height), true, false);
                ctx.LineTo(new Point(0, height), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
