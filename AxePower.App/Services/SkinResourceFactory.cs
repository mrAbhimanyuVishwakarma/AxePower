using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using AxePower.Core.Services.Skins;

namespace AxePower.App.Services;

/// <summary>Turns a compiled skin into the WPF resource dictionary the app's styles read.</summary>
public static class SkinResourceFactory
{
    public static ResourceDictionary Build(CompiledSkin skin)
    {
        var dictionary = new ResourceDictionary();
        foreach (var resource in skin.Resources)
        {
            dictionary[resource.Key] = resource switch
            {
                ColorResource c => ToColor(c.Argb),
                SolidBrushResource s => Freeze(new SolidColorBrush(ToColor(s.Argb))),
                GradientBrushResource g => Gradient(g),
                ShadowResource d => Freeze(new DropShadowEffect
                {
                    BlurRadius = d.BlurRadius,
                    ShadowDepth = d.ShadowDepth,
                    Direction = d.Direction,
                    Opacity = d.Opacity,
                    Color = ToColor(d.Argb)
                }),
                NullResource => null,
                ImageResource i => Freeze(new BitmapImage(new Uri($"pack://application:,,,{i.UriSource}", UriKind.Absolute))),
                _ => throw new InvalidOperationException($"Unknown skin resource {resource.GetType().Name}")
            };
        }
        return dictionary;
    }

    public static Color ToColor(string argb)
    {
        var (a, r, g, b) = Hex.Parse(argb);
        return Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b);
    }

    private static LinearGradientBrush Gradient(GradientBrushResource g)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(g.StartX, g.StartY), EndPoint = new Point(g.EndX, g.EndY) };
        foreach (var stop in g.Stops)
            brush.GradientStops.Add(new GradientStop(ToColor(stop.Argb), stop.Offset));
        return Freeze(brush);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}
