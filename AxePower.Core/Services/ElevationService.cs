using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;

namespace AxePower.Core.Services;

/// <summary>
/// The one place that answers "are we admin?" and "become admin".
///
/// AxePower ships an <c>asInvoker</c> manifest on purpose. Requesting
/// <c>requireAdministrator</c> there would make <see cref="Models.AppSettings.RunAsAdministrator"/>
/// impossible to switch off and would force a UAC prompt on every boot. Instead the app starts
/// unelevated and relaunches itself through the <c>runas</c> verb when the setting asks for it,
/// while the logon scheduled task registers at <c>HighestAvailable</c> so the boot path is
/// elevated with no prompt at all.
/// </summary>
public class ElevationService
{
    /// <summary>
    /// Passed to the relaunched process so a refused UAC prompt cannot turn into a relaunch
    /// loop. Group Policy can silently deny elevation, in which case the child comes back
    /// unelevated and would otherwise try again forever.
    /// </summary>
    public const string ElevationAttemptedArgument = "--elevation-attempted";

    /// <summary>Win32 <c>ERROR_CANCELLED</c> — the user dismissed the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    private readonly LogService? _logService;

    public ElevationService(LogService? logService = null)
    {
        _logService = logService;
        IsElevated = CheckElevated();
    }

    /// <summary>True when the current process holds the built-in Administrators role.</summary>
    public bool IsElevated { get; }

    public static bool CheckElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            // Fails closed: if the token cannot be read, AxePower behaves as not elevated and offers
            // "Relaunch with Admin" rather than assuming rights it may not have.
            return false;
        }
    }

    /// <summary>
    /// True when this process was itself started by a failed elevation attempt, so it must not
    /// try again.
    /// </summary>
    public static bool HasAttemptedElevation(string[] args) =>
        args.Contains(ElevationAttemptedArgument, StringComparer.OrdinalIgnoreCase);

    public enum RelaunchResult
    {
        /// <summary>An elevated process was started; the caller should shut itself down.</summary>
        Started,
        /// <summary>The user dismissed the UAC prompt — carry on unelevated.</summary>
        Cancelled,
        /// <summary>Elevation could not be attempted at all — carry on unelevated.</summary>
        Failed
    }

    /// <summary>
    /// Starts a second, elevated copy of AxePower carrying the current command line forward.
    /// The caller shuts this instance down only on <see cref="RelaunchResult.Started"/>.
    /// </summary>
    public RelaunchResult TryRelaunchElevated(params string[] forwardedArgs)
    {
        try
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
            {
                _logService?.LogWarning("Cannot relaunch elevated: the executable path is unknown.");
                return RelaunchResult.Failed;
            }

            // The sentinel always travels with the relaunch, and never twice
            var args = forwardedArgs
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Where(a => !a.Equals(ElevationAttemptedArgument, StringComparison.OrdinalIgnoreCase))
                .Append(ElevationAttemptedArgument)
                .ToArray();

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            Process.Start(psi);
            _logService?.LogInfo("Relaunching AxePower with administrator rights.");
            return RelaunchResult.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _logService?.LogInfo("Administrator relaunch declined at the UAC prompt; continuing as a standard user.");
            return RelaunchResult.Cancelled;
        }
        catch (Exception ex)
        {
            _logService?.LogError("Failed to relaunch AxePower with administrator rights.", ex);
            return RelaunchResult.Failed;
        }
    }
}
