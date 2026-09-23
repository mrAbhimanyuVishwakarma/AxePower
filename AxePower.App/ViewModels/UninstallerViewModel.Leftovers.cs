using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.App.ViewModels;

/// <summary>Leftover review, cleanup, admin relaunch, history and settings for Uninstaller Pro.</summary>
public sealed partial class UninstallerViewModel
{
    private UninstallSession? _reviewSession;
    private HashSet<string> _reviewedAppIds = new(StringComparer.OrdinalIgnoreCase);

    // ── Leftover review state ─────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<LeftoverItemViewModel> _leftoverItems = new();

    /// <summary>The review list grouped by app; blocked items only when "Show blocked items" is on.</summary>
    [ObservableProperty]
    private ICollectionView? _leftoverView;

    [ObservableProperty]
    private bool _isLeftoverReviewVisible;

    [ObservableProperty]
    private string _leftoverApplicationName = string.Empty;

    /// <summary>Summary of the last cleanup, e.g. "3 deleted · 1 needs admin".</summary>
    [ObservableProperty]
    private string _cleanupSummary = string.Empty;

    [ObservableProperty]
    private int _leftoverNeedsAdminCount;

    [ObservableProperty]
    private int _leftoverBlockedCount;

    public string SelectedLeftoverCount => $"{LeftoverItems.Count(i => i.IsSelected)} items selected";
    public bool IsAdminBannerVisible => LeftoverNeedsAdminCount > 0 && !IsElevated;
    public string ReviewBackupFolder => _reviewSession is null ? string.Empty : _manifests.GetSessionFolder(_reviewSession.SessionId);

    // ── History state ─────────────────────────────────────────────────────────

    public ObservableCollection<UninstallSessionViewModel> Sessions { get; } = new();

    // ── Standalone scan ───────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanScanSelected))]
    private async Task ScanLeftoversForSelectedAsync()
    {
        if (SelectedApplication is null) return;

        var app = SelectedApplication.Application;
        StatusMessage = $"Scanning leftovers for {app.DisplayName}...";
        IsBusy = true;
        IsCancellable = true;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        try
        {
            // An app that is still installed is scanned for information: its own folder is
            // blocked and nothing is pre-ticked. Broken entries are never pre-ticked either.
            var stillInstalled = !app.IsBrokenEntry && await _verifier.IsStillInstalledAsync(app, ct);
            var snapshot = await _discoveryService.DiscoverRegistryApplicationsAsync(ct);
            var leftovers = await _leftoverScanner.ScanAsync(app, _settings.DefaultScanMode, _settings.Clone(),
                new LeftoverScanOptions
                {
                    InstalledApplications = snapshot,
                    AllowPreselection = !stillInstalled && !app.IsBrokenEntry,
                    ApplicationStillInstalled = stillInstalled,
                    IsElevated = IsElevated,
                }, ct);

            var session = new UninstallSession
            {
                IsStandaloneScan = true,
                ApplicationNames = [app.DisplayName],
                LeftoverItems = leftovers.ToList(),
                ScanMode = _settings.DefaultScanMode.ToString(),
                WasDryRun = _settings.DryRunMode,
                EndTime = DateTime.Now,
            };

            if (leftovers.Count > 0)
            {
                await _historyService.SaveSessionAsync(session);
                PrepareLeftoverReview(session, [app.Id]);
            }

            StatusMessage = stillInstalled
                ? $"{app.DisplayName} is still installed: {leftovers.Count} related item(s), none pre-selected."
                : $"Found {leftovers.Count} potential leftover(s) for {app.DisplayName}.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Leftover scan cancelled.";
        }
        catch (Exception ex)
        {
            _log.LogError("[Uninstaller] Leftover scan failed", ex);
            _notification.Show("Leftover scan failed. Check Logs.");
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            IsCancellable = false;
            IsBusy = false;
        }
    }

    private bool CanScanSelected() => CanStartWork() && SelectedApplication is not null;

    // ── Review ────────────────────────────────────────────────────────────────

