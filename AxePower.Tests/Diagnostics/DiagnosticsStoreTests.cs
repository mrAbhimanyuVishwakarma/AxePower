using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.Core.Services.Diagnostics;
using Xunit;

namespace AxePower.Tests.Diagnostics;

/// <summary>Structured events on disk: format, thread safety, rolling, retention and deletion.</summary>
public class DiagnosticsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_Diag_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DiagnosticsStore Store(DiagnosticsRetention? retention = null) => new(_dir, retention, () => _now);

    private string AllText() => string.Concat(Directory.GetFiles(_dir).Select(File.ReadAllText));

    [Fact]
    public void Events_AreStructured_WithCategoryLevelEventIdAndCorrelation()
    {
        var log = new LogService(Store());

        using (LogService.BeginCorrelation())
        {
            log.LogWarning("[LanDrop] Upload rejected");
            log.Log(LogLevels.Error, "Update", LogEvents.UpdateVerificationFailed, "Signature did not verify");
        }
        log.LogInfo("No tag here");

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_dir)));
        Assert.Equal(3, lines.Length);
        var first = JsonDocument.Parse(lines[0]).RootElement;
        var second = JsonDocument.Parse(lines[1]).RootElement;
        var third = JsonDocument.Parse(lines[2]).RootElement;

        Assert.Equal("WARN", first.GetProperty("level").GetString());
        Assert.Equal("LanDrop", first.GetProperty("category").GetString());
        Assert.Equal("Upload rejected", first.GetProperty("message").GetString());
        Assert.Equal(0, first.GetProperty("eventId").GetInt32());
        Assert.Equal("DiagnosticsStoreTests.Events_AreStructured_WithCategoryLevelEventIdAndCorrelation", first.GetProperty("event").GetString());

        Assert.Equal(3001, second.GetProperty("eventId").GetInt32());
        Assert.Equal("Update.VerificationFailed", second.GetProperty("event").GetString());
        Assert.Equal(first.GetProperty("correlationId").GetString(), second.GetProperty("correlationId").GetString());

        Assert.Equal("App", third.GetProperty("category").GetString());
        Assert.False(third.TryGetProperty("correlationId", out _)); // outside the scope
    }

    [Fact]
    public async Task Correlation_FollowsTheAsyncFlow_AndNestedScopesRestoreTheOuterOne()
    {
        string? outer, inner, afterInner, otherFlow = "unset";
        using (LogService.BeginCorrelation())
        {
            outer = LogService.CurrentCorrelationId;
            await Task.Yield();
            Assert.Equal(outer, LogService.CurrentCorrelationId);
            using (LogService.BeginCorrelation()) inner = LogService.CurrentCorrelationId;
            afterInner = LogService.CurrentCorrelationId;
            await Task.Run(() => { }).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        var thread = new Thread(() => otherFlow = LogService.CurrentCorrelationId);
        thread.UnsafeStart();
        thread.Join();

        Assert.NotNull(outer);
        Assert.NotEqual(outer, inner);
        Assert.Equal(outer, afterInner);
        Assert.Null(LogService.CurrentCorrelationId);
        Assert.Null(otherFlow);
    }

    [Fact]
    public void ManyThreadsAtOnce_LoseNoEvents_AndEveryLineIsValidJson()
    {
        var log = new LogService(Store());

        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, thread =>
        {
            for (int i = 0; i < 250; i++) log.LogInfo($"[Stress] thread {thread} event {i}");
        });

        var lines = Directory.GetFiles(_dir).SelectMany(File.ReadAllLines).ToList();
        Assert.Equal(2000, lines.Count);
        Assert.All(lines, line => Assert.Equal("Stress", JsonDocument.Parse(line).RootElement.GetProperty("category").GetString()));
        Assert.Equal(2000, lines.Distinct().Count());
    }

    [Fact]
    public void AFullFile_RollsIntoTheNextOne()
    {
        var store = Store(new DiagnosticsRetention(TimeSpan.FromDays(14), 10 * 1024 * 1024, 4 * 1024));
        var log = new LogService(store);

        for (int i = 0; i < 100; i++) log.LogInfo($"[Roll] event {i} " + new string('x', 100));

        var files = store.Files();
        Assert.True(files.Count > 1, "expected more than one file");
        Assert.All(files, f => Assert.True(f.Bytes <= 4 * 1024, $"{f.Name} is {f.Bytes} bytes"));
        Assert.Equal(100, store.ReadEntries().Count);
        Assert.Equal("event 0 " + new string('x', 100), store.ReadEntries()[0].Message);
    }

    [Fact]
    public void Retention_RemovesFilesPastTheAgeLimit()
    {
        var store = Store();
        new LogService(store).LogInfo("[Old] from long ago");
        var old = Assert.Single(store.Files());
        File.SetLastWriteTimeUtc(old.FullPath, _now - TimeSpan.FromDays(15));
        _now = _now.AddDays(1);
        new LogService(store).LogInfo("[New] today");

        store.EnforceRetention();

        var left = Assert.Single(store.Files());
        Assert.NotEqual(old.Name, left.Name);
    }

    [Fact]
    public void Retention_RemovesTheOldestFiles_UntilTheTotalFits()
    {
        var store = Store(new DiagnosticsRetention(TimeSpan.FromDays(14), 3 * 1024, 1024));
        var log = new LogService(store);
        for (int i = 0; i < 60; i++) log.LogInfo($"[Size] event {i} " + new string('y', 80));

        store.EnforceRetention();

        Assert.True(store.Summarize().Bytes <= 3 * 1024);
        var kept = store.ReadEntries();
        Assert.Equal($"event 59 {new string('y', 80)}", kept[^1].Message); // the newest survive
        Assert.DoesNotContain(kept, e => e.Message.StartsWith("event 0 ", StringComparison.Ordinal));
    }

    [Fact]
    public void DeleteAll_RemovesEveryDiagnosticsFile_AndNothingElse()
    {
        var store = Store(new DiagnosticsRetention(TimeSpan.FromDays(14), 10 * 1024 * 1024, 2048));
        var log = new LogService(store);
        for (int i = 0; i < 50; i++) log.LogInfo($"[Delete] event {i} " + new string('z', 80));
        var unrelated = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(unrelated, "not ours");

        var result = store.DeleteAll();

        Assert.True(result.Complete);
        Assert.True(result.Deleted > 1);
        Assert.Empty(store.Files());
        Assert.Equal(new DiagnosticsSummary(0, 0, null), store.Summarize());
        Assert.True(File.Exists(unrelated));

        log.LogInfo("[Delete] recording again");
        Assert.Single(store.ReadEntries());
    }

    [Fact]
    public void DeleteAll_WhileAFileIsOpenForReading_StillDeletesIt()
    {
        var store = Store();
        new LogService(store).LogInfo("[Open] someone is reading this");
        var path = Assert.Single(store.Files()).FullPath;
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        Assert.True(store.DeleteAll().Complete);
    }

    [Fact]
    public void LegacyLogs_AreDeleted_AndNothingElseNearThem()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "AxePower.log"), "old");
        File.WriteAllText(Path.Combine(_dir, "AxePower.log.old"), "older");
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{}");

        Assert.Equal(2, DiagnosticsStore.DeleteLegacyLogs(_dir));
        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
        Assert.Equal(0, DiagnosticsStore.DeleteLegacyLogs(_dir));
    }

    [Fact]
    public void ATornOrForeignLine_IsSkipped()
    {
        var store = Store();
        new LogService(store).LogInfo("[Torn] good line");
        var path = Assert.Single(store.Files()).FullPath;
        File.AppendAllText(path, "{\"time\":\"2026-10-06T10:00:00Z\",\"lev");

        Assert.Equal("good line", Assert.Single(store.ReadEntries()).Message);
    }

    [Fact]
    public void TheInMemoryLogService_WritesNothingToDisk()
    {
        var log = new LogService();
        var seen = new List<LogEntry>();
        log.OnLogAdded += (_, e) => seen.Add(e);

        log.LogError("[Memory] only in memory", new InvalidOperationException("boom"));

        Assert.Null(log.Store);
        Assert.Single(seen);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void AnUnwritableFolder_DoesNotThrow_AndTheEventStillReachesTheApp()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        File.WriteAllText(_dir, "a file where the folder should be");
        try
        {
            var log = new LogService(new DiagnosticsStore(_dir));
            var seen = 0;
            log.OnLogAdded += (_, _) => seen++;

            log.LogInfo("[Blocked] still logged in memory");

            Assert.Equal(1, seen);
        }
        finally
        {
            File.Delete(_dir);
        }
    }
}
