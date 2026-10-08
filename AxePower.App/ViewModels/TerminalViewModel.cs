using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Services;

namespace AxePower.App.ViewModels;

public partial class TerminalViewModel : ObservableObject
{
    private readonly TerminalService _terminal;
    private const int MaxChars = 120000;

    private readonly List<string> _history = new();
    private int _historyIndex = -1;

    [ObservableProperty]
    private string _outputText = string.Empty;

    [ObservableProperty]
    private string _inputText = string.Empty;

    // Toggle states drive the CMD / PowerShell button styling.
    [ObservableProperty]
    private bool _isPowerShellActive = true;

    [ObservableProperty]
    private bool _isCmdActive;

    [ObservableProperty]
    private bool _isElevated;

    [ObservableProperty]
    private string _elevationBadge = "STANDARD";

    /// <summary>Raised when text is appended, so the view can auto-scroll to the end.</summary>
    public event Action? OutputAppended;

    public TerminalViewModel(TerminalService terminal)
    {
        _terminal = terminal;
        IsElevated = TerminalService.IsProcessElevated();
        ElevationBadge = IsElevated ? "ADMINISTRATOR" : "STANDARD";

        _terminal.OutputReceived += OnOutput;

        OutputText = IsElevated
            ? "AxePower Terminal — running with ADMINISTRATOR privileges.\n"
            : "AxePower Terminal — running as standard user. Use \"Relaunch with Admin\" (top bar) for full access.\n";

        // No session yet: a PowerShell session costs ~74 MB and 25 threads, so it starts when the
        // Terminal page is first shown (EnsureSession) or a command is sent, not at app start.
    }

    /// <summary>Starts the default PowerShell session if none is running. Called when the page is shown.</summary>
    public void EnsureSession()
    {
        if (!_terminal.HasSession) _terminal.StartSession(_terminal.CurrentShell);
    }

    private void OnOutput(string line)
    {
        Application.Current?.Dispatcher?.BeginInvoke(new Action(() => Append(line + "\n")));
    }

    private void Append(string text)
    {
        var sb = new StringBuilder(OutputText);
        sb.Append(text);
        if (sb.Length > MaxChars)
        {
            sb.Remove(0, sb.Length - MaxChars);
        }
        OutputText = sb.ToString();
        OutputAppended?.Invoke();
    }

    [RelayCommand]
    private void UsePowerShell()
    {
        IsPowerShellActive = true;
        IsCmdActive = false;
        Append("\n[Starting PowerShell session…]\n");
        _terminal.StartSession(TerminalShell.PowerShell);
    }

    [RelayCommand]
    private void UseCmd()
    {
        IsPowerShellActive = false;
        IsCmdActive = true;
        Append("\n[Starting Command Prompt session…]\n");
        _terminal.StartSession(TerminalShell.Cmd);
    }

    [RelayCommand]
    private void RunCommand()
    {
        var cmd = InputText;
        if (string.IsNullOrWhiteSpace(cmd)) return;

        _history.Add(cmd);
        _historyIndex = _history.Count;
        _terminal.SendCommand(cmd);
        InputText = string.Empty;
    }

    [RelayCommand]
    private void Clear()
    {
        OutputText = string.Empty;
        OutputAppended?.Invoke();
    }

    [RelayCommand]
    private void Stop()
    {
        // Cancel a running external task (e.g. a Doctor diagnostic) if one registered a hook,
        // otherwise interrupt the interactive shell by restarting it.
        if (_terminal.ActiveExternalCancel != null)
        {
            _terminal.ActiveExternalCancel.Invoke();
            Append("\n[Stop requested — cancelling running task…]\n");
            return;
        }

        Append("\n[Session stopped — starting a fresh shell…]\n");
        _terminal.StartSession(IsCmdActive ? TerminalShell.Cmd : TerminalShell.PowerShell);
    }

    /// <summary>Recall previous command (↑). Returns the command to place in the input box.</summary>
    public string? HistoryPrevious()
    {
        if (_history.Count == 0) return null;
        _historyIndex = Math.Max(0, _historyIndex - 1);
        return _history[_historyIndex];
    }

    /// <summary>Recall next command (↓). Empty string means "past the newest entry".</summary>
    public string? HistoryNext()
    {
        if (_history.Count == 0) return null;
        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        return _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
    }
}
