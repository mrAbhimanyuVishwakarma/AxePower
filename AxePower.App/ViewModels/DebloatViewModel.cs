using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.App.Models;
using AxePower.Core.Services;
using AxePower.App.Services;
using AxePower.Core.Services.Operations;

namespace AxePower.App.ViewModels;

public partial class DebloatViewModel : ObservableObject
{
    private readonly IConfirmationPrompts _confirm;
    private readonly DebloatService _debloatService;
    private readonly NotificationService _notificationService;
    private readonly LogService _logService;
    private readonly RestorePointService _restorePointService;

    public ObservableCollection<DebloatToolItem> Tools { get; } = new();

    public bool IsElevated { get; }

    [ObservableProperty]
    private bool _isCreatingRestorePoint;

    [ObservableProperty]
    private string _restorePointStatus = string.Empty;

    public DebloatViewModel(
        DebloatService debloatService,
        NotificationService notificationService,
        LogService logService,
        RestorePointService restorePointService,
        IConfirmationPrompts? confirm = null)
    {
        _confirm = confirm ?? new ConfirmationPrompts();
        _debloatService = debloatService;
        _notificationService = notificationService;
        _logService = logService;
        _restorePointService = restorePointService;

        IsElevated = TerminalService.IsProcessElevated();

        InitializeTools();
    }

    private void InitializeTools()
    {
        Tools.Clear();

        // 1. Win11Debloat by Raphire
        Tools.Add(new DebloatToolItem
        {
            Id = "Win11Debloat",
            Name = "Win11 Debloat",
            LaunchButtonTitle = "Win11 Debloat",
            Author = "Raphire",
            CreditUrl = "https://github.com/Raphire/Win11Debloat",
            IconGlyph = "\uE756",
            Tagline = "Remove unwanted apps and keep Windows 11 fast and clean",
            Description = "Safely removes pre-installed sponsored apps you don't need, stops web search ads from cluttering your Start menu, and turns off background tracking so your computer stays private and snappy.",
            Highlights = new()
            {
                "One-click safe cleanup for pre-installed bloatware and widgets",
                "Stops background tracking and Start menu web advertisements",
                "Includes an easy Restore option if you ever want any app back"
            },
            Tags = new() { "Windows 11", "Privacy", "Clean PC" },
            CommandPreview = "irm https://debloat.raphi.re/ | iex"
        });

        // 2. WinUtil by Chris Titus Tech
        Tools.Add(new DebloatToolItem
        {
            Id = "WinUtil",
            Name = "WinUtil",
            LaunchButtonTitle = "WinUtil",
            Author = "Chris Titus Tech",
            CreditUrl = "https://github.com/ChrisTitusTech/winutil",
            IconGlyph = "\uE90F",
            Tagline = "All-in-one toolbox to tune your PC, install apps, and manage updates",
            Description = "An easy control center that speeds up your computer with safe performance tweaks, lets you install popular apps in a single click, and keeps updates secure without unwanted restarts.",
            Highlights = new()
            {
                "Safe performance tweaks to make your PC feel faster and more responsive",
                "Install your favorite programs in 1 click without opening a web browser",
                "Keeps security updates active while stopping surprise PC restarts"
            },
            Tags = new() { "All-in-One", "Performance", "App Installer" },
            CommandPreview = "irm https://christitus.com/win | iex"
        });
    }

    [RelayCommand]
    public void LaunchTool(DebloatToolItem? tool)
    {
        if (tool == null) return;

        // A third-party script AxePower cannot see into: high risk, irreversible, and said so.
        var highlights = tool.Highlights.Count > 0 ? tool.Highlights : new List<string> { tool.Description };
        if (!_confirm.Confirm(ConfirmationCatalog.RunDebloatScript(tool.Name, tool.Author, highlights, tool.CommandPreview))) return;

        _notificationService.Show($"Opening {tool.Name} by {tool.Author}...");

        bool launched = tool.Id switch
        {
            "Win11Debloat" => _debloatService.LaunchWin11Debloat(),
            "WinUtil"      => _debloatService.LaunchWinUtil(),
            _              => _debloatService.LaunchScript(tool.Name, $"-NoProfile -ExecutionPolicy Bypass -Command \"{tool.CommandPreview}\"", tool.Name)
        };

        if (!launched)
        {
            _notificationService.Show($"{tool.Name} was cancelled or failed to start.");
        }
    }

    [RelayCommand]
    public void OpenCredit(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

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
            _notificationService.ShowFailure("Open the repository page", ex);
        }
    }

    [RelayCommand]
    public void CopyCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;

        try
        {
            Clipboard.SetText(command);
            _notificationService.Show("Command copied to clipboard!");
        }
        catch (Exception ex)
        {
            _notificationService.ShowFailure("Copy to the clipboard", ex);
        }
    }

    [RelayCommand]
    public async Task CreateRestorePointAsync()
    {
        if (IsCreatingRestorePoint) return;

        IsCreatingRestorePoint = true;
        RestorePointStatus = "Creating System Restore checkpoint...";

        try
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var progress = new Progress<string>(msg => _notificationService.Show(msg));
            bool ok = await _restorePointService.CreateRestorePointAsync($"AxePower Debloat Checkpoint — {stamp}", progress);

            if (ok)
            {
                RestorePointStatus = "System Restore checkpoint created.";
                _notificationService.Show("System Restore Point created successfully.");
            }
            else
            {
                RestorePointStatus = "Could not create Restore Point.";
                _notificationService.Show("Could not create Restore Point. Check if System Protection is enabled.");
            }
        }
        catch (Exception ex)
        {
            RestorePointStatus = "Error creating checkpoint.";
            _notificationService.ShowFailure("Create a restore point", ex);
        }
        finally
        {
            IsCreatingRestorePoint = false;
        }
    }
}
