using System.IO;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller;
using AxePower.Core.Services.Uninstaller.Leftovers;
using Microsoft.Win32;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// Isolated end-to-end run of the leftover pipeline: scan with evidence, pre-selection, backup,
/// deletion, honest results and registry restore. Everything lives in a temp folder and a
/// disposable HKCU\Software\AxePower.Tests\{guid} key.
/// </summary>
public class SyntheticLeftoverCleanerTest : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly TestRegistryKey _registry = new();
    private readonly string _historyRoot;

    public SyntheticLeftoverCleanerTest()
    {
        _historyRoot = _temp.Combine("history");
    }

    public void Dispose()
    {
        _registry.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task ScanCleanAndRestore_AreIsolatedHonestAndReversible()
    {
        // ── Setup: the removed app's leftovers and an unrelated app ──────────
        var appFolder = _temp.Combine("Vendor", "Synthetic Leftover App");
        _temp.CreateFile(@"Vendor\Synthetic Leftover App\config.json", "{}");
        _temp.CreateFile(@"Vendor\Synthetic Leftover App\app.log", "log");
        var unrelatedFile = _temp.CreateFile(@"UnrelatedVendor\ImportantApp\database.sqlite", "UNRELATED");

        var appKey = $@"{_registry.SubKey}\Synthetic Leftover App";
        using (var key = Registry.CurrentUser.CreateSubKey(appKey, true))
        {
            key.SetValue("InstallDir", appFolder);
            key.SetValue("Version", "1.0.0");
            key.SetValue("ConfigSetting", 42, RegistryValueKind.DWord);
        }
        using (var key = Registry.CurrentUser.CreateSubKey($@"{_registry.SubKey}\ImportantApp", true))
            key.SetValue("UnrelatedKey", "ShouldNeverBeTouched");

        var app = new InstalledApplication
        {
            Id = "synthetic-test-app",
            DisplayName = "Synthetic Leftover App",
            Publisher = "AxePower Test Vendor",
            InstallLocation = appFolder,
        };

        var protection = new ProtectionService();
        var scanner = new LeftoverScannerService(protection,
        [
            new InstallLocationFinder(),
            new CommonFolderFinder([_temp.Path, _temp.Combine("Vendor")]),
            new SoftwareKeyFinder([_registry.FullPath]),
        ]);

        // ── Scan ─────────────────────────────────────────────────────────────
        var found = await scanner.ScanAsync(app, ScanMode.Moderate, new UninstallerSettings(), new LeftoverScanOptions());

        var folder = Assert.Single(found, i => i.Path.Equals(appFolder, StringComparison.OrdinalIgnoreCase));
        Assert.True(folder.HasEvidence(nameof(ConfidenceRecords.ExplicitConnection)));
        Assert.True(folder.IsSelected, "The app's own folder is pre-selected");
        Assert.Null(folder.BlockedReason);

        var regItem = Assert.Single(found, i => i.Path.EndsWith(@"\Synthetic Leftover App", StringComparison.OrdinalIgnoreCase) && i.ItemType == LeftoverItemType.RegistryKey);
        Assert.True(regItem.HasEvidence(nameof(ConfidenceRecords.ExplicitConnection)), "Its values point into the app folder");
        Assert.True(regItem.IsSelected);

        Assert.DoesNotContain(found, i => i.Path.Contains("ImportantApp", StringComparison.OrdinalIgnoreCase));

        // ── Clean ────────────────────────────────────────────────────────────
        var manifests = new BackupManifestStore(_historyRoot);
        var backup = new RegistryBackupService(manifests);
        var cleanup = new LeftoverCleanupService(protection, backup, new FileCleanupService(), manifests);
        var sessionId = Guid.NewGuid().ToString();

        var result = await cleanup.CleanAsync(found.Where(i => i.IsSelected).ToList(),
            new UninstallerSettings { MoveToRecycleBin = false },
            new CleanupContext { SessionId = sessionId, IsElevated = false });

        Assert.All(result.Items, r => Assert.Equal(CleanupItemStatus.Deleted, r.Status));
        Assert.Equal(result.Items.Count, result.DeletedCount);
        Assert.False(Directory.Exists(appFolder));
        using (var gone = Registry.CurrentUser.OpenSubKey(appKey)) Assert.Null(gone);

        var regResult = result.Items.Single(r => r.ItemType == LeftoverItemType.RegistryKey);
        var regFile = Path.Combine(manifests.GetSessionFolder(sessionId), regResult.BackupFile);
        Assert.True(new FileInfo(regFile).Length > 0, "The key was exported before it was deleted");
        Assert.Equal(result.Items.Count, manifests.Load(sessionId).Entries.Count);

        // Unrelated items are untouched
        Assert.True(File.Exists(unrelatedFile));
        using (var unrelated = Registry.CurrentUser.OpenSubKey($@"{_registry.SubKey}\ImportantApp"))
            Assert.Equal("ShouldNeverBeTouched", unrelated?.GetValue("UnrelatedKey"));

        // ── Restore ──────────────────────────────────────────────────────────
        var report = await backup.RestoreSessionAsync(sessionId);

        Assert.Equal(1, report.Restored);
        Assert.Equal(0, report.Failed);
        using (var restored = Registry.CurrentUser.OpenSubKey(appKey))
        {
            Assert.NotNull(restored);
            Assert.Equal(appFolder, restored!.GetValue("InstallDir"));
            Assert.Equal(42, restored.GetValue("ConfigSetting"));
        }
    }

    [Fact]
    public async Task LockedFile_IsReportedAsFailed_AndStaysOnDisk()
    {
        var locked = _temp.CreateFile("locked_in_use.dat", "LOCKED");
        var files = new FileCleanupService();

        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await files.DeleteFileAsync(locked, new UninstallerSettings { MoveToRecycleBin = false }));
            Assert.True(File.Exists(locked));
        }
    }
}
