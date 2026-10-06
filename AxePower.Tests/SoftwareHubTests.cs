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
    private readonly GraphicsDriverService _graphicsDriverService;
    private readonly RecordingTerminalService _terminalService;
    private readonly RestorePointService _restorePointService;

    public SoftwareHubTests()
    {
        _logService = new LogService();
        _hubService = new WinGetHubService(_logService);
        _updateShieldService = new WindowsUpdateShieldService(_logService);
        _driverUpdateService = new DriverUpdateService(_logService);
        _firmwareUpdateService = new FirmwareUpdateService(_logService);
        _notificationService = new NotificationService();
        _graphicsDriverService = new GraphicsDriverService(_logService);
        _terminalService = new RecordingTerminalService();
        _restorePointService = new RestorePointService(_logService);
    }

    [Fact]
    public void GraphicsDriverService_DetectsOnlyRealVendorAdapters()
    {
        var adapters = _graphicsDriverService.DetectAdapters();

        foreach (var adapter in adapters)
        {
            Assert.NotEqual(GpuVendor.Unknown, adapter.Vendor);
            Assert.False(string.IsNullOrWhiteSpace(adapter.Name));
            Assert.False(string.IsNullOrWhiteSpace(adapter.VendorName));
            // Every detected vendor must have an official source to install from
            Assert.StartsWith("https://", adapter.OfficialUrl);
            Assert.False(string.IsNullOrWhiteSpace(adapter.ToolName));
        }

        // A dedicated GPU is always listed before an integrated one
        var ordered = adapters.OrderByDescending(a => a.IsDedicated).ToList();
        Assert.Equal(ordered.Select(a => a.Name), adapters.Select(a => a.Name));
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
        Assert.DoesNotContain("fitgirl", ids);   // pointed users to cracked games; removed 2026-09-25
        Assert.Contains("dlss5swapper", ids);
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
        Assert.Contains("camstudio", ids);
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
            _logService,
            _graphicsDriverService,
            _terminalService,
            _restorePointService);

        Assert.Equal("Updaters", vm.ActiveTab);
        Assert.Equal("All", vm.SelectedCategory);
        Assert.True(vm.IsCategoryListCollapsed);
        Assert.NotEmpty(vm.AllPackages);
        Assert.Equal(vm.AllPackages.Count, vm.FilteredPackages.Count);
        Assert.NotNull(vm.FirmwareInfo);

        vm.ToggleCategoryListCommand.Execute(null);
        Assert.False(vm.IsCategoryListCollapsed);
        vm.ToggleCategoryListCommand.Execute(null);
        Assert.True(vm.IsCategoryListCollapsed);

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
            _logService,
            _graphicsDriverService,
            _terminalService,
            _restorePointService);

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
            _logService,
            _graphicsDriverService,
            _terminalService,
            _restorePointService);

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

    [Fact]
    public void DLSS5SwapperPackage_HasPortableConfiguration()
    {
        var packages = _hubService.GetAvailablePackages();
        var dlss = packages.FirstOrDefault(p => p.Id == "dlss5swapper");

        Assert.NotNull(dlss);
        Assert.Equal("DLSS 5 Swapper", dlss.Name);
        Assert.Equal(PackageCategory.Gaming, dlss.Category);
        Assert.Contains("portable", dlss.LaunchProtocolOrExe, System.StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://github.com/rakanki911/DLSS5-Swapper", dlss.WebUrl);

        var vm = new SoftwareHubViewModel(
            _hubService,
            _updateShieldService,
            _driverUpdateService,
            _firmwareUpdateService,
            _notificationService,
            _logService,
            _graphicsDriverService,
            _terminalService,
            _restorePointService);

        var card = vm.AllPackages.FirstOrDefault(p => p.Id == "dlss5swapper");
        Assert.NotNull(card);
        Assert.Equal("Download & Run", card.InstallButtonText);
        Assert.Equal("Run", card.OpenButtonText);
    }

    [Fact]
    public void GraphicsDriverService_GetCuratedNvidiaDrivers_ReturnsValidReleases()
    {
        var drivers = _graphicsDriverService.GetCuratedNvidiaDrivers();

        Assert.NotNull(drivers);
        Assert.True(drivers.Count >= 5, "Expected at least 5 curated NVIDIA driver releases");

        foreach (var driver in drivers)
        {
            Assert.False(string.IsNullOrWhiteSpace(driver.Version));
            Assert.False(string.IsNullOrWhiteSpace(driver.Badge));
            Assert.False(string.IsNullOrWhiteSpace(driver.Description));
            Assert.StartsWith("https://us.download.nvidia.com/Windows/", driver.DownloadUrlDesktop);
            Assert.StartsWith("https://us.download.nvidia.com/Windows/", driver.DownloadUrlNotebook);
            Assert.Contains(driver.Version, driver.DownloadUrlDesktop);
        }
    }

    [Fact]
    public void GraphicsDriverService_DetectAdapters_ReturnsAdaptersWithoutThrowing()
    {
        var adapters = _graphicsDriverService.DetectAdapters();

        Assert.NotNull(adapters);
        Assert.NotEmpty(adapters);
        // On any Windows machine with real display drivers, adapters should be detected via WMI or Registry fallback
        foreach (var adapter in adapters)
        {
            Assert.False(string.IsNullOrWhiteSpace(adapter.Name));
            Assert.False(string.IsNullOrWhiteSpace(adapter.VendorName));
            Assert.NotEqual(GpuVendor.Unknown, adapter.Vendor);
        }
    }

    [Fact]
    public void CamStudioPackage_HasValidMetadataAndWebUrl()
    {
        var packages = _hubService.GetAvailablePackages();
        var camStudio = packages.FirstOrDefault(p => p.Id == "camstudio");

        Assert.NotNull(camStudio);
        Assert.Equal("CamStudio", camStudio.Name);
        Assert.Equal(PackageCategory.Utilities, camStudio.Category);
        Assert.Equal("https://camstudio.org/", camStudio.WebUrl);
        Assert.Contains("CamStudio", camStudio.LaunchProtocolOrExe);
    }

    [Fact]
    public void SoftwareHubViewModel_MultiSelection_SelectsAndClearsCorrectly()
    {
        var vm = new SoftwareHubViewModel(
            _hubService,
            _updateShieldService,
            _driverUpdateService,
            _firmwareUpdateService,
            _notificationService,
            _logService,
            _graphicsDriverService,
            _terminalService,
            _restorePointService);

        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.HasSelectedPackages);

        // Select Uninstalled
        vm.SelectUninstalledCommand.Execute(null);
        Assert.True(vm.SelectedCount > 0);
        Assert.True(vm.HasSelectedPackages);
        Assert.All(vm.AllPackages.Where(p => p.IsSelected), p => Assert.False(p.IsInstalled));

        // Clear Selection
        vm.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.HasSelectedPackages);
        Assert.All(vm.AllPackages, p => Assert.False(p.IsSelected));

        // Individual selection updates counts reactively
        var first = vm.AllPackages.First(p => !p.IsWebLinkOnly);
        first.IsSelected = true;
        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.HasSelectedPackages);

        first.IsSelected = false;
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.HasSelectedPackages);
    }

    /// <summary>
    /// Stands in for the Terminal page. The Software Hub no longer types scripts into it (see
    /// SoftwareHub/SoftwareHubViewModelTests), but the view model still needs one.
    /// </summary>
    private sealed class RecordingTerminalService : TerminalService
    {
        public List<string> Commands { get; } = new();
        public override void SendCommand(string command) => Commands.Add(command);
    }
}

