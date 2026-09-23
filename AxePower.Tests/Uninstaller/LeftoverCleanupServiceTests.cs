using System.IO;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller;
using AxePower.Core.Services.Uninstaller.Leftovers;
using Microsoft.Win32;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// Backups fail closed and results are honest (D5, D6). Registry work happens only under a
/// disposable HKCU\Software\AxePower.Tests\{guid} key; nothing in HKLM is touched.
/// </summary>
public class LeftoverCleanupServiceTests : IDisposable
{
    private readonly TestRegistryKey _key = new();
    private readonly TempFolder _temp = new();
    private readonly BackupManifestStore _manifests;
    private readonly RegistryBackupService _backup;

    public LeftoverCleanupServiceTests()
    {
        _manifests = new BackupManifestStore(_temp.Combine("history"));
        _backup = new RegistryBackupService(_manifests);
    }

    public void Dispose()
    {
        _key.Dispose();
        _temp.Dispose();
    }

    private LeftoverCleanupService Service(IRegistryBackupService? backup = null) =>
        new(new ProtectionService(), backup ?? _backup, new FileCleanupService(), _manifests);

    private static CleanupContext Context(bool elevated = false) => new() { SessionId = Guid.NewGuid().ToString(), IsElevated = elevated };

    private string CreateKey(string name)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{_key.SubKey}\{name}", true);
        key.SetValue("Data", "value");
        return $@"{_key.FullPath}\{name}";
    }

    private static LeftoverItem KeyItem(string path) => new()
    {
        ItemType = LeftoverItemType.RegistryKey,
        Path = path,
        ApplicationName = "Test",
        Evidence = [ConfidenceRecords.ExplicitConnection],
    };

    [Fact]
    public async Task RegistryKey_IsExportedThenDeleted_AndRestores()
    {
        var path = CreateKey("App");
        var context = Context();

        var result = await Service().CleanAsync([KeyItem(path)], new UninstallerSettings(), context);

        var item = Assert.Single(result.Items);
        Assert.Equal(CleanupItemStatus.Deleted, item.Status);
        Assert.False(RegistryPath.KeyExists(path));
        Assert.True(File.Exists(Path.Combine(_manifests.GetSessionFolder(context.SessionId), item.BackupFile)));

        var report = await _backup.RestoreSessionAsync(context.SessionId);
        Assert.Equal(1, report.Restored);
        Assert.True(RegistryPath.KeyExists(path));
    }

    [Fact]
    public async Task FailedBackup_LeavesTheKeyInPlace()
    {
        var path = CreateKey("App");

        var result = await Service(new FailingBackup()).CleanAsync([KeyItem(path)], new UninstallerSettings(), Context());

        Assert.Equal(CleanupItemStatus.SkippedBackupFailed, Assert.Single(result.Items).Status);
        Assert.Equal(0, result.DeletedCount);
        Assert.True(RegistryPath.KeyExists(path));
    }

    [Fact]
    public async Task HkcuBackupCanBeTurnedOff_ButTheKeyIsStillDeletedHonestly()
    {
        var path = CreateKey("App");

        var result = await Service(new FailingBackup()).CleanAsync([KeyItem(path)],
            new UninstallerSettings { BackupRegistryChanges = false }, Context());

        Assert.Equal(CleanupItemStatus.Deleted, Assert.Single(result.Items).Status);
        Assert.False(RegistryPath.KeyExists(path));
    }

    [Fact]
    public async Task DryRun_DeletesNothing_AndWritesNoBackups()
    {
        var path = CreateKey("App");
        var context = Context();

        var result = await Service().CleanAsync([KeyItem(path)], new UninstallerSettings { DryRunMode = true }, context);

        Assert.Equal(CleanupItemStatus.DryRun, Assert.Single(result.Items).Status);
        Assert.True(RegistryPath.KeyExists(path));
        Assert.False(Directory.Exists(_manifests.GetSessionFolder(context.SessionId)));
    }

    [Fact]
    public async Task ProtectedAdminAndMissingItems_AreReportedAsSuch_NotCleaned()
    {
        var items = new[]
        {
            KeyItem(@"HKCU\Software\Microsoft\Windows"),                                  // blocked
            KeyItem(@"HKLM\SOFTWARE\AxePower.Tests.DoesNotExist\App"),                    // needs admin
            KeyItem($@"{_key.FullPath}\NeverCreated"),                                    // already gone
            new LeftoverItem { ItemType = LeftoverItemType.Service, Path = @"HKLM\SYSTEM\CurrentControlSet\Services\x" },
        };

        var result = await Service().CleanAsync(items, new UninstallerSettings(), Context(elevated: false));

        Assert.Equal(CleanupItemStatus.Blocked, result.Items[0].Status);
        Assert.Equal(CleanupItemStatus.SkippedNeedsAdmin, result.Items[1].Status);
        Assert.Equal(CleanupItemStatus.AlreadyGone, result.Items[2].Status);
        Assert.Equal(CleanupItemStatus.Blocked, result.Items[3].Status);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(items.Length, result.BlockedCount + result.SkippedNeedsAdminCount + result.AlreadyGoneCount);
    }

    [Fact]
    public async Task ComKeysWithoutExplicitEvidence_AreBlocked()
    {
        var item = new LeftoverItem
        {
            ItemType = LeftoverItemType.RegistryKey,
            Path = @"HKCU\Software\Classes\CLSID\{11111111-2222-3333-4444-555555555555}",
        };

        var result = await Service().CleanAsync([item], new UninstallerSettings(), Context());

        Assert.Equal(CleanupItemStatus.Blocked, Assert.Single(result.Items).Status);
    }

    [Theory]
    [InlineData(RegistryValueKind.String)]
    [InlineData(RegistryValueKind.ExpandString)]
    [InlineData(RegistryValueKind.DWord)]
    [InlineData(RegistryValueKind.QWord)]
    [InlineData(RegistryValueKind.Binary)]
    [InlineData(RegistryValueKind.MultiString)]
    public async Task TypedValues_RoundTrip(RegistryValueKind kind)
    {
        object data = kind switch
        {
            RegistryValueKind.String => "plain text",
            RegistryValueKind.ExpandString => @"%TEMP%\app",
            RegistryValueKind.DWord => 42,
            RegistryValueKind.QWord => 1L << 40,
            RegistryValueKind.Binary => new byte[] { 1, 2, 3, 255 },
            _ => new[] { "one", "two" },
        };
        using (var key = Registry.CurrentUser.CreateSubKey(_key.SubKey, true))
            key.SetValue("Value", data, kind);

        var item = new LeftoverItem { ItemType = LeftoverItemType.RegistryValue, Path = _key.FullPath, ValueName = "Value", ApplicationName = "Test" };
        var context = Context();

        var result = await Service().CleanAsync([item], new UninstallerSettings(), context);
        Assert.Equal(CleanupItemStatus.Deleted, Assert.Single(result.Items).Status);
        Assert.False(RegistryPath.ValueExists(_key.FullPath, "Value"));

        var report = await _backup.RestoreSessionAsync(context.SessionId);
        Assert.Equal(1, report.Restored);

        using var restored = Registry.CurrentUser.OpenSubKey(_key.SubKey)!;
        Assert.Equal(kind, restored.GetValueKind("Value"));
        var value = restored.GetValue("Value", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        Assert.Equal(data, value);
    }

    [Fact]
    public async Task Files_AreDeletedAndCountsMatchTheItems()
    {
        var file = _temp.CreateFile(@"App\settings.ini");
        var folder = _temp.Combine("App2");
        _temp.CreateFile(@"App2\data.bin");
        var items = new[]
        {
            new LeftoverItem { ItemType = LeftoverItemType.File, Path = file, SizeBytes = 1 },
            new LeftoverItem { ItemType = LeftoverItemType.Folder, Path = folder, SizeBytes = 1 },
        };

        var result = await Service().CleanAsync(items, new UninstallerSettings { MoveToRecycleBin = false }, Context());

        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(2, result.TotalBytesReclaimed);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(folder));
        Assert.Equal("2 deleted", result.Summary);
    }

    private sealed class FailingBackup : IRegistryBackupService
    {
        public Task<bool> ExportKeyAsync(string fullKeyPath, string targetFile, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> ImportAsync(string regFile, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public RegistryBackupItem? BackupValue(string fullKeyPath, string valueName) => null;
        public bool RestoreValue(RegistryBackupItem item) => false;
        public Task<RestoreReport> RestoreSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult(new RestoreReport());
    }
}
