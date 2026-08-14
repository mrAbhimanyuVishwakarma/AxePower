using System.Diagnostics;
using System.Text;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Uninstalls applications via Windows Package Manager (winget).
/// Falls back gracefully if winget is unavailable.
/// </summary>
public sealed class WingetUninstallExecutor : IUninstallExecutor
{
    public ApplicationType SupportedType => ApplicationType.WinGet;

    public async Task<UninstallResult> ExecuteAsync(
        InstalledApplication application,
        UninstallerSettings settings,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(application.WingetId))
            return Fail(application, "No WinGet package ID available.", sw);

        // Security: validate that the WinGet ID contains only safe characters
        if (!IsValidWingetId(application.WingetId))
            return Fail(application, $"Invalid WinGet package ID: '{application.WingetId}'", sw);

        var wingetPath = FindWinget();
        if (wingetPath is null)
            return Fail(application, "winget.exe not found on this system.", sw);

        // Use ArgumentList to avoid shell injection from the package ID
        if (settings.DryRunMode)
        {
            return new UninstallResult
            {
                ApplicationId = application.Id,
                ApplicationName = application.DisplayName,
                Success = true,
                WasDryRun = true,
                CommandExecuted = $"[DRY RUN] Would execute: winget uninstall --id {application.WingetId} --exact --accept-source-agreements",
                UninstallMethod = "WinGet",
                Duration = sw.Elapsed,
            };
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = wingetPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            // Use ArgumentList for safe argument passing
            psi.ArgumentList.Add("uninstall");
            psi.ArgumentList.Add("--id");
            psi.ArgumentList.Add(application.WingetId);
            psi.ArgumentList.Add("--exact");
            psi.ArgumentList.Add("--accept-source-agreements");
            psi.ArgumentList.Add("--disable-interactivity");

            using var process = Process.Start(psi);
            if (process is null)
                return Fail(application, "Failed to start winget.exe.", sw);

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
                ErrorMessage = !success ? $"winget exited {exitCode}: {stderr.Trim()}" : string.Empty,
                CommandExecuted = $"winget uninstall --id {application.WingetId} --exact",
                UninstallMethod = "WinGet",
                Duration = sw.Elapsed,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Fail(application, ex.Message, sw);
        }
    }

    private static bool IsValidWingetId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        // WinGet IDs are Publisher.AppName format — only alphanumeric, dots, hyphens, underscores
        return System.Text.RegularExpressions.Regex.IsMatch(id, @"^[\w\.\-]+$");
    }

    private static string? FindWinget()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\winget.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        try
        {
            var psi = new ProcessStartInfo("where", "winget")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var line = p?.StandardOutput.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(line) && File.Exists(line)) return line;
        }
        catch { }

        return null;
    }

    private static UninstallResult Fail(InstalledApplication app, string msg, Stopwatch sw) =>
        new()
        {
            ApplicationId = app.Id,
            ApplicationName = app.DisplayName,
            Success = false,
            ErrorMessage = msg,
            UninstallMethod = "WinGet",
            Duration = sw.Elapsed,
        };
}
