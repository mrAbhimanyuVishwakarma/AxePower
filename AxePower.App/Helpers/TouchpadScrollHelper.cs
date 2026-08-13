using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AxePower.App.Helpers;

/// <summary>
/// Provides smooth, proportional scrolling for precision touchpads and mouse wheels in WPF ScrollViewers.
/// Resolves the default WPF issue where touchpad micro-events cause excessive and jerky scrolling speed.
/// </summary>
public static class TouchpadScrollHelper
{
    private const double ScrollMultiplier = 0.4; // 120 delta (1 mouse wheel click) -> 48px; 15 delta (touchpad tick) -> 6px

    public static void Initialize()
    {
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel),
            true);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        var source = e.OriginalSource as DependencyObject;
        var scrollViewer = FindScrollViewer(source) ?? sender as ScrollViewer;

        if (scrollViewer == null) return;

        // Check if Shift is held down for horizontal scrolling
        bool isHorizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (isHorizontal)
        {
            if (scrollViewer.ScrollableWidth > 0)
            {
                double scrollDelta = e.Delta * ScrollMultiplier;
                double targetOffset = scrollViewer.HorizontalOffset - scrollDelta;
                targetOffset = Math.Clamp(targetOffset, 0, scrollViewer.ScrollableWidth);

                if (Math.Abs(targetOffset - scrollViewer.HorizontalOffset) > 0.001)
                {
                    scrollViewer.ScrollToHorizontalOffset(targetOffset);
                    e.Handled = true;
                }
            }
        }
        else
        {
            if (scrollViewer.ScrollableHeight > 0)
            {
                double scrollDelta = e.Delta * ScrollMultiplier;
                double targetOffset = scrollViewer.VerticalOffset - scrollDelta;
                targetOffset = Math.Clamp(targetOffset, 0, scrollViewer.ScrollableHeight);

                if (Math.Abs(targetOffset - scrollViewer.VerticalOffset) > 0.001)
                {
                    scrollViewer.ScrollToVerticalOffset(targetOffset);
                    e.Handled = true;
                }
            }
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is ScrollViewer sv && (sv.ScrollableHeight > 0 || sv.ScrollableWidth > 0))
            {
                return sv;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
