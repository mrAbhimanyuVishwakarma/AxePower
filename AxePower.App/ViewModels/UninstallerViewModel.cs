using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.App.Services;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.App.ViewModels;

/// <summary>
/// ViewModel for the main Uninstaller Pro page: discovery, filtering, the confirmed and verified
/// uninstall batch, and its settings. The leftover review and history live in
/// UninstallerViewModel.Leftovers.cs.
/// </summary>
public sealed partial class UninstallerViewModel : ObservableObject
{
    private readonly IApplicationDiscoveryService _discoveryService;
    private readonly IBatchUninstallService _batchService;
    private readonly ILeftoverScannerService _leftoverScanner;
    private readonly ILeftoverCleanupService _leftoverCleanup;
    private readonly IUninstallHistoryService _historyService;
    private readonly IRegistryBackupService _registryBackup;
    private readonly IProtectionService _protectionService;
    private readonly IInstallStateVerifier _verifier;
    private readonly IFileSystemProbe _fs;
    private readonly BackupManifestStore _manifests;
    private readonly SettingsService _settingsService;
    private readonly ElevationService _elevation;
    private readonly IUninstallerPrompts _prompts;
    private readonly NotificationService _notification;
    private readonly LogService _log;

    private readonly ObservableCollection<InstalledApplicationViewModel> _allApplications = new();
    private readonly ICollectionView _applicationsView;
    private CancellationTokenSource? _discoveryCts;
    private CancellationTokenSource? _batchCts;
    private CancellationTokenSource? _scanCts;
    private UninstallBatchControl? _batchControl;
    private UninstallerSettings _settings;
    private bool _loadingSettings;

