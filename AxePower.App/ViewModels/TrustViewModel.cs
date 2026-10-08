using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using AxePower.App.Services;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Friends;
using AxePower.Core.Services.Music;
using AxePower.Core.Services.Operations;
using AxePower.Core.Services.Processes;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services.ShareScreen;
using AxePower.Core.Services.Trust;

namespace AxePower.App.ViewModels;

/// <summary>A label and a live value, with an optional plain-words detail.</summary>
public sealed record TrustFact(string Label, string Value, string? Detail = null);

public sealed record TrustClientRow(string Id, string Name, string Address, string Since);

/// <summary>One port AxePower can open, with its live state and the controls that close it.</summary>
public sealed partial class TrustListenerRow : ObservableObject
{
    public TrustListenerRow(LocalListener listener) => Listener = listener;

    public LocalListener Listener { get; }
    public string Id => Listener.Id;
    public string Name => Listener.Name;
    public string WhenOpen => Listener.WhenOpen;
    public string WhoCanConnect => Listener.WhoCanConnect;
    public string Control => Listener.Control;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _state = "Closed";
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private IReadOnlyList<TrustClientRow> _clients = Array.Empty<TrustClientRow>();
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private bool _canRotate;
}

/// <summary>One local data store with its live size and what may be done with it.</summary>
public sealed partial class TrustStoreRow : ObservableObject
{
    public TrustStoreRow(LocalDataStore store, string path)
    {
        Store = store;
        Path = path;
    }

    public LocalDataStore Store { get; }
    public string Path { get; }
    public string Id => Store.Id;
    public string Name => Store.Name;
    public string Contents => Store.Contents;
    public string PathDisplay => Store.PathDisplay;
    public bool Deletable => Store.Deletable;
    public string? Note => Store.Note;

    [ObservableProperty] private string _size = "…";
    [ObservableProperty] private bool _exists;
}

/// <summary>One network connection with its live status.</summary>
public sealed partial class TrustConnectionRow : ObservableObject
{
    public TrustConnectionRow(NetworkConnection connection) => Connection = connection;

    public NetworkConnection Connection { get; }
    public string Name => Connection.Name;
    public string Hosts => Connection.Hosts;
    public string Trigger => Connection.Trigger;
    public string DataSent => Connection.DataSent;
    public string Control => Connection.Control;

    [ObservableProperty] private string _status = "Only when used";
}

/// <summary>
/// Trust & Permissions: in one place, what AxePower can do on this PC, what it listens on, what it
/// keeps where, what it sends where and when, and how this build is checked. Descriptions come from
/// <see cref="TrustInventory"/> (kept in step with docs/security/PRIVACY.md by tests); state is
/// read from the running services, refreshed on their events and every few seconds while shown.
/// </summary>
public partial class TrustViewModel : ObservableObject, IDisposable
{
    private readonly ElevationService? _elevation;
    private readonly SettingsService _settings;
    private readonly LanDropService? _lanDrop;
    private readonly IShareScreenService? _share;
    private readonly ShareViewerHost? _viewerHost;
    private readonly MediaMtxServer? _mediaServer;
    private readonly AccountService? _account;
    private readonly FriendsService? _friends;
    private readonly LogService _log;
    private readonly RecentlyPlayedService? _recentPlays;
    private readonly RecentSearchesService? _recentSearches;
    private readonly StreamCache? _streamCache;
    private readonly ShareChatStore? _chat;
    private readonly BatteryService? _battery;
    private readonly IUserShellLauncher? _shell;
    private readonly IConfirmationPrompts _confirm;
    private readonly DispatcherTimer? _timer;

