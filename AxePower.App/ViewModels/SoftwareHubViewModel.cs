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

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isBusy;

    public bool IsNotInstalled => !IsInstalled && !IsWebLinkOnly;
    public bool CanOpen => IsInstalled && !IsWebLinkOnly;
    public bool CanUninstall => IsInstalled && !IsWebLinkOnly && !string.IsNullOrEmpty(WinGetId);

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
    private readonly NotificationService _notificationService;
    private readonly LogService _logService;

    [ObservableProperty]
    private string _activeTab = "Updaters"; // "Updaters", "Apps", "Updates", "Console"

    [ObservableProperty]
    private string _selectedCategory = "All";

    [ObservableProperty]
    private string _searchText = string.Empty;

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
        NotificationService notificationService,
        LogService logService)
    {
        _hubService = hubService;
        _updateShieldService = updateShieldService;
        _driverUpdateService = driverUpdateService;
        _notificationService = notificationService;
        _logService = logService;

        LoadPackages();
        LoadShieldSettings();

        _ = RefreshStatusesAsync();
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
