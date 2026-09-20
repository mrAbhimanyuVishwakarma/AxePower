using System;
using System.Diagnostics;
using System.Security.Principal;

namespace AxePower.Core.Services;

public enum TerminalShell
{
    PowerShell,
    Cmd
}

/// <summary>
/// Owns a single persistent interactive shell (cmd.exe or powershell.exe) with redirected
/// stdin/stdout/stderr, and doubles as the app's central output bus: other features
/// (Software Hub, System Doctor) forward their live logs here so the Terminal page is the
/// one place command output appears. The shell inherits the host process's privilege level,
/// so when AxePower is relaunched elevated the terminal is elevated too.
/// </summary>
public class TerminalService
{
    private Process? _process;
    private readonly object _lock = new();

    /// <summary>A single line of output (no trailing newline).</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Raised when a background feature wants the UI to surface the Terminal page.</summary>
    public event Action? NavigateToTerminalRequested;

    /// <summary>The current shell kind (defaults to PowerShell).</summary>
    public TerminalShell CurrentShell { get; private set; } = TerminalShell.PowerShell;

    public bool IsRunning
    {
        get { lock (_lock) { return _process is { HasExited: false }; } }
    }

    /// <summary>
    /// Set by a long-running external producer (e.g. a Doctor diagnostic) so the Terminal's
    /// Stop button can cancel it. Cleared when the task ends.
    /// </summary>
    public Action? ActiveExternalCancel { get; set; }

    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public void RequestNavigateToTerminal() => NavigateToTerminalRequested?.Invoke();

    /// <summary>Kills any existing session and starts a fresh shell of the requested kind.</summary>
    public void StartSession(TerminalShell shell)
    {
        StopSession();

        lock (_lock)
        {
            CurrentShell = shell;
            var psi = new ProcessStartInfo
            {
                FileName = shell == TerminalShell.Cmd ? "cmd.exe" : "powershell.exe",
                // Both echo prompt + command + output over redirected stdin, giving a real-terminal feel.
                Arguments = shell == TerminalShell.Cmd ? "/Q /K" : "-NoLogo -NoProfile",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            try
            {
                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.OutputDataReceived += OnData;
                _process.ErrorDataReceived += OnData;
                _process.Exited += (_, _) =>
                    OutputReceived?.Invoke("\n[Shell session ended — press CMD or PowerShell above to start a new one.]");
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _process = null;
                OutputReceived?.Invoke($"[Terminal error] Could not start shell: {ex.Message}");
            }
        }
    }

    private void OnData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != null) OutputReceived?.Invoke(e.Data);
    }

    /// <summary>Writes a user command to the live shell's stdin, starting a session if needed.</summary>
    public void SendCommand(string command)
    {
        lock (_lock)
        {
            if (_process is null || _process.HasExited)
            {
                StartSession(CurrentShell);
            }

            try
            {
                _process!.StandardInput.WriteLine(command);
                _process.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                OutputReceived?.Invoke($"[Terminal error] {ex.Message}");
            }
        }
    }

    /// <summary>Surfaces a line from an external producer (Software Hub, System Doctor) in the Terminal.</summary>
    public void AppendExternalLine(string line)
    {
        if (!string.IsNullOrEmpty(line)) OutputReceived?.Invoke(line);
    }

    public void StopSession()
    {
        lock (_lock)
        {
            if (_process is null) return;
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
            try { _process.Dispose(); } catch { }
            _process = null;
        }
    }
}
