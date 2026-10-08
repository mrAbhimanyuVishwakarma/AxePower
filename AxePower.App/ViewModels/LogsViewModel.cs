using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AxePower.Core.Models;
using AxePower.Core.Services;
using System.Windows.Threading;

namespace AxePower.App.ViewModels;

public partial class LogsViewModel : ObservableObject
{
    private readonly LogService _logService;
    private readonly Dispatcher _dispatcher;

    public ObservableCollection<LogEntry> Logs => _logService.Logs;

    public LogsViewModel(LogService logService)
    {
        _logService = logService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        
        // Subscribe to log events to safely marshal them to the UI thread.
        // Background services keep logging from their timers while the app is closing, and
        // Invoke on a dispatcher that is shutting down throws on the timer's thread, where
        // nothing can catch it. A log line is never worth taking the process down for.
        //
        // Queued, never waited for: a service that logs while holding a lock would otherwise wait
        // for the UI thread, and deadlock if the UI thread is waiting for that lock (found at exit:
        // the sensor reader logged from a telemetry tick while the UI thread was disposing it).
        _logService.OnLogAdded += (s, e) =>
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
            _dispatcher.BeginInvoke(() => _logService.AddLogToCollection(e));
        };
    }
}
