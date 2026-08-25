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

    public bool IsNotInstalled => !IsInstalled && !IsWebLinkOnly && !HasCustomInstaller;
    public bool CanOpen => IsInstalled && !IsWebLinkOnly && !HasCustomInstaller;
    public bool CanUninstall => IsInstalled && !IsWebLinkOnly && !HasCustomInstaller && !string.IsNullOrEmpty(WinGetId);
    public bool ShowCustomInstallerButtons => HasCustomInstaller;

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
            Path.Combine(Directory.GetCurrentDirectory(), "Ninite Installer.exe"),
            @"C:\Users\Abhimanyu Vishwakarm\GitHub\AxePower\Ninite Installer.exe"
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
            _parent.AppendLog($"\n>>> Installing {Name} ({WinGetId})...");
            var progress = new Progress<string>(_parent.AppendLog);
            bool success = await _hubService.InstallPackageAsync(WinGetId, progress);
            if (success)
            {
                IsInstalled = true;
                _parent.Notify($"✓ Successfully installed {Name}!");
            }
            else
            {
                _parent.Notify($"Installation of {Name} finished with notices. Check logs.");
            }
        }
        finally
        {
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
    private readonly NotificationService _notificationService;
    private readonly LogService _logService;

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

    public SoftwareHubViewModel(
        IWinGetHubService hubService,
        IWindowsUpdateShieldService updateShieldService,
        DriverUpdateService driverUpdateService,
        FirmwareUpdateService firmwareUpdateService,
        NotificationService notificationService,
        LogService logService)
    {
        _hubService = hubService;
        _updateShieldService = updateShieldService;
        _driverUpdateService = driverUpdateService;
        _firmwareUpdateService = firmwareUpdateService;
        _notificationService = notificationService;
        _logService = logService;

        LoadPackages();
        LoadShieldSettings();

        _ = RefreshStatusesAsync();
        _ = LoadFirmwareInfoAsync();
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
        SelectedCategory = category;
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value)
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
            ActiveTab = "Console";
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
    }

    public void Notify(string message)
    {
        _notificationService.Show(message);
    }
}
