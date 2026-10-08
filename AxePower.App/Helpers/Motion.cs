using System;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace AxePower.App.Helpers;

/// <summary>
/// Reduced motion (Prompt 28): AxePower follows Windows' "Animation effects" switch (Settings →
/// Accessibility → Visual effects). With it off, nothing slides or spins.
/// <para>
/// Values are read when a page or style loads. Storyboards in templates are frozen, so they
/// cannot follow a change while they are on screen; a page opened after the switch is flipped
/// follows it, and everything does after a restart.
/// </para>
/// </summary>
public static class Motion
{
    /// <summary>Test seam: overrides the Windows setting when set.</summary>
    public static bool? ReducedOverride { get; set; }

    /// <summary>True when Windows' animation effects are off.</summary>
    public static bool IsReduced => ReducedOverride ?? !SystemParameters.ClientAreaAnimation;

    /// <summary>The animation's duration, or zero (an instant change) when motion is reduced.</summary>
    public static Duration Duration(TimeSpan normal) => new(IsReduced ? TimeSpan.Zero : normal);
}

/// <summary>
/// <c>Duration="{a11y:Motion 0:0:0.16}"</c>: that duration, or instant when motion is reduced.
/// </summary>
[MarkupExtensionReturnType(typeof(Duration))]
public sealed class MotionExtension : MarkupExtension
{
    public MotionExtension() { }

    public MotionExtension(string normal) => Normal = normal;

    /// <summary>The duration with animation effects on, as h:m:s (e.g. 0:0:0.16).</summary>
    public string Normal { get; set; } = "0:0:0.2";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Motion.Duration(TimeSpan.Parse(Normal, CultureInfo.InvariantCulture));
}

/// <summary>
/// <c>Visibility="{a11y:MotionOnly}"</c>: Visible, or Collapsed when motion is reduced. For purely
/// decorative movement, such as a busy spinner beside text that already says what is happening.
/// </summary>
[MarkupExtensionReturnType(typeof(Visibility))]
public sealed class MotionOnlyExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Motion.IsReduced ? Visibility.Collapsed : Visibility.Visible;
}
