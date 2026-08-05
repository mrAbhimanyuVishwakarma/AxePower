using System;
using System.Collections.ObjectModel;
using System.IO;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class LogService
{
    private readonly string _logFilePath;
    
    public ObservableCollection<LogEntry> Logs { get; } = new ObservableCollection<LogEntry>();

    public LogService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "AxePower");
        Directory.CreateDirectory(appFolder);
        _logFilePath = Path.Combine(appFolder, "AxePower.log");
    }

    public void LogInfo(string message) => Log(message, "INFO");
    
    public void LogWarning(string message) => Log(message, "WARN");
    
    public void LogError(string message, Exception? ex = null)
    {
        var fullMessage = ex != null ? $"{message}\n{ex}" : message;
        Log(fullMessage, "ERROR");
    }

    private void Log(string message, string level)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Message = message,
            Level = level
        };

        // UI thread dispatching might be needed if called from background thread, 
        // but let's handle that at the ViewModel level or just rely on WPF's built in mechanism if possible.
        // Actually, since we'll bind this collection in WPF, we should make sure we add it on the UI thread.
        // For simplicity in the Core library, we'll expose an event that the App can subscribe to for UI thread marshaling.
        OnLogAdded?.Invoke(this, entry);
        
        try
        {
            File.AppendAllText(_logFilePath, entry.ToString() + Environment.NewLine);
        }
        catch 
        {
            // Ignore file write errors to prevent crashing
        }
    }

    public event EventHandler<LogEntry>? OnLogAdded;
    
    public void AddLogToCollection(LogEntry entry)
    {
        Logs.Insert(0, entry);
        if (Logs.Count > 1000)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }
}
