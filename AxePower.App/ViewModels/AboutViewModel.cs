using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AxePower.Core.Services;

namespace AxePower.App.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    private readonly DriverUpdateService? _driverUpdateService;
    private readonly NotificationService? _notificationService;
    private readonly UpdateService? _updateService;
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty]
    private string _version = "1.2.0";

    [ObservableProperty]
    private string _runtime = ".NET 8.0";

    [ObservableProperty]
    private string _lastCheckedTime = "Today, " + DateTime.Now.ToString("h:mm tt");

    [ObservableProperty]
    private string _updateStatusTitle = "AxePower is up to date";

    [ObservableProperty]
    private string _checkButtonText = "Check for updates";

    [ObservableProperty]
    private bool _isPrivacyPolicyOpen;

    // Update Properties
    [ObservableProperty]
    private bool _isCheckingForUpdate;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _latestVersionTag = "1.2.0";

    [ObservableProperty]
    private string _releaseTitle = "";

    [ObservableProperty]
    private string _releaseNotes = "";

    [ObservableProperty]
    private string? _downloadUrl;

    [ObservableProperty]
    private string? _assetName;

    [ObservableProperty]
    private string _releaseUrl = "";

    [ObservableProperty]
    private bool _isUpdateModalOpen;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _downloadStatusText = "Downloading update...";

    public AboutViewModel()
    {
        var assembly = Assembly.GetExecutingAssembly();
        Version = assembly.GetName().Version?.ToString(3) ?? "1.2.0";
    }

    public AboutViewModel(
        DriverUpdateService driverUpdateService, 
        NotificationService notificationService,
        UpdateService updateService) : this()
    {
        _driverUpdateService = driverUpdateService;
        _notificationService = notificationService;
        _updateService = updateService;
    }

    [RelayCommand]
    private void OpenDeveloper()
    {
        OpenUrl("https://github.com/mrAbhimanyuVishwakarma");
    }

    [RelayCommand]
    private void VisitWebsite()
    {
        OpenUrl("https://github.com/mrAbhimanyuVishwakarma/AxePower_Download");
    }

    [RelayCommand]
    private void ContactSupport()
    {
        OpenUrl("https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues");
    }

    [RelayCommand]
    private void ViewAllReleases()
    {
        OpenUrl("https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases");
    }

    [RelayCommand]
    private void OpenHelp()
    {
        OpenUrl("https://github.com/mrAbhimanyuVishwakarma/AxePower_Download#readme");
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (IsCheckingForUpdate) return;

        IsCheckingForUpdate = true;
        CheckButtonText = "Checking...";
        LastCheckedTime = "Today, " + DateTime.Now.ToString("h:mm tt");

        if (_updateService == null)
        {
            IsCheckingForUpdate = false;
            UpdateStatusTitle = "Already Updated";
            CheckButtonText = "✓ Already Updated";
            _notificationService?.Show("Already Updated");
            _ = ResetCheckButtonTextAfterDelay();
            return;
        }

        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            IsCheckingForUpdate = false;

            if (result.HasUpdate)
            {
                IsUpdateAvailable = true;
                UpdateStatusTitle = "Update Available (v" + result.LatestVersion + ")";
                CheckButtonText = "Update Now";
                LatestVersionTag = result.LatestVersion;
                ReleaseTitle = result.ReleaseTitle;
                ReleaseNotes = result.ReleaseNotes;
                DownloadUrl = result.DownloadUrl;
                AssetName = result.AssetName;
                ReleaseUrl = result.ReleaseUrl;
                IsUpdateModalOpen = true;
                _notificationService?.Show($"🚀 New update available: v{result.LatestVersion}!");
            }
            else
            {
                // Version matches -> Already Updated without opening any release page!
                IsUpdateAvailable = false;
                UpdateStatusTitle = "Already Updated";
                CheckButtonText = "✓ Already Updated";
                _notificationService?.Show("Already Updated");
                _ = ResetCheckButtonTextAfterDelay();
            }
        }
        catch (Exception ex)
        {
            IsCheckingForUpdate = false;
            CheckButtonText = "Check for updates";
            _notificationService?.Show("Failed to check for updates: " + ex.Message);
        }
    }

    private async Task ResetCheckButtonTextAfterDelay()
    {
        await Task.Delay(3000);
        if (!IsUpdateAvailable && !IsCheckingForUpdate)
        {
            CheckButtonText = "Check for updates";
        }
    }

    [RelayCommand]
    private async Task DownloadAndApplyUpdate()
    {
        if (_updateService == null || string.IsNullOrWhiteSpace(DownloadUrl))
        {
            OpenUrl(ReleaseUrl);
            return;
        }

        // If it's a web page or zip file rather than an installer, open in browser
        if (DownloadUrl.StartsWith("http") && !DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !DownloadUrl.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            OpenUrl(DownloadUrl);
            return;
        }

        try
        {
            IsDownloadingUpdate = true;
            DownloadProgress = 0;
            DownloadStatusText = "Downloading " + (AssetName ?? "update installer") + "...";
            _downloadCts = new CancellationTokenSource();

            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                DownloadStatusText = $"Downloading update... {p:0}%";
            });

            var installerPath = await _updateService.DownloadUpdateAsync(DownloadUrl, progress, _downloadCts.Token);

            DownloadStatusText = "Launching installer...";
            await Task.Delay(500);

            bool launched = _updateService.ApplyUpdate(installerPath);
            if (launched)
            {
                Application.Current?.Shutdown();
            }
            else
            {
                IsDownloadingUpdate = false;
                _notificationService?.Show("Failed to start installer. Please run manually from downloads.");
            }
        }
        catch (OperationCanceledException)
        {
            IsDownloadingUpdate = false;
            DownloadStatusText = "Download cancelled.";
        }
        catch (Exception ex)
        {
            IsDownloadingUpdate = false;
            _notificationService?.Show("Download failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private void DismissUpdateModal()
    {
        _downloadCts?.Cancel();
        IsDownloadingUpdate = false;
        IsUpdateModalOpen = false;
    }

    [RelayCommand]
    private void OpenPrivacyPolicy()
    {
        IsPrivacyPolicyOpen = true;
    }

    [RelayCommand]
    private void ClosePrivacyPolicy()
    {
        IsPrivacyPolicyOpen = false;
    }

    [RelayCommand]
    private void UpdateDrivers()
    {
        if (_driverUpdateService == null) return;
        var result = _driverUpdateService.LaunchDriverAndSystemUpdate();
        switch (result)
        {
            case DriverUpdateLaunchResult.StartedNew:
                _notificationService?.Show("Update PC started in background (minimized).");
                break;
            case DriverUpdateLaunchResult.FocusedExisting:
                _notificationService?.Show("Update PC is already running. Showing active window.");
                break;
            case DriverUpdateLaunchResult.CancelledByUser:
                _notificationService?.Show("Update PC administrator request was cancelled.");
                break;
            case DriverUpdateLaunchResult.Failed:
                _notificationService?.Show("Failed to launch Update PC.");
                break;
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notificationService?.Show("Failed to open link: " + ex.Message);
        }
    }
}
