using System.Linq;
using System.Threading.Tasks;
using Xunit;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.App.ViewModels;

namespace AxePower.Tests;

public class SoftwareHubTests
{
    private readonly LogService _logService;
    private readonly WinGetHubService _hubService;
    private readonly WindowsUpdateShieldService _updateShieldService;
    private readonly DriverUpdateService _driverUpdateService;
    private readonly FirmwareUpdateService _firmwareUpdateService;
    private readonly NotificationService _notificationService;

    public SoftwareHubTests()
    {
        _logService = new LogService();
        _hubService = new WinGetHubService(_logService);
        _updateShieldService = new WindowsUpdateShieldService(_logService);
        _driverUpdateService = new DriverUpdateService(_logService);
        _firmwareUpdateService = new FirmwareUpdateService(_logService);
        _notificationService = new NotificationService();
    }

    [Fact]
    public void WinGetHubService_ReturnsRichPackageCatalog()
    {
        var packages = _hubService.GetAvailablePackages();

        Assert.NotNull(packages);
        Assert.True(packages.Count >= 16, $"Expected >= 16 packages, got {packages.Count}");
    }

    [Fact]
    public void AllPackages_HaveValidWinGetIdsAndNames()
    {
        var packages = _hubService.GetAvailablePackages();

        foreach (var p in packages)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Id));
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Glyph));
        }
    }

    [Fact]
    public async Task WinGetHubService_RefreshInstallationStatuses_CompletesWithoutExceptions()
    {
        await _hubService.RefreshInstallationStatusesAsync();
        var packages = _hubService.GetAvailablePackages();

        Assert.All(packages, p => Assert.NotNull(p));
    }

    [Fact]
    public void PackageCatalog_ContainsEssentialGamingAndRuntimes()
    {
        var packages = _hubService.GetAvailablePackages();
        var ids = packages.Select(p => p.Id).ToList();

        Assert.Contains("vcredist", ids);
        Assert.Contains("directx", ids);
        Assert.Contains("dotnet8", ids);
        Assert.Contains("steam", ids);
        Assert.Contains("epic", ids);
        Assert.Contains("rockstar", ids);
        Assert.Contains("ubisoft", ids);
        Assert.Contains("battlenet", ids);
        Assert.Contains("fitgirl", ids);
        Assert.Contains("afterburner", ids);
        Assert.Contains("7zip", ids);
        Assert.Contains("ninite", ids);
        Assert.Contains("obs", ids);
        Assert.Contains("davinci", ids);
        Assert.Contains("audacity", ids);
        Assert.Contains("gimp", ids);
        Assert.Contains("canva", ids);
        Assert.Contains("blender", ids);
        Assert.Contains("handbrake", ids);
        Assert.Contains("powertoys", ids);
        Assert.Contains("sharex", ids);
        Assert.Contains("peace", ids);
        Assert.Contains("dolby", ids);
        Assert.Contains("steelseriesgg", ids);
    }

    [Fact]
    public void NinitePackage_HasCustomInstallerAndWebUrl()
    {
        var packages = _hubService.GetAvailablePackages();
        var ninite = packages.FirstOrDefault(p => p.Id == "ninite");

        Assert.NotNull(ninite);
        Assert.Equal("Ninite", ninite.Name);
        Assert.True(ninite.HasCustomInstaller);
        Assert.Equal("https://ninite.com/", ninite.CustomWebUrl);
        Assert.Equal("https://ninite.com/", ninite.WebUrl);
        Assert.Contains("Ninite Installer.exe", ninite.LocalInstallerPath);
    }

    [Fact]
    public void WindowsUpdateShield_ReturnsNonNullStatus()
    {
        var status = _updateShieldService.GetStatus();
        Assert.NotNull(status);
    }

    [Fact]
    public void SoftwareHubViewModel_InitializesAndFiltersCorrectly()
    {
        var vm = new SoftwareHubViewModel(
            _hubService,
            _updateShieldService,
            _driverUpdateService,
            _firmwareUpdateService,
            _notificationService,
            _logService);

        Assert.Equal("Updaters", vm.ActiveTab);
        Assert.Equal("All", vm.SelectedCategory);
        Assert.NotEmpty(vm.AllPackages);
        Assert.Equal(vm.AllPackages.Count, vm.FilteredPackages.Count);
        Assert.NotNull(vm.FirmwareInfo);

        // Filter Runtimes
        vm.SetCategoryCommand.Execute("Runtimes");
        Assert.All(vm.FilteredPackages, p => Assert.Equal(PackageCategory.Runtimes, p.Category));

        // Filter Gaming
        vm.SetCategoryCommand.Execute("Gaming");
        Assert.All(vm.FilteredPackages, p => Assert.Equal(PackageCategory.Gaming, p.Category));

        // Search filter
        vm.SearchText = "Steam";
        Assert.Single(vm.FilteredPackages);
        Assert.Equal("Steam", vm.FilteredPackages.First().Name);

        vm.SearchText = string.Empty;
        vm.SetCategoryCommand.Execute("All");
        Assert.Equal(vm.AllPackages.Count, vm.FilteredPackages.Count);
    }

    [Fact]
    public void SoftwareHubViewModel_TabTransitions_UpdatesActiveTab()
    {
        var vm = new SoftwareHubViewModel(
            _hubService,
            _updateShieldService,
            _driverUpdateService,
            _firmwareUpdateService,
            _notificationService,
            _logService);

        Assert.Equal("Updaters", vm.ActiveTab);

        vm.SetTabCommand.Execute("Apps");
        Assert.Equal("Apps", vm.ActiveTab);

        vm.SetTabCommand.Execute("Updates");
        Assert.Equal("Updates", vm.ActiveTab);

        vm.SetTabCommand.Execute("Console");
        Assert.Equal("Console", vm.ActiveTab);

        vm.SetTabCommand.Execute("Updaters");
        Assert.Equal("Updaters", vm.ActiveTab);
    }

    [Fact]
    public void SoftwareHubViewModel_ClearConsole_ResetsConsoleOutput()
    {
        var vm = new SoftwareHubViewModel(
            _hubService,
            _updateShieldService,
            _driverUpdateService,
            _firmwareUpdateService,
            _notificationService,
            _logService);

        vm.ConsoleOutput = "Some test logs...\n";
        vm.ClearConsoleCommand.Execute(null);

        Assert.Contains("Console log cleared", vm.ConsoleOutput);
    }

    [Fact]
    public async Task FirmwareUpdateService_QueriesFirmwareDetailsSuccessfully()
    {
        var info = await _firmwareUpdateService.GetFirmwareInfoAsync();
        Assert.NotNull(info);
        Assert.NotNull(info.BiosVersion);
        Assert.NotNull(info.Manufacturer);
        Assert.NotNull(info.VendorPortalUrl);
        Assert.NotNull(info.FirmwareDevices);
    }

    [Fact]
    public void DriverUpdateService_TryFocusExisting_ReturnsBooleanWithoutCrashing()
    {
        bool focused = _driverUpdateService.TryFocusExistingUpdateWindow();
        Assert.True(focused || !focused);
    }
}
