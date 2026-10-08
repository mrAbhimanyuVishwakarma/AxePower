using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.App.Views;
using AxePower.App.Windows;
using AxePower.Core.Services;
using AxePower.Core.Services.Diagnostics;
using AxePower.Tests.Shell;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AxePower.Tests.Diagnostics;

/// <summary>
/// The Settings card and the inspect-and-export window build and bind without errors under each
/// base skin. Windows are shown off-screen without activation. Set AXEPOWER_RENDER_DIR to also
/// save PNGs for a visual check; the owner still has to look at the real app.
/// </summary>
[Collection(WpfCollection.Name)]
public class DiagnosticsViewRenderTests : IDisposable
{
    private static readonly string[] BaseSkins = { "dark", "light", "glass" };
    private readonly WpfApplicationFixture _wpf;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_DiagRender_" + Guid.NewGuid().ToString("N"));

    public DiagnosticsViewRenderTests(WpfApplicationFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private LogService StoredLog()
    {
        var log = new LogService(new DiagnosticsStore(Path.Combine(_dir, "diag")));
        log.LogInfo("[LanDrop] Local HTTP server started on http://192.168.1.23:5050 (Private network)");
        log.LogWarning("[Music] Tags unreadable", new InvalidOperationException("bad frame"));
        return log;
    }

    private static async Task Settle(UIElement element)
    {
        await Task.Delay(100);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
    }

    private static void SaveIfAsked(Visual visual, int width, int height, string name)
    {
        var dir = Environment.GetEnvironmentVariable("AXEPOWER_RENDER_DIR");
        if (!string.IsNullOrEmpty(dir)) ShellTestHost.SavePng(visual, width, height, Path.Combine(dir, name + ".png"));
    }

    [Fact]
    public Task TheInspectAndExportWindow_BindsWithoutErrors() => _wpf.RunAsync(async () =>
    {
        ShellTestHost.UseAppResources();
        var log = StoredLog();
        var vm = new DiagnosticsViewModel(log, new SettingsService(new LogService(), Path.Combine(_dir, "settings")), new NoPrompts(), Path.Combine(_dir, "none"));
        var errors = new System.Collections.Generic.List<string>();

        foreach (var skin in BaseSkins)
        {
            using var applied = ShellTestHost.ApplySkin(skin);
            var bundle = vm.CreateBundle()!;
            bundle.IncludeSettings = true;
            bundle.SelectedItem = bundle.Items.First(i => i.Name.StartsWith("logs/", StringComparison.Ordinal));
            using var bindings = new ShellTestHost.BindingErrors();
            var window = new DiagnosticsWindow
            {
                DataContext = bundle, Width = 920, Height = 640,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
                ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            await Settle(window);
            SaveIfAsked((Visual)window.Content, 920, 620, $"diagnostics-window-{skin}");
            errors.AddRange(bindings.Errors.Select(e => $"[{skin}] {e}"));
            window.Close();
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    });

    [Fact]
    public Task TheSettingsCard_BindsWithoutErrors() => _wpf.RunAsync(async () =>
    {
        ShellTestHost.UseAppResources();
        var settingsDir = TestServiceContainer.NewSettingsDirectory();
        var services = TestServiceContainer.Create(settingsDir);
        var log = StoredLog();
        services.AddSingleton<IDiagnosticsPrompts, NoPrompts>();
        services.AddTransient(sp => new DiagnosticsViewModel(log, sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<IDiagnosticsPrompts>(), Path.Combine(_dir, "none")));
        using var provider = services.BuildServiceProvider();
        var errors = new System.Collections.Generic.List<string>();

        foreach (var skin in BaseSkins)
        {
            using var applied = ShellTestHost.ApplySkin(skin);
            var vm = provider.GetRequiredService<SettingsViewModel>();
            Assert.NotNull(vm.Diagnostics);
            vm.SelectedCategory = "Diagnostics";
            using var bindings = new ShellTestHost.BindingErrors();
            var window = new Window
            {
                Content = new SettingsView { DataContext = vm }, Width = 1100, Height = 760,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
                ShowInTaskbar = false, ShowActivated = false,
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("WindowBackgroundBrush"),
            };
            window.Show();
            await Settle(window);
            SaveIfAsked((Visual)window.Content, 1100, 760, $"settings-diagnostics-{skin}");
            errors.AddRange(bindings.Errors.Where(e => e.Contains("Diagnostics", StringComparison.Ordinal) || e.Contains("Summary", StringComparison.Ordinal)
                || e.Contains("Retention", StringComparison.Ordinal) || e.Contains("HasFiles", StringComparison.Ordinal) || e.Contains("IsStored", StringComparison.Ordinal)
                || e.Contains("InspectCommand", StringComparison.Ordinal) || e.Contains("DeleteAllCommand", StringComparison.Ordinal)).Select(e => $"[{skin}] {e}"));
            window.Close();
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        try { Directory.Delete(settingsDir, true); } catch (IOException) { }
    });

    private sealed class NoPrompts : IDiagnosticsPrompts
    {
        public bool ConfirmDeleteAll(DiagnosticsSummary summary) => false;
        public string? ChooseBundlePath(string suggestedName) => null;
        public void ShowBundle(DiagnosticsBundleViewModel bundle) { }
    }
}
