namespace AxePower.Core.Services.Trust;

/// <summary>A folder or file where AxePower keeps data on this PC.</summary>
/// <param name="Deletable">
/// True only for logs, caches and histories (owner's choice, 2026-10-06). Settings, change history,
/// uninstaller backups and received files can be opened from the Trust page but never deleted there.
/// </param>
public sealed record LocalDataStore(string Id, string Name, string Contents, string PathDisplay, Func<string> ResolvePath, bool Deletable, string? Note = null);

/// <summary>A kind of data kept in AxePower's cloud (Supabase), only for people who sign in.</summary>
public sealed record CloudDataCategory(string Id, string Name, string Contents, string When, string Retention);

/// <summary>A place AxePower connects to over the network, what triggers it, and what is sent.</summary>
/// <param name="CodeFiles">The source files that open this connection; a test checks every networking file is listed somewhere.</param>
public sealed record NetworkConnection(
    string Id, string Name, string Hosts, string Trigger, string DataSent, bool Optional, string Control, IReadOnlyList<string> CodeFiles);

/// <summary>A port AxePower opens on this PC for other devices or for itself.</summary>
public sealed record LocalListener(string Id, string Name, string Address, string WhenOpen, string WhoCanConnect, string Control);

/// <summary>
/// Everything AxePower stores, opens and contacts, written from the code (see docs/security/PRIVACY.md,
/// which tests keep in step with this list). The Trust & Permissions page shows it with live state.
/// When a feature adds a folder, a port or a network call, it must be added here.
/// </summary>
public static class TrustInventory
{
    private static string Local(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower" }.Concat(parts).ToArray());

    private static string Roaming(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AxePower" }.Concat(parts).ToArray());

    public static IReadOnlyList<LocalDataStore> DataStores { get; } = new LocalDataStore[]
    {
        new("settings", "Settings", "settings.json and its backup: every option you set in AxePower.",
            @"%LOCALAPPDATA%\AxePower", () => Local(), Deletable: false, "Reset individual settings in Settings instead."),
        new("diagnostics", "Diagnostics logs", "Redacted logs of what AxePower did and what failed. Kept 14 days or 20 MB.",
            @"%LOCALAPPDATA%\AxePower\Diagnostics", () => Local("Diagnostics"), Deletable: true),
        new("operations", "Change history", "Every change AxePower made to Windows, with the snapshots needed to roll it back.",
            @"%LOCALAPPDATA%\AxePower\Operations", () => Local("Operations"), Deletable: false, "Deleting it would remove the ability to roll changes back."),
        new("music-history", "Music history", "Recently played songs and recent YouTube searches with their results.",
            @"%LOCALAPPDATA%\AxePower\Music", () => Local("Music"), Deletable: true, "Deletes the history only, not your music."),
        new("music-cache", "Music caches", "Cover art thumbnails and the streaming cache. Rebuilt when needed.",
            @"%LOCALAPPDATA%\AxePower\Music\covers, stream-cache", () => Local("Music"), Deletable: true),
        new("chat", "Friends chat", "Saved chat messages and received attachments from Friends rooms, encrypted with your Windows account.",
            @"%LOCALAPPDATA%\AxePower\Chat", () => Local("Chat"), Deletable: true),
        new("account", "Account session", "Your sign-in session, encrypted with your Windows account. Removed when you sign out.",
            @"%LOCALAPPDATA%\AxePower\Account", () => Local("Account"), Deletable: false, "Sign out to remove it."),
        new("uninstaller", "Uninstaller history and backups", "Uninstall sessions, the files and registry keys backed up before cleanup, and Windows Installer logs.",
            @"%LOCALAPPDATA%\AxePower\UninstallerHistory, UninstallLogs", () => Local("UninstallerHistory"), Deletable: false, "These backups are what lets you restore a cleanup."),
        new("received", "Received files", "Files sent to this PC from your phone through Phone Hub / LAN Drop.",
            @"%USERPROFILE%\Downloads\AxePower_LanDrop (or the folder you chose)", () => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AxePower_LanDrop"), Deletable: false, "Your files: manage them in File Explorer."),
        new("battery", "Battery history", "Battery capacity and charge readings over time, for the Battery page.",
            @"%LOCALAPPDATA%\AxePower\BatteryHistory", () => Local("BatteryHistory"), Deletable: true),
        new("downloads", "Downloaded installers", "Graphics driver installers AxePower downloaded for you.",
            @"%LOCALAPPDATA%\AxePower\Downloads", () => Local("Downloads"), Deletable: true),
        new("tools", "Downloaded tools", "FFmpeg, MediaMTX, scrcpy and portable apps AxePower fetched for Share Screen, Android control and Software Hub.",
            @"%LOCALAPPDATA%\AxePower\tools, Tools", () => Local("tools"), Deletable: false, "Remove portable apps from Software Hub."),
        new("game-art", "Game artwork", "Posters for your installed games, downloaded from Steam's servers.",
            @"%APPDATA%\AxePower\CoverArt", () => Roaming("CoverArt"), Deletable: true),
        new("skins", "Custom skins", "Skins you added yourself.",
            @"%APPDATA%\AxePower\Skins", () => Roaming("Skins"), Deletable: false),
        new("share", "Share Screen working files", "The media server's settings for the room you host and, if you use Tailscale for HTTPS, its certificate.",
            @"%LOCALAPPDATA%\AxePower\share", () => Local("share"), Deletable: false, "Rewritten for each room."),
        new("scripts", "Driver update script", "The PowerShell script AxePower writes to run a driver update. It stays after the update has run.",
            @"%LOCALAPPDATA%\AxePower\Scripts", () => Local("Scripts"), Deletable: true),
        new("webview", "Share Screen viewer data", "The embedded browser's cache for the Share Screen viewer.",
            @"%LOCALAPPDATA%\AxePower\webview2", () => Local("webview2"), Deletable: true, "Close the Friends page first if it is open."),
    };

    public static IReadOnlyList<CloudDataCategory> CloudData { get; } = new CloudDataCategory[]
    {
        new("auth", "Sign-in", "Email address and the providers you signed in with (Google, GitHub, Discord, Steam). Passwords are held by Supabase Auth, hashed, and never seen by AxePower.",
            "When you create the account or sign in.", "Until you delete the account."),
        new("profile", "Profile", "Username, display name, avatar link, whether others can find you, and when the profile was created or renamed.",
            "When you create or edit your profile.", "Until you delete the account."),
        new("steam", "Steam link", "Your Steam ID, if you signed in with Steam.", "When you sign in with Steam.", "Until you delete the account."),
        new("friends", "Friends", "Friend requests and friendships, and the people you blocked.", "When you add, accept, remove or block someone.", "Until removed, or the account is deleted."),
        new("presence", "Presence", "Online or away, and (only with \"Show friends what I'm doing\") what you are playing or doing.",
            "While you are signed in and Friends is active.", "Replaced as it changes; removed with the account."),
        new("invites", "Room invites", "An invitation to a Friends room, including its room link, sent to one friend.", "When you invite a friend to a room.", "Expires after 10 minutes."),
    };

    public static IReadOnlyList<NetworkConnection> Connections { get; } = new NetworkConnection[]
    {
        new("updates", "AxePower updates (GitHub)", "api.github.com, github.com, objects.githubusercontent.com, release-assets.githubusercontent.com",
            "At start-up when \"Check for updates automatically\" is on, and when you click Check for updates. The installer is downloaded only when you choose to install.",
            "A standard web request (your IP address and AxePower's version). Nothing about you or your PC.",
            Optional: true, "Settings › Updates & Maintenance: turn off automatic checks.", new[] { "UpdateService.cs" }),
        new("account", "AxePower account (Supabase)", "kjnqsgdlcytpooueymdt.supabase.co",
            "Only if you sign in: signing in, refreshing the session, saving your profile, and account deletion.",
            "Email and password (or the provider's sign-in result), username, display name and avatar link.",
            Optional: true, "Do not sign in, or sign out on this page.", new[] { "SupabaseAccountApi.cs" }),
        new("friends", "Friends and presence (Supabase Realtime)", "kjnqsgdlcytpooueymdt.supabase.co",
            "While you are signed in: a live connection for friend requests, presence and invites.",
            "Online/away, activity (only if \"Show friends what I'm doing\" is on), friend requests and room invites.",
            Optional: true, "Settings › Friends: appear offline or stop sharing activity; or sign out.", new[] { "RealtimeFeed.cs" }),
        new("sign-in-providers", "Google, GitHub, Discord or Steam sign-in", "accounts.google.com, github.com, discord.com, steamcommunity.com (in your browser)",
            "Only when you choose that provider to sign in. It happens in your browser; AxePower receives the result on 127.0.0.1.",
            "What you approve on the provider's page; AxePower receives a sign-in code.",
            Optional: true, "Use email sign-in or no account.", new[] { "OAuthLoopback.cs" }),
        new("remote-access", "Remote access for AxePower Mobile (Supabase Realtime)", "kjnqsgdlcytpooueymdt.supabase.co",
            "Turned off in this version: it never connects. It returns once its connection is private to your account.",
            "Nothing while turned off.", Optional: true, "Nothing to do: it is off.", new[] { "CloudBridgeService.cs" }),
        new("youtube", "YouTube Music", "youtube.com, music.youtube.com, googlevideo.com, i.ytimg.com",
            "When you search, play or save a song from YouTube in Music, and for its captions as lyrics.",
            "Your search words and the songs you play, as YouTube sees any viewer.",
            Optional: true, "Use only This PC as the music source.", new[] { "YouTubeProvider.cs", "YouTubeCaptionLyricsProvider.cs", "CoverArtService.cs" }),
        new("lyrics", "Lyrics (LRCLIB, KuGou)", "lrclib.net, lyrics.kugou.com",
            "When the lyrics panel is open for the playing song.", "The song's title, artist, album and length.",
            Optional: true, "Keep the lyrics panel closed.", new[] { "LrcLibLyricsService.cs" }),
        new("game-art", "Game artwork (Steam CDN)", "cdn.cloudflare.steamstatic.com, shared.cloudflare.steamstatic.com, steamcdn-a.akamaihd.net, store.steampowered.com",
            "When Game Mode shows posters for your installed games (once per game, then cached).", "The game's Steam ID or name.",
            Optional: true, "Delete game artwork on this page; posters are fetched again when shown.", new[] { "GamePosterService.cs" }),
        new("latency", "Network latency on the Dashboard (ping)", "1.1.1.1 (Cloudflare)",
            "Every 5 seconds, only while the Dashboard is on screen, for the latency on its network tile.",
            "A ping: an empty echo request with your IP address. Nothing about you or your PC.",
            Optional: true, "Leave the Dashboard (any other page, or the tray, stops it).", new[] { "SystemTelemetryService.cs" }),
        new("dns-benchmark", "DNS benchmark (ping)", "1.1.1.1, 9.9.9.9, 8.8.8.8, 208.67.222.222",
            "Only when you run the DNS benchmark in System Doctor: three pings to each provider.",
            "A ping: an empty echo request with your IP address.",
            Optional: true, "Do not run the benchmark.", new[] { "DnsOptimizerService.cs" }),
        new("drivers", "Graphics drivers (NVIDIA, AMD)", "nvidia.com, us.download.nvidia.com, amd.com",
            "When you check for or download a graphics driver.", "Your graphics card model and current driver version in the query.",
            Optional: true, "Do not use the driver check.", new[] { "GraphicsDriverService.cs" }),
        new("share-tools", "Share Screen tools (FFmpeg, MediaMTX)", "api.github.com, github.com, www.gyan.dev",
            "The first time you use Share Screen, to download the tools, and when they are updated.", "A standard download request.",
            Optional: true, "Do not use Share Screen.", new[] { "ShareToolProvider.cs" }),
        new("portable-tools", "Portable tools and Android control (GitHub)", "api.github.com, github.com",
            "When you install a portable tool from Software Hub, or set up Android control (scrcpy).", "A standard download request.",
            Optional: true, "Do not install those tools.", new[] { "PortableTools.cs", "AndroidControlService.cs" }),
        new("software-hub", "Software Hub installs (WinGet, Microsoft Store)", "Microsoft's WinGet and Store sources, and each publisher's download server",
            "When you install, update or remove software from Software Hub.", "What WinGet sends: the package names and your Windows version.",
            Optional: true, "Do not use Software Hub installs.", new[] { "WinGetHubService.cs" }),
        new("share-peers", "Friends rooms and Share Screen viewers (direct)", "The other devices in the room, on your network or over Tailscale",
            "While you host or join a room.", "Your screen and sound (when sharing), voice, chat and remote input. Chat, voice and control are end-to-end encrypted.",
            Optional: true, "End the room on this page or on the Friends page.", new[] { "ShareViewerHost.cs", "ShareRoomClient.cs", "ShareRoomSession.cs", "MediaMtxServer.cs", "ShareViewerPage.cs" }),
        new("phone-hub", "Phone Hub and LAN Drop (your network)", "Your phone, on the same network",
            "While the LAN Drop server is running.", "Files and text you send, and the music remote. Plain HTTP on your network, protected by the access link.",
            Optional: true, "Stop the server on this page.", new[] { "LanDropService.cs" }),
    };

    public static IReadOnlyList<LocalListener> Listeners { get; } = new LocalListener[]
    {
        new("landrop", "Phone Hub / LAN Drop portal", "http://<this PC's network address>:5050",
            "Only after you click Start in Phone Hub or LAN Drop. Not on Public networks unless you allow it for that session.",
            "Devices on your network that have the access link (QR code). Five wrong tries lock an address out.",
            "Stop it, disconnect devices, or rotate the access link on this page."),
        new("share-viewer", "Friends room / Share Screen viewer page", "http(s)://<this PC's network address>:<room port>",
            "Only while you host a room.", "People with the room link. You approve remote control each time.", "End the room on this page."),
        new("media-server", "Share Screen media server (MediaMTX)", "Local WebRTC ports while sharing",
            "Only while you share your screen in a room.", "Viewers admitted by the room page.", "End the room on this page."),
        new("oauth", "Sign-in callback", "http://127.0.0.1:<random port>",
            "Only for the few minutes of a Google, GitHub, Discord or Steam sign-in.", "Only this PC (loopback); it accepts one answer and closes.", "Nothing to do: it closes itself."),
        new("adb", "Android control (adb)", "127.0.0.1:5037",
            "Only while Android control is running (started by scrcpy).", "Only this PC (loopback).", "Close Android control."),
    };
}