    public TrustViewModel(
        SettingsService settings,
        LogService log,
        IConfirmationPrompts confirm,
        ElevationService? elevation = null,
        LanDropService? lanDrop = null,
        IShareScreenService? share = null,
        ShareViewerHost? viewerHost = null,
        MediaMtxServer? mediaServer = null,
        AccountService? account = null,
        FriendsService? friends = null,
        RecentlyPlayedService? recentPlays = null,
        RecentSearchesService? recentSearches = null,
        StreamCache? streamCache = null,
        ShareChatStore? chat = null,
        BatteryService? battery = null,
        IUserShellLauncher? shell = null,
        bool startTimer = true)
    {
        _settings = settings;
        _log = log;
        _confirm = confirm;
        _elevation = elevation;
        _lanDrop = lanDrop;
        _share = share;
        _viewerHost = viewerHost;
        _mediaServer = mediaServer;
        _account = account;
        _friends = friends;
        _recentPlays = recentPlays;
        _recentSearches = recentSearches;
        _streamCache = streamCache;
        _chat = chat;
        _battery = battery;
        _shell = shell;

        Listeners = TrustInventory.Listeners.Select(l => new TrustListenerRow(l)).ToList();
        Stores = TrustInventory.DataStores.Select(s => new TrustStoreRow(s, ResolvePath(s))).ToList();
        Connections = TrustInventory.Connections.Select(c => new TrustConnectionRow(c)).ToList();
        CloudData = TrustInventory.CloudData;

        if (_lanDrop is not null) _lanDrop.OnClientsChanged += OnServiceChanged;
        if (_share is not null) _share.StatusChanged += OnShareChanged;
        if (_account is not null) _account.StateChanged += OnAccountChanged;

        if (startTimer && System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => RefreshLive(), dispatcher);
        }
        Refresh();
    }

    // -- What the page shows ----------------------------------------------------------

    public ObservableCollection<TrustFact> AppFacts { get; } = new();
    public IReadOnlyList<TrustListenerRow> Listeners { get; }
    public ObservableCollection<TrustFact> AccountFacts { get; } = new();
    public IReadOnlyList<CloudDataCategory> CloudData { get; }
    public IReadOnlyList<TrustStoreRow> Stores { get; }
    public IReadOnlyList<TrustConnectionRow> Connections { get; }
    public ObservableCollection<TrustFact> BuildFacts { get; } = new();

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _lastUpdated = string.Empty;

    /// <summary>The page is on screen: refresh every few seconds.</summary>
    public void Activate()
    {
        Refresh();
        _timer?.Start();
    }

    public void Deactivate() => _timer?.Stop();

    [RelayCommand]
    public void Refresh()
    {
        RefreshApp();
        RefreshLive();
        RefreshAccount();
        RefreshBuild();
        _ = RefreshSizesAsync();
    }

    private void RefreshLive()
    {
        RefreshListeners();
        RefreshConnections();
        LastUpdated = $"Live · updated {DateTime.Now:HH:mm:ss}";
    }

    // -- This app on this PC ------------------------------------------------------------

    private void RefreshApp()
    {
        var settings = _settings.GetSettings();
        bool elevated = _elevation?.IsElevated ?? Environment.IsPrivilegedProcess;
        bool? taskExists = StartupTaskExists();
        AppFacts.Clear();
        AppFacts.Add(new TrustFact("Administrator rights", elevated ? "Running as administrator" : "Running without administrator rights",
            elevated
                ? "Needed for power plans, services, drivers, uninstalling and Windows policy changes. Programs AxePower opens for you (web pages, apps, folders) start with your normal rights."
                : "Changes that need administrator rights (services, policies, uninstalling) are refused until AxePower runs as administrator."));
        AppFacts.Add(new TrustFact("Start with Windows",
            !settings.StartWithWindows ? "Off" : settings.RunAsAdministrator ? "On, as administrator" : "On, with normal rights",
            (taskExists switch
            {
                true => "Through the scheduled task \"AxePower Startup\", which exists.",
                false => "The scheduled task \"AxePower Startup\" is not present right now.",
                null => "Whether the scheduled task \"AxePower Startup\" exists can only be checked with administrator rights.",
            })
            + " Change it in Settings › Startup & Elevation."));
    }

    /// <summary>
    /// Reads the Task Scheduler's own index; no process is started. Null when it cannot be read:
    /// Windows lets only administrators read it, and "unknown" must not be shown as "absent".
    /// </summary>
    private static bool? StartupTaskExists()
    {
        try
        {
            using var tree = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree");
            if (tree is null) return null;
            using var key = tree.OpenSubKey("AxePower Startup");
            return key is not null;
        }
        catch (Exception ex) when (ExpectedFailures.Registry(ex))
        {
            return null;
        }
    }

    // -- Listeners --------------------------------------------------------------------

    private void RefreshListeners()
    {
        foreach (var row in Listeners)
        {
            switch (row.Id)
            {
                case "landrop":
                    bool running = _lanDrop?.IsRunning == true;
                    row.IsOpen = running;
                    row.Address = running ? _lanDrop!.ServerUrl : row.Listener.Address;
                    var clients = running ? _lanDrop!.GetConnectedClients() : Array.Empty<AxePower.Core.Services.LanDrop.LanDropClientSession>();
                    row.Clients = clients.Select(c => new TrustClientRow(c.Id, c.DeviceName, c.ClientIp, c.ConnectedAt.ToString("HH:mm"))).ToList();
                    row.State = running ? $"Open · {clients.Count} device(s) connected" : "Closed";
                    row.CanStop = running;
                    row.CanRotate = running;
                    break;
                case "share-viewer":
                    bool hosting = _viewerHost?.IsRunning == true;
                    row.IsOpen = hosting;
                    row.Address = hosting ? $"port {_viewerHost!.Port}" : row.Listener.Address;
                    var participants = hosting ? _share?.GetParticipants() ?? Array.Empty<ShareParticipant>() : Array.Empty<ShareParticipant>();
                    row.Clients = participants.Select(p => new TrustClientRow(p.Id, p.Role, p.Endpoint, p.ConnectedAt.ToString("HH:mm"))).ToList();
                    row.State = hosting ? $"Open · {participants.Count} participant(s)" : "Closed";
                    row.CanStop = hosting;
                    break;
                case "media-server":
                    bool media = _mediaServer?.IsRunning == true;
                    row.IsOpen = media;
                    row.State = media ? "Running" : "Not running";
                    row.CanStop = media;
                    break;
                case "adb":
                    bool adb = IsProcessRunning("adb");
                    row.IsOpen = adb;
                    row.State = adb ? "Running" : "Not running";
                    break;
                default:
                    row.IsOpen = false;
                    row.State = "Closed (opens only during a provider sign-in)";
                    break;
            }
        }
    }

    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    [RelayCommand]
    private async Task StopListenerAsync(TrustListenerRow? row)
    {
        if (row is null) return;
        if (row.Id == "landrop")
        {
            var clients = row.Clients.Select(c => $"{c.Name} ({c.Address})").ToList();
            if (!_confirm.Confirm(new ActionConfirmation
            {
                Action = "Stop the Phone Hub / LAN Drop server",
                Source = "Trust & Permissions",
                Affected = clients.Count == 0 ? new[] { "No devices are connected." } : clients,
                Risk = RiskLevel.Low,
                Backup = "Nothing is deleted. Received files stay where they are.",
                Reversibility = Reversibility.FullyReversible,
                ConfirmLabel = "Stop the server",
            })) return;
            _lanDrop?.StopServer();
            StatusText = "Phone Hub / LAN Drop server stopped; its access link no longer works.";
        }
        else if (row.Id is "share-viewer" or "media-server" && _share is not null)
        {
            if (!_confirm.Confirm(new ActionConfirmation
            {
                Action = "End the Friends room",
                Source = "Trust & Permissions",
                Affected = row.Clients.Select(c => c.Name).DefaultIfEmpty("No one else is in the room.").ToList(),
                Risk = RiskLevel.Low,
                Backup = "Saved chat stays on this PC.",
                Reversibility = Reversibility.FullyReversible,
                ReversibilityNote = null,
                ConfirmLabel = "End the room",
            })) return;
            await _share.StopAsync();
            StatusText = "Room ended; its link no longer works.";
        }
        RefreshLive();
    }

    [RelayCommand]
    private void RotateAccess(TrustListenerRow? row)
    {
        if (row?.Id != "landrop" || _lanDrop is null) return;
        if (!_confirm.Confirm(new ActionConfirmation
        {
            Action = "Disconnect every device and make a new access link",
            Source = "Trust & Permissions",
            Affected = row.Clients.Select(c => $"{c.Name} ({c.Address})").DefaultIfEmpty("No devices are connected.").ToList(),
            Risk = RiskLevel.Low,
            Backup = "Nothing is deleted. Devices scan the new QR code to connect again.",
            Reversibility = Reversibility.Irreversible,
            ReversibilityNote = "the old link stops working for good.",
            ConfirmLabel = "Disconnect and rotate",
        })) return;
        _lanDrop.DisconnectAllClientsAndRotateAccess();
        StatusText = "All devices disconnected; the old access link no longer works.";
        RefreshLive();
    }

    [RelayCommand]
    private void DisconnectClient(TrustClientRow? client)
    {
        if (client is null || _lanDrop is null) return;
        _lanDrop.DisconnectClient(client.Id);
        StatusText = $"{client.Name} disconnected.";
        RefreshLive();
    }

    // -- Account ------------------------------------------------------------------------

    private void RefreshAccount()
    {
        var state = _account?.State ?? AccountState.SignedOut;
        IsSignedIn = state.Status == AccountStatus.SignedIn;
        AccountFacts.Clear();
        if (!IsSignedIn)
        {
            AccountFacts.Add(new TrustFact("Account", "Not signed in", "Without an account nothing about you is stored in AxePower's cloud. Accounts are optional; they add Friends."));
            return;
        }

        AccountFacts.Add(new TrustFact("Account", state.Profile is { } profile ? $"Signed in as @{profile.Username}" : "Signed in (no username yet)",
            $"{state.Email ?? "No email"}{(state.Provider is { } provider ? $" · via {provider}" : string.Empty)}{(state.IsOffline ? " · offline right now" : string.Empty)}"));
        AccountFacts.Add(new TrustFact("Friends connection", _friends?.IsLive == true ? "Connected" : "Not connected",
            _friends is null ? null : (_friends.AppearOffline ? "You appear offline to friends." : _friends.ShareActivity ? "Friends see what you are doing." : "Friends see only online or away.")));
        AccountFacts.Add(new TrustFact("Remote access (AxePower Mobile)", CloudBridgeService.IsAvailable ? "Available" : "Turned off in this version",
            CloudBridgeService.IsAvailable ? null : CloudBridgeService.UnavailableReason));
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (_account is null || !IsSignedIn) return;
        if (!_confirm.Confirm(new ActionConfirmation
        {
            Action = "Sign out of AxePower on this PC",
            Source = "Trust & Permissions",
            Affected = new[] { "The saved session on this PC is deleted.", "Friends and presence stop until you sign in again." },
            Risk = RiskLevel.Low,
            Backup = "Your account and friends stay in the cloud; sign in again to get them back.",
            Reversibility = Reversibility.FullyReversible,
            ConfirmLabel = "Sign out",
        })) return;
        await _account.SignOutAsync();
        StatusText = "Signed out.";
        RefreshAccount();
    }

    [RelayCommand]
    private async Task DeleteAccountAsync()
    {
        if (_account is null || !IsSignedIn) return;
        var username = _account.State.Profile?.Username;
        if (!_confirm.Confirm(new ActionConfirmation
        {
            Action = username is null ? "Delete your AxePower account" : $"Delete the AxePower account @{username}",
            Source = "Trust & Permissions",
            Affected = TrustInventory.CloudData.Select(c => $"{c.Name}: {c.Contents}").ToList(),
            Risk = RiskLevel.High,
            Backup = "None. Everything listed is deleted from AxePower's cloud at once. Data on this PC (settings, music, logs) is not touched.",
            Reversibility = Reversibility.Irreversible,
            ReversibilityNote = "a deleted account and its friends cannot be restored.",
            ConfirmLabel = "Delete my account",
        })) return;
        try
        {
            await _account.DeleteAccountAsync();
            StatusText = "Your account was deleted.";
        }
        catch (AccountException ex)
        {
            StatusText = "The account was not deleted. " + AxePower.Core.Services.Outcomes.UserMessages.Advice(AxePower.Core.Services.Outcomes.OutcomeClassifier.Classify(ex) ?? AxePower.Core.Services.Outcomes.FailureKind.Unexpected);
        }
        RefreshAccount();
    }

    private void OnAccountChanged(object? sender, AccountState state) => Dispatch(RefreshAccount);

    private void OnServiceChanged(object? sender, EventArgs e) => Dispatch(RefreshLive);

    private void OnShareChanged(object? sender, ShareSessionStatus e) => Dispatch(RefreshLive);

    // -- Local data -------------------------------------------------------------------

    private string ResolvePath(LocalDataStore store) => store.Id switch
    {
        "received" when _lanDrop is not null => _lanDrop.DownloadDirectory,
        "diagnostics" when _log.Store is not null => _log.Store.Directory,
        _ => store.ResolvePath(),
    };

    private async Task RefreshSizesAsync()
    {
        var sizes = await Task.Run(() => Stores.Select(s => (s, Size: SizeOf(s))).ToList());
        foreach (var (row, size) in sizes)
        {
            row.Exists = size is not null;
            row.Size = size is null ? "Nothing stored" : DiagnosticsViewModel.FormatBytes(size.Value);
        }
    }

    private static long? SizeOf(TrustStoreRow row)
    {
        var paths = row.Id switch
        {
            "music-history" => new[] { System.IO.Path.Combine(row.Path, "recently-played.json"), System.IO.Path.Combine(row.Path, "recent-searches.json") },
            "music-cache" => new[] { System.IO.Path.Combine(row.Path, "covers"), System.IO.Path.Combine(row.Path, "stream-cache") },
            "settings" => new[] { System.IO.Path.Combine(row.Path, "settings.json"), System.IO.Path.Combine(row.Path, "settings.backup.json") },
            _ => new[] { row.Path },
        };
        long total = 0;
        bool any = false;
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) { total += new FileInfo(path).Length; any = true; }
                else if (Directory.Exists(path))
                {
                    any = true;
                    foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)) total += file.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Partly unreadable: show what could be counted.
            }
        }
        return any ? total : null;
    }

    [RelayCommand]
    private void OpenStore(TrustStoreRow? row)
    {
        if (row is null) return;
        var folder = Directory.Exists(row.Path) ? row.Path : System.IO.Path.GetDirectoryName(row.Path) ?? row.Path;
        var result = _shell?.OpenFolder(folder);
        StatusText = result is null ? "Opening folders is not available here." : result.Status == ShellLaunchStatus.Started ? $"Opened {row.Name}." : result.Message;
    }

    [RelayCommand]
    private async Task DeleteStoreAsync(TrustStoreRow? row)
    {
        if (row is null || !row.Deletable) return;
        if (!_confirm.Confirm(new ActionConfirmation
        {
            Action = $"Delete {row.Name.ToLowerInvariant()} from this PC",
            Source = "Trust & Permissions",
            Affected = new[] { $"{row.PathDisplay} ({row.Size})", row.Contents },
            Risk = RiskLevel.Medium,
            Backup = "None. Caches are rebuilt when needed; histories start again empty.",
            Reversibility = Reversibility.Irreversible,
            ReversibilityNote = "deleted files are not moved to the Recycle Bin.",
            ConfirmLabel = $"Delete {row.Name.ToLowerInvariant()}",
        })) return;

        int failed = await DeleteAsync(row);
        StatusText = failed == 0 ? $"Deleted {row.Name.ToLowerInvariant()}." : $"Deleted {row.Name.ToLowerInvariant()}, except {failed} file(s) in use. Try again later.";
        await RefreshSizesAsync();
    }

    /// <summary>Deletes one store through the service that owns it, so nothing in memory writes it back. Returns files left behind.</summary>
    private async Task<int> DeleteAsync(TrustStoreRow row)
    {
        switch (row.Id)
        {
            case "diagnostics":
                if (_log.Store is not { } store) return 0;
                var result = store.DeleteAll();
                _log.Logs.Clear();
                return result.Failed.Count;
            case "music-history":
                _recentPlays?.Clear();
                _recentSearches?.Clear();
                return 0;
            case "music-cache":
                _streamCache?.Clear();
                return DeleteContents(System.IO.Path.Combine(row.Path, "covers"));
            case "chat":
                _chat?.Clear();
                return DeleteContents(System.IO.Path.Combine(row.Path, "Attachments"));
            case "battery":
                if (_battery is not null) await _battery.ClearHistoryAsync();
                return 0;
            default:
                return DeleteContents(row.Path);
        }
    }

    /// <summary>Deletes the files and folders inside <paramref name="folder"/>; returns how many could not be removed.</summary>
    internal static int DeleteContents(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        int failed = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Best-effort: an empty folder left behind holds nothing. */ }
        }
        return failed;
    }

    // -- Connections --------------------------------------------------------------------

    private void RefreshConnections()
    {
        var settings = _settings.GetSettings();
        bool signedIn = _account?.State.Status == AccountStatus.SignedIn;
        foreach (var row in Connections)
        {
            row.Status = row.Connection.Id switch
            {
                "updates" => settings.AutoCheckUpdates ? "Checks automatically at start-up" : "Only when you click Check for updates",
                "account" => signedIn ? "In use: you are signed in" : "Not used: you are not signed in",
                "friends" => _friends?.IsLive == true ? "Connected now" : "Not connected",
                "remote-access" => CloudBridgeService.IsAvailable ? "Available" : "Off in this version",
                "youtube" => settings.Music.Providers.Enabled.Contains("youtube", StringComparer.OrdinalIgnoreCase) ? "Enabled as a music source" : "Turned off as a music source",
                "phone-hub" => _lanDrop?.IsRunning == true ? "Server running now" : "Server stopped",
                "share-peers" => _viewerHost?.IsRunning == true ? "Room open now" : "No room open",
                _ => "Only when used",
            };
        }
    }

    // -- This build -----------------------------------------------------------------

    private void RefreshBuild()
    {
        BuildFacts.Clear();
        BuildFacts.Add(new TrustFact("Version", BuildInfo.Version));
        BuildFacts.Add(new TrustFact("Build commit", BuildInfo.Commit ?? "Not recorded (built without git)"));
        BuildFacts.Add(new TrustFact("Update source", BuildInfo.UpdateRepository, "GitHub releases of AxePower's download repository; no other source is accepted."));
        BuildFacts.Add(new TrustFact("Update verification", BuildInfo.UpdateVerification));
        BuildFacts.Add(new TrustFact("This program's signature", "Checking…"));
        _ = Task.Run(() => BuildInfo.ExecutableSignature()).ContinueWith(t =>
        {
            var text = t.IsCompletedSuccessfully ? t.Result : "Unknown (the check failed).";
            Dispatch(() =>
            {
                var index = BuildFacts.ToList().FindIndex(f => f.Label == "This program's signature");
                if (index >= 0) BuildFacts[index] = new TrustFact("This program's signature", text);
            });
        }, TaskScheduler.Default);
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        _timer?.Stop();
        if (_lanDrop is not null) _lanDrop.OnClientsChanged -= OnServiceChanged;
        if (_share is not null) _share.StatusChanged -= OnShareChanged;
        if (_account is not null) _account.StateChanged -= OnAccountChanged;
    }
}
