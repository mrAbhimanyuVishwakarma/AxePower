using System.Diagnostics;
using System.Text.RegularExpressions;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Uninstalls MSI applications using msiexec /x {ProductCode}.
/// Validates the ProductCode format before execution.
/// </summary>
public sealed class MsiUninstallExecutor : IUninstallExecutor
{
    public ApplicationType SupportedType => ApplicationType.MSI;

    // MSI exit code for "reboot required"
    private const int ExitRebootRequired = 3010;
    private const int ExitRebootInitiated = 1641;

    public async Task<UninstallResult> ExecuteAsync(
        InstalledApplication application,
        UninstallerSettings settings,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        if (!IsValidProductCode(application.ProductCode))
        {
            return Fail(application,
                $"Invalid or missing MSI ProductCode: '{application.ProductCode}'", sw);
        }

        // /qb = basic UI (shows progress), /norestart = defer reboot decision to the user
        // We use /qb rather than /q (silent) to keep the user informed and to handle
        // installers that require user decisions. This is the safest default.
        var logPath = GetLogPath(application);
        var arguments = $"/x {application.ProductCode} /qb /norestart /l*v \"{logPath}\"";

        if (settings.DryRunMode)
        {
            return new UninstallResult
            {
                ApplicationId = application.Id,
                ApplicationName = application.DisplayName,
                Success = true,
                WasDryRun = true,
                CommandExecuted = $"[DRY RUN] Would execute: msiexec {arguments}",
                UninstallMethod = "MSI",
                Duration = sw.Elapsed,
            };
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = arguments,
                UseShellExecute = true,  // msiexec will handle UAC elevation itself
            };

            using var process = Process.Start(psi);
            if (process is null)
                return Fail(application, "Failed to start msiexec.exe.", sw);

            await process.WaitForExitAsync(cancellationToken);

            var exitCode = process.ExitCode;
            var requiresReboot = exitCode is ExitRebootRequired or ExitRebootInitiated;
            var success = exitCode == 0 || requiresReboot;

            return new UninstallResult
            {
                ApplicationId = application.Id,
                ApplicationName = application.DisplayName,
                Success = success,
                ExitCode = exitCode,
                RequiresReboot = requiresReboot,
                CommandExecuted = $"msiexec {arguments}",
                UninstallMethod = "MSI",
                ErrorMessage = !success ? $"msiexec exited with code {exitCode}." : string.Empty,
                Duration = sw.Elapsed,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Fail(application, ex.Message, sw);
        }
    }

    /// <summary>Validates MSI ProductCode format: {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}</summary>
    public static bool IsValidProductCode(string productCode)
    {
        if (string.IsNullOrWhiteSpace(productCode)) return false;
        return Regex.IsMatch(productCode, @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");
    }

    private static string GetLogPath(InstalledApplication app)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AxePower", "UninstallLogs");
        Directory.CreateDirectory(logDir);
        var safeName = string.Concat(app.DisplayName
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
            .Replace(" ", "_");
        return Path.Combine(logDir, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    }

    private static UninstallResult Fail(InstalledApplication app, string msg, Stopwatch sw) =>
        new()
        {
            ApplicationId = app.Id,
            ApplicationName = app.DisplayName,
            Success = false,
            ErrorMessage = msg,
            UninstallMethod = "MSI",
            Duration = sw.Elapsed,
        };
}
