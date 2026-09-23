using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AxePower.App.Services;

/// <summary>
/// Moves music events from background threads onto the UI thread. Position ticks are
/// coalesced: if the UI has not caught up, only the latest position is delivered.
/// </summary>
public sealed class MusicDispatcher
{
    private int _positionQueued;
    private TimeSpan _latestPosition;

    private static Dispatcher? UiDispatcher => Application.Current?.Dispatcher;

    public void Post(Action action)
    {
        var dispatcher = UiDispatcher;
        if (dispatcher == null) return;

        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    public void PostPosition(TimeSpan position, Action<TimeSpan> apply)
    {
        _latestPosition = position;
        if (Interlocked.Exchange(ref _positionQueued, 1) == 1) return; // an update is already on its way

        var dispatcher = UiDispatcher;
        if (dispatcher == null) return;

        dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _positionQueued, 0);
            apply(_latestPosition);
        }, DispatcherPriority.Background);
    }
}
