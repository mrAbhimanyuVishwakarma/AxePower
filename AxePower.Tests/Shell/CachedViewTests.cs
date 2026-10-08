using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AxePower.App;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.App.Views;
using AxePower.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AxePower.Tests.Shell;

/// <summary>Game Mode's page is built once and reused on every visit (Prompt 29).</summary>
[Collection(WpfCollection.Name)]
public class CachedViewTests
{
    private readonly WpfApplicationFixture _wpf;

    public CachedViewTests(WpfApplicationFixture wpf) => _wpf = wpf;

    [Fact]
    public Task GameModePage_IsTheSameInstance_OnEveryVisit() => _wpf.RunAsync(async () =>
    {
        ShellTestHost.UseAppResources();
        var settings = TestServiceContainer.NewSettingsDirectory();
        var provider = TestServiceContainer.Create(settings).BuildServiceProvider();
        var vm = provider.GetRequiredService<MainViewModel>();
        var window = new MainWindow(vm, provider.GetRequiredService<ThemeService>(), provider.GetRequiredService<SettingsService>(), null)
        {
            Width = 1280, Height = 840, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
            ShowInTaskbar = false, ShowActivated = false,
        };
        try
        {
            window.Show();
            async Task<GameModeView> Visit()
            {
                vm.NavigateCommand.Execute("GameMode");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var views = ShellTestHost.Descendants((FrameworkElement)window.Content).OfType<GameModeView>().ToList();
                Assert.Single(views);
                vm.NavigateCommand.Execute("Settings");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                return views[0];
            }

            var first = await Visit();
            var second = await Visit();
            var third = await Visit();
            Assert.Same(first, second);
            Assert.Same(first, third);
            Assert.Same(vm.GameModeVm, first.DataContext);
        }
        finally
        {
            window.DisposeTrayIcon();
            window.AllowClose();
            window.Close();
            vm.IsOnScreen = false;
            try { Directory.Delete(settings, recursive: true); } catch { }
        }
    });
}
