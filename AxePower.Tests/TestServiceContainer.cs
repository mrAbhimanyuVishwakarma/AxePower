using System.IO;
using Microsoft.Extensions.DependencyInjection;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests;

/// <summary>
/// A copy of the app's service registrations for tests that need the real view-model graph
/// (MainViewModel, MainWindow), with settings in <paramref name="settingsDir"/> instead of the
/// user's settings.json. Build it on the WPF fixture's thread.
/// </summary>
internal static class TestServiceContainer
{
    public static ServiceCollection Create(string settingsDir)
    {
        var services = new ServiceCollection();

        // Core Services
        services.AddSingleton<LogService>();
        // SettingsViewModel saves as soon as its properties initialise, so the container
        // gets a SettingsService pointing at a temp folder: a test must never rewrite the
        // user's real settings.json.
        services.AddSingleton(sp => new SettingsService(sp.GetRequiredService<LogService>(), settingsDir));
        services.AddSingleton<BatteryService>();
        services.AddSingleton<PowerPlanService>();
        services.AddSingleton(sp => new NotificationService(sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<IAlertSoundService, WindowsAlertSoundService>();
        services.AddSingleton<StartupService>();
        services.AddSingleton<AutoSwitchService>();
        services.AddSingleton<AppKillerService>();
        services.AddSingleton<TimeoutService>();
        services.AddSingleton<AppMonitorService>();
        services.AddSingleton<GameScannerService>();
        services.AddSingleton<AxePower.Core.Services.Skins.SkinCatalog>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<SystemServiceOptimizer>();
        services.AddSingleton<DisplayService>();
        services.AddSingleton<DriverUpdateService>();
        services.AddSingleton<MsiAfterburnerService>();
        services.AddSingleton<ToolsService>();
        services.AddSingleton<DebloatService>();
        services.AddSingleton<AndroidControlService>();
        services.AddSingleton<ControllerNavigationService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<ITweakService, TweakService>();
        services.AddSingleton<ICacheCleanerService, CacheCleanerService>();
        services.AddSingleton<IDnsOptimizerService, DnsOptimizerService>();
        services.AddSingleton<ISystemDoctorService, SystemDoctorService>();
        services.AddSingleton<IWindowsUpdateShieldService, WindowsUpdateShieldService>();
        // Software Hub: allow-listed programs, launches as the signed-in user, catalog, installs
        services.AddSingleton(_ => AxePower.Core.Services.Processes.ExecutableAllowList.Windows());
        services.AddSingleton<AxePower.Core.Services.Processes.IProcessRunner, AxePower.Core.Services.Processes.AllowListedProcessRunner>();
        services.AddSingleton<AxePower.Core.Services.Processes.IUserShellLauncher>(sp => new AxePower.Core.Services.Processes.UserShellLauncher(
            sp.GetRequiredService<LogService>(), sp.GetRequiredService<AxePower.Core.Services.Processes.ExecutableAllowList>()));
        services.AddSingleton(sp => AxePower.Core.Services.SoftwareHub.SoftwareCatalog.CreateDefault(sp.GetRequiredService<LogService>()));
        services.AddSingleton<AxePower.Core.Services.SoftwareHub.IPortableToolDownloader>(sp => new AxePower.Core.Services.SoftwareHub.GitHubPortableToolDownloader(sp.GetRequiredService<LogService>()));
        services.AddSingleton<IWinGetHubService>(sp => new WinGetHubService(
            sp.GetRequiredService<LogService>(),
            sp.GetRequiredService<AxePower.Core.Services.SoftwareHub.SoftwareCatalog>(),
            sp.GetRequiredService<AxePower.Core.Services.Processes.IProcessRunner>(),
            sp.GetRequiredService<AxePower.Core.Services.Processes.IUserShellLauncher>(),
            sp.GetRequiredService<AxePower.Core.Services.SoftwareHub.IPortableToolDownloader>()));
        services.AddSingleton<IServiceThrottlerService, ServiceThrottlerService>();
        services.AddSingleton<IMemoryOptimizerService, MemoryOptimizerService>();
        services.AddSingleton<IDynamicModeAutomationService, DynamicModeAutomationService>();
        services.AddSingleton<LanDropService>();
        services.AddSingleton<FirmwareUpdateService>();
        services.AddSingleton<GraphicsDriverService>();
        services.AddSingleton<SystemTelemetryService>();
        services.AddSingleton<TerminalService>();
        services.AddSingleton<RestorePointService>();
        services.AddSingleton<ElevationService>();
        services.AddSingleton<HardwareMonitorService>();

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
        services.AddSingleton<SystemRestoreService>();
        services.AddSingleton<IUninstallerPrompts, MessageBoxUninstallerPrompts>();

        // ViewModels
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
        services.AddTransient<SystemCareViewModel>();
        services.AddTransient<DebloatViewModel>();
        services.AddTransient<SoftwareHubViewModel>();
        services.AddTransient<ToolsViewModel>();
        services.AddTransient<PhoneHubViewModel>();
        services.AddTransient<LanDropViewModel>();
        services.AddTransient<TerminalViewModel>();
        services.AddTransient<MainViewModel>();
        return services;
    }

    public static string NewSettingsDirectory() =>
        Path.Combine(Path.GetTempPath(), "AxePower.Tests", "di-" + Guid.NewGuid().ToString("N"));
}
