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
        
        // Subscribe to log events to safely marshal them to the UI thread
        _logService.OnLogAdded += (s, e) => 
        {
            _dispatcher.Invoke(() => 
            {
                _logService.AddLogToCollection(e);
            });
        };
    }
}
