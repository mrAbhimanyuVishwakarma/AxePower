using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.Core.Services.SoftwareHub;
using AxePower.App.Services;
using AxePower.Core.Services.Operations;
using AxePower.Core.Services.Processes;

namespace AxePower.App.ViewModels;

public partial class PackageCardViewModel : ObservableObject
{
    private readonly IWinGetHubService _hubService;
    private readonly SoftwareHubViewModel _parent;
    private CancellationTokenSource? _operation;

    public SoftwarePackageItem Item { get; }

    public string Id => Item.Id;
    public string WinGetId => Item.WinGetId;
    public string Name => Item.Name;
    public string Description => Item.Description;
    public PackageCategory Category => Item.Category;
    public string CategoryDisplayName => Item.CategoryDisplayName;
    public string Glyph => Item.Glyph;
    public bool IsWebLinkOnly => Item.IsWebLinkOnly;
    public string WebUrl => Item.WebUrl;
    public string LaunchProtocolOrExe => Item.LaunchProtocolOrExe;
    public bool HasCustomInstaller => Item.HasCustomInstaller;
    public string LocalInstallerPath => Item.LocalInstallerPath;
    public string CustomWebUrl => !string.IsNullOrEmpty(Item.CustomWebUrl) ? Item.CustomWebUrl : Item.WebUrl;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        _parent.NotifySelectionChanged();
    }

    partial void OnIsBusyChanged(bool value) => _parent.NotifyPackageOperationStateChanged();

    private bool CanStartOperation => !IsBusy && _parent.CanStartToolPackageOperation;
    private PortableToolSpec? PortableTool => PortableTools.Find(Id);
    private bool IsPortableArchive => PortableTool?.ZipRelease != null;

    public bool IsNotInstalled => !IsInstalled && !IsWebLinkOnly && !HasCustomInstaller;
    public bool CanOpen => IsInstalled && !IsWebLinkOnly && !ShowCustomInstallerButtons;
    public bool CanUpdate => CanOpen && !IsPortableArchive;
    public bool CanUninstall => CanOpen && !IsPortableArchive;
    public bool CanSelectForBatch => !IsWebLinkOnly && !IsPortableArchive;
    public bool ShowCustomInstallerButtons => HasCustomInstaller;

    public string InstallButtonText => PortableTool != null ? "Download & Run" : "Install";
    public string InstallButtonTooltip => PortableTool != null ? "Download the portable release from GitHub and run it" : "Install this software with WinGet";
    public string OpenButtonText => PortableTool != null ? "Run" : "Open";
    public string UpdateButtonTooltip => PortableTool != null ? "Update to the newest portable release from GitHub" : "Update this software to the latest release with WinGet";

    public PackageCardViewModel(SoftwarePackageItem item, IWinGetHubService hubService, SoftwareHubViewModel parent)
    {
        Item = item;
        _hubService = hubService;
        _parent = parent;
        _isInstalled = item.IsInstalled;
    }

    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotInstalled));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(ShowCustomInstallerButtons));
    }

    [RelayCommand]
    public void OpenApp()
    {
        if (string.IsNullOrEmpty(LaunchProtocolOrExe)) return;
        var result = _hubService.OpenInstalled(Item);
        _parent.Notify(result.Started ? $"Opening {Name}..." : $"Could not open {Name}: {result.Message}");
    }

    [RelayCommand]
    public void OpenLinkWithWarning()
    {
        // No default site: an entry without its own address has nothing to open
        if (string.IsNullOrWhiteSpace(WebUrl)) return;
        string targetUrl = WebUrl;
        _parent.ShowWebLinkWarning(
            $"Open {Name}",
            $"⚠️ Warning: You are about to visit {Name} ({targetUrl}) in your web browser.\n\nAlways ensure you remain on the authentic official domain ({targetUrl}) to avoid clone or phishing sites.",
            targetUrl
        );
    }

    [RelayCommand]
    public void LaunchLocalInstaller()
    {
        if (!CanStartOperation) return;
        var result = _hubService.LaunchLocalInstaller(Item);
        if (result.Succeeded)
        {
            _parent.Notify($"🚀 Launching {Name} Installer...");
            _parent.AppendLog($"\n>>> Launched {Name} Installer.");
        }
        else if (result.OfferedWebUrl != null)
        {
            _parent.Notify($"Local installer file not found. Opening {Name} website...");
            OpenCustomWebUrl();
        }
        else
        {
            _parent.Notify($"Error launching installer: {result.Message}");
            _parent.AppendLog($"\n[ERROR] Could not start installer: {result.Message}");
        }
    }

    [RelayCommand]
    public void OpenCustomWebUrl()
    {
        string targetUrl = !string.IsNullOrEmpty(CustomWebUrl) ? CustomWebUrl : "https://ninite.com/";
        _parent.ShowWebLinkWarning(
            $"Open {Name}",
            $"You are about to visit {Name} ({targetUrl}) in your web browser to customize and download apps.",
            targetUrl
        );
    }

    /// <summary>
    /// Stops the card's running install, update or uninstall, ending its process tree. While the
    /// card is part of a batch, this stops the batch.
    /// </summary>
    [RelayCommand]
    public void Cancel()
    {
        if (_operation != null) _operation.Cancel();
        else _parent.CancelBatch();
    }

    /// <summary>Runs one action with a fresh cancellation source; the card is busy until it ends.</summary>
    private async Task<PackageOperationResult?> RunOperationAsync(PackageAction action)
    {
        if (!CanStartOperation) return null;

        IsBusy = true;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        try
        {
            var progress = new Progress<string>(_parent.AppendLog);
            var result = await _hubService.RunAsync(action, Id, progress, operation.Token);
            if (result.Outcome == PackageOutcome.Cancelled)
            {
                _parent.Notify($"Cancelled {Name}.");
                _parent.AppendLog($"\n[Cancelled] {Name}.");
            }
            return result;
        }
        finally
        {
            _operation = null;
            // Release card busy lock quickly so user is never frozen
            if (action != PackageAction.Uninstall) await Task.Delay(500);
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task InstallAsync()
    {
        if (!CanStartOperation) return;
        _parent.Notify($"🚀 1-Click Install started for {Name}!");
        _parent.AppendLog($"\n>>> 1-Click Install: {Name} ({WinGetId})...");

        var result = await RunOperationAsync(PackageAction.Install);
        if (result is null || result.Outcome == PackageOutcome.Cancelled) return;
        if (result.Succeeded)
        {
            IsInstalled = true;
            _parent.Notify($"✓ {Name} is now installed and ready!");
            _parent.AppendLog($"\n[Success] {Name} is now installed.");
        }
        else if (IsPortableArchive)
        {
            _parent.Notify($"Could not prepare {Name}. Check the download log for details.");
        }
        else
        {
            _parent.Notify(Id == "camstudio"
                ? "CamStudio isn't on WinGet. Download it from the page that just opened, then Refresh."
                : $"{Name} was not installed: {result.Message ?? AxePower.Core.Services.Outcomes.UserMessages.Short(result.Failure)}. Opening its website...");
        }
    }

    [RelayCommand]
    public async Task UpdateAsync()
    {
        if (!CanStartOperation) return;
        _parent.Notify($"🚀 Update started for {Name}...");
        _parent.AppendLog($"\n>>> [Update] Updating {Name} ({WinGetId})...");

        var result = await RunOperationAsync(PackageAction.Upgrade);
        if (result is null || result.Outcome == PackageOutcome.Cancelled) return;
        _parent.Notify(result.Succeeded ? $"✓ {Name} updated successfully." : $"Could not update {Name}. Check logs.");
    }

    [RelayCommand]
    public async Task UninstallAsync()
    {
        if (!CanStartOperation) return;
        _parent.AppendLog($"\n>>> Uninstalling {Name} ({WinGetId})...");

        var result = await RunOperationAsync(PackageAction.Uninstall);
        if (result is null || result.Outcome == PackageOutcome.Cancelled) return;
        if (result.Succeeded)
        {
            IsInstalled = false;
            _parent.Notify($"✓ Uninstalled {Name}.");
        }
        else
        {
            _parent.Notify($"Uninstall of {Name} finished. Check logs.");
        }
    }

    public void Sync()
    {
        IsInstalled = Item.IsInstalled;
    }
}

public partial class SoftwareHubViewModel : ObservableObject
{
    private readonly IConfirmationPrompts _confirm;
    private readonly IWinGetHubService _hubService;
    private readonly IWindowsUpdateShieldService _updateShieldService;
    private readonly FirmwareUpdateService _firmwareUpdateService;
    private readonly GraphicsDriverService _graphicsDriverService;
    private readonly NotificationService _notificationService;
    private readonly LogService _logService;
    private readonly TerminalService _terminalService;
    private readonly RestorePointService _systemRestoreService;

    [ObservableProperty]
    private string _activeTab = "Updaters"; // "Updaters", "Apps", "Updates", "Console"

    [ObservableProperty]
    private string _selectedCategory = "All";

    [ObservableProperty]
    private bool _isCategoryListCollapsed = true;

    [ObservableProperty]
    private string _searchText = string.Empty;

    // BIOS & Firmware Properties
    [ObservableProperty]
    private FirmwareInfo _firmwareInfo = new();

    [ObservableProperty]
    private bool _isCheckingFirmware;

    [ObservableProperty]
    private string _firmwareCheckStatus = "Ready to check for latest BIOS & firmware updates.";

    // Web Link Warning Modal
    [ObservableProperty]
    private bool _isWarningModalOpen;

    [ObservableProperty]
    private string _warningModalTitle = string.Empty;

    [ObservableProperty]
    private string _warningModalMessage = string.Empty;

    [ObservableProperty]
    private string _pendingWebUrl = string.Empty;

    public ObservableCollection<PackageCardViewModel> AllPackages { get; } = new();
    public ObservableCollection<PackageCardViewModel> FilteredPackages { get; } = new();

    // Graphics driver centre — only the vendors actually present on this machine
    public ObservableCollection<GraphicsAdapterInfo> GraphicsAdapters { get; } = new();

    [ObservableProperty]
    private bool _hasGraphicsAdapters;

    [ObservableProperty]
    private bool _isInstallingGraphicsDriver;

    [ObservableProperty]
    private bool _isSecurityOnlyUpdates;

    [ObservableProperty]
    private bool _isNoForcedReboot;

    public bool IsSecurityOnlyActive => IsSecurityOnlyUpdates;
    public bool IsNoForcedRebootActive => IsNoForcedReboot;

    partial void OnIsSecurityOnlyUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSecurityOnlyActive));
    }

    partial void OnIsNoForcedRebootChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNoForcedRebootActive));
    }

    [ObservableProperty]
    private string _consoleOutput = "AxePower Software Hub initialized.\nReady to deploy packages & system driver upgrades.\n";

    public string LiveLogs
    {
        get => ConsoleOutput;
        set => ConsoleOutput = value;
    }

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _isCreatingRestorePoint;

    [ObservableProperty]
    private bool _isChangingUpdatePolicy;

    [ObservableProperty]
    private bool _isResettingUpdateCache;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _hasSelectedPackages;

    [ObservableProperty]
    private bool _isBatchInstalling;

    [ObservableProperty]
    private string _batchInstallStatus = string.Empty;

    private CancellationTokenSource? _batch;

    public SoftwareHubViewModel(
        IWinGetHubService hubService,
        IWindowsUpdateShieldService updateShieldService,
        DriverUpdateService driverUpdateService,
        FirmwareUpdateService firmwareUpdateService,
        NotificationService notificationService,
        LogService logService,
        GraphicsDriverService graphicsDriverService,
        TerminalService terminalService,
        RestorePointService systemRestoreService,
        IConfirmationPrompts? confirm = null,
        IWindowsUpdateCatalogService? windowsUpdates = null,
        IWinGetUpdateService? appUpdates = null,
        IUserShellLauncher? updateHandoff = null)
    {
        _confirm = confirm ?? new ConfirmationPrompts();
        _hubService = hubService;
        _updateShieldService = updateShieldService;
        _firmwareUpdateService = firmwareUpdateService;
        _notificationService = notificationService;
        _logService = logService;
        _graphicsDriverService = graphicsDriverService;
        _terminalService = terminalService;
        _systemRestoreService = systemRestoreService;
        _windowsUpdates = windowsUpdates;
        _appUpdates = appUpdates;
        _updateHandoff = updateHandoff ?? new UserShellLauncher(logService);

        LoadPackages();
        LoadShieldSettings();
        LoadGraphicsAdapters();

        _ = RefreshStatusesAsync();
        _ = LoadFirmwareInfoAsync();
    }

    private void LoadGraphicsAdapters()
    {
        try
        {
            GraphicsAdapters.Clear();
            foreach (var adapter in _graphicsDriverService.DetectAdapters())
            {
                GraphicsAdapters.Add(adapter);
            }
            HasGraphicsAdapters = GraphicsAdapters.Count > 0;
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to detect graphics adapters.", ex);
        }
    }

    [RelayCommand]
    public Task UpdateGraphicsDriverAsync(GraphicsAdapterInfo? adapter) =>
        OpenGraphicsAppAsync(adapter, controlPanel: false);

    [RelayCommand]
    public Task OpenGraphicsCompanionAppAsync(GraphicsAdapterInfo? adapter) =>
        adapter?.IsNvidia == true ? OpenGraphicsAppAsync(adapter, controlPanel: true) : Task.CompletedTask;

    private async Task OpenGraphicsAppAsync(GraphicsAdapterInfo? adapter, bool controlPanel)
    {
        if (adapter == null || !CanCheckForUpdates) return;
        IsInstallingGraphicsDriver = true;
        try
        {
            var name = controlPanel ? adapter.CompanionAppName : adapter.ToolName;
            AppendLog($"\n>>> [Graphics Drivers] {adapter.Name}: {name}");
            var progress = new Progress<string>(AppendLog);
            var (_, message) = controlPanel
                ? await _graphicsDriverService.OpenControlPanelAsync(adapter, progress)
                : await _graphicsDriverService.InstallOrUpdateAsync(adapter, progress);
            Notify(message);
            AppendLog($"[Graphics Drivers] {message}");
        }
        catch (Exception ex)
        {
            NotifyFailure("Open graphics software", ex);
            AppendLog($"[Graphics Drivers Error] {AxePower.Core.Services.Outcomes.OutcomeClassifier.Detail(ex)}");
        }
        finally
        {
            IsInstallingGraphicsDriver = false;
        }
    }

    [ObservableProperty]
    private bool _isNvidiaPickerOpen;

    [ObservableProperty]
    private GraphicsAdapterInfo? _selectedNvidiaAdapter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallSelectedNvidiaDriver))]
    private NvidiaDriverRelease? _selectedNvidiaDriver;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallSelectedNvidiaDriver))]
    private bool _isDownloadingNvidiaDriver;

    [ObservableProperty]
    private string _nvidiaPickerStatus = string.Empty;

    public ObservableCollection<NvidiaDriverRelease> NvidiaDrivers { get; } = new();
    public bool CanInstallSelectedNvidiaDriver => SelectedNvidiaDriver != null && !IsDownloadingNvidiaDriver;

    [RelayCommand]
    public async Task OpenNvidiaPickerAsync(GraphicsAdapterInfo? adapter, CancellationToken ct)
    {
        if (adapter?.IsNvidia != true || !CanCheckForUpdates) return;

        SelectedNvidiaAdapter = adapter;
        SelectedNvidiaDriver = null;
        NvidiaDrivers.Clear();
        NvidiaPickerStatus = "Checking NVIDIA for compatible drivers…";
        IsNvidiaPickerOpen = true;
        try
        {
            var releases = await _graphicsDriverService.GetNvidiaDriversAsync(adapter, ct);
            if (ct.IsCancellationRequested || !IsNvidiaPickerOpen) return;
            foreach (var release in releases) NvidiaDrivers.Add(release);
            SelectedNvidiaDriver = NvidiaDrivers.FirstOrDefault();
            NvidiaPickerStatus = NvidiaDrivers.Count == 0
                ? "No matching releases were found. Use Nvidia Drivers to search the official catalog."
                : "Newest releases first.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logService.LogWarning("Could not load the NVIDIA driver list.", ex);
            NvidiaPickerStatus = "The live driver list is unavailable. Use Nvidia Drivers to search on NVIDIA’s site.";
        }
    }

    [RelayCommand]
    public void CloseNvidiaPicker()
    {
        IsNvidiaPickerOpen = false;
        OpenNvidiaPickerCommand.Cancel();
    }

    [RelayCommand]
    public async Task InstallSelectedNvidiaDriverAsync()
    {
        if (SelectedNvidiaDriver is not { } driver || SelectedNvidiaAdapter is not { IsNvidia: true } adapter ||
            !CanCheckForUpdates) return;

        IsDownloadingNvidiaDriver = true;
        try
        {
            string downloadUrl = driver.GetDownloadUrl(adapter.IsNotebook);
            AppendLog($"\n>>> [NVIDIA Driver Install] Version: {driver.Version} ({driver.Badge})");
            Notify($"Starting download for NVIDIA driver {driver.Version}...");

            var progress = new Progress<string>(AppendLog);
            var (success, message) = await _graphicsDriverService.DownloadAndRunNvidiaDriverAsync(
                downloadUrl, driver.Version, progress);

            Notify(message);
            if (success)
            {
                IsNvidiaPickerOpen = false;
            }
        }
        catch (Exception ex)
        {
            NotifyFailure("Download the NVIDIA driver", ex);
            AppendLog($"\n[ERROR] NVIDIA Driver Install failed: {AxePower.Core.Services.Outcomes.OutcomeClassifier.Detail(ex)}");
        }
        finally
        {
            IsDownloadingNvidiaDriver = false;
        }
    }

    [RelayCommand]
    public void OpenNvidiaAdvancedSearch()
    {
        _graphicsDriverService.OpenNvidiaAdvancedSearch();
        Notify("Opening NVIDIA Advanced Driver Search in browser...");
    }

    public void ShowWebLinkWarning(string title, string message, string url)
    {
        WarningModalTitle = title;
        WarningModalMessage = message;
        PendingWebUrl = url;
        IsWarningModalOpen = true;
    }

    [RelayCommand]
    public void ConfirmOpenWebLink()
    {
        IsWarningModalOpen = false;
        if (!string.IsNullOrEmpty(PendingWebUrl))
        {
            var result = _hubService.OpenWebPage(PendingWebUrl);
            Notify(result.Started ? "Opening link in browser..." : "Failed to open browser: " + result.Message);
        }
    }

    [RelayCommand]
    public void CancelWebLinkWarning()
    {
        IsWarningModalOpen = false;
        PendingWebUrl = string.Empty;
    }

    private void LoadPackages()
    {
        AllPackages.Clear();
        foreach (var pkg in _hubService.GetAvailablePackages())
        {
            AllPackages.Add(new PackageCardViewModel(pkg, _hubService, this));
        }
        ApplyFilter();
    }

    private void LoadShieldSettings()
    {
        var status = _updateShieldService.GetStatus();
        IsSecurityOnlyUpdates = status.IsSecurityOnlyEnabled;
        IsNoForcedReboot = status.IsNoForcedRebootEnabled;
    }

    [RelayCommand]
    public async Task RefreshStatusesAsync()
    {
        if (IsRefreshing) return;

        IsRefreshing = true;
        AppendLog("[Refreshing Statuses] Scanning installed software & runtimes...");
        try
        {
            await _hubService.RefreshInstallationStatusesAsync();
            foreach (var card in AllPackages)
            {
                card.Sync();
            }
            AppendLog("[Status Refresh Complete] All packages synced.");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    public void SetTab(string tabName)
    {
        ActiveTab = tabName;
    }

    [RelayCommand]
    public void SetCategory(string category)
    {
        // OnSelectedCategoryChanged runs the filter.
        SelectedCategory = category;
    }

    [RelayCommand]
    private void ToggleCategoryList()
    {
        IsCategoryListCollapsed = !IsCategoryListCollapsed;
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    // Category controls bind two-way to SelectedCategory, so filter whenever it changes.
    partial void OnSelectedCategoryChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredPackages.Clear();
        var query = AllPackages.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SelectedCategory) && SelectedCategory != "All")
        {
            if (Enum.TryParse<PackageCategory>(SelectedCategory, true, out var cat))
            {
                query = query.Where(p => p.Category == cat);
            }
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(p =>
                p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                p.WinGetId.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
        {
            FilteredPackages.Add(item);
        }
    }

    public void NotifySelectionChanged()
    {
        SelectedCount = AllPackages.Count(p => p.IsSelected && p.CanSelectForBatch);
        HasSelectedPackages = SelectedCount > 0;
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var pkg in FilteredPackages)
        {
            if (pkg.CanSelectForBatch)
            {
                pkg.IsSelected = true;
            }
        }
        NotifySelectionChanged();
    }

    [RelayCommand]
    public void SelectUninstalled()
    {
        foreach (var pkg in FilteredPackages)
        {
            if (!pkg.IsInstalled && pkg.CanSelectForBatch)
            {
                pkg.IsSelected = true;
            }
            else
            {
                pkg.IsSelected = false;
            }
        }
        NotifySelectionChanged();
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var pkg in AllPackages)
        {
            pkg.IsSelected = false;
        }
        NotifySelectionChanged();
    }

    [RelayCommand]
    public async Task InstallSelectedDirectAsync() => await RunBatchInstallAsync(showTerminal: false);

    /// <summary>
    /// The same safe installs as the direct batch, with the Terminal page showing the output live.
    /// Nothing is typed into a shell.
    /// </summary>
    [RelayCommand]
    public async Task InstallSelectedViaTerminalAsync() => await RunBatchInstallAsync(showTerminal: true);

    /// <summary>Stops the running batch: the current install's process tree is ended and the rest are skipped.</summary>
    [RelayCommand]
    public void CancelBatch() => _batch?.Cancel();

    private async Task RunBatchInstallAsync(bool showTerminal)
    {
        if (!CanCheckForUpdates) return;

        var toInstall = AllPackages.Where(p => p.IsSelected && !p.IsInstalled && p.CanSelectForBatch).ToList();
        if (toInstall.Count == 0)
        {
            Notify(showTerminal ? "No uninstalled packages selected for terminal installation." : "No uninstalled packages selected for installation.");
            return;
        }
        if (!_confirm.Confirm(ConfirmationCatalog.InstallPackages(toInstall.Select(c => c.Name).ToList()))) return;

        IsBatchInstalling = true;
        using var batch = new CancellationTokenSource();
        _batch = batch;
        var cards = toInstall.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (showTerminal)
            {
                Notify($"Switched to Terminal. Running installation of {toInstall.Count} packages...");
                _terminalService.RequestNavigateToTerminal();
            }
            else
            {
                Notify($"Starting batch installation of {toInstall.Count} selected packages...");
            }
            AppendLog("\n=======================================================");
            AppendLog($"  Starting Batch Installation ({toInstall.Count} packages)");
            AppendLog("=======================================================");

            var batchProgress = new Progress<PackageBatchProgress>(p => OnBatchProgress(p, cards));
            var result = await _hubService.InstallManyAsync(toInstall.Select(c => c.Id).ToList(), new Progress<string>(AppendLog), batchProgress, batch.Token);

            foreach (var item in result.Items)
            {
                if (!cards.TryGetValue(item.Id, out var card)) continue;
                card.IsBusy = false;
                if (item.Result.Succeeded) card.IsInstalled = true;
            }

            // Names exactly what was installed, what was not and why, and what a cancel skipped (Prompt 30)
            _notificationService.Show(AxePower.Core.Services.Outcomes.UserMessages.For(result.ToOutcome("Install apps")));
            AppendLog("\n=======================================================");
            AppendLog($"  Batch Installation {(result.WasCancelled ? "Cancelled" : "Complete")}: {result.SucceededCount}/{toInstall.Count} succeeded");
            AppendLog("=======================================================\n");

            // A bundled installer that is missing offers its web page, as its card's button does.
            var offer = result.Items.FirstOrDefault(i => i.Result.OfferedWebUrl != null);
            if (offer != null && cards.TryGetValue(offer.Id, out var offerCard))
            {
                Notify($"Local installer file not found. Opening {offerCard.Name} website...");
                offerCard.OpenCustomWebUrl();
            }

            // Deselect successfully installed packages
            foreach (var card in toInstall.Where(c => c.IsInstalled))
            {
                card.IsSelected = false;
            }
            NotifySelectionChanged();
        }
        finally
        {
            _batch = null;
            foreach (var card in toInstall) card.IsBusy = false;
            IsBatchInstalling = false;
            BatchInstallStatus = string.Empty;
        }
    }

    private void OnBatchProgress(PackageBatchProgress p, IReadOnlyDictionary<string, PackageCardViewModel> cards)
    {
        cards.TryGetValue(p.Id, out var card);
        if (p.Outcome is null)
        {
            BatchInstallStatus = $"Installing ({p.Index}/{p.Count}): {p.Name}...";
            Notify($"[{p.Index}/{p.Count}] Installing {p.Name}...");
            AppendLog($"\n>>> [{p.Index}/{p.Count}] Installing {p.Name} ({card?.WinGetId})...");
            if (card != null) card.IsBusy = true;
            return;
        }

        if (card != null) card.IsBusy = false;
        switch (p.Outcome)
        {
            case PackageOutcome.Succeeded:
                if (card != null) card.IsInstalled = true;
                AppendLog($"[Success] ✓ {p.Name} installed.");
                break;
            case PackageOutcome.Cancelled:
                AppendLog($"[Cancelled] {p.Name}: stopped.");
                break;
            default:
                AppendLog($"[Notice] Installation completed with notices for {p.Name}.");
                break;
        }
    }

    [RelayCommand]
    public async Task ToggleSecurityOnlyUpdatesAsync()
    {
        if (!CanCheckForUpdates) return;
        if (!TerminalService.IsProcessElevated())
        {
            Notify("Changing Windows Update policy needs administrator rights — use Relaunch with Admin, then try again.");
            AppendLog("[Update Policy] Security-only change skipped: administrator rights are required.");
            LoadShieldSettings();
            return;
        }

        IsChangingUpdatePolicy = true;
        bool requested = !IsSecurityOnlyUpdates;
        try
        {
            bool ok = await _updateShieldService.SetSecurityOnlyUpdatesAsync(requested);
            LoadShieldSettings();
            if (ok && IsSecurityOnlyUpdates == requested)
            {
                Notify(requested
                    ? "The 365-day feature update deferral is configured. Windows determines whether it applies."
                    : "The feature update deferral setting was cleared.");
                AppendLog($"[Update Policy] Feature update deferral configured: {requested}.");
            }
            else
            {
                Notify("Windows did not apply the feature-update policy. Check administrator rights and logs.");
                AppendLog("[Update Policy Error] Security-only policy could not be verified.");
            }
        }
        catch (Exception ex)
        {
            LoadShieldSettings();
            NotifyFailure("Change the Windows Update policy", ex);
            AppendLog($"[Update Policy Error] {AxePower.Core.Services.Outcomes.OutcomeClassifier.Detail(ex)}");
        }
        finally
        {
            IsChangingUpdatePolicy = false;
        }
    }

    [RelayCommand]
    public async Task ToggleSecurityOnlyAsync() => await ToggleSecurityOnlyUpdatesAsync();

    [RelayCommand]
    public async Task ToggleNoForcedRebootAsync()
    {
        if (!CanCheckForUpdates) return;
        if (!TerminalService.IsProcessElevated())
        {
            Notify("Changing the automatic-restart policy needs administrator rights — use Relaunch with Admin, then try again.");
            AppendLog("[Update Policy] Restart-policy change skipped: administrator rights are required.");
            LoadShieldSettings();
            return;
        }

        IsChangingUpdatePolicy = true;
        bool requested = !IsNoForcedReboot;
        try
        {
            bool ok = await _updateShieldService.SetNoForcedRebootAsync(requested);
            LoadShieldSettings();
            if (ok && IsNoForcedReboot == requested)
            {
                Notify(requested
                    ? "Signed-in restart protection is configured. It applies only to scheduled automatic installs."
                    : "The signed-in restart protection setting was cleared.");
                AppendLog($"[Update Policy] Signed-in restart protection configured: {requested}.");
            }
            else
            {
                Notify("Windows did not apply the restart policy. Check administrator rights and logs.");
                AppendLog("[Update Policy Error] Restart policy could not be verified.");
            }
        }
        catch (Exception ex)
        {
            LoadShieldSettings();
            NotifyFailure("Change Windows Update's restart policy", ex);
            AppendLog($"[Update Policy Error] {AxePower.Core.Services.Outcomes.OutcomeClassifier.Detail(ex)}");
        }
        finally
        {
            IsChangingUpdatePolicy = false;
        }
    }

    [RelayCommand]
    public async Task ResetUpdateCacheAsync()
    {
        if (!CanCheckForUpdates) return;

        // Stopping the update services and clearing SoftwareDistribution needs admin rights
        if (!TerminalService.IsProcessElevated())
        {
            Notify("Run AxePower as administrator to reset the Windows Update cache.");
            AppendLog("\n[Update Cache Reset] Skipped: administrator rights are required.");
            return;
        }
        if (!_confirm.Confirm(ConfirmationCatalog.ResetWindowsUpdateCache(WindowsUpdateShieldService.UpdateServices))) return;

        IsResettingUpdateCache = true;
        try
        {
            _terminalService.RequestNavigateToTerminal();
            AppendLog("\n>>> [Update Cache Reset] Stopping services, clearing the download cache, restoring prior service state...");
            var progress = new Progress<string>(AppendLog);
            bool ok = await _updateShieldService.ResetWindowsUpdateCacheAsync(progress);
            Notify(ok
                ? "Windows Update cache reset. Check for updates again."
                : "Windows Update cache reset did not complete. See the Terminal for details.");
        }
        catch (Exception ex)
        {
            NotifyFailure("Reset the Windows Update cache", ex);
            AppendLog($"[Update Cache Reset Error] {AxePower.Core.Services.Outcomes.OutcomeClassifier.Detail(ex)}");
        }
        finally
        {
            IsResettingUpdateCache = false;
        }
    }

    [RelayCommand]
    public void ClearLogs()
    {
        LiveLogs = "Console log cleared.\n";
    }

    [RelayCommand]
    public void ClearConsole() => ClearLogs();

    public async Task LoadFirmwareInfoAsync(bool forceRefresh = false)
    {
        try
        {
            var info = await _firmwareUpdateService.GetFirmwareInfoAsync(forceRefresh);
            FirmwareInfo = info;
            FirmwareCheckStatus = info.UpdateStatus;
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to load firmware info.", ex);
        }
    }

    [RelayCommand]
    public async Task CheckFirmwareUpdatesAsync()
    {
        if (!CanCheckForUpdates) return;

        IsCheckingFirmware = true;
        FirmwareCheckStatus = "Scanning Windows Update catalog & firmware devices...";
        AppendLog("[BIOS & Firmware] Initiating system firmware check...");

        try
        {
            var (hasUpdates, statusMessage) = await _firmwareUpdateService.CheckForFirmwareUpdatesAsync();
            FirmwareCheckStatus = statusMessage;
            FirmwareInfo.HasPendingUpdates = hasUpdates;
            FirmwareInfo.UpdateStatus = statusMessage;
            OnPropertyChanged(nameof(FirmwareInfo));

            AppendLog($"[BIOS & Firmware] {statusMessage}");
            Notify(statusMessage);
        }
        catch (Exception ex)
        {
            FirmwareCheckStatus = AxePower.Core.Services.Outcomes.UserMessages.Failed("Scan for BIOS and firmware updates", ex).Text;
            AppendLog($"[BIOS & Firmware Error] {AxePower.Core.Services.Outcomes.OutcomeClassifier.Detail(ex)}");
        }
        finally
        {
            IsCheckingFirmware = false;
        }
    }

    [RelayCommand]
    public void OpenVendorBiosPortal()
    {
        var result = _firmwareUpdateService.OpenVendorBiosPortal(FirmwareInfo);
        var message = result.Started ? "Requested the manufacturer's BIOS support page." : result.Message;
        Notify(message);
        AppendLog($"[BIOS & Firmware] {message}");
    }

    [RelayCommand]
    public async Task CreateRestorePointAsync()
    {
        if (!CanCheckForUpdates) return;

        if (!TerminalService.IsProcessElevated())
        {
            Notify("Creating a restore point needs admin — click \"Relaunch with Admin\" (top bar), then try again.");
            return;
        }

        IsCreatingRestorePoint = true;
        try
        {
            _terminalService.RequestNavigateToTerminal();
            var progress = new Progress<string>(AppendLog);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            bool ok = await _systemRestoreService.CreateRestorePointAsync($"AxePower Restore Point — {stamp}", progress);
            Notify(ok ? "✓ System Restore point created." : "Restore point finished with notices. Check the Terminal.");
        }
        catch (Exception ex)
        {
            NotifyFailure("Create a restore point", ex);
            AppendLog($"[Restore Point Error] {AxePower.Core.Services.Outcomes.OutcomeClassifier.Detail(ex)}");
        }
        finally
        {
            IsCreatingRestorePoint = false;
        }
    }

    public void AppendLog(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var sb = new StringBuilder(LiveLogs);
        sb.AppendLine(message);
        if (sb.Length > 20000)
        {
            sb.Remove(0, 5000);
        }
        LiveLogs = sb.ToString();

        // Mirror into the central Terminal page so it's the one place output appears.
        _terminalService.AppendExternalLine(message);
    }

    public void Notify(string message)
    {
        _notificationService.Show(message);
    }

    /// <summary>"Could not <paramref name="action"/>" with what to do about it (Prompt 30).</summary>
    public void NotifyFailure(string action, Exception error) => _notificationService.ShowFailure(action, error);
}
