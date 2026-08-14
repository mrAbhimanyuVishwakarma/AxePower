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

        double multiplier = GetScrollMultiplier(scrollViewer);

        // Check if Shift is held down for horizontal scrolling
        bool isHorizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (isHorizontal)
        {
            if (scrollViewer.ScrollableWidth > 0)
            {
                double scrollDelta = e.Delta * multiplier;
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
                double scrollDelta = e.Delta * multiplier;
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

    private static double GetScrollMultiplier(ScrollViewer scrollViewer)
    {
        // If ScrollableHeight is large (> 500), it's definitely scrolling in physical pixels
        if (scrollViewer.ScrollableHeight > 500)
        {
            return 0.85; // 120 delta -> ~102px; 15 delta (trackpad) -> ~12.75px
        }

        // Check if hosted inside an ItemsControl with explicit Pixel scroll unit
        var parentItemsControl = FindParentItemsControl(scrollViewer);
        if (parentItemsControl != null)
        {
            var unit = VirtualizingPanel.GetScrollUnit(parentItemsControl);
            if (unit == ScrollUnit.Pixel)
            {
                return 0.85;
            }
            if (scrollViewer.CanContentScroll && unit == ScrollUnit.Item)
            {
                // Item mode: 120 delta -> 3.0 items; 15 delta -> ~0.38 item
                return 0.025;
            }
        }

        // Standard ScrollViewer (Settings, About, Battery, etc. which scroll in pixels)
        return 0.85;
    }

    private static ItemsControl? FindParentItemsControl(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is ItemsControl ic) return ic;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
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
