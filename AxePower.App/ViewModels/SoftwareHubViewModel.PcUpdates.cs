using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AxePower.Core.Services;
using AxePower.Core.Services.Operations;
using AxePower.Core.Services.Outcomes;
using AxePower.Core.Services.Processes;
using AxePower.Core.Services.SoftwareHub;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels;

public sealed partial class PcAppUpdateRow : ObservableObject
{
    private readonly Action _selectionChanged;

    internal PcAppUpdateRow(WinGetUpdateCandidate candidate, Action selectionChanged)
    {
        Candidate = candidate;
        _selectionChanged = selectionChanged;
    }

    internal WinGetUpdateCandidate Candidate { get; }
    public string Name => Candidate.Name;
    public bool CanSelect => Candidate.CanSelect;
    public string Detail => $"{Candidate.InstalledVersion} → {Candidate.AvailableVersion} · {Candidate.Source}";
    public string? SelectionNote => CanSelect ? null : "Individual update unavailable. Check WinGet eligibility.";
    public string ReviewLabel => $"{Name} ({Candidate.Id}) · {Detail}";

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}

public sealed record PcAppUpdateResult(string Name, string Status, string Detail);

public partial class SoftwareHubViewModel
{
    private readonly IWindowsUpdateCatalogService? _windowsUpdates;
    private readonly IWinGetUpdateService? _appUpdates;
    private readonly IUserShellLauncher _updateHandoff;
    private bool _stopAfterCurrent;
    private bool _hasAppPreview;
    private bool _canRunWinGetBulk;
    private bool _appRestartRequired;
    private bool? _windowsRestartRequired;

    public ObservableCollection<WindowsUpdateCandidate> WindowsUpdates { get; } = new();
    public ObservableCollection<WindowsUpdateCandidate> DeviceUpdates { get; } = new();
    public ObservableCollection<PcAppUpdateRow> AppUpdates { get; } = new();
    public ObservableCollection<PcAppUpdateResult> AppUpdateResults { get; } = new();

    [ObservableProperty]
    private string _lastPcCheck = "Last checked: Never";

    [ObservableProperty]
    private string? _lastAppUpdate;

    [ObservableProperty]
    private string _windowsUpdateSummary = "Check for Windows updates and use Windows Settings to install them.";

    [ObservableProperty]
    private string _appUpdateSummary = "Check WinGet to see which installed apps have updates.";

    [ObservableProperty]
    private string _deviceUpdateSummary = "Check for drivers and firmware offered through Windows Update.";

    [ObservableProperty]
    private string? _pcUpdateProgress;

    [ObservableProperty]
    private string? _updateHandoffStatus;

    [ObservableProperty]
    private bool _pcRestartRequired;

    [ObservableProperty]
    private bool _includeUnknownAppVersions;

    [ObservableProperty]
    private bool _isCheckingAllUpdates;

    [ObservableProperty]
    private bool _isApplyingPcUpdates;

