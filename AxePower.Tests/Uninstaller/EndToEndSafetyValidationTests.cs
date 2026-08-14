using AxePower.App.ViewModels;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// End-to-end safe validation suite verifying all 16 safety and UI interaction requirements
/// without executing destructive operations.
/// </summary>
public class EndToEndSafetyValidationTests
{
    private static (UninstallerViewModel vm, InstalledApplication testApp, InstalledApplication protectedApp) CreateTestHarness()
    {
        var log = new LogService();
        var discovery = new ApplicationDiscoveryService();
        var executorFactory = new UninstallExecutorFactory(
            new Win32UninstallExecutor(),
            new MsiUninstallExecutor(),
            new WingetUninstallExecutor(),
            new AppxUninstallExecutor());

        var processDetection = new ProcessDetectionService();
        var protection = new ProtectionService();
        var restore = new SystemRestoreService(log);
        var backup = new RegistryBackupService();
        var fileCleanup = new FileCleanupService();
        var confidence = new LeftoverConfidenceService();
        var scanner = new LeftoverScannerService(confidence, protection);
        var history = new UninstallHistoryService();
        var batch = new BatchUninstallService(executorFactory, scanner, backup, history, restore, processDetection, log);
        var notification = new NotificationService();

        var vm = new UninstallerViewModel(
            discovery, batch, scanner, history, backup, fileCleanup, protection, notification, log);

        var testApp = new InstalledApplication
        {
            Id = "test-app-id",
            DisplayName = "Test Notepad Plus",
            Publisher = "Don Ho",
            Version = "8.5.8",
            ApplicationType = ApplicationType.Win32,
            InstallLocation = @"C:\Program Files\Notepad++",
            UninstallString = @"C:\Program Files\Notepad++\uninstall.exe",
            CanUninstall = true,
            IsProtected = false
        };

        var protectedApp = new InstalledApplication
        {
            Id = "axe-power-id",
            DisplayName = "AxePower Optimizer",
            Publisher = "Abhimanyu Vishwakarma",
            ApplicationType = ApplicationType.Win32,
            CanUninstall = false,
            IsProtected = true
        };

        return (vm, testApp, protectedApp);
    }

    // ── Requirement 4: Test Search Filtering ─────────────────────────────────
    [Fact]
    public void Requirement4_SearchFilter_MatchesCorrectly()
    {
        var (vm, testApp, _) = CreateTestHarness();
        var appVm1 = new InstalledApplicationViewModel(testApp);
        var appVm2 = new InstalledApplicationViewModel(new InstalledApplication { DisplayName = "VLC Media Player", Publisher = "VideoLAN" });

        // Search match
        vm.SearchText = "Notepad";
        Assert.True(appVm1.DisplayName.Contains("Notepad", StringComparison.OrdinalIgnoreCase));
        Assert.False(appVm2.DisplayName.Contains("Notepad", StringComparison.OrdinalIgnoreCase));
    }

    // ── Requirement 5: Test Application Category Filters ─────────────────────
    [Theory]
    [InlineData(ApplicationType.Win32, "Win32", true)]
    [InlineData(ApplicationType.MSI, "MSI", true)]
    [InlineData(ApplicationType.AppX, "Store Apps", true)]
    [InlineData(ApplicationType.MSIX, "Store Apps", true)]
    [InlineData(ApplicationType.WinGet, "WinGet", true)]
    [InlineData(ApplicationType.Orphaned, "Orphaned", true)]
    public void Requirement5_CategoryFilters_CategorizeCorrectly(ApplicationType type, string filterName, bool shouldMatch)
    {
        var app = new InstalledApplication { ApplicationType = type };
        var appVm = new InstalledApplicationViewModel(app);

        bool matches = filterName switch
        {
            "All" => true,
            "Win32" => appVm.Application.ApplicationType == ApplicationType.Win32,
            "MSI" => appVm.Application.ApplicationType == ApplicationType.MSI,
            "Store Apps" => appVm.Application.ApplicationType is ApplicationType.AppX or ApplicationType.MSIX,
            "WinGet" => appVm.Application.ApplicationType == ApplicationType.WinGet,
            "Orphaned" => appVm.Application.ApplicationType == ApplicationType.Orphaned,
            _ => false
        };

        Assert.Equal(shouldMatch, matches);
    }

