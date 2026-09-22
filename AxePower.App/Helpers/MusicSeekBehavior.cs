using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AxePower.App.ViewModels.Music;

namespace AxePower.App.Helpers;

/// <summary>
/// Makes a seek slider seek only when a drag is released, while clicks and arrow keys still
/// seek at once. Attach with <c>helpers:MusicSeekBehavior.IsEnabled="True"</c> on a Slider
/// whose DataContext is a <see cref="NowPlayingViewModel"/>.
/// </summary>
public static class MusicSeekBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(MusicSeekBehavior), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider) return;

        if ((bool)e.NewValue)
        {
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
        }
        else
        {
            slider.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));
            slider.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
        }
    }

    private static void OnDragStarted(object sender, DragStartedEventArgs e) =>
        ((sender as FrameworkElement)?.DataContext as NowPlayingViewModel)?.BeginSeekDrag();

    private static void OnDragCompleted(object sender, DragCompletedEventArgs e) =>
        ((sender as FrameworkElement)?.DataContext as NowPlayingViewModel)?.EndSeekDrag();
}
