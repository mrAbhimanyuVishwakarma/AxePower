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

namespace AxePower.App.ViewModels;

public partial class PhoneHubViewModel : ObservableObject
{
    private readonly LanDropService _lanDropService;
    private readonly AndroidControlService _androidControlService;
    private readonly NotificationService _notificationService;
    private readonly IWinGetHubService? _winGetHubService;

    [ObservableProperty]
    private bool _isSpaceDeskInstalled;

    [ObservableProperty]
    private bool _isSpaceDeskBusy;

    [ObservableProperty]
    private string _activeTab = "LanDrop"; // "LanDrop", "ScreenMirror", "RemoteControls"

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

        // Subscribe to LAN Drop events
        _lanDropService.OnFileReceived += (s, item) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ReceivedFiles.Insert(0, item);
                OnPropertyChanged(nameof(ReceivedCount));
                OnPropertyChanged(nameof(HasReceivedFiles));
                _notificationService.Show($"📥 Received '{item.FileName}' from {item.DeviceName}");
            });
        };

        _lanDropService.OnTextReceived += (s, msg) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ClipboardMessages.Insert(0, msg);
                _notificationService.Show($"📋 Quick Text received from {msg.SenderDevice}");
            });
        };

        _lanDropService.OnStateChanged += (s, state) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsServerRunning = state.IsRunning;
                ServerUrl = state.ServerUrl;
                UpdateQrCode();
                StatusMessage = IsServerRunning
                    ? $"Server active on {ServerUrl} — Scan QR code on your phone"
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
        if (tabName == "ScreenMirror")
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
        if (!IsServerRunning && !string.IsNullOrEmpty(value))
        {
            ServerUrl = $"http://{value}:{PortNumber}/";
        }
    }

    partial void OnPortNumberChanged(int value)
    {
        if (!IsServerRunning && !string.IsNullOrEmpty(SelectedIpAddress))
        {
            ServerUrl = $"http://{SelectedIpAddress}:{value}/";
        }
    }

    private void UpdateQrCode()
    {
        if (IsServerRunning && !string.IsNullOrEmpty(ServerUrl))
        {
            try
            {
                byte[] bmpBytes = QrCodeGenerator.GenerateBmpBytes(ServerUrl, 8, 3, 0x0000F0FF, 0x000B101D);
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
        else
        {
            QrCodeImage = null;
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
            else
            {
                StatusMessage = "Failed to start server. Try selecting a different port or network adapter.";
                _notificationService.Show("❌ Failed to start LAN Drop server.");
            }
        }
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

    [RelayCommand]
    public void CopyServerUrl()
    {
        if (string.IsNullOrEmpty(ServerUrl)) return;
        try
        {
            Clipboard.SetText(ServerUrl);
            _notificationService.Show("📋 Copied Server URL to clipboard!");
        }
        catch { }
    }

    [RelayCommand]
    public void OpenInBrowser()
    {
        if (string.IsNullOrEmpty(ServerUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ServerUrl,
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
        catch { }
    }

    // ─── Android Screen Mirroring Commands ─────────────────────────────

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        IsDetectingDevices = true;
        try
        {
            var list = await _androidControlService.GetConnectedDevicesAsync();
            Application.Current?.Dispatcher.Invoke(() =>
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
        catch { }

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
                // Fallback direct winget execution
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "winget.exe",
                        Arguments = "install --id Datronicsoft.SpacedeskDriver.Server --source winget --accept-source-agreements --accept-package-agreements --disable-interactivity",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(35000);
                    installed = (p?.ExitCode == 0);
                }
                catch { }
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
            catch { }
        }
        finally
        {
            IsSpaceDeskBusy = false;
            CheckSpaceDeskInstalled();
        }
    }
}
