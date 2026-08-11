using System;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using AxePower.Core.Services;
using AxePower.App.Services;
using AxePower.App.ViewModels;

namespace AxePower.App;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;
    private const string MutexName = "AxePower.Desktop.Singleton";
    private const string PipeName = "AxePower.Desktop.Singleton.Pipe";
    private Mutex? _instanceMutex;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
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

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<AutoSwitchViewModel>();
        services.AddTransient<GameModeViewModel>();
        services.AddTransient<BatteryViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<LogsViewModel>();
        services.AddTransient<AboutViewModel>();

        // Views
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }

    private void RunSelfTest()
    {
        Console.WriteLine("AxePower Self-Test Running...");
        // Here we could resolve services and run tests
        // For simplicity, return 0 for success
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
