using System;
using System.Collections.Generic;
using System.Linq;
using AxePower.App.ViewModels;
using AxePower.Core.Services;

namespace AxePower.Tests;

public class ToolsTests
{
    private sealed class FakeTools : ToolsService
    {
        public Dictionary<string, string> Installed { get; } = new();
        public HashSet<string> Running { get; } = new();
        public override string? FindExe(ToolDefinition tool) => Installed.TryGetValue(tool.Id, out var exe) ? exe : null;
        public override bool IsRunning(string exePath) => Running.Contains(exePath);
    }

    [Fact]
    public void Catalog_IsTheAgreedApps_EachFromItsPublisher()
    {
        Assert.Equal(new[] { "MSI Afterburner", "CPU-Z", "GPU-Z", "CrystalDiskInfo", "Revo Uninstaller", "WizTree" }, ToolsService.Tools.Select(t => t.Name));
        Assert.Equal(new[] { "Guru3D.Afterburner", "CPUID.CPU-Z", "TechPowerUp.GPU-Z", "CrystalDewWorld.CrystalDiskInfo", "RevoUninstaller.RevoUninstaller", "AntibodySoftware.WizTree" }, ToolsService.Tools.Select(t => t.WinGetId));
        Assert.All(ToolsService.Tools, t =>
        {
            Assert.StartsWith("https://", t.WebUrl);
            Assert.NotEmpty(t.ExeNames);
            Assert.All(t.ExeNames, e => Assert.EndsWith(".exe", e, StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void EveryApp_ShipsItsOwnIcon()
    {
        using var stream = typeof(ToolsViewModel).Assembly.GetManifestResourceStream("AxePower.g.resources");
        Assert.NotNull(stream);
        using var reader = new System.Resources.ResourceReader(stream!);
        var names = reader.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
        Assert.All(ToolsService.Tools, t => Assert.Contains($"resources/icons/tool_{t.Id}.png", names));
    }

    [Fact]
    public void TimerCard_OpensTheTimerPanel()
    {
        using var service = new PowerTimerService(new NoPowerActions(), () => DateTime.UtcNow, log: null, startClock: false);
        using var timer = new PowerTimerViewModel(service);
        var page = new ToolsViewModel(new FakeTools(), timer: timer);

        page.TimerCard.OpenCommand.Execute(null);
        Assert.True(timer.IsFlyoutOpen);
    }

    private sealed class NoPowerActions : IPowerActions
    {
        public void Run(PowerTimerAction action) { }
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\CPUID\\CPU-Z\\cpuz.exe\",0", "C:\\Program Files\\CPUID\\CPU-Z\\cpuz.exe")]
    [InlineData("C:\\Tools\\GPU-Z.exe", "C:\\Tools\\GPU-Z.exe")]
    [InlineData("C:\\CrystalDiskInfo\\DiskInfo64.exe, 1", "C:\\CrystalDiskInfo\\DiskInfo64.exe")]
    [InlineData("C:\\App\\icon.ico", null)]
    public void InstalledAppsIcon_GivesTheProgramPath(string displayIcon, string? expected) =>
        Assert.Equal(expected, ToolsService.ExePathOf(displayIcon));

    [Fact]
    public void Card_OffersInstall_ThenOpen_ThenShowsRunning()
    {
        var service = new FakeTools();
        var page = new ToolsViewModel(service);
        var cpuz = page.Cards.Single(c => c.Definition.Id == "cpuz");

        page.Refresh();
        Assert.False(cpuz.IsInstalled);
        Assert.Equal("Install", cpuz.PrimaryText);
        Assert.Equal("Not installed", cpuz.StatusText);

        service.Installed["cpuz"] = @"C:\Program Files\CPUID\CPU-Z\cpuz.exe";
        page.Refresh();
        Assert.Equal("Open", cpuz.PrimaryText);
        Assert.Equal("Installed", cpuz.StatusText);

        service.Running.Add(@"C:\Program Files\CPUID\CPU-Z\cpuz.exe");
        page.RefreshRunning();
        Assert.Equal("Running", cpuz.StatusText);
    }

    [Fact]
    public void Page_GroupsAppsByUse_ThenTimerAndRoutines()
    {
        var page = new ToolsViewModel(new FakeTools());
        Assert.Equal(new[] { "Monitoring", "Cleanup", "Hardware info", "Timer & Routines" }, page.Groups.Select(g => g.Title));
        Assert.Equal(new[] { "MSI Afterburner" }, page.Groups[0].Cards.Select(c => c.Name));
        Assert.Equal(new[] { "Revo Uninstaller", "WizTree" }, page.Groups[1].Cards.Select(c => c.Name));
        Assert.Equal(new object[] { page.TimerCard, page.RoutinesCard }, page.Groups[3].Items);
    }

    [Fact]
    public void Page_BalancesItsTwoColumns_WithNoGaps()
    {
        var page = new ToolsViewModel(new FakeTools());
        Assert.Equal(new[] { "Monitoring", "Hardware info" }, page.LeftColumn.Select(g => g.Title));
        Assert.Equal(new[] { "Cleanup", "Timer & Routines" }, page.RightColumn.Select(g => g.Title));
        Assert.Equal(page.LeftColumn.Sum(g => g.Items.Count), page.RightColumn.Sum(g => g.Items.Count));
    }
}
