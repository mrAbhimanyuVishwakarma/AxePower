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
        _logService.OnLogAdded += (s, e) =>
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;

            try
            {
                _dispatcher.Invoke(() => _logService.AddLogToCollection(e));
            }
            catch (TaskCanceledException)
            {
                // The dispatcher shut down between the check and the call
            }
        };
    }
}
