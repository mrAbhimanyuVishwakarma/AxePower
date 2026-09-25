using System.Threading.Tasks;
using Xunit;
using AxePower.Core.Services;
using AxePower.App.ViewModels;

namespace AxePower.Tests;

public class ControllerNavigationTests
{
    private readonly LogService _logService;
    private readonly ControllerNavigationService _controllerService;
    private readonly PowerPlanService _powerPlanService;
    private readonly BatteryService _batteryService;
    private readonly NotificationService _notificationService;
    private readonly SettingsService _settingsService;
    private readonly AppKillerService _appKillerService;

    public ControllerNavigationTests()
    {
        _logService = new LogService();
        _controllerService = new ControllerNavigationService(_logService);
        _settingsService = new SettingsService(_logService, System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"axepower_tests_{System.Guid.NewGuid():N}"));
        _powerPlanService = TestPowerPlan.Create(_logService, _settingsService);
        _batteryService = new BatteryService(_logService);
        _notificationService = new NotificationService();
        _appKillerService = new AppKillerService();
    }

    [Fact]
    public void ControllerNavigationService_IsRunning_InitialStateIsFalse()
    {
        bool isRunning = _controllerService.IsRunning();
        Assert.False(isRunning);
    }

    [Fact]
    public async Task ControllerNavigationService_StartAndStop_LifecycleWorksSmoothly()
    {
        // Start engine
        var startResult = await _controllerService.StartAsync();
        Assert.Equal(ControllerNavigationState.Running, startResult);
        Assert.True(_controllerService.IsRunning());

        // Stop engine
        var stopResult = await _controllerService.StopAsync();
        Assert.Equal(ControllerNavigationState.Stopped, stopResult);
        Assert.False(_controllerService.IsRunning());
    }

    [Fact]
    public async Task ControllerNavigationService_ToggleAsync_TransitionsBetweenStates()
    {
        // Initial state is stopped
        Assert.False(_controllerService.IsRunning());

        // Toggle 1: Start
        var state1 = await _controllerService.ToggleAsync();
        Assert.Equal(ControllerNavigationState.Running, state1);
        Assert.True(_controllerService.IsRunning());

        // Toggle 2: Stop
        var state2 = await _controllerService.ToggleAsync();
        Assert.Equal(ControllerNavigationState.Stopped, state2);
        Assert.False(_controllerService.IsRunning());
    }

    [Fact]
    public async Task DashboardViewModel_ToggleControllerNavigationCommand_ExecutesSuccessfully()
    {
        var vm = new DashboardViewModel(
            _powerPlanService,
            _batteryService,
            _notificationService,
            _settingsService,
            _appKillerService,
            _controllerService);

        Assert.False(vm.IsControllerBusy);
        Assert.False(vm.IsControllerNavigationEnabled);

        // Execute toggle command
        await vm.ToggleControllerNavigationAsync();
        Assert.True(vm.IsControllerNavigationEnabled);

        // Execute toggle command again to stop
        await vm.ToggleControllerNavigationAsync();
        Assert.False(vm.IsControllerNavigationEnabled);
    }
}
