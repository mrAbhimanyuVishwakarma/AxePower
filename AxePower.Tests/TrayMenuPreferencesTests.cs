using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AxePower.App.Models;
using AxePower.Core.Models;

namespace AxePower.Tests;

public class TrayMenuPreferencesTests
{
    /// <summary>
    /// The repository root, found by walking up from the test binaries to AxePower.sln. Not
    /// [CallerFilePath]: deterministic CI builds rewrite source paths to /_/, so it would point nowhere.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("AxePower.sln not found above " + AppContext.BaseDirectory);
    }

    private static string MainWindowXaml() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "AxePower.App", "MainWindow.xaml"));

    [Fact]
    public void EveryCatalogEntry_IsANamedItemInTheTrayMenu()
    {
        string xaml = MainWindowXaml();
        foreach (var entry in TrayMenuCatalog.Entries)
            Assert.Contains($"x:Name=\"{TrayMenuCatalog.ElementName(entry.Id)}\"", xaml);

        // One separator between each pair of groups
        int groups = TrayMenuCatalog.Entries.Select(e => e.Group).Distinct().Count();
        for (int i = 1; i < groups; i++) Assert.Contains($"x:Name=\"TraySeparator{i}\"", xaml);
    }

    [Fact]
    public void SettingsAndExit_AreAlwaysShown_AndNothingElseIs()
    {
        Assert.Equal(new[] { "Settings", "Exit" }, TrayMenuCatalog.Entries.Where(e => e.Permanent).Select(e => e.Id));
        Assert.Equal(TrayMenuCatalog.Entries.Count, TrayMenuCatalog.Entries.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void DefaultsShowTheRequestedShortcuts()
    {
        var defaults = AppSettings.CreateForNewInstall();
        var visibleItems = TrayMenuCatalog.Entries.Where(entry => !defaults.HiddenTrayMenuItems.Contains(entry.Id));
        Assert.Equal(new[]
        {
            "Eco", "Smart", "Beast", "GameMode", "Dashboard", "GameModePage", "Battery", "Music",
            "UpdatePc", "Uninstaller", "Tools", "Settings", "Exit"
        }, visibleItems.Select(entry => entry.Id));
    }

    [Fact]
    public void SavedChoicesTakePrecedenceOverTheDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"hiddenTrayMenuItems":["Music"]}""")!;
        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings.Clone()))!;
        Assert.Equal(new[] { "Music" }, reloaded.HiddenTrayMenuItems);
        Assert.Equal(new AppSettings().HiddenTrayMenuItems, JsonSerializer.Deserialize<AppSettings>("{}")!.HiddenTrayMenuItems);
        Assert.Empty(JsonSerializer.Deserialize<AppSettings>("""{"hiddenTrayMenuItems":[]}""")!.HiddenTrayMenuItems);
    }

    [Fact]
    public void HiddenItems_SurviveASettingsCopy_AsTheirOwnList()
    {
        var original = new AppSettings { HiddenTrayMenuItems = new() { "Beast", "UpdatePc" } };
        var copy = original.Clone();

        Assert.Equal(new[] { "Beast", "UpdatePc" }, copy.HiddenTrayMenuItems);
        copy.HiddenTrayMenuItems.Add("Eco");
        Assert.Equal(2, original.HiddenTrayMenuItems.Count);
    }
}
