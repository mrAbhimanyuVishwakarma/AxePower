using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AxePower.Core.Helpers;
using AxePower.Core.Models;
using AxePower.Core.Models.Music;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Music;
using AxePower.Core.Services.ShareScreen;
using AxePower.Core.Services.Skins;
using AxePower.Core.Services.Uninstaller;
using Xunit;

namespace AxePower.Tests.Nullability;

/// <summary>
/// System.Text.Json writes an explicit JSON null into properties the model declares non-nullable.
/// These tests load saved files with nulls where values belong and prove each store either restores
/// the declared default or rejects the record, instead of handing a null on to code that will crash.
/// </summary>
public class JsonNullRepairTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_NullTests_" + Guid.NewGuid().ToString("N"));

    public JsonNullRepairTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly JsonSerializerOptions SettingsJson = new() { Converters = { new JsonStringEnumConverter() } };

    // -- The helper -------------------------------------------------------------

    [Fact]
    public void Repair_RestoresDeclaredDefaults_InNestedModels()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"appRules":null,"music":{"libraryFolders":null,"miniPlayer":{"hotkey":null}}}""", SettingsJson)!;

        var result = JsonNullRepair.Repair(settings);

        Assert.True(result.IsComplete);
        Assert.Contains("AppSettings.AppRules", result.Restored);
        Assert.Contains("AppSettings.Music.LibraryFolders", result.Restored);
        Assert.Contains("AppSettings.Music.MiniPlayer.Hotkey", result.Restored);
        Assert.Empty(settings.AppRules);
        Assert.Empty(settings.Music.LibraryFolders);
        Assert.Equal(new AppSettings().Music.MiniPlayer.Hotkey, settings.Music.MiniPlayer.Hotkey);
    }

    [Fact]
    public void Repair_DropsNullItems_FromListsOfNonNullableItems()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"appsToClose":["chrome",null,"steam"]}""", SettingsJson)!;

        var result = JsonNullRepair.Repair(settings);

        Assert.Equal(new[] { "chrome", "steam" }, settings.AppsToClose);
        Assert.Contains("AppSettings.AppsToClose[1]", result.Dropped);
    }

    [Fact]
    public void Repair_ReportsWhatHasNoDefault_AndDescribeNamesFieldsNotValues()
    {
        // A positional record has no parameterless constructor, so nothing can be restored.
        var message = JsonSerializer.Deserialize<ShareChatMessage>("""{"id":"secret-id","sender":null,"text":"hello"}""")!;

        var result = JsonNullRepair.Repair(message);

        Assert.False(result.IsComplete);
        Assert.Equal(new[] { "ShareChatMessage.Sender" }, result.Missing);
        Assert.DoesNotContain("secret-id", result.Describe());
        Assert.DoesNotContain("hello", result.Describe());
    }

    [Fact]
    public void RepairItems_DropsNullAndIncompleteItems_KeepsTheRest()
    {
        var messages = JsonSerializer.Deserialize<List<ShareChatMessage>>("""
            [{"id":"1","sender":"Ana","text":"first","timestamp":"2026-10-06T10:00:00"},
             null,
             {"id":"2","sender":"Ana","text":null,"timestamp":"2026-10-06T10:01:00"},
             {"id":"3","sender":"Ben","text":"third","timestamp":"2026-10-06T10:02:00"}]
            """)!;

        var result = JsonNullRepair.RepairItems(messages);

        Assert.Equal(new[] { "1", "3" }, messages.Select(m => m.Id));
        Assert.Equal(2, result.Dropped.Count);
        Assert.True(result.IsComplete);
    }

    // -- settings.json ----------------------------------------------------------

    [Fact]
    public void Settings_WithEveryValueNull_LoadAsDefaults_AndNothingNonNullableIsNull()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(new AppSettings(), SettingsJson))!;
        NullEveryLoadableValue(json, j => JsonSerializer.Deserialize<AppSettings>(j, SettingsJson));
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json.ToJsonString());

        var service = new SettingsService(new LogService(), _dir);
        service.LoadSettings();
        var loaded = service.GetSettings(); // threw ArgumentNullException in Clone() before Prompt 22

        Assert.Empty(NullNonNullables(loaded, nameof(AppSettings)));
    }

    [Fact]
    public void Settings_WithANullSection_KeepTheValuesThatWereThere()
    {
        var expected = !new AppSettings().AutoSwitchEnabled;
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            $$"""{"autoSwitchEnabled":{{(expected ? "true" : "false")}},"appRules":null,"music":null,"routines":[null]}""");

        var service = new SettingsService(new LogService(), _dir);
        service.LoadSettings();
        var loaded = service.GetSettings();

        Assert.Equal(expected, loaded.AutoSwitchEnabled);
        Assert.Empty(loaded.AppRules);
        Assert.Empty(loaded.Routines);
        Assert.NotNull(loaded.Music.Providers.Enabled);
    }

    [Fact]
    public void Settings_ThatAreTheWordNull_FallBackToTheBackup()
    {
        var expected = !new AppSettings().AutoSwitchEnabled;
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "null");
        File.WriteAllText(Path.Combine(_dir, "settings.backup.json"),
            $$"""{"autoSwitchEnabled":{{(expected ? "true" : "false")}}}""");

        var service = new SettingsService(new LogService(), _dir);
        service.LoadSettings();

        Assert.Equal(expected, service.GetSettings().AutoSwitchEnabled);
    }

    // -- Music ------------------------------------------------------------------

    [Fact]
    public void RecentlyPlayed_WithNullFields_LoadsWithEmptyDefaults_AndDropsNullEntries()
    {
        File.WriteAllText(Path.Combine(_dir, "recently-played.json"), """
            [{"provider":"youtube","sourceId":null,"path":null,"title":"Song","artists":null,"playedUtc":"2026-10-06T10:00:00Z"},
             null]
            """);

        using var service = new RecentlyPlayedService(new MusicStoragePaths(_dir), new FixedClock());
        var plays = service.Get(streamed: true).Concat(service.Get(streamed: false)).ToList();

        var play = Assert.Single(plays);
        Assert.Equal("Song", play.Title);
        Assert.Empty(play.Artists);
        Assert.Equal(string.Empty, play.Path);
        Assert.Equal(string.Empty, play.SourceId);
    }

    [Fact]
    public void RecentSearches_WithNullResults_LoadWithEmptyLists()
    {
        File.WriteAllText(Path.Combine(_dir, "recent-searches.json"), """
            [{"query":"lofi","searchedUtc":"2026-10-06T10:00:00Z","results":[null,{"title":"Track","artists":null}]},
             {"query":"rain","searchedUtc":"2026-10-06T09:00:00Z","results":null}]
            """);

        var service = new RecentSearchesService(new MusicStoragePaths(_dir), new FixedClock());
        var searches = service.Get();

        Assert.Equal(2, searches.Count);
        var lofi = searches.Single(s => s.Query == "lofi");
        var track = Assert.Single(lofi.Results);
        Assert.Empty(track.Artists);
        Assert.Empty(searches.Single(s => s.Query == "rain").Results);
    }

    [Fact]
    public void PlaybackState_WithNullQueueEntries_LoadsWithoutThem()
    {
        File.WriteAllText(Path.Combine(_dir, "playback-state.json"), """
            {"version":1,"queue":[null,{"trackId":7,"path":null,"stream":{"title":"S","artists":null}}],"index":0}
            """);

        var state = new PlaybackStateStore(new MusicStoragePaths(_dir)).Load();

        Assert.NotNull(state);
        var entry = Assert.Single(state.Queue);
        Assert.Equal(7, entry.TrackId);
        Assert.Equal(string.Empty, entry.Path);
        Assert.Empty(entry.Stream!.Artists);
    }

    // -- Account, chat, uninstaller backups, skins ------------------------------

    [Fact]
    public void AccountSession_WithANullAccessToken_LoadsAndRefreshes_ButANullRefreshTokenIsSignedOut()
    {
        var store = new AccountSessionStore(new LogService(), _dir, b => b, b => b);

        File.WriteAllText(store.FilePath, """{"v":1,"accessToken":null,"refreshToken":"r","userId":"u","expiresAtUtc":"2026-10-06T10:00:00Z"}""");
        var session = store.Load();
        Assert.NotNull(session);
        Assert.Equal(string.Empty, session.AccessToken);

        File.WriteAllText(store.FilePath, """{"v":1,"accessToken":"a","refreshToken":null,"userId":"u"}""");
        Assert.Null(store.Load());
    }

    [Fact]
    public void SavedChat_DropsMessagesWithMissingFields_KeepsTheRest()
    {
        var store = new ShareChatStore(_dir, null, b => b, b => b);
        File.WriteAllText(store.ChatFilePath, """
            [{"id":"1","sender":"Ana","text":"kept","timestamp":"2026-10-06T10:00:00"},
             {"id":"2","sender":null,"text":"lost","timestamp":"2026-10-06T10:01:00"}]
            """);

        var messages = store.LoadMessages();

        Assert.Equal("kept", Assert.Single(messages).Text);
    }

    [Fact]
    public void BackupIndex_WithNullEntriesAndSession_LoadsEmpty_AndStillRecords()
    {
        var store = new BackupManifestStore(_dir);
        var session = Guid.NewGuid().ToString();
        var folder = store.GetSessionFolder(session);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), """{"SessionId":null,"Entries":[null]}""");

        var manifest = store.Load(session);

        Assert.Equal(session, manifest.SessionId);
        Assert.Empty(manifest.Entries);
    }

    [Fact]
    public void Skin_WithNullSections_ReadsWithDefaults()
    {
        var skin = SkinCatalog.Read("""{"schemaVersion":1,"id":"test","name":"Test","colors":null,"glass":null,"overrides":null}""", "test.json");

        Assert.NotNull(skin.Colors);
        Assert.NotNull(skin.Glass);
        Assert.Empty(skin.Overrides);
    }

    // -- Helpers ----------------------------------------------------------------

    /// <summary>Sets every string, array and object in <paramref name="root"/> to null wherever the file still loads.</summary>
    internal static void NullEveryLoadableValue(JsonNode root, Func<string, object?> load)
    {
        var slots = new List<(JsonObject Parent, string Key)>();
        void Collect(JsonNode? node)
        {
            if (node is JsonObject o) foreach (var (key, value) in o.ToList()) { slots.Add((o, key)); Collect(value); }
            else if (node is JsonArray a) foreach (var item in a) Collect(item);
        }
        Collect(root);
        slots.Reverse(); // innermost first, so nested values are nulled before their parents
        foreach (var (parent, key) in slots)
        {
            var old = parent[key];
            if (old is null || (old is JsonValue v && v.GetValueKind() != JsonValueKind.String)) continue;
            parent[key] = null;
            try { load(root.ToJsonString()); }
            catch (JsonException) { parent[key] = old; } // an enum stored as a string cannot be null
        }
    }

    /// <summary>Every property declared non-nullable that holds null, as dotted paths.</summary>
    internal static List<string> NullNonNullables(object root, string name)
    {
        var context = new NullabilityInfoContext();
        var found = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object? value, string path, int depth)
        {
            if (value is null or string || depth > 10 || value.GetType().IsValueType || !seen.Add(value)) return;
            if (value is IEnumerable items)
            {
                int i = 0;
                foreach (var item in items) Walk(item, $"{path}[{i++}]", depth + 1);
                return;
            }
            if (value.GetType().Assembly != typeof(AppSettings).Assembly) return;
            foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.PropertyType.IsValueType) continue;
                var child = property.GetValue(value);
                if (child is null && context.Create(property).ReadState == NullabilityState.NotNull) found.Add($"{path}.{property.Name}");
                Walk(child, $"{path}.{property.Name}", depth + 1);
            }
        }
        Walk(root, name, 0);
        return found;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    }
}
