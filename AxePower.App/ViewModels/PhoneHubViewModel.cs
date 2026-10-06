using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using AxePower.Core.Helpers;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.Core.Services.LanDrop;

namespace AxePower.App.ViewModels;

public partial class PhoneHubViewModel : ObservableObject
{
    private readonly LanDropService _lanDropService;
    private readonly AndroidControlService _androidControlService;
    private readonly NotificationService _notificationService;
    private readonly IWinGetHubService? _winGetHubService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpaceDeskButtonText))]
    private bool _isSpaceDeskInstalled;

    [ObservableProperty]
    private bool _isSpaceDeskBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLanDropTabActive))]
    [NotifyPropertyChangedFor(nameof(IsScreenMirrorTabActive))]
    [NotifyPropertyChangedFor(nameof(IsSecondMonitorTabActive))]
    [NotifyPropertyChangedFor(nameof(IsPhoneLinkTabActive))]
    private string _activeTab = "ScreenMirror"; // "ScreenMirror", "SecondMonitor", "LanDrop", "PhoneLink"

    public bool IsLanDropTabActive => ActiveTab == "LanDrop";
    public bool IsScreenMirrorTabActive => ActiveTab == "ScreenMirror";
    public bool IsSecondMonitorTabActive => ActiveTab == "SecondMonitor";
    public bool IsPhoneLinkTabActive => ActiveTab == "PhoneLink";
    public string SpaceDeskButtonText => IsSpaceDeskInstalled ? "Launch SpaceDesk Console" : "Install SpaceDesk Driver";

    // ─── 1. LAN Drop State ─────────────────────────────────────────────
    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private string _selectedIpAddress = string.Empty;

    [ObservableProperty]
    private int _portNumber = 5050;

    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    private ImageSource? _qrCodeImage;

    [ObservableProperty]
    private string _quickTextInput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready. Start server to share files with mobile devices.";

    public ObservableCollection<string> AvailableIpAddresses { get; } = new();
    public ObservableCollection<LanDropFileItem> ReceivedFiles { get; } = new();
    public ObservableCollection<LanDropFileItem> SharedFiles { get; } = new();
    public ObservableCollection<LanDropTextMessage> ClipboardMessages { get; } = new();
    public ObservableCollection<LanDropClientSession> ConnectedClients { get; } = new();

    /// <summary>Shown while the selected adapter's network is Public or cannot be classified.</summary>
    [ObservableProperty]
    private bool _isPublicNetworkWarningVisible;

    [ObservableProperty]
    private string _networkWarningText = string.Empty;

    /// <summary>True after a start was refused on an untrusted network, offering a one-session override.</summary>
    [ObservableProperty]
    private bool _canStartOnUntrustedNetwork;

    public int ConnectedClientsCount => ConnectedClients.Count;
    public bool HasConnectedClients => ConnectedClients.Count > 0;

    public int ReceivedCount => ReceivedFiles.Count;
    public int SharedCount => SharedFiles.Count;
    public bool HasReceivedFiles => ReceivedFiles.Count > 0;
    public bool HasSharedFiles => SharedFiles.Count > 0;

    // ─── 2. Android Screen Control (scrcpy) State ──────────────────────
    [ObservableProperty]
    private bool _stayAwake = true;

    [ObservableProperty]
    private bool _turnScreenOff = false;

    [ObservableProperty]
    private bool _forwardAudio = true;

    [ObservableProperty]
    private string _selectedResolution = "1080p (Balanced)";

    [ObservableProperty]
    private string _selectedFps = "60 FPS (Default)";

    [ObservableProperty]
    private string _wirelessIpAddress = string.Empty;

    [ObservableProperty]
    private bool _isDetectingDevices;

    [ObservableProperty]
    private bool _isLaunchingScrcpy;

    [ObservableProperty]
    private AdbDeviceInfo? _selectedDevice;

    public ObservableCollection<AdbDeviceInfo> ConnectedDevices { get; } = new();
    public bool HasConnectedDevices => ConnectedDevices.Count > 0;

    public ObservableCollection<string> AvailableResolutions { get; } = new()
    {
        "Native (Best Quality)",
        "1080p (Balanced)",
        "720p (Ultra Low Latency)"
    };

    public ObservableCollection<string> AvailableFps { get; } = new()
    {
        "60 FPS (Default)",
        "120 FPS (High Refresh)",
        "30 FPS (Power Saver)"
    };

    public PhoneHubViewModel(
        LanDropService lanDropService,
        AndroidControlService androidControlService,
        NotificationService notificationService,
        IWinGetHubService? winGetHubService = null)
    {
        _lanDropService = lanDropService;
        _androidControlService = androidControlService;
        _notificationService = notificationService;
        _winGetHubService = winGetHubService;

        SaveDirectory = _lanDropService.DownloadDirectory;

        RefreshIpList();
        CheckSpaceDeskInstalled();
        UpdateQrCode();
        RefreshNetworkClassification();

        _lanDropService.OnClientsChanged += (s, e) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(RefreshConnectedClients);
        };

        // Subscribe to LAN Drop events
        _lanDropService.OnFileReceived += (s, item) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                ReceivedFiles.Insert(0, item);
                OnPropertyChanged(nameof(ReceivedCount));
                OnPropertyChanged(nameof(HasReceivedFiles));
                _notificationService.Show($"📥 Received '{item.FileName}' from {item.DeviceName}");
            });
        };

        _lanDropService.OnTextReceived += (s, msg) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                ClipboardMessages.Insert(0, msg);
                _notificationService.Show($"📋 Quick Text received from {msg.SenderDevice}");
            });
        };

        _lanDropService.OnStateChanged += (s, state) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsServerRunning = state.IsRunning;
                if (!string.IsNullOrEmpty(state.ServerUrl))
                {
                    ServerUrl = state.ServerUrl;
                    // The QR and the copy button hand out the link that actually works: the
                    // plain address is refused by the portal without its access token.
                    PortalUrl = string.IsNullOrEmpty(state.PortalUrl) ? state.ServerUrl : state.PortalUrl;
                }
                UpdateQrCode();
                RefreshNetworkClassification();
                RefreshConnectedClients();
                StatusMessage = IsServerRunning
                    ? $"Server active on {ServerUrl} — Scan QR code on your phone"
                    : _lanDropService.LastRefusedNetwork is not null
                        ? "Not started: Windows treats this network as Public or cannot classify it."
                        : "Server stopped.";
            });
        };

        // Initial device detection
        _ = RefreshDevicesAsync();
    }

    [RelayCommand]
    public void SetTab(string tabName)
    {
        ActiveTab = tabName;
        if (tabName == "LanDrop")
        {
            UpdateQrCode();
            if (!IsServerRunning)
            {
                _ = _lanDropService.StartServerAsync(SelectedIpAddress, PortNumber);
            }
        }
        else if (tabName == "ScreenMirror")
        {
            _ = RefreshDevicesAsync();
        }
    }

    private void RefreshIpList()
    {
        AvailableIpAddresses.Clear();
        var ips = LanDropService.GetLocalIpAddresses();
        foreach (var ip in ips)
        {
            AvailableIpAddresses.Add(ip);
        }

        SelectedIpAddress = AvailableIpAddresses.FirstOrDefault() ?? "127.0.0.1";
        ServerUrl = $"http://{SelectedIpAddress}:{PortNumber}/";
    }

    partial void OnSelectedIpAddressChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            ServerUrl = $"http://{value}:{PortNumber}/";
            UpdateQrCode();
            RefreshNetworkClassification();
            if (IsServerRunning)
            {
                _ = _lanDropService.StartServerAsync(value, PortNumber);
            }
        }
    }

    partial void OnPortNumberChanged(int value)
    {
        if (!string.IsNullOrEmpty(SelectedIpAddress))
        {
            ServerUrl = $"http://{SelectedIpAddress}:{value}/";
            UpdateQrCode();
            if (IsServerRunning)
            {
                _ = _lanDropService.StartServerAsync(SelectedIpAddress, value);
            }
        }
    }

    private void UpdateQrCode()
    {
        if (!string.IsNullOrEmpty(ServerUrl))
        {
            try
            {
                // Pure high-contrast black QR on white background (0x00000000 on 0x00FFFFFF) with 4-module quiet zone
                byte[] bmpBytes = QrCodeGenerator.GenerateBmpBytes(ShareLink, 8, 4, 0x00000000, 0x00FFFFFF);
                using var ms = new MemoryStream(bmpBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
                QrCodeImage = bitmap;
            }
            catch
            {
                QrCodeImage = null;
            }
        }
    }

    // ─── LAN Drop Commands ─────────────────────────────────────────────

    [RelayCommand]
    public async Task ToggleServerAsync()
    {
        if (IsServerRunning)
        {
            _lanDropService.StopServer();
            _notificationService.Show("LAN Drop server stopped.");
        }
        else
        {
            StatusMessage = "Starting local web server...";
            bool started = await _lanDropService.StartServerAsync(SelectedIpAddress, PortNumber);
            if (started)
            {
                _notificationService.Show($"🚀 LAN Drop active at {ServerUrl}");
            }
            else if (_lanDropService.LastRefusedNetwork is not null)
            {
                StatusMessage = "Not started: Windows treats this network as Public or cannot classify it.";
                _notificationService.Show("⚠️ LAN Drop did not start on an untrusted network.");
            }
            else
            {
                StatusMessage = "Failed to start server. Try selecting a different port or network adapter.";
                _notificationService.Show("❌ Failed to start LAN Drop server.");
            }
        }
    }

    /// <summary>
    /// Starts on a Public or unclassified network after the user has seen the warning. The
    /// override applies to this server session only; the next start is refused again.
    /// </summary>
    [RelayCommand]
    public async Task StartOnUntrustedNetworkAsync()
    {
        StatusMessage = "Starting local web server on an untrusted network...";
        bool started = await _lanDropService.StartServerAsync(SelectedIpAddress, PortNumber, allowUntrustedNetwork: true);
        if (started)
        {
            _notificationService.Show($"⚠️ LAN Drop active at {ServerUrl} on an untrusted network for this session.");
        }
        else
        {
            StatusMessage = "Failed to start server. Try selecting a different port or network adapter.";
            _notificationService.Show("❌ Failed to start LAN Drop server.");
        }
    }

    [RelayCommand]
    public void DisconnectAllClients()
    {
        _lanDropService.DisconnectAllClientsAndRotateAccess();
        RefreshConnectedClients();
        _notificationService.Show("🔒 Disconnected all devices. Phones need the new QR code to reconnect.");
    }

    [RelayCommand]
    public void DisconnectClient(LanDropClientSession? client)
    {
        if (client == null) return;
        _lanDropService.DisconnectClient(client.Id);
        RefreshConnectedClients();
        _notificationService.Show($"Disconnected {client.DeviceName}.");
    }

    private void RefreshConnectedClients()
    {
        ConnectedClients.Clear();
        foreach (var client in _lanDropService.GetConnectedClients())
        {
            ConnectedClients.Add(client);
        }
        OnPropertyChanged(nameof(ConnectedClientsCount));
        OnPropertyChanged(nameof(HasConnectedClients));
    }

    public void RefreshNetworkClassification()
    {
        var classification = _lanDropService.IsRunning || string.IsNullOrEmpty(SelectedIpAddress)
            ? _lanDropService.CurrentNetworkClassification
            : NetworkProfileClassifier.ClassifyCurrentNetwork(SelectedIpAddress);
        IsPublicNetworkWarningVisible = classification.IsPublicOrUnclassified;
        NetworkWarningText = classification.WarningMessage ?? string.Empty;
        CanStartOnUntrustedNetwork = !_lanDropService.IsRunning && _lanDropService.LastRefusedNetwork is not null;
    }

    [RelayCommand]
    public void AddFilesToShare()
    {
        var ofd = new OpenFileDialog
        {
            Title = "Select files to share with mobile devices",
            Multiselect = true
        };

        if (ofd.ShowDialog() == true)
        {
            foreach (var file in ofd.FileNames)
            {
                _lanDropService.AddSharedFile(file);
            }
            RefreshSharedFilesList();
            _notificationService.Show($"Added {ofd.FileNames.Length} file(s) for mobile download.");
        }
    }

    [RelayCommand]
    public void RemoveSharedFile(LanDropFileItem? item)
    {
        if (item == null) return;
        _lanDropService.RemoveSharedFile(item.Id);
        RefreshSharedFilesList();
    }

    [RelayCommand]
    public void ClearSharedFiles()
    {
        _lanDropService.ClearSharedFiles();
        RefreshSharedFilesList();
        _notificationService.Show("Cleared all shared files.");
    }

    private void RefreshSharedFilesList()
    {
        SharedFiles.Clear();
        foreach (var item in _lanDropService.GetSharedFiles())
        {
            SharedFiles.Add(item);
        }
        OnPropertyChanged(nameof(SharedCount));
        OnPropertyChanged(nameof(HasSharedFiles));
    }

    [RelayCommand]
    public void OpenReceivedFile(LanDropFileItem? item)
    {
        if (item == null || !File.Exists(item.FilePath))
        {
            _notificationService.Show("File not found on disk.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.FilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notificationService.Show("Could not open file: " + ex.Message);
        }
    }

    [RelayCommand]
    public void ShowReceivedFileInFolder(LanDropFileItem? item)
    {
        if (item == null || !File.Exists(item.FilePath))
        {
            OpenSaveDirectory();
            return;
        }

        try
        {
            Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
        }
        catch
        {
            OpenSaveDirectory();
        }
    }

    [RelayCommand]
    public void DeleteReceivedFile(LanDropFileItem? item)
    {
        if (item == null) return;

        try
        {
            if (File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }
            ReceivedFiles.Remove(item);
            OnPropertyChanged(nameof(ReceivedCount));
            OnPropertyChanged(nameof(HasReceivedFiles));
            _notificationService.Show($"Deleted {item.FileName}");
        }
        catch (Exception ex)
        {
            _notificationService.Show("Error deleting file: " + ex.Message);
        }
    }

    [RelayCommand]
    public void ClearReceivedFiles()
    {
        ReceivedFiles.Clear();
        _lanDropService.ClearReceivedFiles();
        OnPropertyChanged(nameof(ReceivedCount));
        OnPropertyChanged(nameof(HasReceivedFiles));
        _notificationService.Show("Cleared received history.");
    }

    [RelayCommand]
    public void OpenSaveDirectory()
    {
        try
        {
            if (!Directory.Exists(SaveDirectory))
            {
                Directory.CreateDirectory(SaveDirectory);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = SaveDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notificationService.Show("Could not open folder: " + ex.Message);
        }
    }

    /// <summary>
    /// The link with the access token, when the server has published one. Everything handed to
    /// a phone — the QR code, the copy button, "open in browser" — uses this, because the
    /// portal refuses a request that does not carry the token.
    /// </summary>
    public string ShareLink => string.IsNullOrEmpty(PortalUrl) ? ServerUrl : PortalUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareLink))]
    private string _portalUrl = string.Empty;

    [RelayCommand]
    public void CopyServerUrl()
    {
        if (string.IsNullOrEmpty(ServerUrl)) return;
        try
        {
            Clipboard.SetText(ShareLink);
            _notificationService.Show("📋 Copied Server URL to clipboard!");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another app holds the clipboard. Say so; never log what was being copied.
            _notificationService.Show("Could not copy: another app is using the clipboard. Try again.");
        }
    }

    [RelayCommand]
    public void OpenInBrowser()
    {
        if (string.IsNullOrEmpty(ServerUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ShareLink,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notificationService.Show("Failed to open browser: " + ex.Message);
        }
    }

    [RelayCommand]
    public void SendQuickText()
    {
        if (string.IsNullOrWhiteSpace(QuickTextInput)) return;
        _lanDropService.PostTextMessageFromPc(QuickTextInput);
        QuickTextInput = string.Empty;
        _notificationService.Show("Sent text to LAN Drop web portal!");
    }

    [RelayCommand]
    public void CopyMessageText(LanDropTextMessage? msg)
    {
        if (msg == null || string.IsNullOrEmpty(msg.Content)) return;
        try
        {
            Clipboard.SetText(msg.Content);
            _notificationService.Show("📋 Copied message to clipboard!");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another app holds the clipboard. Say so; never log what was being copied.
            _notificationService.Show("Could not copy: another app is using the clipboard. Try again.");
        }
    }

    // ─── Android Screen Mirroring Commands ─────────────────────────────

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        IsDetectingDevices = true;
        try
        {
            var list = await _androidControlService.GetConnectedDevicesAsync();
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                ConnectedDevices.Clear();
                foreach (var d in list)
                {
                    ConnectedDevices.Add(d);
                }
                SelectedDevice = ConnectedDevices.FirstOrDefault();
                OnPropertyChanged(nameof(HasConnectedDevices));
            });
        }
        finally
        {
            IsDetectingDevices = false;
        }
    }

    [RelayCommand]
    public async Task LaunchScrcpyAsync()
    {
        IsLaunchingScrcpy = true;
        _notificationService.Show("Launching Android Control (scrcpy)...");

        try
        {
            int maxFps = SelectedFps.Contains("120") ? 120 :
                         SelectedFps.Contains("30") ? 30 : 60;

            int maxSize = SelectedResolution.Contains("720") ? 720 :
                          SelectedResolution.Contains("1080") ? 1080 : 0;

            var options = new AndroidControlOptions
            {
                StayAwake = StayAwake,
                TurnScreenOff = TurnScreenOff,
                ForwardAudio = ForwardAudio,
                MaxFps = maxFps,
                MaxSize = maxSize,
                SerialOrIp = SelectedDevice?.Serial
            };

            var result = await _androidControlService.LaunchOrInstallAsync(options);
            switch (result)
            {
                case AndroidControlLaunchResult.Launched:
                    _notificationService.Show("Android Control (scrcpy) launched successfully.");
                    break;
                case AndroidControlLaunchResult.AlreadyRunning:
                    _notificationService.Show("Android Control is already running. Showing window.");
                    break;
                case AndroidControlLaunchResult.DownloadedAndLaunched:
                    _notificationService.Show("Latest scrcpy downloaded & launched successfully.");
                    break;
                case AndroidControlLaunchResult.Failed:
                    _notificationService.Show("Could not launch Android Control. Ensure your Android device is connected with USB debugging enabled.");
                    break;
            }
        }
        finally
        {
            IsLaunchingScrcpy = false;
        }
    }

    [RelayCommand]
    public async Task ConnectWirelessAdbAsync()
    {
        if (string.IsNullOrWhiteSpace(WirelessIpAddress))
        {
            _notificationService.Show("Enter your phone's IP:Port (e.g. 192.168.1.50:5555)");
            return;
        }

        _notificationService.Show($"Connecting to {WirelessIpAddress}...");
        bool ok = await _androidControlService.ConnectWirelessAdbAsync(WirelessIpAddress);
        if (ok)
        {
            _notificationService.Show($"Connected to {WirelessIpAddress} wirelessly!");
            await RefreshDevicesAsync();
        }
        else
        {
            _notificationService.Show("Failed to connect wirelessly. Ensure Wireless Debugging is enabled.");
        }
    }

    [RelayCommand]
    public async Task InstallApkAsync()
    {
        var ofd = new OpenFileDialog
        {
            Title = "Select APK to install on Android phone",
            Filter = "Android Package (*.apk)|*.apk|All Files (*.*)|*.*"
        };

        if (ofd.ShowDialog() == true)
        {
            _notificationService.Show($"Installing {Path.GetFileName(ofd.FileName)} on device...");
            bool ok = await _androidControlService.InstallApkAsync(ofd.FileName, SelectedDevice?.Serial);
            if (ok)
            {
                _notificationService.Show($"✅ Successfully installed {Path.GetFileName(ofd.FileName)}!");
            }
            else
            {
                _notificationService.Show($"❌ Failed to install {Path.GetFileName(ofd.FileName)}.");
            }
        }
    }

    public void CheckSpaceDeskInstalled()
    {
        IsSpaceDeskInstalled = GetSpaceDeskExePath() != null;
    }

    public static string? GetSpaceDeskExePath()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "datronicsoft", "spacedesk", "spacedeskServiceTray.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "datronicsoft", "spacedesk", "spacedeskDriverConsole.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "datronicsoft", "spacedesk", "spacedeskSystemTray.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "datronicsoft", "spacedesk", "spacedeskServiceTray.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "datronicsoft", "spacedesk", "spacedeskDriverConsole.exe")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\spacedesk Windows DRIVER")
                         ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\spacedesk Windows DRIVER")
                         ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\datronicsoft\spacedesk");
            if (key != null)
            {
                var installLoc = key.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(installLoc))
                {
                    string tray = Path.Combine(installLoc, "spacedeskServiceTray.exe");
                    if (File.Exists(tray)) return tray;
                    string console = Path.Combine(installLoc, "spacedeskDriverConsole.exe");
                    if (File.Exists(console)) return console;
                }
            }
        }
        catch (Exception caught) when (AxePower.Core.Services.ExpectedFailures.Registry(caught)) { /* Not found in the registry: the folder search below runs next. */ }

        return null;
    }

    [RelayCommand]
    public async Task SpaceDeskActionAsync()
    {
        if (IsSpaceDeskBusy) return;

        IsSpaceDeskBusy = true;
        try
        {
            string? exe = GetSpaceDeskExePath();
            if (!string.IsNullOrEmpty(exe))
            {
                // Open SpaceDesk
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = true
                    });
                    _notificationService.Show("🖥️ Opening SpaceDesk Driver Console...");
                }
                catch (Exception ex)
                {
                    _notificationService.Show($"Error opening SpaceDesk: {ex.Message}");
                }
                return;
            }

            // Not installed -> Install via WinGet or Fallback to Web
            _notificationService.Show("⬇️ Installing SpaceDesk Windows Driver via WinGet...");

            bool installed = false;
            if (_winGetHubService != null)
            {
                var progress = new Progress<string>(_ => { });
                installed = await _winGetHubService.InstallPackageAsync("Datronicsoft.SpacedeskDriver.Server", progress);
            }
            else
            {
                // Fallback direct execution via winget
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "winget.exe",
                        Arguments = "install --id Datronicsoft.SpacedeskDriver.Server --source winget --accept-source-agreements --accept-package-agreements --silent",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        await p.WaitForExitAsync();
                        installed = p.ExitCode == 0;
                    }
                }
                catch { /* winget unavailable: installed stays false and the user gets the download page. */ }
            }

            CheckSpaceDeskInstalled();
            if (IsSpaceDeskInstalled || installed)
            {
                IsSpaceDeskInstalled = true;
                _notificationService.Show("✓ SpaceDesk installed successfully! Launching...");
                string? launchedExe = GetSpaceDeskExePath();
                if (!string.IsNullOrEmpty(launchedExe))
                {
                    Process.Start(new ProcessStartInfo { FileName = launchedExe, UseShellExecute = true });
                }
            }
            else
            {
                // Open official website in browser
                _notificationService.Show("Opening SpaceDesk download page in browser...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.spacedesk.net/",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            _notificationService.Show($"SpaceDesk: {ex.Message}");
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "https://www.spacedesk.net/", UseShellExecute = true });
            }
                        catch (Exception openError) when (openError is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _notificationService.Show("Could not open the SpaceDesk website. Open it from your browser instead.");
            }
        }
        finally
        {
            IsSpaceDeskBusy = false;
            CheckSpaceDeskInstalled();
        }
    }

    [ObservableProperty]
    private bool _isQrModalOpen;

    [ObservableProperty]
    private string _qrModalTitle = string.Empty; // ShowDownloadQr always assigns the modal fields before opening

    [ObservableProperty]
    private string _qrModalSubtitle = string.Empty;

    [ObservableProperty]
    private string _qrModalUrl = string.Empty;

    [ObservableProperty]
    private string _qrModalBadge = string.Empty;

    [ObservableProperty]
    private ImageSource? _qrModalImage;

    [RelayCommand]
    public void ShowDownloadQr(string platform)
    {
        switch (platform?.ToLowerInvariant())
        {
            case "android":
                QrModalTitle = "📱 SpaceDesk for Android";
                QrModalSubtitle = "Scan this QR code with your phone's camera to install directly from the Google Play Store.";
                QrModalUrl = "https://play.google.com/store/apps/details?id=ph.spacedesk.beta";
                QrModalBadge = "GOOGLE PLAY";
                break;
            case "ios":
                QrModalTitle = "🍎 SpaceDesk for iOS (iPhone & iPad)";
                QrModalSubtitle = "Scan this QR code with your iPhone or iPad camera to install directly from the Apple App Store.";
                QrModalUrl = "https://apps.apple.com/in/app/spacedesk-usb-display-for-pc/id1069217220";
                QrModalBadge = "APP STORE";
                break;
            case "phonelink_android":
                QrModalTitle = "📱 Link to Windows (Android)";
                QrModalSubtitle = "Scan this QR code with your Android phone to install 'Link to Windows' from Google Play.";
                QrModalUrl = "https://play.google.com/store/apps/details?id=com.microsoft.appmanager";
                QrModalBadge = "GOOGLE PLAY";
                break;
            case "phonelink_ios":
                QrModalTitle = "🍎 Link to Windows (iPhone)";
                QrModalSubtitle = "Scan this QR code with your iPhone to install 'Link to Windows' from Apple App Store.";
                QrModalUrl = "https://apps.apple.com/in/app/link-to-windows/id6443686328";
                QrModalBadge = "APP STORE";
                break;
            case "web":
            default:
                QrModalTitle = "🌐 SpaceDesk HTML5 Web Viewer";
                QrModalSubtitle = "Scan to open the browser-based second display viewer directly on any device without installing any app.";
                QrModalUrl = "https://viewer.spacedesk.net/";
                QrModalBadge = "WEB VIEWER";
                break;
        }

        QrModalImage = GenerateQrImage(QrModalUrl);
        IsQrModalOpen = true;
    }

    [RelayCommand]
    public void LaunchPhoneLink()
    {
        try
        {
            _notificationService.Show("Opening Windows Phone Link...");
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-phonelink:",
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-yourphone:",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _notificationService.Show("Could not launch Phone Link: " + ex.Message);
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://aka.ms/phonelink",
                        UseShellExecute = true
                    });
                }
                                catch (Exception openError) when (openError is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _notificationService.Show("Could not open the Phone Link page. Open it from your browser instead.");
                }
            }
        }
    }

    [RelayCommand]
    public void CloseQrModal()
    {
        IsQrModalOpen = false;
    }

    [RelayCommand]
    public void OpenQrModalUrl()
    {
        if (!string.IsNullOrWhiteSpace(QrModalUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = QrModalUrl, UseShellExecute = true });
            }
                        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _notificationService.Show("Could not open the link. Open it from your browser instead.");
            }
        }
    }

    private static ImageSource? GenerateQrImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            byte[] bmpBytes = QrCodeGenerator.GenerateBmpBytes(url, 8, 4, 0x00000000, 0x00FFFFFF);
            using var stream = new MemoryStream(bmpBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    public void OpenSpaceDeskWebsite()
    {
        try { Process.Start(new ProcessStartInfo { FileName = "https://www.spacedesk.net/", UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _notificationService.Show("Could not open the SpaceDesk website.");
        }
    }

    [RelayCommand]
    public void OpenWindowsDisplaySettings()
    {
        try { Process.Start(new ProcessStartInfo { FileName = "ms-settings:display", UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _notificationService.Show("Could not open Display settings.");
        }
    }
}
