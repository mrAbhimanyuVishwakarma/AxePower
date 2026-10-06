using System.Windows;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Operations;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.App.Services;

/// <summary>What to do when the user presses Cancel while an uninstaller is running.</summary>
public enum StopWaitingChoice
{
    KeepWaiting,

    /// <summary>Stop waiting; the uninstaller keeps running on its own.</summary>
    StopWaiting,

    /// <summary>Terminate the uninstaller (offered for silent uninstalls only).</summary>
    StopUninstaller,
}

/// <summary>
/// The questions Uninstaller Pro asks the user. The app shows message boxes; tests use a fake,
/// so no automated test ever waits on a dialog.
/// </summary>
public interface IUninstallerPrompts
{
    bool ConfirmBatch(IReadOnlyList<(InstalledApplication App, UninstallPlan Plan)> plans,
        IReadOnlyList<(InstalledApplication App, string Reason)> skipped, bool restorePoint, bool dryRun);

    RunningAppDecision AskRunningApp(InstalledApplication app, IReadOnlyList<RunningProcessInfo> processes, bool closeAttemptFailed);

    StopWaitingChoice AskStopWaiting(string applicationName, bool isQuiet);

    bool AskStopStalledUninstaller(InstalledApplication app, TimeSpan elapsed);

    /// <param name="items">One line per leftover: its type and path, as the review list shows it.</param>
    bool ConfirmCleanLeftovers(IReadOnlyList<string> items, bool dryRun, bool recycle);

    bool ConfirmRestore(string sessionDescription);

    bool ConfirmRelaunchAsAdmin(int itemsNeedingAdmin);
}

/// <summary>
/// Uninstaller Pro's questions. Uninstalling, cleaning leftovers and restoring use the standard
/// confirmation (Prompt 27, <see cref="ConfirmationCatalog"/>); the questions asked while an
/// uninstaller is running stay simple message boxes.
/// </summary>
public sealed class DialogUninstallerPrompts : IUninstallerPrompts
{
    private const string Title = "Uninstaller";
    private readonly IConfirmationPrompts _confirm;

    public DialogUninstallerPrompts(IConfirmationPrompts confirm)
    {
        _confirm = confirm;
    }

    public bool ConfirmBatch(IReadOnlyList<(InstalledApplication App, UninstallPlan Plan)> plans,
        IReadOnlyList<(InstalledApplication App, string Reason)> skipped, bool restorePoint, bool dryRun) =>
        _confirm.Confirm(ConfirmationCatalog.UninstallApps(
            plans.Select(p => (p.App.DisplayName, p.Plan.Description)).ToList(),
            skipped.Select(s => s.App.DisplayName).ToList(), restorePoint, dryRun));

    public bool ConfirmCleanLeftovers(IReadOnlyList<string> items, bool dryRun, bool recycle) =>
        _confirm.Confirm(ConfirmationCatalog.CleanLeftovers(items, dryRun, recycle));

    public bool ConfirmRestore(string sessionDescription) =>
        _confirm.Confirm(ConfirmationCatalog.RestoreUninstallBackup(sessionDescription));

    public RunningAppDecision AskRunningApp(InstalledApplication app, IReadOnlyList<RunningProcessInfo> processes, bool closeAttemptFailed)
    {
        var names = string.Join(", ", processes.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(5));
        var text = closeAttemptFailed
            ? $"{app.DisplayName} did not close ({names}).\n\n" +
              "Yes: force it to close (unsaved work is lost).\nNo: uninstall anyway.\nCancel: skip this app."
            : $"{app.DisplayName} is running ({names}).\n\n" +
              "Yes: close it first.\nNo: uninstall anyway.\nCancel: skip this app.";

        return MessageBox.Show(text, Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) switch
        {
            MessageBoxResult.Yes => closeAttemptFailed ? RunningAppDecision.ForceClose : RunningAppDecision.Close,
            MessageBoxResult.No => RunningAppDecision.Continue,
            _ => RunningAppDecision.Skip,
        };
    }

    public StopWaitingChoice AskStopWaiting(string applicationName, bool isQuiet)
    {
        var stop = MessageBox.Show(
            $"{applicationName}'s uninstaller is still running.\n\n" +
            "No more apps will be started. Stop waiting for this one too?\n" +
            "(Stopping waiting doesn't close the uninstaller.)",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (stop != MessageBoxResult.Yes) return StopWaitingChoice.KeepWaiting;
        if (!isQuiet) return StopWaitingChoice.StopWaiting;

        var kill = MessageBox.Show(
            $"The silent uninstaller for {applicationName} has no window of its own.\n\n" +
            "Stop the uninstaller itself? This can leave the app half-removed.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return kill == MessageBoxResult.Yes ? StopWaitingChoice.StopUninstaller : StopWaitingChoice.StopWaiting;
    }

    public bool AskStopStalledUninstaller(InstalledApplication app, TimeSpan elapsed) =>
        MessageBox.Show(
            $"The silent uninstaller for {app.DisplayName} has been running for {elapsed.TotalMinutes:F0} minutes.\n\n" +
            "Yes: stop the uninstaller.\nNo: keep waiting.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmRelaunchAsAdmin(int itemsNeedingAdmin) =>
        MessageBox.Show(
            $"Restart AxePower as administrator to clean {itemsNeedingAdmin} protected item(s)?\n\n" +
            "This review is saved; reopen it from History after the restart.",
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
}
