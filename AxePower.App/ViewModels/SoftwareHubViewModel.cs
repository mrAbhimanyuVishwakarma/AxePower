using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.App.ViewModels;

public partial class PackageCardViewModel : ObservableObject
{
    private readonly IWinGetHubService _hubService;
    private readonly SoftwareHubViewModel _parent;

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

    public bool IsNotInstalled => !IsInstalled && !IsWebLinkOnly && !HasCustomInstaller;
    public bool CanOpen => IsInstalled && !IsWebLinkOnly && !ShowCustomInstallerButtons;
    public bool CanUpdate => IsInstalled && !IsWebLinkOnly && !ShowCustomInstallerButtons;
    public bool CanUninstall => IsInstalled && !IsWebLinkOnly && !ShowCustomInstallerButtons;
    public bool ShowCustomInstallerButtons => HasCustomInstaller;

    public string InstallButtonText => Id == "dlss5swapper" ? "Download & Run" : "Install";
    public string InstallButtonTooltip => Id == "dlss5swapper" ? "Download latest portable release from GitHub and run it" : "Install this software via PowerShell";
    public string OpenButtonText => Id == "dlss5swapper" ? "Run" : "Open";
    public string UpdateButtonTooltip => Id == "dlss5swapper" ? "Update to the newest portable release from GitHub" : "Update this software to the latest release via PowerShell";

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
        try
        {
            string target = LaunchProtocolOrExe;
            if (!target.Contains("://") && !File.Exists(target))
            {
                string exeName = Path.GetFileName(target);
                target = exeName;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
            _parent.Notify($"Opening {Name}...");
        }
        catch (Exception ex)
        {
            _parent.Notify($"Could not open {Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenLinkWithWarning()
    {
        string targetUrl = !string.IsNullOrEmpty(WebUrl) ? WebUrl : "https://fitgirl-repacks.site/";
        _parent.ShowWebLinkWarning(
            $"Open {Name}",
            $"⚠️ Warning: You are about to visit {Name} ({targetUrl}) in your web browser.\n\nAlways ensure you remain on the authentic official domain ({targetUrl}) to avoid clone or phishing sites.",
            targetUrl
        );
    }

    [RelayCommand]
    public void LaunchLocalInstaller()
    {
        string[] candidatePaths = new[]
        {
            LocalInstallerPath,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ninite Installer.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Ninite Installer.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "Ninite Installer.exe")
        };

        string? foundPath = candidatePaths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));

        if (foundPath != null)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = foundPath,
                    UseShellExecute = true
                });
                _parent.Notify($"🚀 Launching {Name} Installer...");
                _parent.AppendLog($"\n>>> Launched {Name} Installer: {foundPath}");
            }
            catch (Exception ex)
            {
                _parent.Notify($"Error launching installer: {ex.Message}");
                _parent.AppendLog($"\n[ERROR] Could not start installer: {ex.Message}");
            }
        }
        else
        {
            _parent.Notify($"Local installer file not found. Opening {Name} website...");
            OpenCustomWebUrl();
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

    [RelayCommand]
    public async Task InstallAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        try
        {
            _parent.Notify($"🚀 1-Click Install started for {Name}!");
            _parent.AppendLog($"\n>>> 1-Click Install: {Name} ({WinGetId})...");
            var progress = new Progress<string>(_parent.AppendLog);
            bool success = await _hubService.InstallPackageAsync(WinGetId, progress);

            if (success)
            {
                IsInstalled = true;
                _parent.Notify($"✓ {Name} is now installed and ready!");
                _parent.AppendLog($"\n[Success] {Name} is now installed.");
            }
            else
            {
                _parent.Notify(Id == "camstudio"
                    ? "CamStudio isn't on WinGet. Download it from the page that just opened, then Refresh."
                    : $"Installation of {Name} did not complete. Opening website...");
            }
        }
        finally
        {
            // Release card busy lock quickly so user is never frozen
            await Task.Delay(500);
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task UpdateAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        try
        {
            _parent.Notify($"🚀 Update started for {Name}...");
            _parent.AppendLog($"\n>>> [Update] Updating {Name} ({WinGetId})...");
            var progress = new Progress<string>(_parent.AppendLog);
            bool success = await _hubService.UpgradePackageAsync(WinGetId, progress);
            if (success)
            {
                _parent.Notify($"✓ {Name} updated successfully.");
            }
            else
            {
                _parent.Notify($"Could not update {Name}. Check logs.");
            }
        }
        finally
        {
            await Task.Delay(500);
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task UninstallAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        try
        {
            _parent.AppendLog($"\n>>> Uninstalling {Name} ({WinGetId})...");
            var progress = new Progress<string>(_parent.AppendLog);
            bool success = await _hubService.UninstallPackageAsync(WinGetId, progress);
            if (success)
            {
                IsInstalled = false;
                _parent.Notify($"✓ Uninstalled {Name}.");
            }
            else
            {
                _parent.Notify($"Uninstall of {Name} finished. Check logs.");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Sync()
    {
        IsInstalled = Item.IsInstalled;
    }
}

public partial class SoftwareHubViewModel : ObservableObject
{
    private readonly IWinGetHubService _hubService;
    private readonly IWindowsUpdateShieldService _updateShieldService;
    private readonly DriverUpdateService _driverUpdateService;
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
    private string _searchText = string.Empty;

    // BIOS & Firmware Properties
    [ObservableProperty]
    private FirmwareInfo _firmwareInfo = new();

    [ObservableProperty]
    private bool _isCheckingFirmware;

    [ObservableProperty]
    private string _firmwareCheckStatus = "Ready to check for latest BIOS & firmware updates.";

    // Hero Banner Properties
    [ObservableProperty]
    private string _lastUpdatedText = DateTime.Now.ToString("M/d/yyyy h:mm tt");

    [ObservableProperty]
    private string _nextScheduledUpdateText = DateTime.Now.AddDays(7).ToString("M/d/yyyy h:mm tt");

    [ObservableProperty]
    private bool _isCheckingAllUpdates;

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
    private bool _isUpgradingAll;

    [ObservableProperty]
    private bool _isCreatingRestorePoint;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _hasSelectedPackages;

    [ObservableProperty]
    private bool _isBatchInstalling;

    [ObservableProperty]
    private string _batchInstallStatus = string.Empty;

    public SoftwareHubViewModel(
        IWinGetHubService hubService,
        IWindowsUpdateShieldService updateShieldService,
        DriverUpdateService driverUpdateService,
        FirmwareUpdateService firmwareUpdateService,
        NotificationService notificationService,
        LogService logService,
        GraphicsDriverService graphicsDriverService,
        TerminalService terminalService,
        RestorePointService systemRestoreService)
    {
        _hubService = hubService;
        _updateShieldService = updateShieldService;
        _driverUpdateService = driverUpdateService;
        _firmwareUpdateService = firmwareUpdateService;
        _notificationService = notificationService;
        _logService = logService;
        _graphicsDriverService = graphicsDriverService;
        _terminalService = terminalService;
        _systemRestoreService = systemRestoreService;

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
    public async Task UpdateGraphicsDriverAsync(GraphicsAdapterInfo? adapter)
    {
        if (adapter == null || IsInstallingGraphicsDriver) return;

        IsInstallingGraphicsDriver = true;
        try
        {
            AppendLog($"\n>>> [Graphics Drivers] {adapter.VendorName} — {adapter.Name} via {adapter.ToolName}");
            var progress = new Progress<string>(AppendLog);
            var (_, message) = await _graphicsDriverService.InstallOrUpdateAsync(adapter, progress);
            Notify(message);
            AppendLog($"[Graphics Drivers] {message}");
        }
        catch (Exception ex)
        {
            Notify($"Graphics driver update failed: {ex.Message}");
            AppendLog($"[Graphics Drivers Error] {ex.Message}");
        }
        finally
        {
            IsInstallingGraphicsDriver = false;
        }
    }

    [RelayCommand]
    public void OpenGraphicsVendorSite(GraphicsAdapterInfo? adapter)
    {
        if (adapter == null) return;
        _graphicsDriverService.OpenOfficialPage(adapter);
        Notify($"Opening the official {adapter.VendorName} driver site...");
    }

    [RelayCommand]
    public void OpenGraphicsCompanionApp(GraphicsAdapterInfo? adapter)
    {
        if (adapter == null || !adapter.HasCompanionApp) return;
        _graphicsDriverService.OpenCompanionApp(adapter);
        Notify($"Opening {adapter.CompanionAppName} in the Microsoft Store...");
    }

    [ObservableProperty]
    private bool _isNvidiaPickerOpen;

    [ObservableProperty]
    private GraphicsAdapterInfo? _selectedNvidiaAdapter;

    [ObservableProperty]
    private NvidiaDriverRelease? _selectedNvidiaDriver;

    [ObservableProperty]
    private bool _isDownloadingNvidiaDriver;

    [ObservableProperty]
    private bool _isNotebookVariant = true;

    public ObservableCollection<NvidiaDriverRelease> CuratedNvidiaDrivers { get; } = new();

    [RelayCommand]
    public void OpenNvidiaPicker(GraphicsAdapterInfo? adapter)
    {
        SelectedNvidiaAdapter = adapter ?? GraphicsAdapters.FirstOrDefault(a => a.IsNvidia);
        IsNotebookVariant = SelectedNvidiaAdapter?.IsNotebook ?? true;

        CuratedNvidiaDrivers.Clear();
        foreach (var driver in _graphicsDriverService.GetCuratedNvidiaDrivers())
        {
            CuratedNvidiaDrivers.Add(driver);
        }

        SelectedNvidiaDriver = CuratedNvidiaDrivers.FirstOrDefault(d => d.Badge.Contains("RECOMMENDED"))
                               ?? CuratedNvidiaDrivers.FirstOrDefault();

        IsNvidiaPickerOpen = true;
    }

    [RelayCommand]
    public void CloseNvidiaPicker()
    {
        IsNvidiaPickerOpen = false;
    }

    [RelayCommand]
    public async Task InstallSelectedNvidiaDriverAsync()
    {
        if (SelectedNvidiaDriver == null || IsDownloadingNvidiaDriver) return;

        IsDownloadingNvidiaDriver = true;
        try
        {
            string downloadUrl = SelectedNvidiaDriver.GetDownloadUrl(IsNotebookVariant);
            AppendLog($"\n>>> [NVIDIA Driver Rollback/Install] Version: {SelectedNvidiaDriver.Version} ({SelectedNvidiaDriver.Badge})");
            Notify($"Starting download for NVIDIA Driver {SelectedNvidiaDriver.Version}...");

            var progress = new Progress<string>(AppendLog);
            var (success, message) = await _graphicsDriverService.DownloadAndRunNvidiaDriverAsync(
                downloadUrl, SelectedNvidiaDriver.Version, progress);

            Notify(message);
            if (success)
            {
                IsNvidiaPickerOpen = false;
            }
        }
        catch (Exception ex)
        {
            Notify($"Download failed: {ex.Message}");
            AppendLog($"\n[ERROR] NVIDIA Driver Install failed: {ex.Message}");
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
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = PendingWebUrl,
                    UseShellExecute = true
                });
                Notify("Opening link in browser...");
            }
            catch (Exception ex)
            {
                Notify("Failed to open browser: " + ex.Message);
            }
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

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    // Category chips bind two-way to SelectedCategory, so filter whenever it changes.
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
        SelectedCount = AllPackages.Count(p => p.IsSelected);
        HasSelectedPackages = SelectedCount > 0;
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var pkg in FilteredPackages)
        {
            if (!pkg.IsWebLinkOnly)
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
            if (!pkg.IsInstalled && !pkg.IsWebLinkOnly)
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
    public async Task InstallSelectedDirectAsync()
    {
        if (IsBatchInstalling) return;

        var toInstall = AllPackages.Where(p => p.IsSelected && !p.IsInstalled && !p.IsWebLinkOnly).ToList();
        if (toInstall.Count == 0)
        {
            Notify("No uninstalled packages selected for installation.");
            return;
        }

        IsBatchInstalling = true;
        try
        {
            Notify($"Starting batch installation of {toInstall.Count} selected packages...");
            AppendLog("\n=======================================================");
            AppendLog($"  Starting Batch Installation ({toInstall.Count} packages)");
            AppendLog("=======================================================");

            int current = 0;
            int successful = 0;

            foreach (var card in toInstall)
            {
                current++;
                BatchInstallStatus = $"Installing ({current}/{toInstall.Count}): {card.Name}...";
                Notify($"[{current}/{toInstall.Count}] Installing {card.Name}...");
                AppendLog($"\n>>> [{current}/{toInstall.Count}] Installing {card.Name} ({card.WinGetId})...");

                var progress = new Progress<string>(AppendLog);
                card.IsBusy = true;
                bool ok = false;
                try
                {
                    if (card.HasCustomInstaller)
                    {
                        card.LaunchLocalInstaller();
                        ok = true;
                    }
                    else
                    {
                        ok = await _hubService.InstallPackageAsync(card.WinGetId, progress);
                    }
                }
                finally
                {
                    card.IsBusy = false;
                }

                if (ok)
                {
                    card.IsInstalled = true;
                    successful++;
                    AppendLog($"[Success] ✓ {card.Name} installed.");
                }
                else
                {
                    AppendLog($"[Notice] Installation completed with notices for {card.Name}.");
                }
            }

            Notify($"✓ Batch installation complete: {successful}/{toInstall.Count} installed.");
            AppendLog("\n=======================================================");
            AppendLog($"  Batch Installation Complete: {successful}/{toInstall.Count} succeeded");
            AppendLog("=======================================================\n");

            // Deselect successfully installed packages
            foreach (var card in toInstall.Where(c => c.IsInstalled))
            {
                card.IsSelected = false;
            }
            NotifySelectionChanged();
        }
        finally
        {
            IsBatchInstalling = false;
            BatchInstallStatus = string.Empty;
        }
    }

    [RelayCommand]
    public void InstallSelectedViaTerminal()
    {
        var toInstall = AllPackages.Where(p => p.IsSelected && !p.IsInstalled && !p.IsWebLinkOnly).ToList();
        if (toInstall.Count == 0)
        {
            Notify("No uninstalled packages selected for terminal installation.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Write-Host '=======================================================' -ForegroundColor Cyan");
        sb.AppendLine($"Write-Host '  AxePower Software Hub — Batch Installation ({toInstall.Count} packages)' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host '=======================================================' -ForegroundColor Cyan");

        int index = 0;
        foreach (var card in toInstall)
        {
            index++;
            sb.AppendLine($"Write-Host '`n[{index}/{toInstall.Count}] Installing {card.Name}...' -ForegroundColor Yellow");

            if (card.Id == "dlss5swapper")
            {
                sb.AppendLine("Start-Process 'https://github.com/rakanki911/DLSS5-Swapper/releases'");
            }
            else if (card.Id == "camstudio")
            {
                // Not published on WinGet: go straight to the official download page
                sb.AppendLine("Write-Host 'CamStudio is not on WinGet. Opening the official download page...' -ForegroundColor DarkYellow; Start-Process 'https://camstudio.org/'");
            }
            else if (!string.IsNullOrEmpty(card.WinGetId))
            {
                string source = System.Text.RegularExpressions.Regex.IsMatch(card.WinGetId, "^9[A-Z0-9]{11}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    ? "msstore" : "winget";
                sb.AppendLine($"winget install --id '{card.WinGetId}' --exact --source {source} --accept-source-agreements --accept-package-agreements");
            }
            else if (!string.IsNullOrEmpty(card.WebUrl))
            {
                sb.AppendLine($"Start-Process '{card.WebUrl}'");
            }
        }

        sb.AppendLine("Write-Host '`n=======================================================' -ForegroundColor Green");
        sb.AppendLine("Write-Host '  All batch installation commands executed!' -ForegroundColor Green");
        sb.AppendLine("Write-Host '=======================================================' -ForegroundColor Green");

        Notify($"Switched to Terminal. Running installation of {toInstall.Count} packages...");
        _terminalService.RequestNavigateToTerminal();
        _terminalService.SendCommand(sb.ToString());
    }

    [RelayCommand]
    public void LaunchElevatedDriverUpdate()
    {
        AppendLog("\n>>> Launching Elevated 3-Step Driver & System Updater...");
        var result = _driverUpdateService.LaunchDriverAndSystemUpdate();
        switch (result)
        {
            case DriverUpdateLaunchResult.StartedNew:
                Notify("Elevated Update PC process started.");
                AppendLog("Updater launched in background.");
                break;
            case DriverUpdateLaunchResult.FocusedExisting:
                Notify("Update PC is already running.");
                AppendLog("Focused existing updater window.");
                break;
            case DriverUpdateLaunchResult.CancelledByUser:
                Notify("Administrator prompt was cancelled.");
                AppendLog("[Cancelled] User denied UAC prompt.");
                break;
            case DriverUpdateLaunchResult.Failed:
                Notify("Failed to launch Update PC.");
                AppendLog("[Error] Failed to start driver updater process.");
                break;
        }
    }

    [RelayCommand]
    public async Task UpgradeAllAppsAsync()
    {
        if (IsUpgradingAll) return;

        IsUpgradingAll = true;
        try
        {
            // Live progress now streams to the shared Terminal page.
            _terminalService.RequestNavigateToTerminal();
            var progress = new Progress<string>(AppendLog);
            bool ok = await _hubService.UpgradeAllPackagesAsync(progress);
            if (ok)
            {
                Notify("✓ All applications upgraded successfully!");
            }
            else
            {
                Notify("Batch upgrade completed with notices. Check logs.");
            }
        }
        finally
        {
            IsUpgradingAll = false;
        }
    }

    [RelayCommand]
    public async Task ToggleSecurityOnlyUpdatesAsync()
    {
        bool newState = !IsSecurityOnlyUpdates;
        bool ok = await _updateShieldService.SetSecurityOnlyUpdatesAsync(newState);
        if (ok)
        {
            IsSecurityOnlyUpdates = newState;
            Notify(newState
                ? "Windows Update set to Security-Only updates (no feature bloat)."
                : "Windows Update restored to default updates.");
            AppendLog($"[Policy Changed] Security-Only Updates: {newState}");
        }
    }

    [RelayCommand]
    public async Task ToggleSecurityOnlyAsync() => await ToggleSecurityOnlyUpdatesAsync();

    [RelayCommand]
    public async Task ToggleNoForcedRebootAsync()
    {
        bool newState = !IsNoForcedReboot;
        bool ok = await _updateShieldService.SetNoForcedRebootAsync(newState);
        if (ok)
        {
            IsNoForcedReboot = newState;
            Notify(newState
                ? "Forced reboots during gaming/active hours are now blocked."
                : "Default Windows reboot behavior restored.");
            AppendLog($"[Policy Changed] No Forced Reboot: {newState}");
        }
    }

    [RelayCommand]
    public async Task ResetUpdateCacheAsync()
    {
        // Stopping the update services and clearing SoftwareDistribution needs admin rights
        if (!TerminalService.IsProcessElevated())
        {
            Notify("Run AxePower as administrator to reset the Windows Update cache.");
            AppendLog("\n[Update Cache Reset] Skipped: administrator rights are required.");
            return;
        }

        AppendLog("\n>>> [Update Cache Reset] Stopping services, clearing the download cache, restarting services...");
        var progress = new Progress<string>(AppendLog);
        bool ok = await _updateShieldService.ResetWindowsUpdateCacheAsync(progress);
        Notify(ok
            ? "Windows Update cache reset. Check for updates again."
            : "Windows Update cache reset did not complete. See the console for details.");
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
        if (IsCheckingFirmware) return;

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
            FirmwareCheckStatus = $"Firmware scan error: {ex.Message}";
            AppendLog($"[BIOS & Firmware Error] {ex.Message}");
        }
        finally
        {
            IsCheckingFirmware = false;
        }
    }

    [RelayCommand]
    public void OpenVendorBiosPortal()
    {
        if (FirmwareInfo != null)
        {
            _firmwareUpdateService.OpenVendorBiosPortal(FirmwareInfo);
            Notify($"Opening official {FirmwareInfo.VendorName} BIOS support portal...");
            AppendLog($"[BIOS & Firmware] Opened vendor portal: {FirmwareInfo.VendorPortalUrl}");
        }
    }

    [RelayCommand]
    public void TriggerWindowsFirmwareScan()
    {
        _firmwareUpdateService.TriggerInteractiveScan();
        Notify("Triggered Windows Update firmware capsule interactive scan.");
        AppendLog("[BIOS & Firmware] USOClient StartInteractiveScan triggered.");
    }

    [RelayCommand]
    public async Task CheckAllUpdatesAsync()
    {
        if (IsCheckingAllUpdates) return;

        IsCheckingAllUpdates = true;
        LastUpdatedText = DateTime.Now.ToString("M/d/yyyy h:mm tt");
        NextScheduledUpdateText = DateTime.Now.AddDays(7).ToString("M/d/yyyy h:mm tt");
        Notify("Checking for system, driver & BIOS updates...");
        AppendLog($"[{DateTime.Now:T}] [System Update] Initiating full system update check...");

        try
        {
            await CheckFirmwareUpdatesAsync();
            await RefreshStatusesAsync();
            Notify("✓ System scan completed. All components checked.");
            AppendLog($"[{DateTime.Now:T}] [System Update] Full system scan completed successfully.");
        }
        catch (Exception ex)
        {
            Notify($"Update check error: {ex.Message}");
            AppendLog($"[System Update Error] {ex.Message}");
        }
        finally
        {
            IsCheckingAllUpdates = false;
        }
    }

    [RelayCommand]
    public async Task CreateRestorePointAsync()
    {
        if (IsCreatingRestorePoint) return;

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
}
