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

    /// <summary>A handled failure worth knowing about: the message plus the exception's type and message, no stack.</summary>
    public void LogWarning(string message, Exception ex) => Log($"{message} ({ex.GetType().Name}: {ex.Message})", "WARN");
    
    public void LogError(string message, Exception? ex = null)
    {
        var fullMessage = ex != null ? $"{message}\n{ex}" : message;
        Log(fullMessage, "ERROR");
    }

    private void Log(string message, string level)
    {
        // Every line is redacted here, so no caller can leak a token, room secret or email address.
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Message = LogRedactor.Redact(message),
            Level = level
        };

        // UI thread dispatching might be needed if called from background thread, 
        // but let's handle that at the ViewModel level or just rely on WPF's built in mechanism if possible.
        // Actually, since we'll bind this collection in WPF, we should make sure we add it on the UI thread.
        // For simplicity in the Core library, we'll expose an event that the App can subscribe to for UI thread marshaling.
        OnLogAdded?.Invoke(this, entry);
        
        try
        {
            var fi = new FileInfo(_logFilePath);
            if (fi.Exists && fi.Length > 5 * 1024 * 1024)
            {
                var backup = _logFilePath + ".old";
                File.Delete(backup);
                File.Move(_logFilePath, backup);
            }
            File.AppendAllText(_logFilePath, entry.ToString() + Environment.NewLine);
        }
        catch (Exception)
        {
            // Best-effort, and broad on purpose: logging runs inside other catch blocks, so a locked or
            // read-only log file must never throw out of here. The entry still reached the in-app
            // log through OnLogAdded above.
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
