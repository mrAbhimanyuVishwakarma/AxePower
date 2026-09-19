using System.Windows;
using AxePower.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AxePower.Tests;

[Collection(WpfCollection.Name)]
public class AppStartupTests
{
    private readonly WpfApplicationFixture _wpf;

    public AppStartupTests(WpfApplicationFixture wpf) => _wpf = wpf;

    [Fact]
    public void AppAndAllViews_CanBeInstantiated()
    {
        _wpf.Invoke(() =>
        {
            // Instantiate all views
            var dashboardView = new AxePower.App.Views.DashboardView();
            var batteryView = new AxePower.App.Views.BatteryView();
            var settingsView = new AxePower.App.Views.SettingsView();
            var aboutView = new AxePower.App.Views.AboutView();
            var gameModeView = new AxePower.App.Views.GameModeView();
            var logsView = new AxePower.App.Views.LogsView();
            var uninstallerView = new AxePower.App.Views.UninstallerView();
            var softwareHubView = new AxePower.App.Views.SoftwareHubView();
            var systemOptimizerView = new AxePower.App.Views.SystemOptimizerView();
            var systemDoctorView = new AxePower.App.Views.SystemDoctorView();
            var phoneHubView = new AxePower.App.Views.PhoneHubView();
            var lanDropView = new AxePower.App.Views.LanDropView();
            var autoSwitchView = new AxePower.App.Views.AutoSwitchView();
            var terminalView = new AxePower.App.Views.TerminalView();

            if (_wpf.Services != null)
            {
                var mainWin = _wpf.Services.GetService<AxePower.App.MainWindow>();
                Assert.NotNull(mainWin);
            }

            Assert.NotNull(dashboardView);
            Assert.NotNull(batteryView);
            Assert.NotNull(settingsView);
            Assert.NotNull(aboutView);
            Assert.NotNull(gameModeView);
            Assert.NotNull(logsView);
            Assert.NotNull(uninstallerView);
            Assert.NotNull(softwareHubView);
            Assert.NotNull(systemOptimizerView);
            Assert.NotNull(systemDoctorView);
            Assert.NotNull(phoneHubView);
            Assert.NotNull(lanDropView);
            Assert.NotNull(autoSwitchView);
            Assert.NotNull(terminalView);
        });
    }
}
