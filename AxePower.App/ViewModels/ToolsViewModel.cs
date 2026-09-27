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
using AxePower.Core.Services.Routines;

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

    public const string TimerAndRoutines = "Timer & Routines";

    /// <summary>Every group in page order: the app groups, then AxePower's own Timer &amp; Routines.</summary>
    public IReadOnlyList<ToolGroup> Groups { get; }

    /// <summary>The page's two columns. Each group goes, in order, to whichever column has fewer cards so far.</summary>
    public IReadOnlyList<ToolGroup> LeftColumn { get; }
    public IReadOnlyList<ToolGroup> RightColumn { get; }

    public TimerCard TimerCard { get; }
    public RoutinesCard RoutinesCard { get; }

    /// <summary>Set by the main window: opens another page, such as Routines.</summary>
    public Action<string>? NavigateTo { get; set; }

    public ToolsViewModel(ToolsService tools, IWinGetHubService? winget = null, MsiAfterburnerService? afterburner = null, NotificationService? notifications = null,
        PowerTimerViewModel? timer = null, SettingsService? settings = null, RoutineEngine? routines = null)
    {
        _tools = tools;
        _winget = winget;
        _afterburner = afterburner;
        _notifications = notifications;
        TimerCard = new TimerCard(timer);
        RoutinesCard = new RoutinesCard(settings, routines, () => NavigateTo?.Invoke("Routines"));

        var groups = ToolsService.Tools
            .GroupBy(t => t.Group)
            .OrderBy(g => ToolsService.GroupOrder.ToList().IndexOf(g.Key) is var i && i < 0 ? int.MaxValue : i)
            .Select(g => new ToolGroup(g.Key, g.Select(t => (object)new ToolCard(t, this)).ToList()))
            .ToList();
        groups.Add(new ToolGroup(TimerAndRoutines, new object[] { TimerCard, RoutinesCard }));
        Groups = groups;

        var left = new List<ToolGroup>();
        var right = new List<ToolGroup>();
        foreach (var group in Groups)
        {
            var shorter = left.Sum(g => g.Items.Count) <= right.Sum(g => g.Items.Count) ? left : right;
            shorter.Add(group);
        }
        LeftColumn = left;
        RightColumn = right;
    }

    public IEnumerable<ToolCard> Cards => Groups.SelectMany(g => g.Cards);

    /// <summary>Re-checks what is installed and running: when the page opens, after an action, and on "Check again".</summary>
    [RelayCommand]
    public void Refresh()
    {
        foreach (var card in Cards) card.Refresh();
        RoutinesCard.Refresh();
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

/// <summary>A titled stack of cards: app cards, or AxePower's own Timer and Routines cards.</summary>
public sealed record ToolGroup(string Title, IReadOnlyList<object> Items)
{
    public IEnumerable<ToolCard> Cards => Items.OfType<ToolCard>();
}

/// <summary>The power timer's card: its live status, and a button that opens its panel.</summary>
public sealed partial class TimerCard
{
    public TimerCard(PowerTimerViewModel? timer) => Timer = timer;

    public PowerTimerViewModel? Timer { get; }

    /// <summary>Opens the timer's panel in the bottom-right corner, where it is set and cancelled.</summary>
    [RelayCommand]
    private void Open()
    {
        if (Timer != null) Timer.IsFlyoutOpen = true;
    }
}

/// <summary>If-then routines (skillsMD/ROUTINES_PLAN.md): how many are on and running, and a button to the Routines page.</summary>
public sealed partial class RoutinesCard : ObservableObject
{
    private readonly SettingsService? _settings;
    private readonly RoutineEngine? _engine;
    private readonly Action _open;

    public RoutinesCard(SettingsService? settings, RoutineEngine? engine, Action open)
    {
        _settings = settings;
        _engine = engine;
        _open = open;
        Refresh();
    }

    [ObservableProperty]
    private string _statusText = "None yet";

    [ObservableProperty]
    private bool _isActive;

    [RelayCommand]
    private void Open() => _open();

    public void Refresh()
    {
        var routines = _settings?.GetSettings().Routines ?? new();
        int on = routines.Count(r => r.Enabled);
        int running = _engine?.RunningCount ?? 0;
        IsActive = running > 0;
        StatusText = routines.Count == 0 ? "None yet" : running > 0 ? $"{running} running" : $"{on} on";
    }
}

/// <summary>One app on the Tools page.</summary>
public partial class ToolCard : ObservableObject
{
    private readonly ToolsViewModel _owner;

    public ToolCard(ToolDefinition definition, ToolsViewModel owner)
    {
        Definition = definition;
        _owner = owner;
        _bundledIcon = BundledIcon.Load(definition.Id);
        _icon = _bundledIcon;
    }

    /// <summary>The app's own icon, shipped with AxePower so it shows before the app is installed.</summary>
    private readonly ImageSource? _bundledIcon;

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
            Icon = _bundledIcon ?? (exe != null ? ExeIcon.Load(exe) : null);
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

/// <summary>Resources/icons/tool_{id}.png: each app's icon, taken from its publisher's own program file.</summary>
internal static class BundledIcon
{
    public static ImageSource? Load(string toolId)
    {
        try
        {
            var image = new BitmapImage(new Uri($"pack://application:,,,/AxePower;component/Resources/icons/tool_{toolId}.png", UriKind.Absolute));
            image.Freeze();
            return image;
        }
        catch
        {
            // No icon shipped for this app, or no WPF application to read resources from
            return null;
        }
    }
}

/// <summary>The icon inside an app's program file, for an app AxePower ships no icon for.</summary>
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
