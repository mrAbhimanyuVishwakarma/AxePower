using System.Text;
using System.Windows;
using AxePower.Core.Models.Uninstaller;
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

    bool ConfirmCleanLeftovers(int count, int needsAdmin, bool dryRun, bool recycle);

    bool ConfirmRestore(string sessionDescription);

    bool ConfirmRelaunchAsAdmin(int itemsNeedingAdmin);
}

/// <summary>Message-box prompts, matching the rest of AxePower.</summary>
public sealed class MessageBoxUninstallerPrompts : IUninstallerPrompts
{
    private const string Title = "Uninstaller Pro";

    public bool ConfirmBatch(IReadOnlyList<(InstalledApplication App, UninstallPlan Plan)> plans,
        IReadOnlyList<(InstalledApplication App, string Reason)> skipped, bool restorePoint, bool dryRun)
    {
        var text = new StringBuilder();
        text.AppendLine(dryRun
            ? "Dry run: nothing will be uninstalled. These commands would run:"
            : plans.Count == 1 ? "This will run:" : $"These {plans.Count} uninstallers will run one after another:");
        text.AppendLine();
        foreach (var (app, plan) in plans.Take(15))
            text.AppendLine($"•  {app.DisplayName}: {plan.Description}");
        if (plans.Count > 15) text.AppendLine($"   …and {plans.Count - 15} more");

        if (skipped.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Skipped (protected):");
            foreach (var (app, reason) in skipped.Take(5))
                text.AppendLine($"•  {app.DisplayName}");
        }

        if (!dryRun)
        {
            text.AppendLine();
            text.AppendLine(restorePoint
                ? "A System Restore point is created first (when Windows allows it)."
                : "No System Restore point will be created.");
            text.AppendLine("Each app is checked afterwards; leftovers are only offered once it is really gone.");
        }

        return MessageBox.Show(text.ToString(), Title, MessageBoxButton.OKCancel, MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

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

    public bool ConfirmCleanLeftovers(int count, int needsAdmin, bool dryRun, bool recycle)
    {
        var text = dryRun
            ? $"Dry run: check {count} item(s) without deleting anything?"
            : $"Delete {count} leftover item(s)?\n\n" +
              "Registry items are exported to .reg backups first; if a backup fails the item is kept.\n" +
              (recycle ? "Files and folders go to the Recycle Bin." : "Files and folders are deleted permanently.");
        if (needsAdmin > 0)
            text += $"\n\n{needsAdmin} item(s) need administrator rights and will be skipped.";
        return MessageBox.Show(text, Title, MessageBoxButton.OKCancel, MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    public bool ConfirmRestore(string sessionDescription) =>
        MessageBox.Show(
            $"Restore the registry backups from {sessionDescription}?\n\n" +
            "Keys and values are written back; HKLM items need administrator rights. " +
            "Files and folders are restored from the Recycle Bin, not here.",
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public bool ConfirmRelaunchAsAdmin(int itemsNeedingAdmin) =>
        MessageBox.Show(
            $"Restart AxePower as administrator to clean {itemsNeedingAdmin} protected item(s)?\n\n" +
            "This review is saved; reopen it from History after the restart.",
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
}