    [ObservableProperty]
    private bool _canStopSelectedUpdates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpgradeSelectedApps))]
    private int _selectedAppUpdateCount;

    public bool CanCheckForUpdates => !IsCheckingAllUpdates && !IsApplyingPcUpdates && !IsChangingUpdatePolicy
        && !IsResettingUpdateCache && !IsCreatingRestorePoint && !IsInstallingGraphicsDriver && !IsBatchInstalling
        && !IsCheckingFirmware && !IsDownloadingNvidiaDriver && !AllPackages.Any(card => card.IsBusy);

    internal bool CanStartToolPackageOperation => !IsCheckingAllUpdates && !IsApplyingPcUpdates && !IsBatchInstalling;
    internal void NotifyPackageOperationStateChanged() => NotifyUpdateActions();
    public bool CanReviewAppUpdates => CanCheckForUpdates && _hasAppPreview && !_appRestartRequired;
    public bool CanUpgradeSelectedApps => CanReviewAppUpdates && SelectedAppUpdateCount > 0;
    public bool CanUpgradeAllApps => CanCheckForUpdates && _canRunWinGetBulk && !_appRestartRequired;

    partial void OnIsCheckingAllUpdatesChanged(bool value) => NotifyUpdateActions();
    partial void OnIsApplyingPcUpdatesChanged(bool value) => NotifyUpdateActions();
    partial void OnIsChangingUpdatePolicyChanged(bool value) => NotifyUpdateActions();
    partial void OnIsResettingUpdateCacheChanged(bool value) => NotifyUpdateActions();
    partial void OnIsCreatingRestorePointChanged(bool value) => NotifyUpdateActions();
    partial void OnIsInstallingGraphicsDriverChanged(bool value) => NotifyUpdateActions();
    partial void OnIsBatchInstallingChanged(bool value) => NotifyUpdateActions();
    partial void OnIsCheckingFirmwareChanged(bool value) => NotifyUpdateActions();
    partial void OnIsDownloadingNvidiaDriverChanged(bool value) => NotifyUpdateActions();

    private void NotifyUpdateActions()
    {
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanReviewAppUpdates));
        OnPropertyChanged(nameof(CanUpgradeSelectedApps));
        OnPropertyChanged(nameof(CanUpgradeAllApps));
    }

    private void UpdateAppSelection() => SelectedAppUpdateCount = AppUpdates.Count(app => app.CanSelect && app.IsSelected);

    [RelayCommand]
    public async Task CheckAllUpdatesAsync()
    {
        if (!CanCheckForUpdates) return;

        IsCheckingAllUpdates = true;
        _hasAppPreview = _canRunWinGetBulk = false;
        WindowsUpdates.Clear();
        DeviceUpdates.Clear();
        AppUpdates.Clear();
        UpdateAppSelection();
        WindowsUpdateSummary = "Checking Windows Update…";
        AppUpdateSummary = "Checking WinGet…";
        DeviceUpdateSummary = "Checking the Windows driver catalog…";
        PcUpdateProgress = "Checking for updates…";
        UpdateHandoffStatus = null;

        try
        {
            var windowsScan = ReadWindowsUpdatesAsync();
            var appScan = ReadAppUpdatesAsync();
            await Task.WhenAll(windowsScan, appScan);
            var windows = await windowsScan;
            var apps = await appScan;

            foreach (var update in windows.Candidates)
                (update.IsDeviceUpdate ? DeviceUpdates : WindowsUpdates).Add(update);
            foreach (var app in apps.Candidates)
                AppUpdates.Add(new PcAppUpdateRow(app, UpdateAppSelection));

            _hasAppPreview = apps.Succeeded;
            _canRunWinGetBulk = apps.CanBulkUpgrade || apps.Succeeded;
            _windowsRestartRequired = windows.RebootRequired ?? _windowsRestartRequired;
            PcRestartRequired = _appRestartRequired || _windowsRestartRequired == true;
            WindowsUpdateSummary = windows.Succeeded
                ? WithDetail(CountMessage(WindowsUpdates.Count, "No Windows updates were returned.", "Windows update"), windows.Detail)
                : WithDetail("Windows Update check was incomplete. Open Windows Update for more options.", windows.Detail);
            if (windows.RebootRequired is null && _windowsRestartRequired != true)
                WindowsUpdateSummary += " Windows restart status was unavailable.";
            DeviceUpdateSummary = windows.Succeeded
                ? CountMessage(DeviceUpdates.Count, "No driver updates were returned by Windows Update.", "device update")
                : "The Windows driver catalog could not be checked completely. Use Windows Optional Updates or your vendor.";
            AppUpdateSummary = _appRestartRequired
                ? "Restart before starting another app update."
                : WithDetail(apps.Succeeded && !apps.IsPartial
                    ? CountMessage(AppUpdates.Count, "No app upgrades were returned by WinGet.", "app upgrade")
                    : "WinGet could not provide a complete app preview.", apps.Detail);

            if (windows.Succeeded || apps.Succeeded)
                LastPcCheck = $"Last checked: {DateTime.Now:g}";

            PcUpdateProgress = windows.Succeeded && apps.Succeeded && !apps.IsPartial
                ? "Check complete. Review the results below."
                : windows.Succeeded || apps.Succeeded
                    ? "Check partly complete. Review the available results below."
                    : "The update sources could not be checked. See the details below.";
        }
        finally
        {
            IsCheckingAllUpdates = false;
        }
    }

    private async Task<WindowsUpdateScanResult> ReadWindowsUpdatesAsync()
    {
        try
        {
            if (_windowsUpdates is not null) return await _windowsUpdates.ScanAsync();
            return new(Array.Empty<WindowsUpdateCandidate>(), false, null, "Windows Update discovery is unavailable.");
        }
        catch (Exception ex)
        {
            _logService.LogError("Could not read the Windows Update catalog.", ex);
            return new(Array.Empty<WindowsUpdateCandidate>(), false, null, UserMessages.Failed("check Windows Update", ex).Text);
        }
    }

    private async Task<WinGetUpdateScanResult> ReadAppUpdatesAsync()
    {
        try
        {
            if (_appUpdates is not null) return await _appUpdates.ScanAsync();
            return new(Array.Empty<WinGetUpdateCandidate>(), false, "WinGet discovery is unavailable.");
        }
        catch (Exception ex)
        {
            _logService.LogError("Could not read the WinGet upgrade list.", ex);
            return new(Array.Empty<WinGetUpdateCandidate>(), false, UserMessages.Failed("check WinGet", ex).Text);
        }
    }

    [RelayCommand]
    public async Task UpgradeSelectedAppsAsync()
    {
        if (!CanUpgradeSelectedApps || _appUpdates is null) return;

        var selected = AppUpdates.Where(x => x.CanSelect && x.IsSelected).ToArray();
        if (!_confirm.Confirm(ConfirmationCatalog.UpgradeSelectedPackages(selected.Select(x => x.ReviewLabel).ToArray()))) return;

        BeginAppUpgrade();
        CanStopSelectedUpdates = true;
        try
        {
            foreach (var app in selected)
            {
                if (_stopAfterCurrent)
                {
                    AppUpdateResults.Add(new(app.Name, "Skipped", "You stopped the queue before this app started."));
                    continue;
                }
                if (_appRestartRequired)
                {
                    AppUpdateResults.Add(new(app.Name, "Skipped", "Restart before trying this update again."));
                    continue;
                }

                PcUpdateProgress = $"Updating {app.Name}…";
                var result = await RunAppUpgradeAsync(() => _appUpdates.UpgradeAsync(app.Candidate, AppProgress()), app.Name);
                AppUpdateResults.Add(new(app.Name, AppResultStatus(result), result.Detail));
                ReportRestart(result);
                if (result.Outcome == WinGetUpdateOutcome.Applied)
                    LastAppUpdate = $"Last app update: {DateTime.Now:g}";
            }
            PcUpdateProgress = _stopAfterCurrent ? "Update queue stopped. Review the results below."
                : "Update queue finished. Review the results below.";
        }
        finally
        {
            CanStopSelectedUpdates = false;
            IsApplyingPcUpdates = false;
        }
    }

    [RelayCommand]
    public async Task UpgradeAllAppsAsync()
    {
        if (!CanUpgradeAllApps || _appUpdates is null) return;

        bool includeUnknown = IncludeUnknownAppVersions;
        var preview = _hasAppPreview
            ? AppUpdates.Where(x => includeUnknown || x.CanSelect).Select(x => $"Preview · {x.ReviewLabel}").ToArray()
            : Array.Empty<string>();
        if (!_confirm.Confirm(ConfirmationCatalog.UpgradeAllPackages(preview, includeUnknown))) return;

        BeginAppUpgrade();
        try
        {
            PcUpdateProgress = "WinGet is updating all eligible apps…";
            var result = await RunAppUpgradeAsync(() => _appUpdates.UpgradeAllAsync(includeUnknown, AppProgress()), "WinGet bulk update");
            AppUpdateResults.Add(new("WinGet bulk update", AppResultStatus(result), result.Detail));
            ReportRestart(result);
            PcUpdateProgress = "WinGet bulk update finished. Check again to see which updates remain.";
        }
        finally
        {
            IsApplyingPcUpdates = false;
        }
    }

    private void BeginAppUpgrade()
    {
        IsApplyingPcUpdates = true;
        _stopAfterCurrent = false;
        _hasAppPreview = _canRunWinGetBulk = false;
        AppUpdateResults.Clear();
        AppUpdateSummary = "Check again before starting another upgrade. This list shows the earlier preview.";
    }

    private static string AppResultStatus(WinGetUpdateResult result) => result.Outcome switch
    {
        WinGetUpdateOutcome.Applied => "Updated",
        WinGetUpdateOutcome.Finished => "Finished",
        WinGetUpdateOutcome.NoApplicableUpdates => "No applicable update",
        WinGetUpdateOutcome.RestartRequired => "Restart required",
        WinGetUpdateOutcome.Refused => "Not started",
        WinGetUpdateOutcome.Partial => "Some updates did not finish",
        WinGetUpdateOutcome.Cancelled => "Cancelled",
        _ => "Failed"
    };

    private void ReportRestart(WinGetUpdateResult result)
    {
        if (!result.RebootRequired) return;
        _appRestartRequired = true;
        PcRestartRequired = true;
        AppUpdateSummary = "Restart before starting another app update.";
        NotifyUpdateActions();
    }

    [RelayCommand]
    private void StopSelectedUpdates()
    {
        if (!CanStopSelectedUpdates) return;
        _stopAfterCurrent = true;
        CanStopSelectedUpdates = false;
        PcUpdateProgress = "Stopping after the current app finishes…";
    }

    private IProgress<string> AppProgress() => new Progress<string>(message =>
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        AppendLog(message);
        if (IsApplyingPcUpdates && !_stopAfterCurrent)
            PcUpdateProgress = message.Trim();
    });

    private async Task<WinGetUpdateResult> RunAppUpgradeAsync(Func<Task<WinGetUpdateResult>> upgrade, string name)
    {
        try
        {
            return await upgrade();
        }
        catch (Exception ex)
        {
            _logService.LogError($"Could not update {name}.", ex);
            return new(false, false, UserMessages.Failed($"update {name}", ex).Text, WinGetUpdateOutcome.Failed);
        }
    }

    [RelayCommand]
    private void OpenWindowsUpdateSettings() => OpenUpdatePage(UserShellLauncher.WindowsUpdateSettings, "Windows Update",
        "Open Settings, then search for Windows Update.");

    [RelayCommand]
    private void OpenWindowsOptionalUpdates() => OpenUpdatePage(UserShellLauncher.OptionalUpdateSettings, "Optional Updates",
        "Open Windows Update in Settings and look for Optional updates.");

    [RelayCommand]
    private void OpenWindowsRestartOptions() => OpenUpdatePage(UserShellLauncher.RestartUpdateSettings, "Windows restart options",
        "Open Windows Update or use the Start menu when you are ready to restart.");

    [RelayCommand]
    private void OpenStoreUpdates() => OpenUpdatePage("ms-windows-store://downloadsandupdates", "Microsoft Store updates",
        "If Microsoft Store is installed and permitted, open it and go to Downloads.");

    private void OpenUpdatePage(string target, string page, string fallback)
    {
        var result = _updateHandoff.OpenUri(target);
        UpdateHandoffStatus = result.Started ? $"Requested {page}." : $"Could not open {page}. {fallback}";
        AppendLog($"[Update PC] {page}: {result.Message}");
    }

    private static string CountMessage(int count, string empty, string item) =>
        count == 0 ? empty : $"{count} {item}{(count == 1 ? "" : "s")} found.";

    private static string WithDetail(string summary, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? summary : $"{summary} {detail}";
}
