using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AxePower.Core.Services;

/// <summary>
/// Creates a Windows System Restore point. All of the underlying calls
/// (Enable-ComputerRestore, the HKLM frequency tweak, Checkpoint-Computer) require
/// administrator rights, so the host process must already be elevated — callers should
/// check <see cref="TerminalService.IsProcessElevated"/> first.
/// </summary>
public class RestorePointService
{
    private readonly LogService _log;

    public RestorePointService(LogService log)
    {
        _log = log;
    }

    public async Task<bool> CreateRestorePointAsync(string description, IProgress<string> progress)
    {
        // Ensure restore is enabled on the system drive, remove the once-per-24h throttle so the
        // point is actually created, then checkpoint.
        var script =
            "$ErrorActionPreference='Stop';" +
            "Write-Output '[Restore Point] Enabling System Restore on system drive...';" +
            "Enable-ComputerRestore -Drive \"$env:SystemDrive\\\";" +
            "New-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' " +
            "-Name 'SystemRestorePointCreationFrequency' -Value 0 -PropertyType DWord -Force | Out-Null;" +
            $"Write-Output '[Restore Point] Creating checkpoint: {description}';" +
            $"Checkpoint-Computer -Description \"{description}\" -RestorePointType 'MODIFY_SETTINGS';" +
            "Write-Output '[Restore Point] Done. Verify under System Protection > System Restore.'";

        progress.Report($"\n>>> Creating System Restore point \"{description}\"...");
        _log.LogInfo("[SystemRestore] Creating restore point...");

        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) progress.Report(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) progress.Report($"[ERROR] {e.Data}");
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                bool ok = process.ExitCode == 0;
                progress.Report(ok
                    ? "[Restore Point] ✓ Restore point created successfully."
                    : "[Restore Point] Finished with errors — see messages above.");
                _log.LogInfo($"[SystemRestore] Restore point creation exit code {process.ExitCode}.");
                return ok;
            }
            catch (Exception ex)
            {
                progress.Report($"[ERROR] {ex.Message}");
                _log.LogError("[SystemRestore] Restore point creation failed", ex);
                return false;
            }
        });
    }
}