    // ── Observable Properties ─────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallSelectedCommand), nameof(UninstallThisCommand),
        nameof(ScanLeftoversForSelectedCommand), nameof(CleanSelectedLeftoversCommand))]
    private bool _isLoading;

    /// <summary>A batch, scan, cleanup or restore is running; starting another is disabled.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(UninstallSelectedCommand), nameof(UninstallThisCommand),
        nameof(ScanLeftoversForSelectedCommand), nameof(CleanSelectedLeftoversCommand), nameof(RestoreSessionCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isCancellable;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedFilter = "All";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDesktopSection))]
    [NotifyPropertyChangedFor(nameof(IsWindowsAppsSection))]
    private string _selectedAppSection = "Desktop";

    [ObservableProperty]
    private int _appCount;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallThisCommand), nameof(ScanLeftoversForSelectedCommand))]
    private InstalledApplicationViewModel? _selectedApplication;

    [ObservableProperty]
    private bool _isProgressVisible;

    [ObservableProperty]
    private UninstallProgress _currentProgress = new();

    [ObservableProperty]
    private bool _showSettingsPanel;

    [ObservableProperty]
    private bool _showHistoryPanel;

    [ObservableProperty]
    private UninstallSession? _lastSession;

    /// <summary>Per-app outcomes of the last batch.</summary>
    public ObservableCollection<UninstallResult> BatchResults { get; } = new();

    [ObservableProperty]
    private bool _isResultsVisible;

    public ICollectionView ApplicationsView => _applicationsView;

    public string[] FilterOptions { get; } = ["All", "Win32", "MSI", "Broken", "Protected"];

    public bool IsDesktopSection => SelectedAppSection == "Desktop";
    public bool IsWindowsAppsSection => SelectedAppSection == "WindowsApps";

    public UninstallerSettings Settings => _settings;

    public bool IsElevated => _elevation.IsElevated;

    // Computed properties for XAML bindings
    public bool IsDetailPanelVisible        => SelectedApplication is not null && !ShowSettingsPanel && !ShowHistoryPanel;
    public bool IsSelectedProtected         => SelectedApplication?.IsProtected ?? false;
    public string SelectedProtectionReason  => SelectedApplication is null
        ? string.Empty
        : _protectionService.GetProtectionReason(SelectedApplication.Application) ?? string.Empty;
    public bool IsSelectedCountVisible      => SelectedCount > 0;
    public bool IsAllSelected
    {
        get => _allApplications.Where(a => a.CanUninstall).All(a => a.IsSelected);
        set
        {
            foreach (var a in _allApplications.Where(x => x.CanUninstall && _applicationsView.Filter(x)))
                a.IsSelected = value;
        }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public UninstallerViewModel(
        IApplicationDiscoveryService discoveryService,
        IBatchUninstallService batchService,
        ILeftoverScannerService leftoverScanner,
        ILeftoverCleanupService leftoverCleanup,
        IUninstallHistoryService historyService,
        IRegistryBackupService registryBackup,
        IProtectionService protectionService,
        IInstallStateVerifier verifier,
        IFileSystemProbe fs,
        BackupManifestStore manifests,
        SettingsService settingsService,
        ElevationService elevation,
        IUninstallerPrompts prompts,
        NotificationService notification,
        LogService log)
    {
        _discoveryService = discoveryService;
        _batchService     = batchService;
        _leftoverScanner  = leftoverScanner;
        _leftoverCleanup  = leftoverCleanup;
        _historyService   = historyService;
        _registryBackup   = registryBackup;
        _protectionService = protectionService;
        _verifier         = verifier;
        _fs               = fs;
        _manifests        = manifests;
        _settingsService  = settingsService;
        _elevation        = elevation;
        _prompts          = prompts;
        _notification     = notification;
        _log              = log;

        _settings = (settingsService.GetSettings().Uninstaller ?? new UninstallerSettings()).Clone();
        LoadSettingProperties();

        _applicationsView = CollectionViewSource.GetDefaultView(_allApplications);
        _applicationsView.Filter = FilterApplications;
        _applicationsView.SortDescriptions.Add(
            new SortDescription(nameof(InstalledApplicationViewModel.DisplayName),
                ListSortDirection.Ascending));
    }

    private bool CanStartWork() => !IsBusy && !IsLoading;

    // ── Discovery ─────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        _discoveryCts?.Cancel();
        _discoveryCts = new CancellationTokenSource();
        var ct = _discoveryCts.Token;

        IsLoading = true;
        StatusMessage = "Discovering applications...";

        try
        {
            var apps = await _discoveryService.DiscoverApplicationsAsync(ct);
            if (ct.IsCancellationRequested) return;

            var selectedIds = _allApplications.Where(a => a.IsSelected).Select(a => a.Application.Id).ToHashSet();
            var viewModels = apps.Select(app =>
            {
                var vm = new InstalledApplicationViewModel(app) { IsSelected = selectedIds.Contains(app.Id) };
                vm.PropertyChanged += OnApplicationSelectionChanged;
                return vm;
            }).ToList();

            OnUiThread(() =>
            {
                foreach (var vm in _allApplications)
                    vm.PropertyChanged -= OnApplicationSelectionChanged;
                _allApplications.Clear();
                foreach (var vm in viewModels)
                    _allApplications.Add(vm);

                UpdateCounts();
                StatusMessage = $"{AppCount} applications found";
            });

            _log.LogInfo($"[Uninstaller] Discovery complete: {apps.Count} entries, {AppCount} shown.");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Discovery cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to discover applications.";
            _log.LogError("[Uninstaller] Discovery failed", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Refresh is independent of a running batch, but not allowed during one (it would change
    // the list under the queue).
    private bool CanRefresh() => !IsBusy;

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var vm in _allApplications.Where(a => a.CanUninstall && _applicationsView.Filter(a)))
            vm.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var vm in _allApplications)
            vm.IsSelected = false;
    }

    // ── Uninstall ─────────────────────────────────────────────────────────────

    /// <summary>Uninstalls the checked rows.</summary>
    [RelayCommand(CanExecute = nameof(CanStartWork))]
    private async Task UninstallSelectedAsync()
    {
        var selected = _allApplications
            .Where(a => a.IsSelected && a.Application.CanUninstall)
            .Select(a => a.Application)
            .ToList();

        if (selected.Count == 0)
        {
            _notification.Show("No applications selected for uninstall.");
            return;
        }

        await RunBatchAsync(selected);
    }

    /// <summary>The details panel's button: acts on the app being viewed, whatever is checked.</summary>
    [RelayCommand(CanExecute = nameof(CanUninstallThis))]
    private async Task UninstallThisAsync()
    {
        if (SelectedApplication is null) return;
        await RunBatchAsync([SelectedApplication.Application]);
    }

    private bool CanUninstallThis() => CanStartWork() && SelectedApplication is { CanUninstall: true };

    /// <summary>From the results list: the app is still registered but its uninstaller is gone.</summary>
    [RelayCommand(CanExecute = nameof(CanStartWork))]
    private async Task RemoveBrokenEntryAsync(UninstallResult? result)
    {
        if (result is null) return;
        await RefreshAsync();
        var broken = _allApplications.FirstOrDefault(a => a.Application.Id == result.ApplicationId && a.IsBroken);
        if (broken is null)
        {
            _notification.Show($"{result.ApplicationName} is no longer listed as a broken entry.");
            return;
        }
        await RunBatchAsync([broken.Application]);
    }

    private async Task RunBatchAsync(IReadOnlyList<InstalledApplication> requested)
    {
        // Protected apps are skipped one by one instead of aborting the whole batch
        var skipped = requested
            .Select(a => (App: a, Reason: _protectionService.GetProtectionReason(a)))
            .Where(x => x.Reason is not null)
            .Select(x => (x.App, x.Reason!))
            .ToList();
        var runnable = requested.Where(a => skipped.All(s => !ReferenceEquals(s.App, a))).ToList();

        if (runnable.Count == 0)
        {
            _notification.Show(skipped.Count == 1 ? skipped[0].Item2 : "All selected applications are protected.");
            return;
        }

        var settings = _settings.Clone();
        var plans = runnable.Select(a => (a, UninstallCommandBuilder.Build(a, settings.PreferQuietUninstall, _fs))).ToList();
        if (!_prompts.ConfirmBatch(plans, skipped, settings.CreateSystemRestorePoint, settings.DryRunMode))
            return;

        IsBusy = true;
        IsCancellable = true;
        IsProgressVisible = true;
        IsResultsVisible = false;
        IsLeftoverReviewVisible = false;
        StatusMessage = $"Uninstalling {runnable.Count} application(s)...";

        _batchControl = new UninstallBatchControl();
        _batchCts = new CancellationTokenSource();
        var progressReporter = new Progress<UninstallProgress>(p =>
        {
            CurrentProgress = p;
            StatusMessage = p.StatusMessage;
        });

        try
        {
            var session = await _batchService.ExecuteBatchAsync(
                runnable, settings, progressReporter, new PromptInteraction(_prompts), _batchControl, _batchCts.Token);

            LastSession = session;
            BatchResults.Clear();
            foreach (var result in session.Results) BatchResults.Add(result);
            IsResultsVisible = BatchResults.Count > 0;

            StatusMessage = session.OutcomeSummary;
            _notification.Show(session.WasCancelled ? $"Stopped. {session.OutcomeSummary}" : session.OutcomeSummary);

            if (session.LeftoverItems.Count > 0)
                PrepareLeftoverReview(session);

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "Uninstall failed.";
            _log.LogError("[Uninstaller] Batch uninstall failed", ex);
            _notification.Show("Uninstall encountered an error. Check Logs for details.");
        }
        finally
        {
            _batchControl = null;
            _batchCts?.Dispose();
            _batchCts = null;
            IsCancellable = false;
            IsProgressVisible = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Stops the queue after the current app. If an uninstaller is running, asks whether to stop
    /// waiting for it (and, for silent uninstalls only, whether to stop it). Never kills silently.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsCancellable))]
    private void Cancel()
    {
        _scanCts?.Cancel();

        var control = _batchControl;
        if (control is null) return;

        control.RequestStop();
        StatusMessage = "Stopping after the current app...";

        if (!control.IsUninstallerRunning) return;
        switch (_prompts.AskStopWaiting(control.CurrentApplicationName, control.IsCurrentQuiet))
        {
            case StopWaitingChoice.StopWaiting:
                control.AbandonCurrentWait();
                break;
            case StopWaitingChoice.StopUninstaller:
                control.StopCurrentUninstaller();
                break;
        }
    }

    [RelayCommand]
    private void CloseResults() => IsResultsVisible = false;

    [RelayCommand]
    private void CloseDetails() => SelectedApplication = null;

    [RelayCommand]
    private void OpenLog(UninstallResult? result)
    {
        if (result is null || string.IsNullOrEmpty(result.LogPath) || !File.Exists(result.LogPath))
        {
            _notification.Show("No log was written for this uninstall.");
            return;
        }
        ShellOpen(result.LogPath);
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        ShowSettingsPanel = !ShowSettingsPanel;
        if (ShowSettingsPanel) ShowHistoryPanel = false;
    }

    [RelayCommand]
    private void OpenInstallLocation()
    {
        var path = SelectedApplication?.Application.InstallLocation;
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            ShellOpen(path);
    }

    // ── Filter Logic ──────────────────────────────────────────────────────────

    partial void OnSelectedApplicationChanged(InstalledApplicationViewModel? value)
    {
        if (value is not null)
            value.PlanDescription = UninstallCommandBuilder.Build(value.Application, _settings.PreferQuietUninstall, _fs).Description;
        OnPropertyChanged(nameof(IsDetailPanelVisible));
        OnPropertyChanged(nameof(IsSelectedProtected));
        OnPropertyChanged(nameof(SelectedProtectionReason));
    }

    partial void OnShowSettingsPanelChanged(bool value) => OnPropertyChanged(nameof(IsDetailPanelVisible));
    partial void OnShowHistoryPanelChanged(bool value) => OnPropertyChanged(nameof(IsDetailPanelVisible));

    [RelayCommand]
    private void SelectFilter(string filter)
    {
        SelectedFilter = filter;
    }

    [RelayCommand]
    private void SelectAppSection(string? section)
    {
        SelectedApplication = null;
        SelectedAppSection = string.Equals(section, "WindowsApps", StringComparison.OrdinalIgnoreCase)
            ? "WindowsApps"
            : "Desktop";
        SelectedFilter = "All";
    }

    partial void OnSearchTextChanged(string value) => RefreshView();

    partial void OnSelectedFilterChanged(string value) => RefreshView();

    partial void OnSelectedAppSectionChanged(string value) => RefreshView();

    private void RefreshView()
    {
        _applicationsView.Refresh();
        UpdateCounts();
    }

    private bool FilterApplications(object obj)
    {
        if (obj is not InstalledApplicationViewModel vm) return false;
        var app = vm.Application;

        bool isWindowsApp = vm.ApplicationTypeLabel == "AppX";
        if (IsWindowsAppsSection != isWindowsApp) return false;

        // Visibility settings (the Protected chip always shows protected apps)
        if (app.IsProtected && !_settings.ShowProtectedApplications && SelectedFilter != "Protected") return false;
        if (vm.IsBroken && !_settings.ShowOrphanedApplications && SelectedFilter != "Broken") return false;
        if (app.IsSystemComponent && !_settings.ShowSystemComponents) return false;
        if (app.IsUpdate && !_settings.ShowUpdates) return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            bool matchesText = vm.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                               vm.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase);
            if (!matchesText) return false;
        }

        return SelectedFilter switch
        {
            "Win32"      => vm.ApplicationTypeLabel == "Win32",
            "MSI"        => vm.ApplicationTypeLabel == "MSI",
            "Broken"     => vm.IsBroken,
            "Protected"  => app.IsProtected,
            _            => true, // All
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void OnApplicationSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledApplicationViewModel.IsSelected))
        {
            SelectedCount = _allApplications.Count(a => a.IsSelected);
            OnPropertyChanged(nameof(IsSelectedCountVisible));
        }
    }

    private void UpdateCounts()
    {
        AppCount = _allApplications.Count(a => _applicationsView.Filter(a));
        SelectedCount = _allApplications.Count(a => a.IsSelected);
        OnPropertyChanged(nameof(IsSelectedCountVisible));
    }

    private static void OnUiThread(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(action);
        else
            action();
    }

    private void ShellOpen(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.LogError($"[Uninstaller] Could not open {path}", ex);
        }
    }

    /// <summary>Answers the batch's questions with the page's prompts, on the UI thread.</summary>
    private sealed class PromptInteraction : IUninstallInteraction
    {
        private readonly IUninstallerPrompts _prompts;

        public PromptInteraction(IUninstallerPrompts prompts) => _prompts = prompts;

        public Task<RunningAppDecision> ResolveRunningApplicationAsync(InstalledApplication app,
            IReadOnlyList<RunningProcessInfo> processes, bool closeAttemptFailed, CancellationToken cancellationToken) =>
            OnUi(() => _prompts.AskRunningApp(app, processes, closeAttemptFailed));

        public Task<bool> ShouldStopStalledUninstallerAsync(InstalledApplication app, TimeSpan elapsed,
            CancellationToken cancellationToken) =>
            OnUi(() => _prompts.AskStopStalledUninstaller(app, elapsed));

        private static Task<T> OnUi<T>(Func<T> ask) =>
            System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess()
                ? d.InvokeAsync(ask).Task
                : Task.FromResult(ask());
    }
}
