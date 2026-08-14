using System;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.App.Helpers;

namespace AxePower.App;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;
    private const string MutexName = "AxePower.Desktop.Singleton";
    private const string PipeName = "AxePower.Desktop.Singleton.Pipe";
    private Mutex? _instanceMutex;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Initialize smooth proportional touchpad & mouse wheel scrolling globally
        TouchpadScrollHelper.Initialize();

        // Handle CLI arguments early
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTest();
            Environment.Exit(0);
            return;
        }

        // Single instance check
        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Send arguments to the primary instance if needed, then exit
            SignalPrimaryInstance(e.Args);
            Environment.Exit(0);
            return;
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

        Services = ConfigureServices();

        var settingsService = Services.GetRequiredService<SettingsService>();
        settingsService.LoadSettings();

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
        
        bool startBackground = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var settings = settingsService.GetSettings();
        // Force show the main window during development
        mainWindow.Show();

        // Start listening for other instances
        Task.Run(ListenForOtherInstances);
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core Services
        services.AddSingleton<LogService>();
        services.AddSingleton<SettingsService>();
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
        services.AddSingleton<UpdateService>();

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
        services.AddSingleton<SystemRestoreService>();
        services.AddSingleton<ProcessDetectionService>();

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

        // Views
        services.AddTransient<MainWindow>();

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
        
        Environment.ExitCode = 0;
    }

    private void SignalPrimaryInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            // Send args
        }
        catch
        {
            // Ignore
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
                
                // Read args and dispatch to UI thread
                Dispatcher.Invoke(() =>
                {
                    var mainWindow = MainWindow;
                    if (mainWindow != null)
                    {
                        if (mainWindow.WindowState == WindowState.Minimized)
                        {
                            mainWindow.WindowState = WindowState.Normal;
                        }
                        mainWindow.Show();
                        mainWindow.Activate();
                    }
                });
            }
            catch
            {
                // Ignore pipe errors
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
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
    }
}
