using System;
using System.Windows;
using System.Windows.Controls;

namespace AxePower.App.Controls;

/// <summary>
/// A decorator that constrains its child element to a strict aspect ratio (e.g. 16:9).
/// Ensures image banner frames never crop, stretch, or distort.
/// </summary>
public class AspectRatioBox : Decorator
{
    public static readonly DependencyProperty AspectRatioProperty =
        DependencyProperty.Register(
            nameof(AspectRatio),
            typeof(double),
            typeof(AspectRatioBox),
            new FrameworkPropertyMetadata(16.0 / 9.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child == null) return new Size(0, 0);

        double width = constraint.Width;
        double height = constraint.Height;
        double ratio = AspectRatio <= 0.001 ? (16.0 / 9.0) : AspectRatio;

        if (double.IsPositiveInfinity(width) && double.IsPositiveInfinity(height))
        {
            Child.Measure(constraint);
            return Child.DesiredSize;
        }

        if (double.IsPositiveInfinity(height))
        {
            height = width / ratio;
        }
        else if (double.IsPositiveInfinity(width))
        {
            width = height * ratio;
        }
        else
        {
            double targetHeight = width / ratio;
            if (targetHeight <= height)
            {
                height = targetHeight;
            }
            else
            {
                width = height * ratio;
            }
        }

        Child.Measure(new Size(width, height));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child == null) return arrangeSize;

        double width = arrangeSize.Width;
        double height = arrangeSize.Height;
        double ratio = AspectRatio <= 0.001 ? (16.0 / 9.0) : AspectRatio;

        double targetWidth = width;
        double targetHeight = height;
        double currentRatio = width / (height > 0.001 ? height : 1.0);

        if (currentRatio > ratio)
        {
            targetWidth = height * ratio;
        }
        else
        {
            targetHeight = width / ratio;
        }

        double x = Math.Max(0.0, (width - targetWidth) / 2.0);
        double y = Math.Max(0.0, (height - targetHeight) / 2.0);

        Child.Arrange(new Rect(x, y, targetWidth, targetHeight));
        return arrangeSize;
    }
}
