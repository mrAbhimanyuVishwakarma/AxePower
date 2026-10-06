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
using AxePower.Core.Services.Operations;
using AxePower.Tests.Shell;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AxePower.Tests.Operations;

/// <summary>
/// The change preview and Settings › Change history build and bind without errors under each base
/// skin, with all three reversibility levels on screen. Set AXEPOWER_RENDER_DIR to save PNGs.
/// </summary>
[Collection(WpfCollection.Name)]
public class OperationViewRenderTests
{
    private static readonly string[] BaseSkins = { "dark", "light", "glass" };
    private readonly WpfApplicationFixture _wpf;

    public OperationViewRenderTests(WpfApplicationFixture wpf) => _wpf = wpf;

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

    /// <summary>A tweak (fully), a service pause (partially) and a recorded irreversible step.</summary>
    private static async Task Populate(OperationTestKit kit)
    {
        kit.Services.Installed["WSearch"] = new ServiceSnapshot(ServiceStart.AutomaticDelayed, Running: true);
        var tweaks = new TweakService(kit.Log, kit.Runner, kit.Root);
        await tweaks.ExecuteAsync(tweaks.PlanApply("gamedvr-disable"));
        var services = new SystemServiceOptimizer(kit.Log, kit.Runner);
        await services.ExecuteAsync(services.PlanPause(new[] { "WSearch" }));
        kit.Runner.Journal.Save(new OperationRecord
        {
            Action = "Remove Xbox app (example)", SourceFeature = "Debloat", Subject = "example", AppVersion = "2.1.0",
            CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow, Status = OperationStatus.Succeeded,
            Steps = { new OperationStep { Kind = "registry.value", Target = "x", Description = "Package Microsoft.XboxApp: installed → removed", Status = StepStatus.Succeeded, Reversibility = Reversibility.Irreversible, ReversibilityNote = "Reinstall it from the Microsoft Store if you need it again." } },
        });
    }

    [Fact]
    public Task ThePreview_BindsWithoutErrors() => _wpf.RunAsync(async () =>
    {
        ShellTestHost.UseAppResources();
        using var kit = new OperationTestKit();
        kit.Services.Installed["WSearch"] = new ServiceSnapshot(ServiceStart.AutomaticDelayed, Running: true);
        var tweaks = new TweakService(kit.Log, kit.Runner, kit.Root);
        var plans = new[] { tweaks.PlanApply("gamedvr-disable"), tweaks.PlanRevert("sticky-keys-disable"), new SystemServiceOptimizer(kit.Log, kit.Runner).PlanPause(new[] { "WSearch" }) };
        var errors = new System.Collections.Generic.List<string>();

        foreach (var skin in BaseSkins)
        {
            using var applied = ShellTestHost.ApplySkin(skin);
            using var bindings = new ShellTestHost.BindingErrors();
            var window = new OperationPreviewWindow
            {
                DataContext = new OperationPreviewViewModel(plans), Width = 760, Height = 900,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            await Settle(window);
            SaveIfAsked((Visual)window.Content, 760, 880, $"preview-{skin}");
            errors.AddRange(bindings.Errors.Select(e => $"[{skin}] {e}"));
            window.Close();
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    });

    [Fact]
    public Task TheChangeHistoryCard_BindsWithoutErrors() => _wpf.RunAsync(async () =>
    {
        ShellTestHost.UseAppResources();
        using var kit = new OperationTestKit();
        await Populate(kit);
        var settingsDir = TestServiceContainer.NewSettingsDirectory();
        var services = TestServiceContainer.Create(settingsDir);
        services.AddSingleton<IOperationPrompts>(new NeverApprove());
        services.AddTransient(sp => new ChangeHistoryViewModel(kit.Runner, sp.GetRequiredService<IOperationPrompts>()));
        using var provider = services.BuildServiceProvider();
        var errors = new System.Collections.Generic.List<string>();

        foreach (var skin in BaseSkins)
        {
            using var applied = ShellTestHost.ApplySkin(skin);
            var vm = provider.GetRequiredService<SettingsViewModel>();
            Assert.NotNull(vm.ChangeHistory);
            foreach (var operation in vm.ChangeHistory!.Operations) operation.IsExpanded = true;
            vm.SelectedCategory = "ChangeHistory";
            using var bindings = new ShellTestHost.BindingErrors();
            var window = new Window
            {
                Content = new SettingsView { DataContext = vm }, Width = 1100, Height = 1100,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false,
                Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush"),
            };
            window.Show();
            await Settle(window);
            SaveIfAsked((Visual)window.Content, 1100, 1100, $"history-{skin}");
            errors.AddRange(bindings.Errors.Where(e => e.Contains("Operation", StringComparison.Ordinal) || e.Contains("Reversibility", StringComparison.Ordinal)
                || e.Contains("ChangeHistory", StringComparison.Ordinal) || e.Contains("RollBack", StringComparison.Ordinal) || e.Contains("Steps", StringComparison.Ordinal))
                .Select(e => $"[{skin}] {e}"));
            window.Close();
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        try { Directory.Delete(settingsDir, true); } catch (IOException) { }
    });

    private sealed class NeverApprove : IOperationPrompts
    {
        public bool Approve(System.Collections.Generic.IReadOnlyList<OperationRecord> plans) => false;
    }
}
