using System.IO;
using Microsoft.Extensions.DependencyInjection;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

[Collection(WpfCollection.Name)]
public class DependencyInjectionTests
{
    private readonly WpfApplicationFixture _wpf;

    public DependencyInjectionTests(WpfApplicationFixture wpf) => _wpf = wpf;

    [Fact]
    public void AllUninstallerServices_CanBeResolvedFromContainer()
    {
        // MainViewModel and the view models it builds read Application.Current and create
        // dispatcher-bound objects. Constructing them off the UI thread while a live WPF
        // Application exists wedges that Application's dispatcher, so the whole graph is built
        // on the fixture's STA thread instead.
        _wpf.Invoke(() =>
        {
            var services = new ServiceCollection();

            // Core Services
            services.AddSingleton<LogService>();
            // SettingsViewModel saves as soon as its properties initialise, so the container
            // gets a SettingsService pointing at a temp folder: a test must never rewrite the
            // user's real settings.json.
            var settingsDir = Path.Combine(Path.GetTempPath(), "AxePower.Tests", "di-" + Guid.NewGuid().ToString("N"));
            services.AddSingleton(sp => new SettingsService(sp.GetRequiredService<LogService>(), settingsDir));
            services.AddSingleton<BatteryService>();
            services.AddSingleton<PowerPlanService>();
            services.AddSingleton<NotificationService>();
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
            services.AddTransient<SoftwareHubViewModel>();
            services.AddTransient<PhoneHubViewModel>();
            services.AddTransient<LanDropViewModel>();
            services.AddTransient<TerminalViewModel>();
            services.AddTransient<MainViewModel>();

            var provider = services.BuildServiceProvider();

            // Every uninstaller service resolves (multi-constructor types pick the injectable one)
            Assert.NotNull(provider.GetRequiredService<IBatchUninstallService>());
            Assert.NotNull(provider.GetRequiredService<ILeftoverScannerService>());
            Assert.NotNull(provider.GetRequiredService<ILeftoverCleanupService>());
            Assert.NotNull(provider.GetRequiredService<UninstallExecutorFactory>());

            var mainVm = provider.GetRequiredService<MainViewModel>();
            Assert.NotNull(mainVm);
            Assert.NotNull(mainVm.UninstallerVm);
            Assert.NotNull(mainVm.OptimizerVm);
            Assert.NotNull(mainVm.DoctorVm);
            Assert.NotNull(mainVm.SoftwareHubVm);
            Assert.NotNull(mainVm.PhoneHubVm);

            // Test navigation
            mainVm.NavigateCommand.Execute("PhoneControl");
            Assert.Equal(mainVm.PhoneHubVm, mainVm.CurrentView);
            mainVm.NavigateCommand.Execute("LanDrop");
            Assert.Equal(mainVm.PhoneHubVm, mainVm.CurrentView);
            mainVm.NavigateCommand.Execute("Uninstaller");
            Assert.Equal(mainVm.UninstallerVm, mainVm.CurrentView);

            mainVm.NavigateCommand.Execute("Optimizer");
            Assert.Equal(mainVm.OptimizerVm, mainVm.CurrentView);

            mainVm.NavigateCommand.Execute("Doctor");
            Assert.Equal(mainVm.DoctorVm, mainVm.CurrentView);

            mainVm.NavigateCommand.Execute("SoftwareHub");
            Assert.Equal(mainVm.SoftwareHubVm, mainVm.CurrentView);

            mainVm.NavigateCommand.Execute("UpdatePc");
            Assert.Equal(mainVm.SoftwareHubVm, mainVm.CurrentView);

            mainVm.NavigateCommand.Execute("Dashboard");
            Assert.Equal(mainVm.DashboardVm, mainVm.CurrentView);

            mainVm.NavigateCommand.Execute("About");
            Assert.Equal(mainVm.AboutVm, mainVm.CurrentView);

            // SystemTelemetryService logs from a one-second timer. Left running, it keeps logging
            // after this collection's dispatcher is gone. Only it is disposed: disposing the whole
            // container would also tear down services whose Dispose blocks (hardware monitor,
            // LAN drop) and stall the shared STA thread.
            provider.GetService<SystemTelemetryService>()?.Dispose();
            try { Directory.Delete(settingsDir, recursive: true); } catch { }
        });
    }

    [Fact]
    public void UninstallerView_CanBeInstantiated_WithoutExceptions()
    {
        // The shared fixture owns the WPF Application and keeps its dispatcher thread alive;
        // see WpfApplicationFixture for why creating one per test takes the test host down.
        _wpf.Invoke(() =>
        {
            var view = new AxePower.App.Views.UninstallerView();
            Assert.NotNull(view);
        });
    }
}
