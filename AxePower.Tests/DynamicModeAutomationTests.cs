using System.Linq;
using System.Threading.Tasks;
using Xunit;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.App.ViewModels;

namespace AxePower.Tests;

public class DynamicModeAutomationTests
{
    private readonly LogService _logService;
    private readonly ServiceThrottlerService _throttlerService;
    private readonly MemoryOptimizerService _memoryOptimizer;
    private readonly NotificationService _notificationService;
    private readonly DynamicModeAutomationService _automationService;
    private readonly SettingsService _settingsService;

    public DynamicModeAutomationTests()
    {
        _logService = new LogService();
        _throttlerService = new ServiceThrottlerService(_logService);
        _memoryOptimizer = new MemoryOptimizerService(_logService);
        _notificationService = new NotificationService();
        _automationService = new DynamicModeAutomationService(
            _throttlerService,
            _memoryOptimizer,
            _notificationService,
            _logService);
        _settingsService = new SettingsService(_logService);
    }

    [Fact]
    public void ServiceThrottler_ProvidesManagedServicesCatalog()
    {
        var services = _throttlerService.GetManagedServices();

        Assert.NotNull(services);
        Assert.True(services.Count >= 4, $"Expected >= 4 throttleable services, found {services.Count}");

        var names = services.Select(s => s.ServiceName).ToList();
        Assert.Contains("SysMain", names);
        Assert.Contains("WSearch", names);
        Assert.Contains("DiagTrack", names);
        Assert.Contains("WerSvc", names);
    }

    [Fact]
    public void MemoryOptimizer_ReturnsAccurateMetrics()
    {
        var status = _memoryOptimizer.GetMemoryStatus();

        Assert.NotNull(status);
        Assert.True(status.TotalPhysicalGb > 0, "Total RAM must be greater than 0 GB");
        Assert.True(status.AvailablePhysicalGb >= 0, "Available RAM must be non-negative");
        Assert.True(status.MemoryLoadPercent >= 0 && status.MemoryLoadPercent <= 100, "Memory load must be between 0 and 100%");
        Assert.False(string.IsNullOrWhiteSpace(status.FormattedSummary));
    }

    [Fact]
    public async Task DynamicModeAutomation_OnModeChanged_HandlesBeastAndEcoCleanly()
    {
        // Should execute smoothly without throwing unhandled exceptions
        await _automationService.OnModeChangedAsync("Beast");
        await _automationService.OnModeChangedAsync("Eco");
        await _automationService.OnModeChangedAsync("Balanced");
    }

    [Fact]
    public void GameModeViewModel_InitializesWithAutomationConfig()
    {
        var gameScanner = new GameScannerService(_logService);
        var appKiller = new AppKillerService();
        var systemServiceOptimizer = new SystemServiceOptimizer();
        var displayService = new DisplayService(_logService);
        var powerPlanService = new PowerPlanService(_logService, displayService, _settingsService);

        var vm = new GameModeViewModel(
            _settingsService,
            _notificationService,
            gameScanner,
            appKiller,
            systemServiceOptimizer,
            powerPlanService,
            controllerService: null,
            automationService: _automationService,
            throttlerService: _throttlerService,
            memoryOptimizer: _memoryOptimizer);

        Assert.True(vm.AutoPauseServicesInBeast);
        Assert.True(vm.AutoFlushRamOnGameLaunch);
        Assert.True(vm.AutoRestoreOnEco);
        Assert.False(string.IsNullOrWhiteSpace(vm.MemorySummary));
    }

    [Theory]
    [InlineData("Lossless Scaling", true)]
    [InlineData("Wallpaper Engine", true)]
    [InlineData("OBS Studio", true)]
    [InlineData("Steamworks Common Redistributables", true)]
    [InlineData("Cyberpunk 2077", false)]
    [InlineData("Delta Force", false)]
    [InlineData("007 First Light", false)]
    [InlineData("Grand Theft Auto V", false)]
    public void GameScannerService_FiltersSoftwareCorrectly(string title, bool shouldBeSoftware)
    {
        bool isSoftware = GameScannerService.IsSoftwareOrUtility(title);
        Assert.Equal(shouldBeSoftware, isSoftware);
    }

    [Fact]
    public void GameModeViewModel_DoesNotAddDuplicatesOrSoftware()
    {
        var gameScanner = new GameScannerService(_logService);
        var appKiller = new AppKillerService();
        var systemServiceOptimizer = new SystemServiceOptimizer();
        var displayService = new DisplayService(_logService);
        var powerPlanService = new PowerPlanService(_logService, displayService, _settingsService);

        var vm = new GameModeViewModel(
            _settingsService,
            _notificationService,
            gameScanner,
            appKiller,
            systemServiceOptimizer,
            powerPlanService,
            controllerService: null,
            automationService: _automationService,
            throttlerService: _throttlerService,
            memoryOptimizer: _memoryOptimizer);

        // Ensure no software like "Lossless Scaling" exists in GameCards
        Assert.DoesNotContain(vm.GameCards, c => GameScannerService.IsSoftwareOrUtility(c.DisplayName));

        // Ensure no duplicates exist in GameCards
        var names = vm.GameCards.Select(c => c.DisplayName.Trim().ToLowerInvariant()).ToList();
        var distinctNames = names.Distinct().ToList();
        Assert.Equal(distinctNames.Count, names.Count);
    }

    [Fact]
    public void GameModeViewModel_MainToggle_SwitchesHeaderAndStats()
    {
        var gameScanner = new GameScannerService(_logService);
        var appKiller = new AppKillerService();
        var systemServiceOptimizer = new SystemServiceOptimizer();
        var displayService = new DisplayService(_logService);
        var powerPlanService = new PowerPlanService(_logService, displayService, _settingsService);

        var vm = new GameModeViewModel(
            _settingsService,
            _notificationService,
            gameScanner,
            appKiller,
            systemServiceOptimizer,
            powerPlanService,
            controllerService: null,
            automationService: _automationService,
            throttlerService: _throttlerService,
            memoryOptimizer: _memoryOptimizer);

        // When off by default
        Assert.False(vm.IsManualGameModeActive);
        Assert.Equal("TURN ON GAMEMODE", vm.GameModeStatusHeader);
        Assert.Equal("0", vm.ServicesPausedCountText);
        Assert.Equal("0 MB", vm.RamFreedText);
        Assert.Equal("None", vm.ActiveGameName);

        // Turn on GameMode
        vm.IsManualGameModeActive = true;
        Assert.Equal("GAMEMODE IS ACTIVE", vm.GameModeStatusHeader);
        Assert.Equal("14", vm.ServicesPausedCountText);
        Assert.Equal("2.3 GB", vm.RamFreedText);

        // Turn off GameMode
        vm.IsManualGameModeActive = false;
        Assert.Equal("TURN ON GAMEMODE", vm.GameModeStatusHeader);
        Assert.Equal("0", vm.ServicesPausedCountText);
        Assert.Equal("0 MB", vm.RamFreedText);
    }
}
