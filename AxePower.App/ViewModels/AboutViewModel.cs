using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AxePower.Core.Helpers;
using AxePower.Core.Services;
using AxePower.Core.Services.Update;

namespace AxePower.App.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    private readonly DriverUpdateService? _driverUpdateService;
    private readonly NotificationService? _notificationService;
    private readonly UpdateService? _updateService;
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReleaseVersionWithDate))]
    private string _version = "1.4.0";

    [ObservableProperty]
    private UpdateManifest? _manifest;

    [ObservableProperty]
    private string _runtime = ".NET 8.0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReleaseVersionWithDate))]
    private string _releaseDate = string.Empty;

    public string ReleaseVersionWithDate => string.IsNullOrWhiteSpace(ReleaseDate) ? $"Version {Version}" : $"Version {Version} • {ReleaseDate}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCheckedDisplay))]
    private string _lastCheckedTime = "Not checked yet";

    public string LastCheckedDisplay => LastCheckedTime == "Not checked yet" ? "Not checked yet" : $"Last checked: {LastCheckedTime}";

    [ObservableProperty]
    private string _updateStatusTitle = "AxePower is up to date";

    [ObservableProperty]
    private string _checkButtonText = "Check for updates";

    [ObservableProperty]
    private string _systemCompatibility = string.Empty;

    [ObservableProperty]
    private string _licenseUser = Environment.MachineName;

    [ObservableProperty]
    private string _licenseType = "AxePower Pro";

    [ObservableProperty]
    private bool _isPrivacyPolicyOpen;

    // Update Properties
    [ObservableProperty]
    private bool _isCheckingForUpdate;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _latestVersionTag = "2.0.3";

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

    // Steam-Style Update & Patch Notes Dialog State
    [ObservableProperty]
    private bool _isSteamUpdateBannerOpen;

    [ObservableProperty]
    private string _steamUpdateBannerMessage = "Your AxePower is already up to date - ";

    [ObservableProperty]
    private bool _isUpdateDownloaded;

    [ObservableProperty]
    private string? _downloadedInstallerPath;

    [ObservableProperty]
    private bool _isSteamPatchNotesModalOpen;

    [ObservableProperty]
    private string _patchNotesCategory = "SMALL UPDATE / PATCH NOTES";

    [ObservableProperty]
    private string _patchNotesDate = "POSTED " + DateTime.Now.ToString("ddd, MMMM d");

    [ObservableProperty]
    private string _patchNotesTitle = string.Empty; // derived from the assembly version in the constructor

    [ObservableProperty]
    private string _patchNotesHeader = "A new AxePower Client update has been released and will be automatically applied.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedLikesCount))]
    [NotifyPropertyChangedFor(nameof(HasLikes))]
    private int _patchNotesLikes = 1;

    [ObservableProperty]
    private bool _isLiked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedCommentsCount))]
    [NotifyPropertyChangedFor(nameof(HasComments))]
    private int _patchNotesComments = 0;

    public string FormattedLikesCount => PatchNotesLikes > 0 ? $"{PatchNotesLikes:N0} " : string.Empty;
    public string FormattedCommentsCount => PatchNotesComments > 0 ? $"{PatchNotesComments:N0} " : string.Empty;
    public bool HasLikes => PatchNotesLikes > 0;
    public bool HasComments => PatchNotesComments > 0;

    public AboutViewModel()
    {
        var assembly = Assembly.GetExecutingAssembly();
        Version = assembly.GetName().Version?.ToString(3) ?? UpdateService.FallbackVersion;
        PatchNotesTitle = $"AxePower Client Update - Version {Version}";

        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                ReleaseDate = File.GetLastWriteTime(path).ToString("MMM dd, yyyy");
            }
            else
            {
                ReleaseDate = DateTime.Now.ToString("MMM dd, yyyy");
            }
        }
        catch
        {
            ReleaseDate = DateTime.Now.ToString("MMM dd, yyyy");
        }

        // Live OS & Hardware Detection
        try
        {
            var osName = Environment.OSVersion.Version.Major switch
            {
                10 when Environment.OSVersion.Version.Build >= 22000 => "Windows 11",
                10 => "Windows 10",
                _ => "Windows " + Environment.OSVersion.Version.Major
            };
            var arch = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
            var cores = Environment.ProcessorCount;
            SystemCompatibility = $"{osName} (Build {Environment.OSVersion.Version.Build}) • {arch} • {cores} Cores • Optimized for laptops";
        }
        catch
        {
            SystemCompatibility = "Windows 10 / 11 • 64-bit • Intel & AMD supported • Optimized for laptops";
        }

        LicenseUser = string.IsNullOrWhiteSpace(Environment.MachineName) ? "This PC" : Environment.MachineName;
        LicenseType = "AxePower Pro";
        GenerateMobileDownloadQr();
    }

    /// <summary>The account shown on the Profile page, which this view model backs. Null in tests that build the page bare.</summary>
    public AccountViewModel? Account { get; init; }

    public AboutViewModel(
        DriverUpdateService driverUpdateService, 
        NotificationService notificationService,
        UpdateService updateService,
        AccountViewModel? account = null) : this()
    {
        Account = account;
        _driverUpdateService = driverUpdateService;
        _notificationService = notificationService;
        _updateService = updateService;
        _updateService.DownloadProgressChanged += OnDownloadProgressChanged;

        _ = SyncCommunityStatsAsync();
    }

    [RelayCommand]
    private void OpenDeveloper()
    {
        OpenUrl("https://github.com/mrAbhimanyuVishwakarma");
    }

    [ObservableProperty]
    private ImageSource? _mobileDownloadQrCode;

    public string MobileAppDownloadUrl => "https://github.com/mrAbhimanyuVishwakarma/Axepower-Mobile-Download/releases";

    public void GenerateMobileDownloadQr()
    {
        try
        {
            byte[] bmpBytes = QrCodeGenerator.GenerateBmpBytes(MobileAppDownloadUrl, 8, 4, 0x00000000, 0x00FFFFFF);
            using var ms = new MemoryStream(bmpBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            MobileDownloadQrCode = bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException or IOException)
        {
            // No QR image: the download link is still shown as text.
            MobileDownloadQrCode = null;
        }
    }

    [RelayCommand]
    private void OpenMobileDownloadLink()
    {
        OpenUrl(MobileAppDownloadUrl);
    }

    [RelayCommand]
    private void VisitWebsite()
    {
        OpenUrl("https://axepower.vercel.app/");
    }

    [RelayCommand]
    private void ContactSupport()
    {
        OpenUrl("https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues");
    }

    [RelayCommand]
    private void ViewAllReleases()
    {
        OpenUrl("https://axepower.vercel.app/");
    }

    [RelayCommand]
    private void OpenHelp()
    {
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
        {
            mainVm.OpenToolHelp(null);
        }
        else
        {
            OpenUrl("https://github.com/mrAbhimanyuVishwakarma/AxePower_Download#readme");
        }
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (IsCheckingForUpdate) return;

        IsCheckingForUpdate = true;
        CheckButtonText = "Checking...";
        LastCheckedTime = DateTime.Now.ToString("MMM dd, yyyy • h:mm tt");

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

            if (result.PublishedAt.HasValue)
            {
                ReleaseDate = result.PublishedAt.Value.ToString("MMM dd, yyyy");
            }

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
                Manifest = result.Manifest;
                IsUpdateModalOpen = true;
                _notificationService?.Show($"🚀 New update available: v{result.LatestVersion}!");
            }
            else
            {
                // Version matches -> Already Updated -> Open Steam Banner (Image 2 & 3)!
                IsUpdateAvailable = false;
                UpdateStatusTitle = "Already Updated";
                CheckButtonText = "✓ Already Updated";
                IsSteamUpdateBannerOpen = true;
                PatchNotesTitle = $"AxePower Client Update - Version {Version}";
                _ = ResetCheckButtonTextAfterDelay();
            }
        }
        catch (Exception ex)
        {
            IsCheckingForUpdate = false;
            CheckButtonText = "Check for updates";
            _notificationService?.ShowFailure("Check for updates", ex);
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
    public void OpenPatchNotesModal()
    {
        IsSteamPatchNotesModalOpen = true;
        _ = SyncCommunityStatsAsync();
    }

    public async Task SyncCommunityStatsAsync()
    {
        if (_updateService == null) return;
        try
        {
            var stats = await _updateService.GetCommunityStatsAsync();
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                PatchNotesLikes = stats.StarsCount;
                PatchNotesComments = stats.DiscussionsCount;
            });
        }
        catch { /* Offline: the like and comment counts stay hidden. */ }
    }

    [RelayCommand]
    public void ClosePatchNotesModal()
    {
        IsSteamPatchNotesModalOpen = false;
    }

    [RelayCommand]
    public void CloseSteamUpdateBanner()
    {
        IsSteamUpdateBannerOpen = false;
    }

    [RelayCommand]
    public void RateUpPatchNotes()
    {
        if (!IsLiked)
        {
            IsLiked = true;
            PatchNotesLikes++;
            _notificationService?.Show("⭐ Opening GitHub to star AxePower repository!");
        }
        else
        {
            IsLiked = false;
            PatchNotesLikes = Math.Max(0, PatchNotesLikes - 1);
        }
        OpenUrl("https://github.com/mrAbhimanyuVishwakarma/AxePower");
    }

    [RelayCommand]
    public void DiscussPatchNotes()
    {
        OpenUrl("https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues");
    }

    [RelayCommand]
    public void SharePatchNotes()
    {
        try
        {
            Clipboard.SetText("https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases");
            _notificationService?.Show("🔗 Release link copied to clipboard!");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _notificationService?.Show("Could not copy: another app is using the clipboard. Try again.");
        }
    }

    [RelayCommand]
    public void ApplyDownloadedUpdate()
    {
        if (!string.IsNullOrWhiteSpace(DownloadedInstallerPath) && File.Exists(DownloadedInstallerPath))
        {
            // Close only once the installer is running: a refused installer (it changed after it
            // was verified) must not take AxePower down with nothing installed
            if (_updateService?.ApplyUpdate(DownloadedInstallerPath) == true)
                Application.Current?.Shutdown();
            else
                _notificationService?.Show(new AxePower.Core.Services.Outcomes.UserMessage(
                    "The update was not started: the downloaded file could not be verified again. Check for updates to download it afresh.", null, IsFailure: true));
        }
        else if (!string.IsNullOrWhiteSpace(DownloadUrl))
        {
            _ = DownloadAndApplyUpdate();
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
            DownloadStatusText = _updateService.IsDownloadInProgress
                ? "Finishing the background download..."
                : "Downloading " + (AssetName ?? "update installer") + "...";
            _downloadCts = new CancellationTokenSource();

            if (!string.IsNullOrWhiteSpace(DownloadedInstallerPath) && File.Exists(DownloadedInstallerPath))
            {
                DownloadStatusText = "Launching installer...";
                if (_updateService.ApplyUpdate(DownloadedInstallerPath))
                {
                    Application.Current?.Shutdown();
                    return;
                }

                DownloadedInstallerPath = null;
                IsUpdateDownloaded = false;
            }

            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                DownloadStatusText = $"Downloading update... {p:0}%";
            });

            var installerPath = await _updateService.DownloadUpdateAsync(DownloadUrl, progress, _downloadCts.Token, Manifest);
            DownloadedInstallerPath = installerPath;
            IsUpdateDownloaded = true;

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
        catch (UpdateVerificationException ex)
        {
            IsDownloadingUpdate = false;
            DownloadStatusText = "Update rejected: it failed the security check, so nothing was installed.";
            _notificationService?.Show(new AxePower.Core.Services.Outcomes.UserMessage(
                "The update was rejected: it failed AxePower's security check, so nothing was installed. Try again later, or download it from the GitHub releases page.",
                AxePower.Core.Services.Outcomes.OutcomeClassifier.Detail(ex), IsFailure: true));
        }
        catch (Exception ex)
        {
            IsDownloadingUpdate = false;
            DownloadStatusText = "Download failed. Try again or open the release page.";
            _notificationService?.ShowFailure("Download the update", ex);
        }
    }

    private void OnDownloadProgressChanged(object? sender, UpdateDownloadProgressEventArgs e)
    {
        void UpdateUi()
        {
            DownloadProgress = e.Percentage;
            if (IsDownloadingUpdate)
            {
                DownloadStatusText = e.StatusText;
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            UpdateUi();
        }
        else
        {
            dispatcher.BeginInvoke(UpdateUi);
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

    /// <summary>The Terms of Service dialog, beside Privacy Policy in the About page's bottom bar.</summary>
    [ObservableProperty]
    private bool _isTermsOpen;

    [RelayCommand]
    private void OpenTerms() => IsTermsOpen = true;

    [RelayCommand]
    private void CloseTerms() => IsTermsOpen = false;

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
            _notificationService?.ShowFailure("Open the link", ex);
        }
    }
}
