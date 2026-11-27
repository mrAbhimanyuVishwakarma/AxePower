using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.Core.Services.Processes;

namespace AxePower.Tests;

public sealed class FirmwareUpdateCatalogTests
{
    [Fact]
    public async Task CatalogFailureDoesNotClaimFirmwareIsCurrent()
    {
        var service = Create(new([], false, null, "Windows Update was unavailable."));

        var result = await service.CheckForFirmwareUpdatesAsync();

        Assert.False(result.HasUpdates);
        Assert.Contains("incomplete", result.StatusMessage);
        Assert.Contains("unavailable", result.StatusMessage);
    }

    [Fact]
    public async Task FirmwareUsesStructuredDriverClassAndPreservesPartialStatus()
    {
        var service = Create(new([
            new("System component", "Firmware update", true, IsFirmware: true),
            new("BIOS utilities", "Windows Update catalog", false)
        ], false, null));

        var result = await service.CheckForFirmwareUpdatesAsync();

        Assert.True(result.HasUpdates);
        Assert.Contains("partial", result.StatusMessage);
        Assert.Contains("System component", result.StatusMessage);
        Assert.DoesNotContain("BIOS utilities", result.StatusMessage);
    }

    [Fact]
    public async Task UnknownDeviceUpdateDoesNotBecomeConfirmedFirmwareFromItsTitle()
    {
        var service = Create(new([new("OEM BIOS bundle", "Device update", true)], true, false));

        var result = await service.CheckForFirmwareUpdatesAsync();

        Assert.False(result.HasUpdates);
        Assert.Contains("could not classify", result.StatusMessage);
        Assert.Contains("manufacturer's support page", result.StatusMessage);
    }

    [Fact]
    public void UnknownManufacturerDoesNotLaunchSearchFallback()
    {
        var launcher = new PortalLauncher(new(ShellLaunchStatus.Started, "Started."));
        var service = new FirmwareUpdateService(new LogService(), launcher);

        var result = service.OpenVendorBiosPortal(new FirmwareInfo { Manufacturer = "Unknown PC manufacturer" });

        Assert.Equal(ShellLaunchStatus.Refused, result.Status);
        Assert.Contains("Windows Optional Updates", result.Message);
        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public void KnownManufacturerReturnsActualLauncherFailure()
    {
        var failure = new ShellLaunchResult(ShellLaunchStatus.Failed, "The browser could not start.");
        var launcher = new PortalLauncher(failure);
        var service = new FirmwareUpdateService(new LogService(), launcher);

        var result = service.OpenVendorBiosPortal(new FirmwareInfo { Manufacturer = "Dell Inc." });

        Assert.Same(failure, result);
        Assert.Equal("https://www.dell.com/support/home/en-us/products", Assert.Single(launcher.Requests));
    }

    private static FirmwareUpdateService Create(WindowsUpdateScanResult result) =>
        new(new LogService(), catalog: new FakeCatalog(result));

    private sealed class FakeCatalog(WindowsUpdateScanResult result) : IWindowsUpdateCatalogService
    {
        public Task<WindowsUpdateScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class PortalLauncher(ShellLaunchResult result) : IUserShellLauncher
    {
        public List<string> Requests { get; } = new();

        public ShellLaunchResult OpenUri(string target)
        {
            Requests.Add(target);
            return result;
        }

        public ShellLaunchResult OpenProgram(string path) => throw new NotSupportedException();
        public ShellLaunchResult RunInstaller(string path) => throw new NotSupportedException();
        public ShellLaunchResult OpenFolder(string path) => throw new NotSupportedException();
    }
}
