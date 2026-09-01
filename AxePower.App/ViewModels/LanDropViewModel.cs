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

public partial class LanDropViewModel : ObservableObject
{
    private readonly LanDropService _lanDropService;
    private readonly NotificationService _notificationService;

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

    public LanDropViewModel(LanDropService lanDropService, NotificationService notificationService)
    {
        _lanDropService = lanDropService;
        _notificationService = notificationService;

        SaveDirectory = _lanDropService.DownloadDirectory;

        // Initialize IP choices
        RefreshIpList();
        UpdateQrCode();

        // Subscribe to service events
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
                }
                UpdateQrCode();
                StatusMessage = IsServerRunning
                    ? $"Server active on {ServerUrl} — Scan QR code on your phone"
                    : "Server stopped.";
            });
        };
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
            RestartServerIfRunning();
            UpdateQrCode();
        }
    }

    partial void OnPortNumberChanged(int value)
    {
        if (!string.IsNullOrEmpty(SelectedIpAddress))
        {
            ServerUrl = $"http://{SelectedIpAddress}:{value}/";
            RestartServerIfRunning();
            UpdateQrCode();
        }
    }

    // The advertised URL/QR must always match the endpoint the server is bound to
    private void RestartServerIfRunning()
    {
        if (!IsServerRunning) return;
        _lanDropService.StopServer();
        _ = _lanDropService.StartServerAsync(SelectedIpAddress, PortNumber);
    }

    private void UpdateQrCode()
    {
        // Never show a scannable QR for an endpoint nobody is listening on
        if (!IsServerRunning)
        {
            QrCodeImage = null;
            return;
        }

        if (!string.IsNullOrEmpty(ServerUrl))
        {
            try
            {
                byte[] bmpBytes = QrCodeGenerator.GenerateBmpBytes(ServerUrl, 8, 4, 0x00000000, 0x00FFFFFF);
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
}
