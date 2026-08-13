using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AxePower.Core.Services;

public class DebloatService
{
    private readonly LogService _logService;
    private Process? _activeProcess;

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public DebloatService(LogService logService)
    {
        _logService = logService;
    }

    /// <summary>
    /// Launches the elevated Windows Debloat & Utility interface.
    /// If an instance is already active, restores and focuses it.
    /// </summary>
    public bool LaunchDebloatUtility()
    {
        try
        {
            if (TryFocusExistingWindow())
            {
                return true;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"& ([scriptblock]::Create((irm 'https://debloat.raphi.re/')))\"",
                UseShellExecute = true,
                Verb = "runas"
            };

            _logService.LogInfo("Launching elevated Win11Debloat by Raphire...");
            _activeProcess = Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _logService.LogWarning("Debloat utility UAC prompt was cancelled by user.");
            return false;
        }
        catch (Exception ex)
        {
            _logService.LogError("Error launching Debloat utility", ex);
            return false;
        }
    }

    private bool TryFocusExistingWindow()
    {
        IntPtr foundHwnd = IntPtr.Zero;

        if (_activeProcess != null)
        {
            try
            {
                if (!_activeProcess.HasExited)
                {
                    _activeProcess.Refresh();
                    if (_activeProcess.MainWindowHandle != IntPtr.Zero)
                    {
                        foundHwnd = _activeProcess.MainWindowHandle;
                    }
                }
                else
                {
                    _activeProcess = null;
                }
            }
            catch
            {
                _activeProcess = null;
            }
        }

        if (foundHwnd == IntPtr.Zero)
        {
            EnumWindows((hWnd, lParam) =>
            {
                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (!string.IsNullOrEmpty(title) && (title.Contains("Win11Debloat", StringComparison.OrdinalIgnoreCase) || title.Contains("Debloat", StringComparison.OrdinalIgnoreCase)))
                {
                    foundHwnd = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }

        if (foundHwnd != IntPtr.Zero)
        {
            ShowWindow(foundHwnd, SW_RESTORE);
            SetForegroundWindow(foundHwnd);
            _logService.LogInfo("Debloat utility is already running. Focused active window.");
            return true;
        }

        return false;
    }
}
