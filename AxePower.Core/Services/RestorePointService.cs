using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services;

/// <summary>
/// Creates a Windows System Restore point. All underlying calls require administrator rights, so
/// the host process must already be elevated; callers check that before starting this operation.
/// </summary>
public class RestorePointService
{
    private static readonly TimeSpan CreationTimeout = TimeSpan.FromMinutes(3);
    private readonly LogService _log;

    public RestorePointService(LogService log)
    {
        _log = log;
    }

    public async Task<bool> CreateRestorePointAsync(string description, IProgress<string> progress)
    {
        string script = BuildPowerShellScript(description);
        string encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        progress.Report($"\n>>> Creating System Restore point \"{description}\"...");
        _log.LogInfo("[SystemRestore] Creating restore point...");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(encodedScript);

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) progress.Report(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) progress.Report($"[ERROR] {e.Data}");
            };

            if (!process.Start())
                throw new InvalidOperationException("PowerShell did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(CreationTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                progress.Report("[ERROR] Restore point creation timed out after three minutes.");
                _log.LogError("[SystemRestore] Restore point creation timed out.");
                return false;
            }

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
    }

    internal static string BuildPowerShellScript(string description)
    {
        // Single-quoted PowerShell literals escape a quote by doubling it. The command itself is
        // then passed as UTF-16 Base64, so punctuation in the description cannot alter the script.
        string safeDescription = "'" + (description ?? string.Empty).Replace("'", "''") + "'";
        return
            "$ErrorActionPreference = 'Stop'\n" +
            "$restoreKey = 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore'\n" +
            "$frequency = Get-ItemProperty -Path $restoreKey -Name 'SystemRestorePointCreationFrequency' -ErrorAction SilentlyContinue\n" +
            "$hadFrequency = $null -ne $frequency\n" +
            "$oldFrequency = if ($hadFrequency) { $frequency.SystemRestorePointCreationFrequency } else { $null }\n" +
            "try {\n" +
            "  Write-Output '[Restore Point] Enabling System Restore on system drive...'\n" +
            "  Enable-ComputerRestore -Drive ($env:SystemDrive + '\\')\n" +
            "  New-ItemProperty -Path $restoreKey -Name 'SystemRestorePointCreationFrequency' -Value 0 -PropertyType DWord -Force | Out-Null\n" +
            $"  Write-Output ('[Restore Point] Creating checkpoint: ' + {safeDescription})\n" +
            $"  Checkpoint-Computer -Description {safeDescription} -RestorePointType 'MODIFY_SETTINGS'\n" +
            "  Write-Output '[Restore Point] Done. Verify under System Protection > System Restore.'\n" +
            "}\n" +
            "finally {\n" +
            "  if ($hadFrequency) { Set-ItemProperty -Path $restoreKey -Name 'SystemRestorePointCreationFrequency' -Value $oldFrequency -Type DWord }\n" +
            "  else { Remove-ItemProperty -Path $restoreKey -Name 'SystemRestorePointCreationFrequency' -ErrorAction SilentlyContinue }\n" +
            "}\n";
    }
}
