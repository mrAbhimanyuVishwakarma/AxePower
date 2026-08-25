using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Moq;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;
using AxePower.App.ViewModels;

namespace AxePower.Tests;

public class TweakServiceTests
{
    private readonly LogService _logService;
    private readonly TweakService _tweakService;

    public TweakServiceTests()
    {
        _logService = new LogService();
        _tweakService = new TweakService(_logService);
    }

    [Fact]
    public void GetAllTweaks_ReturnsNonEmptyTweakCatalog()
    {
        var tweaks = _tweakService.GetAllTweaks();

        Assert.NotNull(tweaks);
        Assert.True(tweaks.Count >= 8, $"Expected at least 8 tweaks, found {tweaks.Count}");
    }

    [Fact]
    public void AllTweaks_HaveValidMetadataAndTargetKeys()
    {
        var tweaks = _tweakService.GetAllTweaks();

        foreach (var tweak in tweaks)
        {
            Assert.False(string.IsNullOrWhiteSpace(tweak.Id), "Tweak ID must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(tweak.Title), $"Tweak {tweak.Id} Title must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(tweak.Description), $"Tweak {tweak.Id} Description must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(tweak.RegistryKey), $"Tweak {tweak.Id} RegistryKey must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(tweak.CategoryDisplayName), $"Tweak {tweak.Id} CategoryDisplayName must not be empty");
        }
    }

    [Fact]
    public void TweakCatalog_ContainsEssentialWinUtilCategories()
    {
        var tweaks = _tweakService.GetAllTweaks();

        var categories = tweaks.Select(t => t.Category).Distinct().ToList();
        Assert.Contains(TweakCategory.Privacy, categories);
        Assert.Contains(TweakCategory.Gaming, categories);
        Assert.Contains(TweakCategory.Windows11, categories);
    }

    [Fact]
    public void TweakCatalog_ContainsSpecificWinUtilFeatures()
    {
        var tweaks = _tweakService.GetAllTweaks();
        var ids = tweaks.Select(t => t.Id).ToList();

        Assert.Contains("telemetry-disable", ids);
        Assert.Contains("bing-search-disable", ids);
        Assert.Contains("copilot-disable", ids);
        Assert.Contains("activity-feed-disable", ids);
        Assert.Contains("gamedvr-disable", ids);
        Assert.Contains("mouse-accel-disable", ids);
        Assert.Contains("classic-context-menu", ids);
        Assert.Contains("sticky-keys-disable", ids);
    }

    [Fact]
    public void SystemOptimizerViewModel_InitializesAndCalculatesCountsCorrectly()
    {
        var notifService = new NotificationService();
        var restoreService = new SystemRestoreService(_logService);

        var vm = new SystemOptimizerViewModel(_tweakService, restoreService, notifService, _logService);

        Assert.Equal(_tweakService.GetAllTweaks().Count, vm.TotalCount);
        Assert.True(vm.RecommendedCount > 0);
        Assert.Equal(vm.TotalCount, vm.AllTweaks.Count);
        Assert.Equal(vm.TotalCount, vm.FilteredTweaks.Count);
    }

    [Fact]
    public void SystemOptimizerViewModel_FilterByCategory_FiltersCorrectly()
    {
        var notifService = new NotificationService();
        var restoreService = new SystemRestoreService(_logService);

        var vm = new SystemOptimizerViewModel(_tweakService, restoreService, notifService, _logService);

        // Filter Privacy
        vm.SetCategoryCommand.Execute("Privacy");
        Assert.All(vm.FilteredTweaks, t => Assert.Equal(TweakCategory.Privacy, t.Category));

        // Filter Gaming
        vm.SetCategoryCommand.Execute("Gaming");
        Assert.All(vm.FilteredTweaks, t => Assert.Equal(TweakCategory.Gaming, t.Category));

        // Filter Windows11
        vm.SetCategoryCommand.Execute("Windows11");
        Assert.All(vm.FilteredTweaks, t => Assert.Equal(TweakCategory.Windows11, t.Category));

        // Reset to All
        vm.SetCategoryCommand.Execute("All");
        Assert.Equal(vm.TotalCount, vm.FilteredTweaks.Count);
    }

    [Fact]
    public void SystemOptimizerViewModel_SearchFiltering_MatchesTitleAndDescription()
    {
        var notifService = new NotificationService();
        var restoreService = new SystemRestoreService(_logService);

        var vm = new SystemOptimizerViewModel(_tweakService, restoreService, notifService, _logService);

        vm.SearchText = "Mouse";
        Assert.NotEmpty(vm.FilteredTweaks);
        Assert.All(vm.FilteredTweaks, t => Assert.Contains("Mouse", t.Title, System.StringComparison.OrdinalIgnoreCase));

        vm.SearchText = "NonExistentTweakQuery123";
        Assert.Empty(vm.FilteredTweaks);

        vm.SearchText = string.Empty;
        Assert.Equal(vm.TotalCount, vm.FilteredTweaks.Count);
    }
}
