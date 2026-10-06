using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Processes;

namespace AxePower.Core.Services;

/// <summary>
/// Creates a Windows System Restore point. All underlying calls require administrator rights, so
/// the host process must already be elevated; callers check that before starting this operation.
/// </summary>
public class RestorePointService
{
    private static readonly TimeSpan CreationTimeout = TimeSpan.FromMinutes(3);
    private readonly LogService _log;
    private readonly IProcessRunner _runner;

    public RestorePointService(LogService log, IProcessRunner? runner = null)
    {
        _log = log;
        _runner = runner ?? new AllowListedProcessRunner(ExecutableAllowList.Windows(), log);
    }

    public async Task<bool> CreateRestorePointAsync(string description, IProgress<string> progress)
    {
        string script = BuildPowerShellScript(description);
        string encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        progress.Report($"\n>>> Creating System Restore point \"{description}\"...");
        _log.LogInfo("[SystemRestore] Creating restore point...");

        var command = new ProcessCommand(AllowedTool.PowerShell,
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encodedScript },
            CreationTimeout);
        var result = await _runner.RunAsync(command, line =>
        {
            if (!string.IsNullOrWhiteSpace(line.Text)) progress.Report(line.IsError ? $"[ERROR] {line.Text}" : line.Text);
        });

        switch (result.Outcome)
        {
            case ProcessRunOutcome.Exited:
                bool ok = result.ExitCode == 0;
                progress.Report(ok
                    ? "[Restore Point] ✓ Restore point created successfully."
                    : "[Restore Point] Finished with errors — see messages above.");
                _log.LogInfo($"[SystemRestore] Restore point creation exit code {result.ExitCode}.");
                return ok;
            case ProcessRunOutcome.TimedOut:
                progress.Report("[ERROR] Restore point creation timed out after three minutes.");
                _log.LogError("[SystemRestore] Restore point creation timed out.");
                return false;
            default:
                progress.Report($"[ERROR] {result.Error ?? result.Outcome.ToString()}");
                _log.LogError($"[SystemRestore] Restore point creation failed: {result.Outcome} {result.Error}");
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
