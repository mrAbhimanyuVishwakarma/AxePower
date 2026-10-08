using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AxePower.App.Helpers;

/// <summary>Centers an active lyric line without putting animation state in a ViewModel.</summary>
public static class LyricsScrollHelper
{
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();

    public static void SmoothCenter(ListBox list, object? item)
    {
        if (item is null) return;

        list.Dispatcher.BeginInvoke(() =>
        {
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container) return;
            var viewer = FindChild<ScrollViewer>(list);
            if (viewer is null || viewer.ViewportHeight <= 0) return;

            var location = container.TransformToAncestor(list).Transform(new Point(0, 0));
            double target = Math.Clamp(
                viewer.VerticalOffset + location.Y - (viewer.ViewportHeight - container.ActualHeight) / 2,
                0,
                viewer.ScrollableHeight);

            if (Motion.IsReduced)
            {
                viewer.ScrollToVerticalOffset(target);
                return;
            }

            var state = States.GetOrCreateValue(viewer);
            state.Start(viewer, target);
        }, DispatcherPriority.Loaded);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private sealed class ScrollState
    {
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        private ScrollViewer? _viewer;
        private double _from;
        private double _to;
        private DateTime _started;

        public ScrollState() => _timer.Tick += OnTick;

        public void Start(ScrollViewer viewer, double target)
        {
            _viewer = viewer;
            _from = viewer.VerticalOffset;
            _to = target;
            _started = DateTime.UtcNow;
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_viewer is null)
            {
                _timer.Stop();
                return;
            }

            double progress = Math.Clamp((DateTime.UtcNow - _started).TotalMilliseconds / 280d, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 3);
            _viewer.ScrollToVerticalOffset(_from + (_to - _from) * eased);
            if (progress >= 1) _timer.Stop();
        }
    }
}

