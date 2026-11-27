using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;
using Xunit;

namespace AxePower.Tests.Diagnostics;

/// <summary>
/// Failures that used to vanish now leave a trace, keep data safe, or reach the user (Prompt 21).
/// </summary>
public class FailureHandlingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_Failures_" + Guid.NewGuid().ToString("N"));

    public FailureHandlingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* test cleanup */ }
    }

    // -- Uninstaller data is never overwritten because it could not be read --------------------

    [Fact]
    public void DamagedBackupIndex_IsKeptAside_NotOverwrittenByTheNextRecord()
    {
        var store = new BackupManifestStore(_dir);
        var session = Guid.NewGuid().ToString();
        var folder = store.GetSessionFolder(session);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{ this is not json");

        store.Record(session, new BackupManifestEntry { Sequence = 1, Path = @"HKCU\Software\Example" });

        var kept = Directory.GetFiles(folder, "manifest.corrupt-*.json");
        Assert.Single(kept);
        Assert.Equal("{ this is not json", File.ReadAllText(kept[0]));
        Assert.Single(store.Load(session).Entries);
    }

    [Fact]
    public async Task DamagedHistory_IsKeptAside_AndANewHistoryStarts()
    {
        var history = new UninstallHistoryService(_dir);
        File.WriteAllText(Path.Combine(_dir, "sessions.json"), "[ broken");

        Assert.Empty(await history.GetSessionsAsync());
        await history.SaveSessionAsync(new UninstallSession { SessionId = "s1", StartTime = DateTime.Now });

        Assert.Single(Directory.GetFiles(_dir, "sessions.json.corrupt-*"));
        Assert.Equal("s1", Assert.Single(await history.GetSessionsAsync()).SessionId);
    }

    [Fact]
    public async Task HistoryThatCannotBeRead_IsNotWipedBySaving()
    {
        var history = new UninstallHistoryService(_dir);
        await history.SaveSessionAsync(new UninstallSession { SessionId = "old", StartTime = DateTime.Now.AddDays(-1) });
        var file = Path.Combine(_dir, "sessions.json");
        var before = File.ReadAllText(file);

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) // another process holds it
        {
            await history.SaveSessionAsync(new UninstallSession { SessionId = "new", StartTime = DateTime.Now });
            await Assert.ThrowsAnyAsync<IOException>(() => history.DeleteSessionAsync("old")); // a user action reports failure
        }

        Assert.Equal(before, File.ReadAllText(file));
        Assert.Equal("old", Assert.Single(await history.GetSessionsAsync()).SessionId);
    }

    // -- The filters swallow what they should, and nothing else -----------------------------------

    [Fact]
    public void ExpectedFailureFilters_LetDefectsThrough()
    {
        var defect = new NullReferenceException();
        Assert.False(ExpectedFailures.Registry(defect));
        Assert.False(ExpectedFailures.FileSystem(defect));
        Assert.False(ExpectedFailures.Process(defect));
        Assert.False(ExpectedFailures.Com(defect));
        Assert.False(ExpectedFailures.Wmi(defect));
        Assert.False(ExpectedFailures.Path(defect));

        Assert.True(ExpectedFailures.Registry(new UnauthorizedAccessException()));
        Assert.True(ExpectedFailures.FileSystem(new IOException()));
        Assert.True(ExpectedFailures.Process(new Win32Exception()));
        Assert.True(ExpectedFailures.Com(new COMException()));
        Assert.True(ExpectedFailures.Path(new ArgumentException()));
    }
}
