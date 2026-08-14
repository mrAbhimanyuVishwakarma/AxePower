using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;
using Microsoft.Win32;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// Fully isolated end-to-end synthetic test validating the Revo-style leftover
/// discovery, scoring, selection, registry backup, file/folder deletion, locked-file handling,
/// and unrelated/protected item safety verification.
/// </summary>
public class SyntheticLeftoverCleanerTest : IDisposable
{
    private readonly string _testAppDir;
    private readonly string _unrelatedDir;
    private readonly string _testRegKeyPath = @"Software\AxePowerLab\AxePower Synthetic TestApp";
    private readonly string _unrelatedRegKeyPath = @"Software\UnrelatedTestVendor\ImportantApp";

    public SyntheticLeftoverCleanerTest()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _testAppDir = Path.Combine(localAppData, "AxePowerLab", "AxePower Synthetic TestApp");
        _unrelatedDir = Path.Combine(localAppData, "UnrelatedTestVendor", "ImportantApp");
    }

    [Fact]
    public async Task Validate_FullLeftoverCleanerLifecycle_IsolatedAndSafe()
    {
        var log = new LogService();
        var protection = new ProtectionService();
        var confidence = new LeftoverConfidenceService();
        var scanner = new LeftoverScannerService(confidence, protection);
        var backup = new RegistryBackupService();
        var fileCleanup = new FileCleanupService();

        var settings = new UninstallerSettings
        {
            DefaultScanMode = ScanMode.Moderate,
            MoveToRecycleBin = false, // Permanent deletion for test fixture cleanup
            DryRunMode = false
        };

        // ─────────────────────────────────────────────────────────────────────
        // 1. SETUP: Create Isolated Test Artifacts
        // ─────────────────────────────────────────────────────────────────────

        // App-specific directory & files (High confidence)
        Directory.CreateDirectory(_testAppDir);
        var configFile = Path.Combine(_testAppDir, "config.json");
        var logFile = Path.Combine(_testAppDir, "app.log");
        await File.WriteAllTextAsync(configFile, "{\"appName\":\"SyntheticTest\",\"version\":\"1.0.0\"}");
        await File.WriteAllTextAsync(logFile, "[2026-08-14] Test log entry.");

        // App-specific Registry Key (High confidence)
        using (var key = Registry.CurrentUser.CreateSubKey(_testRegKeyPath, true))
        {
            key.SetValue("InstallDir", _testAppDir);
            key.SetValue("Version", "1.0.0");
            key.SetValue("ConfigSetting", 42, RegistryValueKind.DWord);
        }

        // Unrelated Artifacts (Must NEVER be detected as leftovers)
        Directory.CreateDirectory(_unrelatedDir);
        var unrelatedFile = Path.Combine(_unrelatedDir, "database.sqlite");
        await File.WriteAllTextAsync(unrelatedFile, "UNRELATED DATA");
        using (var key = Registry.CurrentUser.CreateSubKey(_unrelatedRegKeyPath, true))
        {
            key.SetValue("UnrelatedKey", "ShouldNeverBeTouched");
        }

        var app = new InstalledApplication
        {
            Id = "synthetic-test-app-id",
            DisplayName = "AxePower Synthetic TestApp",
            Publisher = "AxePowerLab",
            Version = "1.0.0",
            InstallLocation = _testAppDir,
            ApplicationType = ApplicationType.Win32,
            CanUninstall = true
        };

        // ─────────────────────────────────────────────────────────────────────
        // 2. SCAN: Run Moderate Leftover Scanner
        // ─────────────────────────────────────────────────────────────────────
        var detectedLeftovers = await scanner.ScanAsync(app, ScanMode.Moderate, settings);

        // ─────────────────────────────────────────────────────────────────────
        // 3. VERIFY DETECTION & CONFIDENCE SCORING
        // ─────────────────────────────────────────────────────────────────────
        Assert.NotEmpty(detectedLeftovers);

        // Verify Application-specific Directory is detected with High confidence
        var folderLeftover = detectedLeftovers.FirstOrDefault(i => i.Path.Equals(_testAppDir, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(folderLeftover);
        Assert.True(folderLeftover.ConfidenceScore >= 80, $"Folder confidence must be >= 80 (was {folderLeftover.ConfidenceScore})");
        Assert.True(folderLeftover.IsSelected, "High-confidence folder must be selected by default");
        Assert.False(folderLeftover.IsProtected, "Test folder must not be protected");

        // Verify Application-specific Registry Key is detected with High confidence
        var regLeftover = detectedLeftovers.FirstOrDefault(i => i.Path.Contains(@"AxePowerLab\AxePower Synthetic TestApp", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(regLeftover);
        Assert.True(regLeftover.ConfidenceScore >= 80, $"Reg key confidence must be >= 80 (was {regLeftover.ConfidenceScore})");
        Assert.True(regLeftover.IsSelected, "High-confidence reg key must be selected by default");

        // Verify UNRELATED items are NOT detected
        Assert.DoesNotContain(detectedLeftovers, i => i.Path.Contains("UnrelatedTestVendor", StringComparison.OrdinalIgnoreCase));

        // ─────────────────────────────────────────────────────────────────────
        // 4. TEST LOW CONFIDENCE & PROTECTED FILTERING
        // ─────────────────────────────────────────────────────────────────────
        // Synthesize low-confidence item (e.g. shared component or low score)
        var lowConfItem = new LeftoverItem
        {
            Path = @"C:\Users\User\Documents\SharedLibrary.dll",
            ConfidenceScore = 35,
            ConfidenceReason = "Partial match in shared documents"
        };
        var (lowScore, lowReason, lowIsProtected, lowIsShared) = confidence.Score(lowConfItem, app);
        lowConfItem.ConfidenceScore = lowScore;
        lowConfItem.IsSelected = lowScore >= 80 && !lowIsProtected && !lowIsShared;
        Assert.False(lowConfItem.IsSelected, "Low-confidence items (<80) must NOT be selected by default");

        // Protected system path check
        var sysItem = new LeftoverItem { Path = @"C:\Windows\System32\drivers\test.sys" };
        var (sysScore, sysReason, sysIsProtected, _) = confidence.Score(sysItem, app);
        Assert.True(sysIsProtected, "Windows system paths must be protected");
        Assert.True(sysScore <= 20, "Protected items must have low confidence");

        // ─────────────────────────────────────────────────────────────────────
        // 5. REGISTRY BACKUP: Create Pre-Cleanup Backup
        // ─────────────────────────────────────────────────────────────────────
        var backupResult = await backup.BackupAsync(
            sessionId: "test-session-123",
            applicationId: app.Id,
            applicationName: app.DisplayName,
            hive: "HKCU",
            keyPath: _testRegKeyPath,
            valueName: "InstallDir");

        Assert.NotNull(backupResult);
        Assert.Equal("HKCU", backupResult.Hive);
        Assert.Equal(_testRegKeyPath, backupResult.KeyPath);
        Assert.Contains(_testAppDir.Replace("\\", "\\\\"), backupResult.ValueData);

        // ─────────────────────────────────────────────────────────────────────
        // 6. LOCKED FILE FAILURE HANDLING
        // ─────────────────────────────────────────────────────────────────────
        var lockedFile = Path.Combine(_testAppDir, "locked_in_use.dat");
        await File.WriteAllTextAsync(lockedFile, "LOCKED BY PROCESS");

        // Open with FileShare.None to simulate active lock
        using (var fileStream = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var deleteResult = await fileCleanup.DeleteFileAsync(lockedFile, settings);
            Assert.False(deleteResult, "FileCleanupService must report false when deleting a locked file without crashing");
            Assert.True(File.Exists(lockedFile), "Locked file must remain on disk upon failed deletion");
        }

        // ─────────────────────────────────────────────────────────────────────
        // 7. CLEANUP: Delete Verified Selected Leftovers
        // ─────────────────────────────────────────────────────────────────────
        // Delete Registry Key
        using (var parentKey = Registry.CurrentUser.OpenSubKey(@"Software\AxePowerLab", true))
        {
            parentKey?.DeleteSubKeyTree("AxePower Synthetic TestApp", throwOnMissingSubKey: false);
        }

        // Delete Folder & files
        var folderDeleteResult = await fileCleanup.DeleteFolderAsync(_testAppDir, settings);
        Assert.True(folderDeleteResult);

        // ─────────────────────────────────────────────────────────────────────
        // 8. POST-CLEANUP VERIFICATION
        // ─────────────────────────────────────────────────────────────────────
        // App artifacts removed
        Assert.False(Directory.Exists(_testAppDir), "Test application directory must be deleted");
        Assert.False(File.Exists(configFile), "Test application config file must be deleted");
        Assert.False(File.Exists(logFile), "Test application log file must be deleted");

        using (var testKey = Registry.CurrentUser.OpenSubKey(_testRegKeyPath))
        {
            Assert.Null(testKey); // Reg key deleted
        }

        // UNRELATED artifacts remain completely intact
        Assert.True(Directory.Exists(_unrelatedDir), "Unrelated directory must remain completely intact");
        Assert.True(File.Exists(unrelatedFile), "Unrelated file must remain completely intact");
        using (var unrelatedKey = Registry.CurrentUser.OpenSubKey(_unrelatedRegKeyPath))
        {
            Assert.NotNull(unrelatedKey);
            Assert.Equal("ShouldNeverBeTouched", unrelatedKey.GetValue("UnrelatedKey"));
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testAppDir)) Directory.Delete(_testAppDir, true);
        }
        catch { }

        try
        {
            if (Directory.Exists(_unrelatedDir)) Directory.Delete(_unrelatedDir, true);
        }
        catch { }

        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\AxePowerLab", true);
            parent?.DeleteSubKeyTree("AxePower Synthetic TestApp", false);
        }
        catch { }

        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\UnrelatedTestVendor", true);
            parent?.DeleteSubKeyTree("ImportantApp", false);
            Registry.CurrentUser.DeleteSubKey("Software\\UnrelatedTestVendor", false);
            Registry.CurrentUser.DeleteSubKey("Software\\AxePowerLab", false);
        }
        catch { }
    }
}
