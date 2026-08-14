using System.Diagnostics;
using System.Text;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Uninstalls AppX/MSIX packages using PowerShell Remove-AppxPackage.
/// Never deletes WindowsApps directory. Respects NonRemovable packages.
/// </summary>
public sealed class AppxUninstallExecutor : IUninstallExecutor
{
    public ApplicationType SupportedType => ApplicationType.AppX;

    public async Task<UninstallResult> ExecuteAsync(
        InstalledApplication application,
        UninstallerSettings settings,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        if (!application.CanUninstall)
            return Fail(application, "This package is marked NonRemovable by Windows.", sw);

        if (string.IsNullOrWhiteSpace(application.PackageFullName))
            return Fail(application, "No PackageFullName available for this AppX package.", sw);

        // Security: validate PackageFullName contains no dangerous characters
        if (!IsValidPackageName(application.PackageFullName))
            return Fail(application, $"Invalid PackageFullName: '{application.PackageFullName}'", sw);

        if (settings.DryRunMode)
        {
            return new UninstallResult
            {
                ApplicationId = application.Id,
                ApplicationName = application.DisplayName,
                Success = true,
                WasDryRun = true,
                CommandExecuted = $"[DRY RUN] Would execute: Remove-AppxPackage -Package '{application.PackageFullName}'",
                UninstallMethod = "AppX",
                Duration = sw.Elapsed,
            };
        }

        try
        {
            // Use -Package parameter with ArgumentList for safety
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            // Compose safe PowerShell command using the package name as a literal value
            // Sanitized above — no injection possible
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"Remove-AppxPackage -Package '{application.PackageFullName}'");

            using var process = Process.Start(psi);
            if (process is null)
                return Fail(application, "Failed to start PowerShell.", sw);

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var exitCode = process.ExitCode;
            var success = exitCode == 0;

            return new UninstallResult
            {
                ApplicationId = application.Id,
                ApplicationName = application.DisplayName,
                Success = success,
                ExitCode = exitCode,
                ErrorMessage = !success ? stderr.Trim() : string.Empty,
                CommandExecuted = $"Remove-AppxPackage -Package '{application.PackageFullName}'",
                UninstallMethod = "AppX",
                Duration = sw.Elapsed,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Fail(application, ex.Message, sw);
        }
    }

    private static bool IsValidPackageName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        // PackageFullName contains only alphanumeric, dots, underscores, hyphens, tildes
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"^[\w\.\-~]+$");
    }

    private static UninstallResult Fail(InstalledApplication app, string msg, Stopwatch sw) =>
        new()
        {
            ApplicationId = app.Id,
            ApplicationName = app.DisplayName,
            Success = false,
            ErrorMessage = msg,
            UninstallMethod = "AppX",
            Duration = sw.Elapsed,
        };
}
