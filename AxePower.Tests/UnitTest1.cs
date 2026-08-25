using System;
using System.Threading;
using System.Windows;
using AxePower.App;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AxePower.Tests;

public class AppStartupTests
{
    [Fact]
    public void AppAndAllViews_CanBeInstantiated()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                {
                    try
                    {
                        var app = new AxePower.App.App();
                        app.InitializeComponent();
                    }
                    catch (InvalidOperationException) { }
                }

                if (Application.Current != null)
                {
                    var stylesUri = new Uri("/AxePower.App;component/Resources/Styles.xaml", UriKind.RelativeOrAbsolute);
                    bool hasStyles = false;
                    foreach (var dict in Application.Current.Resources.MergedDictionaries)
                    {
                        if (dict.Source == stylesUri) { hasStyles = true; break; }
                    }
                    if (!hasStyles)
                    {
                        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = stylesUri });
                    }
                }

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

                var sp = (Application.Current as AxePower.App.App)?.Services;
                if (sp != null)
                {
                    var mainWin = sp.GetService<MainWindow>();
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
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx != null)
        {
            throw new Exception($"[STA Thread Exception] {threadEx.Message}\n{threadEx.StackTrace}", threadEx);
        }
    }
}
