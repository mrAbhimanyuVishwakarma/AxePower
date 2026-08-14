using System.Diagnostics;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Runs a Win32 application's registered uninstall command.
/// Uses QuietUninstallString when available; otherwise uses UninstallString interactively.
/// Does NOT blindly append /S, /SILENT, /QUIET etc. to arbitrary executables.
/// </summary>
public sealed class Win32UninstallExecutor : IUninstallExecutor
{
    public ApplicationType SupportedType => ApplicationType.Win32;

    public async Task<UninstallResult> ExecuteAsync(
        InstalledApplication application,
        UninstallerSettings settings,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // Prefer QuietUninstallString when available
        var commandString = !string.IsNullOrWhiteSpace(application.QuietUninstallString)
            ? application.QuietUninstallString
            : application.UninstallString;

        if (string.IsNullOrWhiteSpace(commandString))
        {
            return Fail(application, "No uninstall command available.", sw);
        }

        // Parse executable and arguments from the command string
        var (exe, args) = ParseCommand(commandString);

        if (string.IsNullOrEmpty(exe))
            return Fail(application, "Could not parse uninstall executable from command.", sw);

        // Security: validate the executable path exists (or is a known system command)
        if (!IsKnownSystemCommand(exe) && !File.Exists(exe))
            return Fail(application, $"Uninstall executable not found: {exe}", sw);

        if (settings.DryRunMode)
        {
            return new UninstallResult
            {
                ApplicationId = application.Id,
                ApplicationName = application.DisplayName,
                Success = true,
                WasDryRun = true,
                CommandExecuted = $"[DRY RUN] Would execute: \"{exe}\" {args}",
                UninstallMethod = "Win32",
                Duration = sw.Elapsed,
            };
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,   // Required for UAC elevation prompt when needed
                Verb = application.RequiresElevation ? "runas" : string.Empty,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return Fail(application, "Failed to start uninstall process.", sw);

            await process.WaitForExitAsync(cancellationToken);

            var exitCode = process.ExitCode;
            var requiresReboot = exitCode == 3010 || exitCode == 1641;

            return new UninstallResult
            {
                ApplicationId = application.Id,
                ApplicationName = application.DisplayName,
                Success = exitCode == 0 || requiresReboot,
                ExitCode = exitCode,
                RequiresReboot = requiresReboot,
                CommandExecuted = $"\"{exe}\" {args}",
                UninstallMethod = "Win32",
                Duration = sw.Elapsed,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Fail(application, ex.Message, sw);
        }
    }

    private static (string exe, string args) ParseCommand(string commandString)
    {
        commandString = commandString.Trim();

        if (commandString.StartsWith('"'))
        {
            var end = commandString.IndexOf('"', 1);
            if (end > 1)
            {
                var exe = commandString[1..end];
                var args = commandString.Length > end + 1 ? commandString[(end + 1)..].Trim() : string.Empty;
                return (exe, args);
            }
        }

        // Handle unquoted paths containing spaces and .exe
        var exeIdx = commandString.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
        {
            var candidateExe = commandString[..(exeIdx + 4)].Trim();
            var candidateArgs = commandString.Length > exeIdx + 4 ? commandString[(exeIdx + 4)..].Trim() : string.Empty;
            if (File.Exists(candidateExe) || IsKnownSystemCommand(candidateExe))
            {
                return (candidateExe, candidateArgs);
            }
        }

        var space = commandString.IndexOf(' ');
        if (space > 0)
            return (commandString[..space], commandString[(space + 1)..].Trim());

        return (commandString, string.Empty);
    }

    private static bool IsKnownSystemCommand(string exe) =>
        exe.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ||
        exe.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
        exe.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
        exe.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase);

    private static UninstallResult Fail(InstalledApplication app, string msg, Stopwatch sw) =>
        new()
        {
            ApplicationId = app.Id,
            ApplicationName = app.DisplayName,
            Success = false,
            ErrorMessage = msg,
            UninstallMethod = "Win32",
            Duration = sw.Elapsed,
        };
}
