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

/// <summary>
/// ViewModel for the main Uninstaller Pro page.
/// Handles application discovery, filtering, sorting, and batch uninstall orchestration.
/// </summary>
public sealed partial class UninstallerViewModel : ObservableObject
{
    private readonly IApplicationDiscoveryService _discoveryService;
    private readonly IBatchUninstallService _batchService;
    private readonly ILeftoverScannerService _leftoverScanner;
    private readonly IUninstallHistoryService _historyService;
    private readonly IRegistryBackupService _registryBackup;
    private readonly IFileCleanupService _fileCleanup;
    private readonly IProtectionService _protectionService;
    private readonly NotificationService _notification;
    private readonly LogService _log;

    private readonly ObservableCollection<InstalledApplicationViewModel> _allApplications = new();
    private ICollectionView _applicationsView = null!;
    private CancellationTokenSource? _discoveryCts;
    private UninstallerSettings _settings = new();

    // ── Observable Properties ─────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedFilter = "All";

    [ObservableProperty]
    private int _appCount;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private InstalledApplicationViewModel? _selectedApplication;

    [ObservableProperty]
    private bool _isProgressVisible;

    [ObservableProperty]
    private UninstallProgress _currentProgress = new();

    [ObservableProperty]
    private bool _showSettingsPanel;

    // Leftover review state
    [ObservableProperty]
    private ObservableCollection<LeftoverItemViewModel> _leftoverItems = new();

    [ObservableProperty]
    private bool _isLeftoverReviewVisible;

    [ObservableProperty]
    private string _leftoverApplicationName = string.Empty;

    [ObservableProperty]
    private UninstallSession? _lastSession;

    public ICollectionView ApplicationsView => _applicationsView;

    public string[] FilterOptions { get; } = ["All", "Win32", "MSI", "Store Apps", "WinGet", "Orphaned", "Protected"];

    public UninstallerSettings Settings => _settings;

    // Computed properties for XAML bindings
    public bool IsDetailPanelVisible        => SelectedApplication is not null;
    public bool IsSelectedProtected         => SelectedApplication?.IsProtected ?? false;
    public bool IsSelectedCountVisible      => SelectedCount > 0;
    public string SelectedLeftoverCount     => $"{LeftoverItems.Count(i => i.IsSelected)} items selected";
    public bool IsAllSelected
    {
        get => _allApplications.Where(a => a.CanUninstall).All(a => a.IsSelected);
        set
        {
            foreach (var a in _allApplications.Where(x => x.CanUninstall))
                a.IsSelected = value;
        }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public UninstallerViewModel(
        IApplicationDiscoveryService discoveryService,
        IBatchUninstallService batchService,
        ILeftoverScannerService leftoverScanner,
        IUninstallHistoryService historyService,
        IRegistryBackupService registryBackup,
        IFileCleanupService fileCleanup,
        IProtectionService protectionService,
        NotificationService notification,
        LogService log)
    {
        _discoveryService = discoveryService;
        _batchService     = batchService;
        _leftoverScanner  = leftoverScanner;
        _historyService   = historyService;
        _registryBackup   = registryBackup;
        _fileCleanup      = fileCleanup;
        _protectionService = protectionService;
        _notification     = notification;
        _log              = log;

        _applicationsView = CollectionViewSource.GetDefaultView(_allApplications);
        _applicationsView.Filter = FilterApplications;
        _applicationsView.SortDescriptions.Add(
            new SortDescription(nameof(InstalledApplicationViewModel.DisplayName),
                ListSortDirection.Ascending));
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _discoveryCts?.Cancel();
        _discoveryCts = new CancellationTokenSource();
        var ct = _discoveryCts.Token;

        IsLoading = true;
        StatusMessage = "Discovering applications...";

        void SafeClear()
        {
            foreach (var vm in _allApplications)
                vm.PropertyChanged -= OnApplicationSelectionChanged;
            _allApplications.Clear();
            SelectedCount = 0;
        }

        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(SafeClear);
        else
            SafeClear();

        try
        {
            var apps = await _discoveryService.DiscoverApplicationsAsync(ct);

            var viewModels = new List<InstalledApplicationViewModel>();
            foreach (var app in apps)
            {
                if (!_settings.ShowProtectedApplications && app.IsProtected) continue;
                if (!_settings.ShowOrphanedApplications  &&
                    app.ApplicationType == ApplicationType.Orphaned) continue;

                var vm = new InstalledApplicationViewModel(app);
                vm.PropertyChanged += OnApplicationSelectionChanged;
                viewModels.Add(vm);
            }

            void SafePopulate()
            {
                _allApplications.Clear();
                foreach (var vm in viewModels)
                    _allApplications.Add(vm);

                AppCount = _allApplications.Count;
                StatusMessage = $"{AppCount} applications found";
            }

            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                dispatcher.Invoke(SafePopulate);
            else
                SafePopulate();

            _log.LogInfo($"[Uninstaller] Discovery complete: {AppCount} apps.");
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

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var vm in _allApplications.Where(a => a.CanUninstall))
            vm.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var vm in _allApplications)
            vm.IsSelected = false;
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        var selected = _allApplications
            .Where(a => a.IsSelected && a.CanUninstall)
            .Select(a => a.Application)
            .ToList();

        if (selected.Count == 0)
        {
            _notification.Show("No applications selected for uninstall.");
            return;
        }

        // Check for protected selection
        foreach (var app in selected)
        {
            var reason = _protectionService.GetProtectionReason(app);
            if (reason is not null)
            {
                _notification.Show(reason);
                return;
            }
        }

        IsProgressVisible = true;
        IsLeftoverReviewVisible = false;
        StatusMessage = $"Uninstalling {selected.Count} application(s)...";

        var progressReporter = new Progress<UninstallProgress>(p =>
        {
            CurrentProgress = p;
            StatusMessage = p.StatusMessage;
        });

        _discoveryCts?.Cancel();
        _discoveryCts = new CancellationTokenSource();

        try
        {
            var session = await _batchService.ExecuteBatchAsync(
                selected, _settings, progressReporter, _discoveryCts.Token);

            LastSession = session;

            var successMsg = $"Uninstalled: {session.SuccessCount}, Failed: {session.FailedCount}";
            StatusMessage = successMsg;
            _notification.Show($"✓ {successMsg}");

            // Show leftover review if items were found
            if (session.LeftoverItems.Count > 0)
            {
                PrepareLeftoverReview(session);
            }

            // Refresh the application list
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Uninstall cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Uninstall failed.";
            _log.LogError("[Uninstaller] Batch uninstall failed", ex);
            _notification.Show("Uninstall encountered an error. Check Logs for details.");
        }
        finally
        {
            IsProgressVisible = false;
        }
    }

