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
using AxePower.Core.Services.ShareScreen;
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

    // -- A joined room's chat says why it stopped -------------------------------------------------

    private const string RoomLink = "http://127.0.0.1:1/axe0011#v=0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused")]
    [InlineData(HttpStatusCode.NotFound, "refused")]
    [InlineData(HttpStatusCode.Conflict, "busy")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "busy")]
    public async Task RefusedAdmission_EndsChatWithAReasonForTheUser(HttpStatusCode status, string expected)
    {
        var ended = new TaskCompletionSource<string>();
        using var session = new ShareRoomSession(new LogService(),
            (_, _, _, _) => throw new HttpRequestException("refused", null, status));
        session.ChatEnded += (_, reason) => ended.TrySetResult(reason);

        session.JoinChat(new Uri(RoomLink));

        Assert.Contains(expected, await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeavingTheRoom_IsNotReportedAsAFailure()
    {
        var admitted = new TaskCompletionSource();
        bool ended = false;
        using var session = new ShareRoomSession(new LogService(), async (_, _, _, ct) =>
        {
            admitted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new Uri("ws://127.0.0.1:1/");
        });
        session.ChatEnded += (_, _) => ended = true;

        session.JoinChat(new Uri(RoomLink));
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Leave();
        await Task.Delay(300);

        Assert.False(ended);
    }

    [Fact]
    public void ChatFailureMessages_AreActionable_AndNameNoSecrets()
    {
        foreach (var ex in new Exception[]
        {
            new HttpRequestException("x", null, HttpStatusCode.Forbidden),
            new HttpRequestException("x", null, HttpStatusCode.TooManyRequests),
            new System.Net.WebSockets.WebSocketException("reset"),
            new IOException("broken pipe"),
            new InvalidOperationException("bug " + RoomLink),
        })
        {
            var text = ShareRoomSession.DescribeChatFailure(ex);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("0123456789abcdef", text);
        }
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
        Assert.False(Connection.IsGone(defect));

        Assert.True(ExpectedFailures.Registry(new UnauthorizedAccessException()));
        Assert.True(ExpectedFailures.FileSystem(new IOException()));
        Assert.True(ExpectedFailures.Process(new Win32Exception()));
        Assert.True(ExpectedFailures.Com(new COMException()));
        Assert.True(ExpectedFailures.Path(new ArgumentException()));
        Assert.True(Connection.IsGone(new SocketException()));
        Assert.True(Connection.IsGone(new ObjectDisposedException("socket")));
    }
}
