using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AxePower.App.ViewModels;
using AxePower.App.Views;
using AxePower.App.Views.SoftwareHub;
using AxePower.Core.Services;
using AxePower.Core.Services.Processes;
using AxePower.Core.Services.SoftwareHub;
using AxePower.Tests.SoftwareHub;
using Xunit;

namespace AxePower.Tests.Shell;

[Collection(WpfCollection.Name)]
public sealed class PcUpdatesViewTests
{
    private static readonly WindowsUpdateCandidate Security =
        new("2026-11 Cumulative Update (KB1234567)", "Windows Update catalog · KB1234567", false);
    private static readonly WindowsUpdateCandidate Device =
        new("System Firmware", "Firmware update · Optional", true, IsOptional: true);
    private static readonly WinGetUpdateCandidate Vlc =
        new("VLC media player", "VideoLAN.VLC", "3.0.20", "3.0.23", "winget", true);
    private static readonly WinGetUpdateCandidate Zip =
        new("7-Zip", "7zip.7zip", "24.08", "26.00", "winget", true);
    private readonly WpfApplicationFixture _wpf;

    public PcUpdatesViewTests(WpfApplicationFixture wpf) => _wpf = wpf;

    [Fact]
    public Task UpdateReviewKeepsPartialResults_AndDecliningDoesNotStartAnInstaller() => _wpf.RunAsync(async () =>
    {
        using var rig = new HubRig();
        var windows = new WindowsCatalog { Result = new(new[] { Security, Device }, true, false) };
        var apps = new WinGetCatalog { Result = new(new[] { Vlc }, true, "Store unavailable.", true, IsPartial: true) };
        var confirm = new SoftwareHubViewModelTests.Confirmations { Answer = false };
        var vm = Build(rig, windows, apps, confirm);

        Assert.False(vm.CanUpgradeAllApps);
        Assert.Equal("Last checked: Never", vm.LastPcCheck);
        await vm.CheckAllUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(Security, Assert.Single(vm.WindowsUpdates));
        Assert.Equal(Device, Assert.Single(vm.DeviceUpdates));
        Assert.Contains("partly complete", vm.PcUpdateProgress);
        Assert.Contains("Store unavailable", vm.AppUpdateSummary);
        Assert.NotEqual("Last checked: Never", vm.LastPcCheck);
        Assert.True(vm.CanUpgradeAllApps);
        Assert.Empty(apps.Selected);

        Assert.Single(vm.AppUpdates).IsSelected = true;
        await vm.UpgradeSelectedAppsCommand.ExecuteAsync(null);
        var prompt = Assert.Single(confirm.Asked);
        Assert.Contains(prompt.Affected, item => item.Contains("VideoLAN.VLC", StringComparison.Ordinal));
        Assert.Contains(prompt.Affected, item => item.Contains("agreements", StringComparison.Ordinal));
        Assert.Empty(apps.Selected);
        Assert.Empty(vm.AppUpdateResults);
    });

    [Fact]
    public Task ConfirmedRestartStopsTheQueue_AndUnknownWindowsRestartDoesNotClearPriorSignal() => _wpf.RunAsync(async () =>
    {
        using var rig = new HubRig();
        var windows = new WindowsCatalog { Result = new(new[] { Security }, true, true) };
        var apps = new WinGetCatalog { Result = new(new[] { Vlc, Zip }, true, null, true) };
        var vm = Build(rig, windows, apps);

        await vm.CheckAllUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.PcRestartRequired);
        windows.Result = new(Array.Empty<WindowsUpdateCandidate>(), false, null);
        await vm.CheckAllUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.PcRestartRequired);
        foreach (var app in vm.AppUpdates) app.IsSelected = true;
        apps.Apply = _ => Task.FromResult(new WinGetUpdateResult(false, true, "Installer requested a restart.", WinGetUpdateOutcome.RestartRequired));

        await vm.UpgradeSelectedAppsCommand.ExecuteAsync(null);

