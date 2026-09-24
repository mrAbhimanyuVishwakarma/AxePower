using System;
using System.IO.Pipes;
using System.Linq;
using AxePower.Core.Models.Music;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;
using AxePower.Core.Services.Music;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.App.ViewModels.Music;
using AxePower.App.ViewModels.ShareScreen;
using AxePower.Core.Services.ShareScreen;
using AxePower.App.Helpers;

namespace AxePower.App;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;
    private const string MutexName = "AxePower.Desktop.Singleton";
    private const string PipeName = "AxePower.Desktop.Singleton.Pipe";

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        string logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AxePower", "AxePower.log");
        try
        {
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] [STARTUP] Process ID: {Environment.ProcessId}, Args: '{string.Join(" ", e.Args)}'\n");
        }
        catch { }

        // Initialize smooth proportional touchpad & mouse wheel scrolling globally
        try
        {
            TouchpadScrollHelper.Initialize();
        }
        catch { }

        // Handle CLI arguments early
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTest();
            Environment.Exit(0);
            return;
        }

        // Single instance check
        try
        {
            var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var existingProcesses = System.Diagnostics.Process.GetProcessesByName("AxePower")
                .Where(p => p.Id != currentProcess.Id && p.Id != Environment.ProcessId)
                .ToList();

            if (existingProcesses.Count > 0 && !e.Args.Contains("--force", StringComparer.OrdinalIgnoreCase))
            {
                bool signaled = SignalPrimaryInstance(e.Args);
                bool hasInteractiveWindow = existingProcesses.Any(p =>
                {
                    try { return p.MainWindowHandle != IntPtr.Zero; }
                    catch { return false; }
                });

                if (signaled && hasInteractiveWindow)
                {
                    try
                    {
                        System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] [SINGLE_INSTANCE_EXIT] Active interactive instance signaled successfully: {string.Join(", ", existingProcesses.Select(p => p.Id))}\n");
                    }
                    catch { }
                    Environment.Exit(0);
                    return;
                }
                else
                {
                    // Clean up headless/orphaned background instances
                    foreach (var proc in existingProcesses)
                    {
                        try { proc.Kill(); } catch { }
                    }
                }
            }
        }
        catch
        {
            // If check fails, proceed
        }

        // Global crash prevention and error logging
        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                var log = Services?.GetService<LogService>();
                log?.LogError($"[Unhandled UI Exception] {args.Exception.Message}", args.Exception);
            }
            catch { }
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                try
                {
                    var log = Services?.GetService<LogService>();
                    log?.LogError($"[Unhandled AppDomain Exception] {ex.Message}", ex);
                }
                catch { }
            }
        };

        try
        {
            Services = ConfigureServices();

            var settingsService = Services.GetRequiredService<SettingsService>();
            settingsService.LoadSettings();

            // Elevation is decided before anything else is constructed: on success this process
            // is replaced outright, so there is no point building services or showing a window.
            if (TryElevateAndExit(settingsService, e.Args)) return;

            var themeService = Services.GetRequiredService<ThemeService>();
            themeService.ApplyTheme(settingsService.GetSettings().Theme);

            var startupService = Services.GetRequiredService<StartupService>();
            startupService.Initialize();

            var batteryService = Services.GetRequiredService<BatteryService>();
            batteryService.StartHistoryCollection();

            // Before the startup switch to Smart, so GPU preferences left by an interrupted
            // Game Mode are put back by that first mode change
            Services.GetRequiredService<GameModeGpuService>().Attach(Services.GetRequiredService<PowerPlanService>());

            var autoSwitchService = Services.GetRequiredService<AutoSwitchService>();
            autoSwitchService.Start();

            var appMonitorService = Services.GetRequiredService<AppMonitorService>();
            appMonitorService.Start();

            var mainWindow = Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;

            // Never veto the close that Windows issues during logoff/shutdown
            SessionEnding += (s, args) => mainWindow.AllowClose();

            // Show and foreground the main window
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.ShowInTaskbar = true;
            mainWindow.Visibility = Visibility.Visible;
            mainWindow.Show();
            mainWindow.Activate();
            mainWindow.Focus();

            var helper = new WindowInteropHelper(mainWindow);
            if (helper.Handle != IntPtr.Zero)
            {
                ShowWindow(helper.Handle, 9); // SW_RESTORE
                SetForegroundWindow(helper.Handle);
            }

            // Start listening for other instances
            Task.Run(ListenForOtherInstances);

            // AxePower Music: media controls need the window handle; restoring the last session
            // (reading tags) runs in the background and never blocks the UI
            _ = InitializeMusicAsync(new WindowInteropHelper(mainWindow).EnsureHandle());
        }
        catch (Exception ex)
        {
            try
            {
                var log = Services?.GetService<LogService>() ?? new LogService();
                log.LogError($"[Startup Failure] {ex.Message}", ex);
            }
            catch { }

            MessageBox.Show($"AxePower failed to start:\n\n{ex.Message}\n\n{ex.StackTrace}", "AxePower Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Honours <see cref="AxePower.Core.Models.AppSettings.RunAsAdministrator"/> by relaunching
    /// elevated. Returns true when an elevated copy took over and this process has exited.
    ///
    /// A refused UAC prompt is not an error: AxePower keeps running as a standard user, with the
    /// sentinel argument on the relaunched command line making sure a denial cannot loop.
    /// </summary>
    private bool TryElevateAndExit(SettingsService settingsService, string[] args)
    {
        var elevation = Services.GetRequiredService<ElevationService>();

        if (!settingsService.GetSettings().RunAsAdministrator) return false;
        if (elevation.IsElevated) return false;
        if (ElevationService.HasAttemptedElevation(args)) return false;

        // --force keeps the elevated child from mistaking this still-closing process for a live
        // instance and exiting again in the single-instance check.
        var forwarded = args.Append("--force").ToArray();

        if (elevation.TryRelaunchElevated(forwarded) != ElevationService.RelaunchResult.Started)
            return false;

        Environment.Exit(0);
        return true;
    }

    internal static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core Services
        services.AddSingleton<LogService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ElevationService>();
        services.AddSingleton<HardwareMonitorService>();
        services.AddSingleton<BatteryService>();
        services.AddSingleton<PowerPlanService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<StartupService>();
        services.AddSingleton<AutoSwitchService>();
        services.AddSingleton<AppKillerService>();
        services.AddSingleton<TimeoutService>();
        services.AddSingleton<AppMonitorService>();
        services.AddSingleton<GameScannerService>();
        services.AddSingleton<GameModeGpuService>();
        services.AddSingleton<GameModeGuard>();
        services.AddSingleton<IGpuDisplayModeController, LenovoGpuDisplayModeService>();
        services.AddSingleton<IGameModePrompts, WindowGameModePrompts>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<SystemServiceOptimizer>();
        services.AddSingleton<DisplayService>();
        services.AddSingleton<DriverUpdateService>();
        services.AddSingleton<MsiAfterburnerService>();
        services.AddSingleton<DebloatService>();
        services.AddSingleton<AndroidControlService>();
        services.AddSingleton<ControllerNavigationService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<ITweakService, TweakService>();
        services.AddSingleton<ICacheCleanerService, CacheCleanerService>();
        services.AddSingleton<IDnsOptimizerService, DnsOptimizerService>();
        services.AddSingleton<ISystemDoctorService, SystemDoctorService>();
        services.AddSingleton<IWindowsUpdateShieldService, WindowsUpdateShieldService>();
        services.AddSingleton<IWinGetHubService, WinGetHubService>();
        services.AddSingleton<IServiceThrottlerService, ServiceThrottlerService>();
        services.AddSingleton<IMemoryOptimizerService, MemoryOptimizerService>();
        services.AddSingleton<IDynamicModeAutomationService, DynamicModeAutomationService>();
        services.AddSingleton<LanDropService>();
        services.AddSingleton<FirmwareUpdateService>();
        services.AddSingleton<GraphicsDriverService>();
        services.AddSingleton<SystemTelemetryService>();
        services.AddSingleton<TerminalService>();
        services.AddSingleton<RestorePointService>();

        // Share Screen: capture, encode and route one display to remote viewers
        services.AddSingleton<IShareToolProvider, ShareToolProvider>();
        services.AddSingleton<IShareDeviceProvider, ShareDeviceProvider>();
        services.AddSingleton<IShareEncoderProbe, ShareEncoderProbe>();
        services.AddSingleton<MediaMtxServer>();
        services.AddSingleton<ShareEncodePipeline>();
        services.AddSingleton<ShareAudioPump>();
        services.AddSingleton<RemoteInputService>();
        services.AddSingleton<ShareViewerHost>();
        services.AddSingleton<IShareScreenService, ShareScreenService>();

        // Uninstaller Pro — Core Services
        services.AddSingleton<IFileSystemProbe, FileSystemProbe>();
        services.AddSingleton<IMsiProductStateReader, MsiProductStateReader>();
        services.AddSingleton<IProcessTreeWatcher, ProcessTreeWatcher>();
        services.AddSingleton<IInstallStateVerifier, InstallStateVerifier>();
        services.AddSingleton<IProcessDetectionService, ProcessDetectionService>();
        services.AddSingleton<BackupManifestStore>();
        services.AddSingleton<IApplicationDiscoveryService, ApplicationDiscoveryService>();
        services.AddSingleton<Win32UninstallExecutor>();
        services.AddSingleton<MsiUninstallExecutor>();
        services.AddSingleton<AppxUninstallExecutor>();
        services.AddSingleton<BrokenEntryExecutor>();
        services.AddSingleton<UninstallExecutorFactory>();
        services.AddSingleton<IBatchUninstallService, BatchUninstallService>();
        services.AddSingleton<ILeftoverScannerService, LeftoverScannerService>();
        services.AddSingleton<ILeftoverCleanupService, LeftoverCleanupService>();
        services.AddSingleton<IRegistryBackupService, RegistryBackupService>();
        services.AddSingleton<IFileCleanupService, FileCleanupService>();
        services.AddSingleton<IProtectionService, ProtectionService>();
        services.AddSingleton<IUninstallHistoryService, UninstallHistoryService>();
        services.AddSingleton<AxePower.Core.Services.Uninstaller.SystemRestoreService>();
        services.AddSingleton<IUninstallerPrompts, MessageBoxUninstallerPrompts>();

        // AxePower Music — Core (each phase registers the services it implements)
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(new MusicStoragePaths());
        services.AddSingleton<ITagReader, TagLibTagReader>();
        services.AddSingleton<LooseTrackReader>();
        services.AddSingleton<ICoverArtService, CoverArtService>();
        services.AddSingleton<IAudioEngine, NAudioEngine>();
        services.AddSingleton<PlaybackStateStore>();
        services.AddSingleton<LibraryDatabase>();
        services.AddSingleton<MusicLibraryRepository>();

        // AxePower Music — Sources. Providers are registered here, never loaded from disk:
        // AxePower runs elevated, so third-party code must not end up inside this process.
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton<StreamCache>();
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<IStreamResolver, StreamResolver>();
        services.AddSingleton<SpotifyAuth>();
        services.AddSingleton<LocalFileProvider>();
        services.AddSingleton<YouTubeProvider>();
        services.AddSingleton<SpotifyProvider>();
        services.AddSingleton<IMusicProvider>(sp => sp.GetRequiredService<LocalFileProvider>());
        services.AddSingleton<IMusicProvider>(sp => sp.GetRequiredService<YouTubeProvider>());
        services.AddSingleton<IMusicProvider>(sp => sp.GetRequiredService<SpotifyProvider>());
        services.AddSingleton<DiscoveryService>();
        services.AddSingleton<MusicRemote>();
        services.AddSingleton<PortalAccessToken>();
        services.AddSingleton<IMusicPlayerService, MusicPlayerService>();
        services.AddSingleton<IPlayerIntegration, SmtcIntegration>();
        services.AddSingleton<PlayerIntegrationHost>();

        // AxePower Music — App
        services.AddSingleton<MusicDispatcher>();
        services.AddSingleton<FileDropHelper>();
        services.AddSingleton<IPlayerIntegration, TaskbarMediaIntegration>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<MusicShortcutHandler>();
        services.AddSingleton<NowPlayingViewModel>();   // singleton on purpose: every surface shares it
        services.AddTransient<MusicViewModel>();
        services.AddTransient<MusicSettingsViewModel>();
        services.AddSingleton<SourcesViewModel>();
        services.AddSingleton<ShortcutsViewModel>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<AutoSwitchViewModel>();
        services.AddTransient<GameModeViewModel>();
        services.AddTransient<BatteryViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<LogsViewModel>();
        services.AddTransient<AboutViewModel>();
        services.AddTransient<UninstallerViewModel>();
        services.AddTransient<SystemOptimizerViewModel>();
        services.AddTransient<SystemDoctorViewModel>();
        services.AddTransient<SoftwareHubViewModel>();
        services.AddTransient<PhoneHubViewModel>();
        services.AddTransient<LanDropViewModel>();
        services.AddTransient<TerminalViewModel>();
        services.AddSingleton<ShareScreenSettingsViewModel>();   // shared by the stage and the Settings page
        services.AddSingleton<ShareScreenViewModel>();   // singleton: a live stream must survive page switches

        // Views
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private void RunSelfTest()
    {
        Console.WriteLine("AxePower Self-Test Running...");
        var sp = ConfigureServices();
        var ds = sp.GetRequiredService<DisplayService>();
        var current = ds.GetCurrentRefreshRate();
        var rates = ds.GetSupportedRefreshRates();
        Console.WriteLine($"DisplayService Initialized: Current {current} Hz, Supported [{string.Join(", ", rates)} Hz]");

        // Reports the plans without creating them; they are created on the first mode apply
        var powerPlans = sp.GetRequiredService<PowerPlanService>();
        var activeMode = Task.Run(powerPlans.GetActivePowerModeAsync).GetAwaiter().GetResult();
        Console.WriteLine(
            $"Power plans: {string.Join(", ", powerPlans.GetPlanPresence().Select(p => $"{p.Plan.Name} {(p.Present ? "present" : "not created yet")}"))}; " +
            $"active mode {activeMode}.");

        // Read-only: asks the firmware whether a MUX exists, changes nothing
        var gameModeSettings = sp.GetRequiredService<SettingsService>().GetSettings();
        sp.GetRequiredService<GameModeGpuService>();
        var displayMode = sp.GetRequiredService<IGpuDisplayModeController>();
        bool muxSupported = Task.Run(displayMode.IsSupportedAsync).GetAwaiter().GetResult();
        Console.WriteLine(
            $"Game Mode GPU: dedicated GPU {(gameModeSettings.GameModeUseDedicatedGpu ? "on" : "off")}, " +
            $"{gameModeSettings.GpuPreferenceBackups.Count} preference(s) to restore; " +
            $"display mode switch {(muxSupported ? "supported" : "not available (non-Lenovo, no MUX, or not elevated)")}.");
        
        var discovery = sp.GetRequiredService<IApplicationDiscoveryService>();
        var apps = Task.Run(async () => await discovery.DiscoverApplicationsAsync()).GetAwaiter().GetResult();
        Console.WriteLine($"Uninstaller Pro Initialized: Discovered {apps.Count} applications.");

        var clock = sp.GetRequiredService<IClock>();
        var music = sp.GetRequiredService<SettingsService>().GetSettings().Music;
        Console.WriteLine($"AxePower Music Initialized: clock {clock.UtcNow:O}, {music.LibraryFolders.Count} library folder(s).");
        var musicPlayer = sp.GetRequiredService<IMusicPlayerService>();
        Console.WriteLine($"Music player resolved: {musicPlayer.GetType().Name}, queue {musicPlayer.State.Queue.Count} item(s).");
        var nowPlaying = sp.GetRequiredService<NowPlayingViewModel>();
        Console.WriteLine($"NowPlayingViewModel resolved: '{nowPlaying.Title}'.");

        var sources = sp.GetRequiredService<ProviderRegistry>();
        var names = sources.All.Select(p => $"{p.Info.Name}{(sources.IsEnabled(p.Info.Id) ? string.Empty : " (off)")}");
        Console.WriteLine(
            $"Music sources: {string.Join(", ", names)}; " +
            $"search via {sources.ActiveId(ProviderKind.Metadata) ?? "none"}, " +
            $"audio via {sources.ActiveId(ProviderKind.Streaming) ?? "none"}.");
        Console.WriteLine(
            $"Stream resolver resolved: {sp.GetRequiredService<IStreamResolver>().GetType().Name}, " +
            $"cache {sp.GetRequiredService<StreamCache>().SizeBytes() / 1024} KB.");

        var musicDiscovery = sp.GetRequiredService<AxePower.Core.Services.Music.DiscoveryService>();
        var shortcutSettings = sp.GetRequiredService<SettingsService>().GetSettings().Music.Shortcuts;
        Console.WriteLine(
            $"Discovery: {(musicDiscovery.IsAvailable ? "available" : "no source")}, " +
            $"{(musicDiscovery.IsEnabled ? "on" : "off")}; variety {sources.Current.DiscoveryVariety}%.");
        Console.WriteLine(
            $"Shortcuts: global {(shortcutSettings.GlobalHotkeysEnabled ? "on" : "off")}, " +
            $"play/pause {shortcutSettings.Resolve(ShortcutAction.PlayPause)}, " +
            $"mini player {shortcutSettings.Resolve(ShortcutAction.ToggleMiniPlayer)}.");
        Console.WriteLine(
            $"Phone portal: token {(sp.GetRequiredService<PortalAccessToken>().Value.Length)} chars, " +
            $"music remote {sp.GetRequiredService<MusicRemote>().GetType().Name} ready.");

        // Reported without opening it: the library is created on first use, and a self-test
        // should not be the thing that brings it into existence.
        var libraryDb = sp.GetRequiredService<LibraryDatabase>();
        Console.WriteLine(
            $"Music library: schema v{LibraryDatabase.SchemaVersion}, " +
            $"{(System.IO.File.Exists(libraryDb.FilePath) ? "present" : "not created yet")} at {libraryDb.FilePath}.");

        var shareDevices = sp.GetRequiredService<IShareDeviceProvider>();
        var shareTools = sp.GetRequiredService<IShareToolProvider>();
        var ffmpeg = shareTools.FindFfmpeg();
        Console.WriteLine(
            $"Share Screen Initialized: {shareDevices.GetDisplays().Count} display(s), " +
            $"{shareDevices.GetRenderDevices().Count} output(s), {shareDevices.GetCaptureDevices().Count} mic(s); " +
            $"FFmpeg {(ffmpeg.IsAvailable ? ffmpeg.Version : "not found")}, " +
            $"media server {(shareTools.FindMediaMtx().IsAvailable ? "ready" : "not downloaded")}.");

        try
        {
            var vm = sp.GetRequiredService<MainViewModel>();
            Console.WriteLine($"MainViewModel resolved successfully. CurrentView={vm.CurrentView?.GetType().Name}");
            var mainWin = sp.GetRequiredService<MainWindow>();
            Console.WriteLine($"MainWindow instantiated successfully: {mainWin.Title}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Self-Test Exception] {ex}");
        }
        
        Environment.ExitCode = 0;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private bool SignalPrimaryInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            client.WriteByte(1);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenForOtherInstances()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                await server.WaitForConnectionAsync();
                int _ = server.ReadByte();
                
                // Read args and dispatch to UI thread
                Dispatcher.Invoke(() =>
                {
                    var mainWindow = MainWindow ?? Current.MainWindow ?? Services?.GetService<MainWindow>();
                    if (mainWindow != null)
                    {
                        if (mainWindow.WindowState == WindowState.Minimized)
                        {
                            mainWindow.WindowState = WindowState.Normal;
                        }
                        mainWindow.Show();
                        mainWindow.Activate();
                        mainWindow.Focus();

                        var helper = new System.Windows.Interop.WindowInteropHelper(mainWindow);
                        if (helper.Handle != IntPtr.Zero)
                        {
                            ShowWindow(helper.Handle, 9); // SW_RESTORE
                            SetForegroundWindow(helper.Handle);
                        }
                    }
                });
            }
            catch
            {
                await Task.Delay(1000);
            }
        }
    }

    private async Task InitializeMusicAsync(IntPtr hwnd)
    {
        try
        {
            // On the UI thread: Windows media controls attach to the window here
            await Services.GetRequiredService<PlayerIntegrationHost>().InitializeAsync(hwnd, CancellationToken.None);

            var player = Services.GetRequiredService<IMusicPlayerService>();
            await Task.Run(() => player.InitializeAsync(CancellationToken.None));

            // Shortcuts and discovery both watch the player, so they start once it has its
            // restored queue — otherwise discovery would fire on the empty startup state.
            Services.GetRequiredService<HotkeyService>().Attach(hwnd);
            Services.GetRequiredService<MusicShortcutHandler>().Start();
            Services.GetRequiredService<DiscoveryService>().Start();
            Services.GetRequiredService<MusicRemote>().Start();
        }
        catch (Exception ex)
        {
            Services.GetService<LogService>()?.LogError("[Music] Startup failed.", ex);
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        if (Services != null)
        {
            try
            {
                // Saves the queue and position, then releases the audio device. Run off the UI
                // thread so its awaits cannot wait on this (blocked) dispatcher.
                var player = Services.GetService<IMusicPlayerService>();
                if (player != null) Task.Run(player.ShutdownAsync).Wait(TimeSpan.FromSeconds(3));
                Services.GetService<PlayerIntegrationHost>()?.Dispose();
            }
            catch { }

            var autoSwitchService = Services.GetService<AutoSwitchService>();
            autoSwitchService?.Stop();

            var appMonitorService = Services.GetService<AppMonitorService>();
            appMonitorService?.Stop();
        }
    }
}
