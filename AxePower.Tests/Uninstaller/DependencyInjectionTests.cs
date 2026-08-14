using Microsoft.Extensions.DependencyInjection;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

public class DependencyInjectionTests
{
    [Fact]
    public void AllUninstallerServices_CanBeResolvedFromContainer()
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
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<AutoSwitchViewModel>();
        services.AddTransient<GameModeViewModel>();
        services.AddTransient<BatteryViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<LogsViewModel>();
        services.AddTransient<AboutViewModel>();
        services.AddTransient<UninstallerViewModel>();
        services.AddTransient<MainViewModel>();

        var provider = services.BuildServiceProvider();

        var mainVm = provider.GetRequiredService<MainViewModel>();
        Assert.NotNull(mainVm);
        Assert.NotNull(mainVm.UninstallerVm);

        // Test navigation
        mainVm.NavigateCommand.Execute("Uninstaller");
        Assert.Equal(mainVm.UninstallerVm, mainVm.CurrentView);

        mainVm.NavigateCommand.Execute("Dashboard");
        Assert.Equal(mainVm.DashboardVm, mainVm.CurrentView);

        mainVm.NavigateCommand.Execute("About");
        Assert.Equal(mainVm.AboutVm, mainVm.CurrentView);

        mainVm.NavigateCommand.Execute("Uninstaller");
        Assert.Equal(mainVm.UninstallerVm, mainVm.CurrentView);
    }

    [Fact]
    public void UninstallerView_CanBeInstantiated_WithoutExceptions()
    {
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                // Ensure Application instance exists for resource resolution
                if (System.Windows.Application.Current is null)
                {
                    var app = new System.Windows.Application();
                }

                var view = new AxePower.App.Views.UninstallerView();
                Assert.NotNull(view);
            }
            catch (Exception ex)
            {
                Assert.Fail($"UninstallerView instantiation failed: {ex}");
            }
        });

        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(5));
    }
}