        Assert.Equal(Vlc, Assert.Single(apps.Selected));
        Assert.Equal(new[] { "Restart required", "Skipped" }, vm.AppUpdateResults.Select(x => x.Status));
        Assert.True(vm.PcRestartRequired);
        await vm.CheckAllUpdatesCommand.ExecuteAsync(null);
        Assert.False(vm.CanReviewAppUpdates);
        Assert.False(vm.CanUpgradeAllApps);
    });

    [Fact]
    public Task BulkUpgradeUsesTheVisibleOptIn_AndReportsOnlyTheAggregateResult() => _wpf.RunAsync(async () =>
    {
        using var rig = new HubRig();
        var apps = new WinGetCatalog { Result = new(new[] { Vlc }, true, null, true) };
        var confirm = new SoftwareHubViewModelTests.Confirmations();
        var vm = Build(rig, new WindowsCatalog(), apps, confirm);

        await vm.CheckAllUpdatesCommand.ExecuteAsync(null);
        Assert.False(vm.IncludeUnknownAppVersions);
        await vm.UpgradeAllAppsCommand.ExecuteAsync(null);
        Assert.False(Assert.Single(apps.Bulk));
        Assert.Equal("WinGet bulk update", Assert.Single(vm.AppUpdateResults).Name);
        Assert.Null(vm.LastAppUpdate);
        Assert.False(vm.CanUpgradeAllApps);
        Assert.Contains(confirm.Asked[0].Affected, line => line.StartsWith("Skip apps", StringComparison.Ordinal));

        await vm.CheckAllUpdatesCommand.ExecuteAsync(null);
        vm.IncludeUnknownAppVersions = true;
        await vm.UpgradeAllAppsCommand.ExecuteAsync(null);
        Assert.Equal(new[] { false, true }, apps.Bulk);
        Assert.Contains(confirm.Asked[1].Affected, line => line.StartsWith("Include apps", StringComparison.Ordinal));
    });

    [Fact]
    public Task UpdateHandoffsRequestOnlyTheExplicitTargets() => _wpf.RunAsync(() =>
    {
        using var rig = new HubRig();
        var vm = Build(rig, new WindowsCatalog(), new WinGetCatalog());
        vm.OpenWindowsUpdateSettingsCommand.Execute(null);
        vm.OpenWindowsOptionalUpdatesCommand.Execute(null);
        vm.OpenWindowsRestartOptionsCommand.Execute(null);
        vm.OpenStoreUpdatesCommand.Execute(null);

        Assert.Equal(new[]
        {
            "ms-settings:windowsupdate", "ms-settings:windowsupdate-optionalupdates", "ms-settings:windowsupdate-restartoptions",
            "ms-windows-store://downloadsandupdates"
        }, rig.Launcher.Launches.Select(x => x.Target));
        Assert.All(rig.Launcher.Launches, x => Assert.True(UserShellLauncher.TryValidateUri(x.Target, UserShellLauncher.DefaultSchemes, out _, out _)));
        Assert.False(UserShellLauncher.TryValidateUri("ms-settings:appsfeatures", UserShellLauncher.DefaultSchemes, out _, out _));
        Assert.False(UserShellLauncher.TryValidateUri("ms-settings:windowsupdate?target=other", UserShellLauncher.DefaultSchemes, out _, out _));
        return Task.CompletedTask;
    });

    [Fact]
    public Task UpdateAndToolInstallActionsCannotCompete() => _wpf.RunAsync(async () =>
    {
        using var rig = new HubRig();
        var finish = new TaskCompletionSource<WinGetUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var apps = new WinGetCatalog { Result = new(new[] { Vlc }, true, null, true), Apply = _ => finish.Task };
        var vm = Build(rig, new WindowsCatalog(), apps);
        var tool = vm.AllPackages.Single(x => x.Id == "vlc");
        await vm.CheckAllUpdatesCommand.ExecuteAsync(null);
        Assert.Single(vm.AppUpdates).IsSelected = true;
        tool.IsBusy = true;
        Assert.False(vm.CanUpgradeSelectedApps);
        tool.IsBusy = false;
        Assert.True(vm.CanUpgradeSelectedApps);

        var update = vm.UpgradeSelectedAppsCommand.ExecuteAsync(null);
        try
        {
            tool.IsSelected = true;
            await tool.InstallAsync();
            await vm.InstallSelectedDirectCommand.ExecuteAsync(null);
            Assert.True(vm.IsApplyingPcUpdates);
            Assert.Empty(rig.Runner.Commands);
            Assert.Equal(Vlc, Assert.Single(apps.Selected));
        }
        finally
        {
            finish.TrySetResult(new(true, false, "Updated.", WinGetUpdateOutcome.Applied));
            await update;
        }
    });

    [Fact]
    public Task UpdateOverviewBindsAtNarrowAndWideLayouts() => _wpf.RunAsync(async () =>
    {
        ShellTestHost.UseAppResources();
        using var skin = ShellTestHost.ApplySkin("dark");
        using var errors = new ShellTestHost.BindingErrors();
        using var rig = new HubRig();
        var vm = Build(rig,
            new WindowsCatalog { Result = new(new[] { Security, Device }, true, false) },
            new WinGetCatalog { Result = new(new[] { Vlc, Zip }, true, null, true) });
        vm.GraphicsAdapters.Clear();
        vm.GraphicsAdapters.Add(new GraphicsAdapterInfo
        {
            Name = "NVIDIA GeForce RTX 4060 Laptop GPU",
            Vendor = GpuVendor.Nvidia,
            VendorName = "NVIDIA",
            DriverVersion = "566.36",
            IsDedicated = true,
            ToolName = "NVIDIA App",
            StoreId = "9N86K0Q4Z859",
            CompanionStoreId = "9NF8H0H7WMLT",
            CompanionAppName = "NVIDIA Control Panel",
            AccentColorHex = "#76B900"
        });
        vm.HasGraphicsAdapters = true;
        var view = new SoftwareHubView { DataContext = vm };
        var surface = new Border { Child = view, Height = 830, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top };
        surface.SetResourceReference(Border.BackgroundProperty, "CardBackgroundBrush");
        var window = new Window
        {
            Content = surface, Width = 1148, Height = 878, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
        };

        window.Show();
        try
        {
            var updater = ShellTestHost.Descendants(view).OfType<UpdatersTab>().Single();
            var graphics = Assert.IsAssignableFrom<FrameworkElement>(updater.FindName("UpdateHeroGraphics"));
            var windows = Assert.IsType<Expander>(updater.FindName("WindowsGroup"));
            var apps = Assert.IsType<Expander>(updater.FindName("AppsGroup"));
            var drivers = Assert.IsType<Expander>(updater.FindName("DriversGroup"));
            var firmware = Assert.IsType<Expander>(updater.FindName("FirmwareGroup"));
            var rows = Assert.IsAssignableFrom<ItemsControl>(updater.FindName("AppUpdateRows"));
            Capture("idle", graphics);
            await vm.CheckAllUpdatesCommand.ExecuteAsync(null);
            Assert.True(vm.CanReviewAppUpdates);
            vm.AppUpdates[0].IsSelected = true;
            Capture("review", graphics);

            var appsToggle = Assert.IsType<ToggleButton>(apps.Template.FindName("GroupToggle", apps));
            appsToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            SizeSurface(1100);
            Assert.False(apps.IsExpanded);
            Assert.False(rows.IsVisible);

            SizeSurface(760);
            Assert.True(rows.IsVisible);
            Assert.True(SelectedApp().IsChecked);
            Assert.False(appsToggle.IsVisible);
            Assert.True(Body(windows).IsVisible);
            Assert.True(Body(firmware).IsVisible);
            Assert.True(Body(drivers).IsVisible);
            Assert.Equal(2, Grid.GetRow(firmware));
            Assert.Equal(3, Grid.GetRow(drivers));

            SizeSurface(1100);
            Assert.False(rows.IsVisible);
            Assert.True(appsToggle.IsVisible);
            Assert.False(Body(windows).IsVisible);
            Assert.Equal(2, Grid.GetRow(drivers));
            Assert.Equal(3, Grid.GetRow(firmware));
            appsToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            SizeSurface(1100);
            Assert.True(rows.IsVisible);
            Assert.True(SelectedApp().IsChecked);

            await vm.UpgradeSelectedAppsCommand.ExecuteAsync(null);
            Assert.Equal("Updated", Assert.Single(vm.AppUpdateResults).Status);
            Capture("results", graphics);
            Assert.Empty(errors.Errors);
        }
        finally
        {
            window.Close();
        }

        static FrameworkElement Body(Expander group) =>
            Assert.IsAssignableFrom<FrameworkElement>(group.Template.FindName("GroupBody", group));

        CheckBox SelectedApp() => ShellTestHost.Descendants(view).OfType<CheckBox>()
            .Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Select VLC media player update");

        void SizeSurface(int width)
        {
            // Size the surface in DIPs, independently of window chrome or monitor DPI.
            surface.Width = width;
            window.Width = width + 48;
            ShellTestHost.Layout(surface, width, 830);
            Assert.Equal(width, surface.ActualWidth, precision: 0);
        }

        void Capture(string state, FrameworkElement graphics)
        {
            var folder = Environment.GetEnvironmentVariable("AXEPOWER_UPDATE_CAPTURE_DIR");
            foreach (int width in new[] { 760, 1100 })
            {
                SizeSurface(width);
                Assert.True(graphics.IsVisible);
                Assert.Equal(width == 760 ? 1 : 0, Grid.GetRow(graphics));
                Assert.Equal(width == 760 ? 0 : 1, Grid.GetColumn(graphics));
                if (!string.IsNullOrWhiteSpace(folder))
                    ShellTestHost.SavePng(surface, width, 830, Path.Combine(folder, $"{state}-{width}.png"));
            }
        }
    });

    private static SoftwareHubViewModel Build(HubRig rig, WindowsCatalog windows, WinGetCatalog apps,
        SoftwareHubViewModelTests.Confirmations? confirm = null)
    {
        var log = new LogService();
        return new SoftwareHubViewModel(rig.Hub, new WindowsUpdateShieldService(log), new DriverUpdateService(log),
            new FirmwareUpdateService(log, rig.Launcher, rig.Runner), new NotificationService(), log,
            new GraphicsDriverService(log, rig.Launcher), new TerminalService(), new RestorePointService(log, rig.Runner),
            confirm ?? new SoftwareHubViewModelTests.Confirmations(), windows, apps, rig.Launcher);
    }

    private sealed class WindowsCatalog : IWindowsUpdateCatalogService
    {
        public WindowsUpdateScanResult Result { get; set; } = new(Array.Empty<WindowsUpdateCandidate>(), true, false);
        public Task<WindowsUpdateScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    private sealed class WinGetCatalog : IWinGetUpdateService
    {
        public WinGetUpdateScanResult Result { get; init; } = new(Array.Empty<WinGetUpdateCandidate>(), true, null, true);
        public List<WinGetUpdateCandidate> Selected { get; } = new();
        public List<bool> Bulk { get; } = new();
        public Func<WinGetUpdateCandidate, Task<WinGetUpdateResult>> Apply { get; set; } =
            _ => Task.FromResult(new WinGetUpdateResult(true, false, "WinGet completed the update.", WinGetUpdateOutcome.Applied));

        public Task<WinGetUpdateScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result);

        public Task<WinGetUpdateResult> UpgradeAsync(WinGetUpdateCandidate candidate, IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            Selected.Add(candidate);
            return Apply(candidate);
        }

        public Task<WinGetUpdateResult> UpgradeAllAsync(bool includeUnknown, IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            Bulk.Add(includeUnknown);
            return Task.FromResult(new WinGetUpdateResult(true, false, "Review WinGet's exit status.", WinGetUpdateOutcome.Finished));
        }
    }
}
