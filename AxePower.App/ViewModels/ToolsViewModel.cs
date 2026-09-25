using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Services;

namespace AxePower.App.ViewModels;

/// <summary>
/// The Tools page: a few well-known free apps, opened when installed or installed from their
/// publisher through WinGet. AxePower never bundles or hosts their files.
/// </summary>
public partial class ToolsViewModel : ObservableObject
{
    private readonly ToolsService _tools;
    private readonly IWinGetHubService? _winget;
    private readonly MsiAfterburnerService? _afterburner;
    private readonly NotificationService? _notifications;

    public IReadOnlyList<ToolGroup> Groups { get; }

    public ToolsViewModel(ToolsService tools, IWinGetHubService? winget = null, MsiAfterburnerService? afterburner = null, NotificationService? notifications = null)
    {
        _tools = tools;
        _winget = winget;
        _afterburner = afterburner;
        _notifications = notifications;

        Groups = ToolsService.Tools
            .GroupBy(t => t.Group)
            .Select(g => new ToolGroup(g.Key, g.Select(t => new ToolCard(t, this)).ToList()))
            .ToList();
    }

    public IEnumerable<ToolCard> Cards => Groups.SelectMany(g => g.Cards);

    /// <summary>Re-checks what is installed and running: when the page opens, after an action, and on "Check again".</summary>
    [RelayCommand]
    public void Refresh()
    {
        foreach (var card in Cards) card.Refresh();
    }

    /// <summary>Only whether each installed app is running. Cheap enough for a timer.</summary>
    public void RefreshRunning()
    {
        foreach (var card in Cards) card.RefreshRunning();
    }

    internal ToolsService Service => _tools;
    internal void Notify(string message) => _notifications?.Show(message);

    internal async Task OpenAsync(ToolCard card)
    {
        if (card.Definition.Id == "afterburner" && _afterburner != null)
        {
            // Same as the old sidebar entry: sets up monitoring and starts RTSS for Game Mode
            var result = await _afterburner.LaunchOrInstallAsync();
            Notify(result switch
            {
                MsiAfterburnerLaunchResult.Launched => "MSI Afterburner opened in the background.",
                MsiAfterburnerLaunchResult.AlreadyRunning => "MSI Afterburner is already running.",
                MsiAfterburnerLaunchResult.InstalledAndLaunched => "MSI Afterburner installed and set up for monitoring.",
                _ => "Could not open MSI Afterburner.",
            });
            return;
        }

        if (card.ExePath == null) return;
        try
        {
            _tools.Launch(card.ExePath);
        }
        catch (Exception ex)
        {
            Notify($"Could not open {card.Definition.Name}: {ex.Message}");
        }
    }

    internal async Task<bool> RunWinGetAsync(ToolCard card, string verb)
    {
        if (_winget == null) return false;
        var progress = new Progress<string>(_ => { });
        return verb switch
        {
            "install" => await _winget.InstallPackageAsync(card.Definition.WinGetId, progress),
            "update" => await _winget.UpgradePackageAsync(card.Definition.WinGetId, progress),
            "uninstall" => await _winget.UninstallPackageAsync(card.Definition.WinGetId, progress),
            _ => false,
        };
    }
}

public sealed record ToolGroup(string Title, IReadOnlyList<ToolCard> Cards);

/// <summary>One app on the Tools page.</summary>
public partial class ToolCard : ObservableObject
{
    private readonly ToolsViewModel _owner;

    public ToolCard(ToolDefinition definition, ToolsViewModel owner)
    {
        Definition = definition;
        _owner = owner;
    }

    public ToolDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Purpose => Definition.Purpose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(PrimaryText), nameof(StatusText))]
    private string? _exePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryText), nameof(StatusText), nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand), nameof(UpdateCommand), nameof(UninstallCommand))]
    private string? _busyText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private ImageSource? _icon;

    public bool IsInstalled => ExePath != null;
    public bool IsIdle => BusyText == null;
    public bool HasIcon => Icon != null;
    public string PrimaryText => BusyText ?? (IsInstalled ? "Open" : "Install");
    public string StatusText => BusyText != null ? BusyText : !IsInstalled ? "Not installed" : IsRunning ? "Running" : "Installed";

    public void Refresh()
    {
        var exe = _owner.Service.FindExe(Definition);
        if (exe != ExePath)
        {
            ExePath = exe;
            Icon = exe != null ? ExeIcon.Load(exe) : null;
        }
        RefreshRunning();
    }

    public void RefreshRunning() => IsRunning = ExePath != null && _owner.Service.IsRunning(ExePath);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task Primary()
    {
        if (IsInstalled)
        {
            await _owner.OpenAsync(this);
            Refresh();
            return;
        }
        if (Definition.Id == "afterburner")
        {
            // Afterburner's own installer path also applies AxePower's monitoring preset
            BusyText = "Installing...";
            try { await _owner.OpenAsync(this); } finally { BusyText = null; Refresh(); }
            return;
        }
        await RunAsync("install", "Installing...", $"{Name} installed.", $"{Name} couldn't be installed. Its official download page is open instead.");
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Update() => RunAsync("update", "Updating...", $"{Name} is up to date.", $"{Name} couldn't be updated.");

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task Uninstall()
    {
        var answer = MessageBox.Show($"Uninstall {Name}?", "AxePower", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        await RunAsync("uninstall", "Uninstalling...", $"{Name} uninstalled.", $"{Name} couldn't be uninstalled.");
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (ExePath != null) _owner.Service.OpenFolder(ExePath);
    }

    [RelayCommand]
    private void OpenWebsite()
    {
        try { Process.Start(new ProcessStartInfo { FileName = Definition.WebUrl, UseShellExecute = true }); } catch { }
    }

    private async Task RunAsync(string verb, string busy, string done, string failed)
    {
        BusyText = busy;
        try
        {
            bool ok = await _owner.RunWinGetAsync(this, verb);
            _owner.Notify(ok ? done : failed);
        }
        finally
        {
            BusyText = null;
            Refresh();
        }
    }
}

/// <summary>The icon inside an app's program file, so each card shows the app's own icon.</summary>
internal static class ExeIcon
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    public static ImageSource? Load(string exePath)
    {
        var large = new IntPtr[1];
        try
        {
            if (ExtractIconEx(exePath, 0, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
        }
    }
}