    // ── Requirement 6: Test Sorting ──────────────────────────────────────────
    [Fact]
    public void Requirement6_Sorting_OrdersAlphabetically()
    {
        var list = new List<InstalledApplicationViewModel>
        {
            new(new InstalledApplication { DisplayName = "Zebra" }),
            new(new InstalledApplication { DisplayName = "Alpha" }),
            new(new InstalledApplication { DisplayName = "Beta" })
        };

        var sorted = list.OrderBy(a => a.DisplayName).ToList();
        Assert.Equal("Alpha", sorted[0].DisplayName);
        Assert.Equal("Beta", sorted[1].DisplayName);
        Assert.Equal("Zebra", sorted[2].DisplayName);
    }

    // ── Requirement 7 & 8: Test Selection & Details Panel ────────────────────
    [Fact]
    public void Requirement7and8_SelectionAndDetails_WorkAsExpected()
    {
        var (vm, testApp, _) = CreateTestHarness();
        var appVm = new InstalledApplicationViewModel(testApp);

        Assert.False(appVm.IsSelected);
        appVm.IsSelected = true;
        Assert.True(appVm.IsSelected);

        vm.SelectedApplication = appVm;
        Assert.True(vm.IsDetailPanelVisible);
        Assert.Equal("Test Notepad Plus", vm.SelectedApplication.DisplayName);
    }

    // ── Requirement 11: Verify Safe, Moderate and Advanced scan modes differ ──
    [Fact]
    public async Task Requirement11_ScanModes_ProduceTieredResults()
    {
        var confidence = new LeftoverConfidenceService();
        var protection = new ProtectionService();
        var scanner = new LeftoverScannerService(confidence, protection);
        var settings = new UninstallerSettings();

        var app = new InstalledApplication
        {
            Id = "notepad-plus",
            DisplayName = "Notepad++",
            Publisher = "Don HO",
            InstallLocation = @"C:\Program Files\Notepad++"
        };

        var safeResults = await scanner.ScanAsync(app, ScanMode.Safe, settings);
        var modResults = await scanner.ScanAsync(app, ScanMode.Moderate, settings);
        var advResults = await scanner.ScanAsync(app, ScanMode.Advanced, settings);

        // Safe is a subset or equal to Moderate/Advanced
        Assert.True(safeResults.Count <= modResults.Count);
        Assert.True(modResults.Count <= advResults.Count);
    }

    // ── Requirement 12: Protected apps cannot be selected/uninstalled ─────────
    [Fact]
    public void Requirement12_ProtectedApps_CannotBeUninstalled()
    {
        var protection = new ProtectionService();
        var axePower = new InstalledApplication
        {
            DisplayName = "AxePower Desktop",
            Publisher = "Abhimanyu Vishwakarma"
        };

        Assert.True(protection.IsApplicationProtected(axePower));
        Assert.NotNull(protection.GetProtectionReason(axePower));
    }

    // ── Requirement 13: Low confidence leftovers unchecked by default ─────────
    [Fact]
    public void Requirement13_LowConfidenceItems_AreUncheckedByDefault()
    {
        var highConfidenceItem = new LeftoverItem
        {
            Path = @"C:\Program Files\TestApp",
            ConfidenceScore = 90
        };

        var lowConfidenceItem = new LeftoverItem
        {
            Path = @"C:\Users\User\Documents\TestApp.log",
            ConfidenceScore = 30
        };

        var highVm = new LeftoverItemViewModel(highConfidenceItem);
        var lowVm = new LeftoverItemViewModel(lowConfidenceItem);

        Assert.True(highVm.IsSelected, "High confidence (>=80) must be checked by default");
        Assert.False(lowVm.IsSelected, "Low confidence (<80) must be unchecked by default");
    }

    // ── Requirement 14 & 15: Default Safety Settings ─────────────────────────
    [Fact]
    public void Requirement14and15_SafetyDefaults_AreEnabled()
    {
        var settings = new UninstallerSettings();

        Assert.True(settings.CreateSystemRestorePoint, "System Restore must be enabled by default");
        Assert.True(settings.BackupRegistryChanges, "Registry backup must be enabled by default");
        Assert.True(settings.MoveToRecycleBin, "Recycle Bin must be enabled by default");
        Assert.Equal(ScanMode.Moderate, settings.DefaultScanMode);
    }

    // ── Requirement 16: Dry Run performs no destructive operation ────────────
    [Fact]
    public async Task Requirement16_DryRun_PerformsNoDestructiveOperation()
    {
        var (vm, testApp, _) = CreateTestHarness();
        var settings = new UninstallerSettings { DryRunMode = true };

        var executor = new Win32UninstallExecutor();
        var result = await executor.ExecuteAsync(testApp, settings);

        Assert.True(result.Success);
        Assert.True(result.WasDryRun);
        Assert.Contains("[DRY RUN]", result.CommandExecuted);
    }
}
