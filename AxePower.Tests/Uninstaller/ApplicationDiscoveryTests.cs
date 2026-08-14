using AxePower.Core.Models.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// Tests for InstalledApplication model helpers.
/// </summary>
public class ApplicationDiscoveryTests
{
    // ── DisplaySize ──────────────────────────────────────────────────────────

    [Fact]
    public void DisplaySize_Zero_ReturnsEmpty()
    {
        var app = new InstalledApplication { EstimatedSizeKb = 0 };
        Assert.Equal(string.Empty, app.DisplaySize);
    }

    [Fact]
    public void DisplaySize_SmallKb_ShowsKb()
    {
        var app = new InstalledApplication { EstimatedSizeKb = 512 };
        Assert.Contains("KB", app.DisplaySize);
    }

    [Fact]
    public void DisplaySize_LargeMb_ShowsMb()
    {
        var app = new InstalledApplication { EstimatedSizeKb = 2048 }; // 2 MB
        Assert.Contains("MB", app.DisplaySize);
    }

    [Fact]
    public void DisplaySize_LargeGb_ShowsGb()
    {
        var app = new InstalledApplication { EstimatedSizeKb = 2 * 1024 * 1024 }; // 2 GB
        Assert.Contains("GB", app.DisplaySize);
    }

    // ── DisplayInstallDate ───────────────────────────────────────────────────

    [Fact]
    public void DisplayInstallDate_ValidDate_FormatsCorrectly()
    {
        var app = new InstalledApplication { InstallDate = "20240115" };
        Assert.Contains("2024", app.DisplayInstallDate);
        Assert.Contains("15", app.DisplayInstallDate);
    }

    [Fact]
    public void DisplayInstallDate_EmptyString_ReturnsEmpty()
    {
        var app = new InstalledApplication { InstallDate = string.Empty };
        Assert.Equal(string.Empty, app.DisplayInstallDate);
    }

    [Fact]
    public void DisplayInstallDate_InvalidFormat_ReturnsRaw()
    {
        var app = new InstalledApplication { InstallDate = "invalid" };
        Assert.Equal(string.Empty, app.DisplayInstallDate);
    }

    // ── DiscoverySource flags ────────────────────────────────────────────────

    [Fact]
    public void DiscoverySource_Flags_CombineCorrectly()
    {
        var combined = DiscoverySource.RegistryHKLM | DiscoverySource.WinGet;
        Assert.True(combined.HasFlag(DiscoverySource.RegistryHKLM));
        Assert.True(combined.HasFlag(DiscoverySource.WinGet));
        Assert.False(combined.HasFlag(DiscoverySource.AppX));
    }

    // ── ApplicationType enum ─────────────────────────────────────────────────

    [Theory]
    [InlineData(ApplicationType.Win32)]
    [InlineData(ApplicationType.MSI)]
    [InlineData(ApplicationType.AppX)]
    [InlineData(ApplicationType.MSIX)]
    [InlineData(ApplicationType.WinGet)]
    [InlineData(ApplicationType.Orphaned)]
    public void ApplicationType_AllValuesAreDefined(ApplicationType type)
    {
        Assert.True(Enum.IsDefined(type));
    }

    [Fact]
    public async Task ApplicationDiscoveryService_CanDiscoverApplications()
    {
        var discovery = new AxePower.Core.Services.Uninstaller.ApplicationDiscoveryService();
        var apps = await discovery.DiscoverApplicationsAsync();
        Assert.NotNull(apps);
        // On any Windows development machine, at least a few apps will be discovered
        Assert.NotEmpty(apps);
    }

    [Fact]
    public async Task MsiUninstallExecutor_DryRun_ReturnsSuccessWithoutExecuting()
    {
        var executor = new AxePower.Core.Services.Uninstaller.MsiUninstallExecutor();
        var app = new InstalledApplication
        {
            Id = "test-msi",
            DisplayName = "Test App",
            ProductCode = "{11111111-2222-3333-4444-555555555555}",
            ApplicationType = ApplicationType.MSI
        };
        var settings = new UninstallerSettings { DryRunMode = true };

        var result = await executor.ExecuteAsync(app, settings);

        Assert.True(result.Success);
        Assert.True(result.WasDryRun);
        Assert.Contains("[DRY RUN]", result.CommandExecuted);
    }

    [Fact]
    public async Task WingetUninstallExecutor_DryRun_ReturnsSuccessWithoutExecuting()
    {
        var executor = new AxePower.Core.Services.Uninstaller.WingetUninstallExecutor();
        var app = new InstalledApplication
        {
            Id = "test-winget",
            DisplayName = "Test Winget App",
            WingetId = "TestPublisher.TestApp",
            ApplicationType = ApplicationType.WinGet
        };
        var settings = new UninstallerSettings { DryRunMode = true };

        var result = await executor.ExecuteAsync(app, settings);

        Assert.True(result.Success);
        Assert.True(result.WasDryRun);
        Assert.Contains("[DRY RUN]", result.CommandExecuted);
    }
}