    [RelayCommand]
    private async Task ScanLeftoversForSelectedAsync()
    {
        if (SelectedApplication is null) return;

        var app = SelectedApplication.Application;
        StatusMessage = $"Scanning leftovers for {app.DisplayName}...";

        try
        {
            _discoveryCts?.Cancel();
            _discoveryCts = new CancellationTokenSource();

            var leftovers = await _leftoverScanner.ScanAsync(
                app, _settings.DefaultScanMode, _settings, _discoveryCts.Token);

            var fakeSession = new UninstallSession
            {
                ApplicationNames = [app.DisplayName],
                LeftoverItems = leftovers.ToList(),
            };
            PrepareLeftoverReview(fakeSession);

            StatusMessage = $"Found {leftovers.Count} potential leftover(s) for {app.DisplayName}.";
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
    }

    [RelayCommand]
    private async Task CleanSelectedLeftoversAsync()
    {
        var toDelete = LeftoverItems.Where(i => i.IsSelected && !i.IsProtected).ToList();
        if (toDelete.Count == 0)
        {
            _notification.Show("No leftover items selected for cleanup.");
            return;
        }

        int cleaned = 0;
        int failed  = 0;

        foreach (var item in toDelete)
        {
            try
            {
                switch (item.Source.ItemType)
                {
                    case LeftoverItemType.RegistryKey:
                    case LeftoverItemType.RegistryValue:
                        if (!_settings.DryRunMode)
                        {
                            // Backup first
                            await _registryBackup.BackupAsync(
                                Guid.NewGuid().ToString(),
                                item.Source.ApplicationId,
                                item.Source.ApplicationName,
                                item.Source.Path.Split('\\')[0],
                                string.Join("\\", item.Source.Path.Split('\\').Skip(1)));

                            DeleteRegistryKey(item.Source.Path);
                        }
                        cleaned++;
                        break;

                    case LeftoverItemType.File:
                        if (await _fileCleanup.DeleteFileAsync(item.Source.Path, _settings))
                            cleaned++;
                        else failed++;
                        break;

                    case LeftoverItemType.Folder:
                        if (await _fileCleanup.DeleteFolderAsync(item.Source.Path, _settings))
                            cleaned++;
                        else failed++;
                        break;

                    default:
                        cleaned++; // Count startup entries etc. as handled
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.LogError($"[Uninstaller] Failed to clean: {item.Source.Path}", ex);
                failed++;
            }
        }

        var msg = $"Cleaned {cleaned} item(s).";
        if (failed > 0) msg += $" {failed} item(s) failed.";
        _notification.Show(msg);
        StatusMessage = msg;

        IsLeftoverReviewVisible = false;
        LeftoverItems.Clear();
    }

    [RelayCommand]
    private void CloseLeftoverReview()
    {
        IsLeftoverReviewVisible = false;
        LeftoverItems.Clear();
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        ShowSettingsPanel = !ShowSettingsPanel;
    }

    [RelayCommand]
    private void OpenInstallLocation()
    {
        var path = SelectedApplication?.Application.InstallLocation;
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true,
                });
            }
            catch { }
        }
    }

    // ── Filter Logic ──────────────────────────────────────────────────────────

    partial void OnSelectedApplicationChanged(InstalledApplicationViewModel? value)
    {
        OnPropertyChanged(nameof(IsDetailPanelVisible));
        OnPropertyChanged(nameof(IsSelectedProtected));
    }

    [RelayCommand]
    private void SelectFilter(string filter)
    {
        SelectedFilter = filter;
    }

    partial void OnSearchTextChanged(string value)
        => _applicationsView.Refresh();

    partial void OnSelectedFilterChanged(string value)
        => _applicationsView.Refresh();


    private bool FilterApplications(object obj)
    {
        if (obj is not InstalledApplicationViewModel vm) return false;

        // Text filter
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            bool matchesText = vm.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                               vm.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase);
            if (!matchesText) return false;
        }

        // Category filter
        return SelectedFilter switch
        {
            "Win32"      => vm.Application.ApplicationType == ApplicationType.Win32,
            "MSI"        => vm.Application.ApplicationType == ApplicationType.MSI,
            "Store Apps" => vm.Application.ApplicationType is ApplicationType.AppX or ApplicationType.MSIX,
            "WinGet"     => vm.Application.ApplicationType == ApplicationType.WinGet,
            "Orphaned"   => vm.Application.ApplicationType == ApplicationType.Orphaned,
            "Protected"  => vm.Application.IsProtected,
            _            => true, // All
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void OnApplicationSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledApplicationViewModel.IsSelected))
            SelectedCount = _allApplications.Count(a => a.IsSelected);
    }

    private void PrepareLeftoverReview(UninstallSession session)
    {
        void Populate()
        {
            LeftoverItems.Clear();
            foreach (var item in session.LeftoverItems)
                LeftoverItems.Add(new LeftoverItemViewModel(item));

            LeftoverApplicationName = string.Join(", ", session.ApplicationNames.Take(3));
            IsLeftoverReviewVisible = true;
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.Invoke(Populate);
        else
            Populate();
    }

    private void DeleteRegistryKey(string fullPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return;
            if (_protectionService.IsPathProtected(fullPath))
            {
                _log.LogWarning($"[Uninstaller] Prevented deletion of protected registry path: {fullPath}");
                return;
            }

            Microsoft.Win32.RegistryKey? baseKey = null;
            string subPath;

            if (fullPath.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
            {
                baseKey = Microsoft.Win32.Registry.CurrentUser;
                subPath = fullPath[5..];
            }
            else if (fullPath.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
            {
                baseKey = Microsoft.Win32.Registry.LocalMachine;
                subPath = fullPath[5..];
            }
            else return;

            // Strict safety: Never delete root or shallow keys (must have at least 2 path segments, e.g. Software\AppName)
            var segments = subPath.Trim('\\', '/').Split('\\');
            if (segments.Length < 2)
            {
                _log.LogWarning($"[Uninstaller] Refusing to delete shallow registry path: {fullPath}");
                return;
            }

            baseKey?.DeleteSubKeyTree(subPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            _log.LogError($"[Uninstaller] Failed to delete registry key: {fullPath}", ex);
        }
    }
}

/// <summary>ViewModel wrapping a LeftoverItem with selection state for the review UI.</summary>
public sealed partial class LeftoverItemViewModel : ObservableObject
{
    public LeftoverItem Source { get; }

    [ObservableProperty]
    private bool _isSelected;

    public LeftoverItemViewModel(LeftoverItem source)
    {
        Source = source;
        _isSelected = source.IsSelected || (source.ConfidenceScore >= 80 && !source.IsProtected && !source.IsSharedComponent);
    }

    public string Path          => Source.Path;
    public string TypeLabel     => Source.ItemType.ToString();
    public int ConfidenceScore  => Source.ConfidenceScore;
    public string ConfidenceLabel => Source.ConfidenceLabel;
    public string Reason        => Source.ConfidenceReason;
    public string DisplaySize   => Source.DisplaySize;
    public bool IsProtected     => Source.IsProtected;
    public bool IsNotProtected  => !Source.IsProtected;
    public bool IsShared        => Source.IsSharedComponent;

}
