using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class SystemDoctorService : ISystemDoctorService
{
    private readonly LogService _log;

    public SystemDoctorService(LogService log)
    {
        _log = log;
    }

    public async Task<bool> RunDiagnosticTaskAsync(
        DiagnosticTaskType taskType, 
        IProgress<string> outputProgress, 
        CancellationToken cancellationToken = default)
    {
        return taskType switch
        {
            DiagnosticTaskType.SfcScan => 
                await ExecuteCliProcessAsync("sfc.exe", "/scannow", "System File Checker (SFC)", outputProgress, cancellationToken),

            DiagnosticTaskType.DismRepair => 
                await ExecuteCliProcessAsync("dism.exe", "/Online /Cleanup-Image /RestoreHealth", "DISM Component Store Repair", outputProgress, cancellationToken),

            DiagnosticTaskType.WinGetReset => 
                await ExecuteCliProcessAsync("winget.exe", "source reset --force", "WinGet Catalog Reset", outputProgress, cancellationToken),

            DiagnosticTaskType.NetworkReset => 
                await RunNetworkStackResetAsync(outputProgress, cancellationToken),

            _ => false
        };
    }

    private async Task<bool> RunNetworkStackResetAsync(IProgress<string> outputProgress, CancellationToken ct)
    {
        outputProgress.Report("=== Starting Network & Winsock Stack Reset ===");

        bool r1 = await ExecuteCliProcessAsync("netsh.exe", "winsock reset", "Winsock Catalog Reset", outputProgress, ct);
        if (ct.IsCancellationRequested) return false;

        bool r2 = await ExecuteCliProcessAsync("netsh.exe", "int ip reset", "TCP/IP Stack Reset", outputProgress, ct);
        if (ct.IsCancellationRequested) return false;

        bool r3 = await ExecuteCliProcessAsync("ipconfig.exe", "/flushdns", "Flush DNS Cache", outputProgress, ct);

        outputProgress.Report("=== Network Stack Reset Completed ===");
        return r1 && r2 && r3;
    }

    private async Task<bool> ExecuteCliProcessAsync(
        string fileName, 
        string arguments, 
        string operationName, 
        IProgress<string> outputProgress, 
        CancellationToken ct)
    {
        outputProgress.Report($"\n[Starting {operationName}] > {fileName} {arguments}");
        _log.LogInfo($"[SystemDoctor] Executing '{fileName} {arguments}'...");

        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };

                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        outputProgress.Report(e.Data);
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        outputProgress.Report($"[ERROR] {e.Data}");
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                while (!process.WaitForExit(500))
                {
                    if (ct.IsCancellationRequested)
                    {
                        try
                        {
                            process.Kill(true);
                            outputProgress.Report($"[ABORTED] {operationName} was cancelled by user.");
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                            // The user asked to stop: tell them if it could not be stopped.
                            outputProgress.Report($"[ABORTED] {operationName} could not be stopped; it may already have finished.");
                        }
                        return false;
                    }
                }

                int exitCode = process.ExitCode;
                bool success = (exitCode == 0);

                if (success)
                {
                    outputProgress.Report($"[COMPLETED] {operationName} finished successfully (Exit Code: {exitCode}).");
                    _log.LogInfo($"[SystemDoctor] '{operationName}' completed with code 0.");
                }
                else
                {
                    outputProgress.Report($"[NOTICE] {operationName} exited with code {exitCode}.");
                    _log.LogWarning($"[SystemDoctor] '{operationName}' completed with code {exitCode}.");
                }

                return success;
            }
            catch (Exception ex)
            {
                outputProgress.Report($"[EXCEPTION] Failed to execute {operationName}: {ex.Message}");
                _log.LogError($"[SystemDoctor] Exception during {operationName}", ex);
                return false;
            }
        });
    }
}
