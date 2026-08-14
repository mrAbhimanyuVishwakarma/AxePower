using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

public class LiveEchoMusicUninstallTest
{
    [Fact]
    public async Task RunControlledUninstallAndScan_EchoMusic()
    {
        var log = new LogService();
        var discovery = new ApplicationDiscoveryService();
        var protection = new ProtectionService();
        var confidence = new LeftoverConfidenceService();
        var scanner = new LeftoverScannerService(confidence, protection);
        var restore = new SystemRestoreService(log);
        var backup = new RegistryBackupService();
        var executor = new Win32UninstallExecutor();
        var settings = new UninstallerSettings
        {
            DefaultScanMode = ScanMode.Moderate,
            CreateSystemRestorePoint = true,
            BackupRegistryChanges = true,
            MoveToRecycleBin = true,
            DryRunMode = false
        };

        // 1. Discover
        var apps = await discovery.DiscoverApplicationsAsync();
        var echoApp = apps.FirstOrDefault(a => a.DisplayName.Contains("Echo Music", StringComparison.OrdinalIgnoreCase));
        if (echoApp == null)
        {
            Console.WriteLine("[INFO] Echo Music is already uninstalled.");
            return;
        }

        Console.WriteLine($"[DISCOVERED] Name: {echoApp.DisplayName}, Version: {echoApp.Version}, Method: {echoApp.ApplicationType}, Path: {echoApp.InstallLocation}");

        // 2. System Restore attempt
        var restoreResult = await restore.TryCreateRestorePointAsync($"Before uninstalling {echoApp.DisplayName}");
        Console.WriteLine($"[RESTORE POINT] Created: {restoreResult}");

        // 3. Uninstall
        Console.WriteLine($"[EXECUTING UNINSTALL] Command: {echoApp.QuietUninstallString ?? echoApp.UninstallString}");
        var uninstallResult = await executor.ExecuteAsync(echoApp, settings);
        Console.WriteLine($"[UNINSTALL RESULT] Success: {uninstallResult.Success}, ExitCode: {uninstallResult.ExitCode}, Duration: {uninstallResult.Duration.TotalSeconds:F1}s");

        // Allow OS/disk to settle
        await Task.Delay(2000);

        // 4. Verify removal from discovery
        var appsAfter = await discovery.DiscoverApplicationsAsync();
        var echoAppAfter = appsAfter.FirstOrDefault(a => a.DisplayName.Contains("Echo Music", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"[POST-UNINSTALL CHECK] Still in registry: {echoAppAfter != null}");

        // 5. Scan leftovers in Moderate mode
        var leftovers = await scanner.ScanAsync(echoApp, ScanMode.Moderate, settings);
        Console.WriteLine($"[LEFTOVER SCAN] Found {leftovers.Count} potential leftovers:");

        foreach (var item in leftovers)
        {
            Console.WriteLine($"---");
            Console.WriteLine($"Type: {item.ItemType}");
            Console.WriteLine($"Location: {item.Path}");
            Console.WriteLine($"Score: {item.ConfidenceScore} ({item.ConfidenceLabel})");
            Console.WriteLine($"Reason: {item.ConfidenceReason}");
            Console.WriteLine($"Protected: {item.IsProtected}");
            Console.WriteLine($"SelectedByDefault: {item.IsSelected}");
        }
    }
}
