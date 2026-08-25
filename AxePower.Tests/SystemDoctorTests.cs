using System.Linq;
using System.Threading.Tasks;
using Xunit;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.App.ViewModels;

namespace AxePower.Tests;

public class SystemDoctorTests
{
    private readonly LogService _logService;
    private readonly CacheCleanerService _cacheService;
    private readonly DnsOptimizerService _dnsService;
    private readonly SystemDoctorService _doctorService;
    private readonly NotificationService _notificationService;

    public SystemDoctorTests()
    {
        _logService = new LogService();
        _cacheService = new CacheCleanerService(_logService);
        _dnsService = new DnsOptimizerService(_logService);
        _doctorService = new SystemDoctorService(_logService);
        _notificationService = new NotificationService();
    }

    [Fact]
    public async Task CacheCleanerService_AnalyzeCaches_ReturnsCategories()
    {
        var summaries = await _cacheService.AnalyzeCachesAsync();

        Assert.NotNull(summaries);
        Assert.NotEmpty(summaries);
        Assert.All(summaries, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
            Assert.False(string.IsNullOrWhiteSpace(s.FormattedSize));
        });
    }

    [Fact]
    public void CacheCategorySummary_FormatBytes_FormatsSizesAccurately()
    {
        Assert.Equal("0 MB", CacheCategorySummary.FormatBytes(0));
        Assert.Equal("10.0 MB", CacheCategorySummary.FormatBytes(10 * 1024 * 1024));
        Assert.Equal("1.50 GB", CacheCategorySummary.FormatBytes((long)(1.5 * 1024 * 1024 * 1024)));
    }

    [Fact]
    public void DnsOptimizerService_ReturnsStandardProvidersCatalog()
    {
        var providers = _dnsService.GetAvailableProviders();

        Assert.NotNull(providers);
        Assert.True(providers.Count >= 4);

        var ids = providers.Select(p => p.Id).ToList();
        Assert.Contains("cloudflare", ids);
        Assert.Contains("google", ids);
        Assert.Contains("quad9", ids);
        Assert.Contains("dhcp", ids);
    }

    [Fact]
    public async Task DnsOptimizerService_GetActiveAdapterName_ReturnsNonEmptyName()
    {
        var adapter = await _dnsService.GetActiveAdapterNameAsync();
        Assert.False(string.IsNullOrWhiteSpace(adapter));
    }

    [Fact]
    public void SystemDoctorViewModel_InitializesWithDefaultsAndTabs()
    {
        var vm = new SystemDoctorViewModel(
            _doctorService, 
            _cacheService, 
            _dnsService, 
            _notificationService, 
            _logService);

        Assert.Equal("Doctor", vm.ActiveTab);
        Assert.NotNull(vm.ConsoleLogs);
        Assert.NotEmpty(vm.DnsProviders);

        // Tab Switching
        vm.SetTabCommand.Execute("Cleaner");
        Assert.Equal("Cleaner", vm.ActiveTab);

        vm.SetTabCommand.Execute("Dns");
        Assert.Equal("Dns", vm.ActiveTab);

        vm.SetTabCommand.Execute("Doctor");
        Assert.Equal("Doctor", vm.ActiveTab);
    }

    [Fact]
    public void SystemDoctorViewModel_ClearConsole_ResetsConsoleOutput()
    {
        var vm = new SystemDoctorViewModel(
            _doctorService, 
            _cacheService, 
            _dnsService, 
            _notificationService, 
            _logService);

        vm.ConsoleLogs = "Random long output...";
        vm.ClearConsoleCommand.Execute(null);

        Assert.Contains("Console output cleared", vm.ConsoleLogs);
    }
}