    private void PrepareLeftoverReview(UninstallSession session, IEnumerable<string>? appIds = null)
    {
        OnUiThread(() =>
        {
            _reviewSession = session;
            _reviewedAppIds = (appIds ?? session.Results.Select(r => r.ApplicationId))
                .Concat(session.LeftoverItems.Select(i => i.ApplicationId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var old in LeftoverItems) old.PropertyChanged -= OnLeftoverSelectionChanged;
            var items = new ObservableCollection<LeftoverItemViewModel>(
                session.LeftoverItems.Select(i => new LeftoverItemViewModel(i, IsElevated)));
            foreach (var item in items) item.PropertyChanged += OnLeftoverSelectionChanged;
            LeftoverItems = items;

            var view = new ListCollectionView(items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LeftoverItemViewModel.ApplicationName)));
            view.Filter = o => _settings.ShowBlockedLeftovers || o is LeftoverItemViewModel { IsBlocked: false };
            LeftoverView = view;

            LeftoverApplicationName = string.Join(", ", session.LeftoverItems.Select(i => i.ApplicationName).Distinct().Take(3));
            CleanupSummary = session.CleanupResult?.Summary ?? string.Empty;
            UpdateLeftoverCounts();
            IsLeftoverReviewVisible = true;
        });
    }

    private void OnLeftoverSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LeftoverItemViewModel.IsSelected))
            OnPropertyChanged(nameof(SelectedLeftoverCount));
    }

    private void UpdateLeftoverCounts()
    {
        LeftoverNeedsAdminCount = LeftoverItems.Count(i => i.IsLocked);
        LeftoverBlockedCount = LeftoverItems.Count(i => i.IsBlocked);
        OnPropertyChanged(nameof(SelectedLeftoverCount));
        OnPropertyChanged(nameof(IsAdminBannerVisible));
        OnPropertyChanged(nameof(ReviewBackupFolder));
    }

    [RelayCommand(CanExecute = nameof(CanStartWork))]
    private async Task CleanSelectedLeftoversAsync()
    {
        var toDelete = LeftoverItems.Where(i => i.IsSelected && i.CanSelect).ToList();
        if (toDelete.Count == 0)
        {
            _notification.Show("No leftover items selected for cleanup.");
            return;
        }

        if (!_prompts.ConfirmCleanLeftovers(toDelete.Count, 0, _settings.DryRunMode, _settings.MoveToRecycleBin))
            return;

        var session = _reviewSession ?? new UninstallSession { IsStandaloneScan = true };
        IsBusy = true;
        StatusMessage = $"Cleaning {toDelete.Count} item(s)...";

        try
        {
            // Re-check against what is installed now, except the apps under review
            var snapshot = await _discoveryService.DiscoverRegistryApplicationsAsync();
            var others = snapshot
                .Where(a => !(_reviewedAppIds.Contains(a.Id) && a.IsBrokenEntry))
                .Select(a => a.InstallLocation)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            var result = await _leftoverCleanup.CleanAsync(
                toDelete.Select(i => i.Source).ToList(),
                _settings.Clone(),
                new CleanupContext { SessionId = session.SessionId, IsElevated = IsElevated, OtherInstallLocations = others });

            var byId = result.Items.ToDictionary(r => r.ItemId);
            foreach (var vm in toDelete)
            {
                if (!byId.TryGetValue(vm.Source.ItemId, out var r)) continue;
                vm.ResultLabel = string.IsNullOrEmpty(r.Reason) ? r.StatusLabel : $"{r.StatusLabel}: {r.Reason}";
                vm.IsSelected = false;
            }

            // Items that are gone leave the list; the rest stay with their result
            var gone = result.Items
                .Where(r => r.Status is CleanupItemStatus.Deleted or CleanupItemStatus.AlreadyGone)
                .Select(r => r.ItemId)
                .ToHashSet();
            foreach (var vm in LeftoverItems.Where(i => gone.Contains(i.Source.ItemId)).ToList())
            {
                vm.PropertyChanged -= OnLeftoverSelectionChanged;
                LeftoverItems.Remove(vm);
            }

            // Record the honest result in the session and save it again
            session.CleanupResult ??= new CleanupResult { BackupFolder = result.BackupFolder, WasDryRun = result.WasDryRun };
            session.CleanupResult.Items.AddRange(result.Items);
            session.CleanedItems.AddRange(toDelete
                .Where(i => byId.TryGetValue(i.Source.ItemId, out var r) && r.Status == CleanupItemStatus.Deleted)
                .Select(i => i.Source));
            session.LeftoverReviewDone = !result.Items.Any(r => r.Status == CleanupItemStatus.SkippedNeedsAdmin);
            await _historyService.SaveSessionAsync(session);

            CleanupSummary = result.Summary;
            StatusMessage = $"Cleanup: {result.Summary}" + (result.TotalBytesReclaimed > 0 ? $" ({result.DisplayReclaimed})" : string.Empty);
            _notification.Show(StatusMessage);

            UpdateLeftoverCounts();
            if (LeftoverItems.Count == 0) IsLeftoverReviewVisible = false;
        }
        catch (Exception ex)
        {
            _log.LogError("[Uninstaller] Leftover cleanup failed", ex);
            _notification.Show("Leftover cleanup failed. Check Logs.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CloseLeftoverReviewAsync()
    {
        IsLeftoverReviewVisible = false;
        foreach (var old in LeftoverItems) old.PropertyChanged -= OnLeftoverSelectionChanged;
        LeftoverItems = new();
        LeftoverView = null;

        if (_reviewSession is { LeftoverReviewDone: false } session)
        {
            session.LeftoverReviewDone = true;
            await _historyService.SaveSessionAsync(session);
        }
        _reviewSession = null;
    }

    [RelayCommand]
    private void OpenReviewBackupFolder()
    {
        var folder = ReviewBackupFolder;
        if (folder.Length > 0 && Directory.Exists(folder)) ShellOpen(folder);
        else _notification.Show("No backups have been written for this review yet.");
    }

    /// <summary>Saves the review, then relaunches AxePower elevated so admin-only items can be cleaned.</summary>
    [RelayCommand]
    private async Task RestartAsAdministratorAsync()
    {
        if (!_prompts.ConfirmRelaunchAsAdmin(LeftoverNeedsAdminCount)) return;

        if (_reviewSession is { } session)
        {
            session.LeftoverReviewDone = false;
            await _historyService.SaveSessionAsync(session);
        }

        // --force stops the elevated child from treating this closing instance as the live one
        if (_elevation.TryRelaunchElevated("--force") == ElevationService.RelaunchResult.Started)
            System.Windows.Application.Current?.Shutdown();
        else
            _notification.Show("Administrator restart was cancelled.");
    }

    // ── History ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleHistoryAsync()
    {
        ShowHistoryPanel = !ShowHistoryPanel;
        if (!ShowHistoryPanel) return;
        ShowSettingsPanel = false;
        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        var sessions = await _historyService.GetSessionsAsync();
        Sessions.Clear();
        foreach (var session in sessions.OrderByDescending(s => s.StartTime))
        {
            try
            {
                var folder = _manifests.GetSessionFolder(session.SessionId);
                Sessions.Add(new UninstallSessionViewModel(session, folder)
                {
                    CanRestore = _manifests.Load(session.SessionId).Entries.Any(e =>
                        e.Status == CleanupItemStatus.Deleted && e.RestoredAt is null &&
                        e.BackupKind is BackupKind.RegistryKey or BackupKind.RegistryValue or BackupKind.ScheduledTask),
                });
            }
            catch (Exception ex)
            {
                // One unreadable session must not empty the whole history panel
                _log.LogError($"[Uninstaller] Could not read session {session.SessionId}", ex);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartWork))]
    private async Task RestoreSessionAsync(UninstallSessionViewModel? session)
    {
        if (session is null || !_prompts.ConfirmRestore($"{session.When} ({session.Title})")) return;

        IsBusy = true;
        try
        {
            var report = await _registryBackup.RestoreSessionAsync(session.Session.SessionId);
            var message = $"Restore: {report.Summary}.";
            if (report.Messages.Count > 0) message += " " + string.Join(" ", report.Messages.Take(2));
            StatusMessage = message;
            _notification.Show(message);
            _log.LogInfo($"[Uninstaller] {message}");
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            _log.LogError("[Uninstaller] Restore failed", ex);
            _notification.Show("Restore failed. Check Logs.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSessionFolder(UninstallSessionViewModel? session)
    {
        if (session is not null && Directory.Exists(session.BackupFolder)) ShellOpen(session.BackupFolder);
    }

    [RelayCommand]
    private void ResumeReview(UninstallSessionViewModel? session)
    {
        if (session is null) return;
        ShowHistoryPanel = false;
        PrepareLeftoverReview(session.Session);
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(UninstallSessionViewModel? session)
    {
        if (session is null) return;
        await _historyService.DeleteSessionAsync(session.Session.SessionId);
        Sessions.Remove(session);
    }

    // ── Settings (persisted under "uninstaller" in settings.json) ────────────

    public ScanMode[] ScanModes { get; } = [ScanMode.Safe, ScanMode.Moderate, ScanMode.Advanced];

    [ObservableProperty] private ScanMode _defaultScanMode;
    [ObservableProperty] private bool _preferQuietUninstall;
    [ObservableProperty] private bool _createSystemRestorePoint;
    [ObservableProperty] private bool _backupRegistryChanges;
    [ObservableProperty] private bool _moveToRecycleBin;
    [ObservableProperty] private bool _autoScanLeftovers;
    [ObservableProperty] private bool _showProtectedApplications;
    [ObservableProperty] private bool _showBrokenEntries;
    [ObservableProperty] private bool _showSystemComponents;
    [ObservableProperty] private bool _showUpdates;
    [ObservableProperty] private bool _showBlockedLeftovers;
    [ObservableProperty] private int _quietStallMinutes;
    [ObservableProperty] private bool _dryRunMode;

    /// <summary>Excluded paths, one per line (registry paths start with HKLM\ or HKCU\).</summary>
    [ObservableProperty] private string _excludedPathsText = string.Empty;

    private void LoadSettingProperties()
    {
        _loadingSettings = true;
        DefaultScanMode = _settings.AllowAdvancedScan || _settings.DefaultScanMode != ScanMode.Advanced
            ? _settings.DefaultScanMode
            : ScanMode.Moderate;
        PreferQuietUninstall = _settings.PreferQuietUninstall;
        CreateSystemRestorePoint = _settings.CreateSystemRestorePoint;
        BackupRegistryChanges = _settings.BackupRegistryChanges;
        MoveToRecycleBin = _settings.MoveToRecycleBin;
        AutoScanLeftovers = _settings.AutoScanLeftovers;
        ShowProtectedApplications = _settings.ShowProtectedApplications;
        ShowBrokenEntries = _settings.ShowOrphanedApplications;
        ShowSystemComponents = _settings.ShowSystemComponents;
        ShowUpdates = _settings.ShowUpdates;
        ShowBlockedLeftovers = _settings.ShowBlockedLeftovers;
        QuietStallMinutes = _settings.QuietStallMinutes;
        DryRunMode = _settings.DryRunMode;
        ExcludedPathsText = string.Join(Environment.NewLine, _settings.ExcludedRegistryPaths.Concat(_settings.ExcludedFilePaths));
        _loadingSettings = false;
    }

    partial void OnDefaultScanModeChanged(ScanMode value) => SaveSetting(s => s.DefaultScanMode = value);
    partial void OnPreferQuietUninstallChanged(bool value)
    {
        SaveSetting(s => s.PreferQuietUninstall = value);
        if (SelectedApplication is not null) OnSelectedApplicationChanged(SelectedApplication);
    }
    partial void OnCreateSystemRestorePointChanged(bool value) => SaveSetting(s => s.CreateSystemRestorePoint = value);
    partial void OnBackupRegistryChangesChanged(bool value) => SaveSetting(s => s.BackupRegistryChanges = value);
    partial void OnMoveToRecycleBinChanged(bool value) => SaveSetting(s => s.MoveToRecycleBin = value);
    partial void OnAutoScanLeftoversChanged(bool value) => SaveSetting(s => s.AutoScanLeftovers = value);
    partial void OnShowProtectedApplicationsChanged(bool value) => SaveSetting(s => s.ShowProtectedApplications = value, refreshList: true);
    partial void OnShowBrokenEntriesChanged(bool value) => SaveSetting(s => s.ShowOrphanedApplications = value, refreshList: true);
    partial void OnShowSystemComponentsChanged(bool value) => SaveSetting(s => s.ShowSystemComponents = value, refreshList: true);
    partial void OnShowUpdatesChanged(bool value) => SaveSetting(s => s.ShowUpdates = value, refreshList: true);
    partial void OnShowBlockedLeftoversChanged(bool value)
    {
        SaveSetting(s => s.ShowBlockedLeftovers = value);
        LeftoverView?.Refresh();
    }
    partial void OnQuietStallMinutesChanged(int value) => SaveSetting(s => s.QuietStallMinutes = Math.Clamp(value, 0, 240));
    partial void OnDryRunModeChanged(bool value) => SaveSetting(s => s.DryRunMode = value);

    partial void OnExcludedPathsTextChanged(string value)
    {
        var lines = (value ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SaveSetting(s =>
        {
            s.ExcludedRegistryPaths = lines.Where(RegistryPath.IsRegistryPath).ToList();
            s.ExcludedFilePaths = lines.Where(l => !RegistryPath.IsRegistryPath(l)).ToList();
        });
    }

    private void SaveSetting(Action<UninstallerSettings> apply, bool refreshList = false)
    {
        if (_loadingSettings) return;
        apply(_settings);
        try
        {
            var all = _settingsService.GetSettings();
            all.Uninstaller = _settings.Clone();
            // UI-only preferences: no other service reacts to them
            _settingsService.SaveSettings(all, notifyListeners: false);
        }
        catch (Exception ex)
        {
            _log.LogError("[Uninstaller] Could not save settings", ex);
        }
        if (refreshList) RefreshView();
    }
}
