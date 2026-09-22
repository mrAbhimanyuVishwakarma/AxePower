using System;
using System.IO.Pipes;
using System.Linq;
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

        // Uninstaller Pro — Core Services
        services.AddSingleton<IApplicationDiscoveryService, ApplicationDiscoveryService>();
        services.AddSingleton<Win32UninstallExecutor>();
        services.AddSingleton<MsiUninstallExecutor>();
        services.AddSingleton<WingetUninstallExecutor>();
        services.AddSingleton<AppxUninstallExecutor>();
        services.AddSingleton<UninstallExecutorFactory>();
        services.AddSingleton<IBatchUninstallService, BatchUninstallService>();
        services.AddSingleton<ILeftoverScannerService, LeftoverScannerService>();
        services.AddSingleton<ILeftoverConfidenceService, LeftoverConfidenceService>();
        services.AddSingleton<IRegistryBackupService, RegistryBackupService>();
        services.AddSingleton<IFileCleanupService, FileCleanupService>();
        services.AddSingleton<IProtectionService, ProtectionService>();
        services.AddSingleton<IUninstallHistoryService, UninstallHistoryService>();
        services.AddSingleton<AxePower.Core.Services.Uninstaller.SystemRestoreService>();
        services.AddSingleton<ProcessDetectionService>();

        // AxePower Music — Core (each phase registers the services it implements)
        services.AddSingleton<IClock, SystemClock>();

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
        
        var discovery = sp.GetRequiredService<IApplicationDiscoveryService>();
        var apps = Task.Run(async () => await discovery.DiscoverApplicationsAsync()).GetAwaiter().GetResult();
        Console.WriteLine($"Uninstaller Pro Initialized: Discovered {apps.Count} applications.");

        var clock = sp.GetRequiredService<IClock>();
        var music = sp.GetRequiredService<SettingsService>().GetSettings().Music;
        Console.WriteLine($"AxePower Music Initialized: clock {clock.UtcNow:O}, {music.LibraryFolders.Count} library folder(s).");

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

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        if (Services != null)
        {
            var autoSwitchService = Services.GetService<AutoSwitchService>();
            autoSwitchService?.Stop();

            var appMonitorService = Services.GetService<AppMonitorService>();
            appMonitorService?.Stop();
        }
    }
}
