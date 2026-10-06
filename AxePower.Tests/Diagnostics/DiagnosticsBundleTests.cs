using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AxePower.Core.Models;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.Diagnostics;
using Xunit;

namespace AxePower.Tests.Diagnostics;

/// <summary>The diagnostic bundle: what the preview shows is exactly what is saved, and nothing personal.</summary>
public class DiagnosticsBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_Bundle_" + Guid.NewGuid().ToString("N"));

    public DiagnosticsBundleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DiagnosticsStore Store() => new(Path.Combine(_dir, "diag"));

    private static AppSettings PersonalSettings()
    {
        var settings = new AppSettings();
        settings.AppsToClose.Add("SecretGameLauncher.exe");
        settings.Music.LibraryFolders.Add(@"D:\Private Music\Ex Playlist");
        settings.Music.Integrations.LastFmUsername = "lastfm_handle_xyz";
        settings.ShareScreen.SavedFriends.Add(new SavedFriend { Name = "Aarav Mehta" });
        settings.Uninstaller.ExcludedFilePaths.Add(@"D:\Taxes\2026");
        settings.GpuPreferenceBackups[@"C:\Games\Hidden\game.exe"] = "GpuPreference=2;";
        return settings;
    }

    [Fact]
    public void ThePreview_ListsReadmeSummaryAndEveryLogFile()
    {
        var store = Store();
        new LogService(store).LogInfo("[Bundle] hello");

        var items = new DiagnosticsBundleBuilder(store, systemSummary: () => "AxePower: 2.1.0").Preview(new DiagnosticsBundleOptions());

        Assert.Equal("README.txt", items[0].Name);
        Assert.Contains(items, i => i.Name == "system-summary.txt");
        var log = Assert.Single(items, i => i.Name.StartsWith("logs/", StringComparison.Ordinal));
        Assert.Contains("hello", log.Content);
        Assert.DoesNotContain(items, i => i.Name == "settings.json");
        foreach (var item in items.Skip(1)) Assert.Contains(item.Name, items[0].Content);
    }

    [Fact]
    public void TheSavedZip_HoldsExactlyThePreviewedFiles_ByteForByte()
    {
        var store = Store();
        new LogService(store).LogWarning("[Bundle] something odd");
        var builder = new DiagnosticsBundleBuilder(store, PersonalSettings, systemSummary: () => "AxePower: 2.1.0");
        var preview = builder.Preview(new DiagnosticsBundleOptions(IncludeSettings: true));
        var zipPath = Path.Combine(_dir, "bundle.zip");

        DiagnosticsBundleBuilder.Write(preview, zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(preview.Select(i => i.Name), zip.Entries.Select(e => e.FullName));
        foreach (var item in preview)
        {
            using var reader = new StreamReader(zip.GetEntry(item.Name)!.Open(), Encoding.UTF8);
            Assert.Equal(item.Content, reader.ReadToEnd());
        }
        Assert.False(File.Exists(zipPath + ".partial"));
    }

    [Fact]
    public void TheSettingsSnapshot_KeepsOnlyCountsOfPersonalLists()
    {
        var snapshot = DiagnosticsBundleBuilder.SettingsSnapshot(PersonalSettings());

        foreach (var personal in new[] { "SecretGameLauncher", "Private Music", "lastfm_handle_xyz", "Aarav", "Taxes", @"Games\\Hidden", "GpuPreference=2" })
            Assert.DoesNotContain(personal, snapshot);
        Assert.Contains("\"appsToClose\": \"[removed: 1 item(s)]\"", snapshot);
        Assert.Contains("\"savedFriends\": \"[removed: 1 item(s)]\"", snapshot);
        Assert.Contains("\"autoSwitchEnabled\"", snapshot); // ordinary settings stay
    }

    [Fact]
    public void EveryPersonalSettingName_StillExistsInTheSettingsModel()
    {
        // A renamed property would silently stop being removed.
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings());
        foreach (var name in DiagnosticsBundleBuilder.PersonalSettings)
            Assert.Contains($"\"{name}\"", json);
    }

    [Fact]
    public void UninstallLogs_AreDecodedRedactedCappedAndRenamed()
    {
        var logs = Path.Combine(_dir, "UninstallLogs");
        Directory.CreateDirectory(logs);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var body = $"MSI (s) Product: Secret Tool -- Removal completed.\r\nPath: {profile}\\AppData\\Local\\SecretTool\r\n" + new string('.', DiagnosticsBundleBuilder.MaxUninstallLogChars);
        File.WriteAllText(Path.Combine(logs, "Secret Tool_20261006_100000.log"), body, Encoding.Unicode); // msiexec writes UTF-16
        var builder = new DiagnosticsBundleBuilder(Store(), uninstallLogDirectory: logs, systemSummary: () => "x");

        var item = Assert.Single(builder.Preview(new DiagnosticsBundleOptions(IncludeUninstallLogs: true)), i => i.Name.StartsWith("uninstall-logs/", StringComparison.Ordinal));

        Assert.DoesNotContain("Secret Tool", item.Name);
        Assert.StartsWith("[earlier lines omitted]", item.Content);
        Assert.True(item.Content.Length <= DiagnosticsBundleBuilder.MaxUninstallLogChars + 64);
        Assert.DoesNotContain(profile, builder.Preview(new DiagnosticsBundleOptions(IncludeUninstallLogs: true)).Single(i => i.Name == item.Name).Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSystemSummary_NamesNeitherTheUserNorTheComputer()
    {
        var summary = LogRedactor.Redact(DiagnosticsBundleBuilder.SystemSummary(DiagnosticsRetention.Default));

        Assert.Contains("Windows:", summary);
        Assert.Contains("Processor:", summary);
        Assert.DoesNotContain(Environment.MachineName, summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheBundleIsRedactedAgain_OnTheWayOut()
    {
        // A line written before a redaction rule existed is caught at export.
        var store = Store();
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(Path.Combine(store.Directory, "axepower-20261001.jsonl"),
            "{\"time\":\"2026-10-01T10:00:00Z\",\"level\":\"WARN\",\"category\":\"Old\",\"eventId\":0,\"event\":\"x\",\"message\":\"token ghp_1234567890abcdefghijABCDEFGHIJ0123\"}\n");

        var log = Assert.Single(new DiagnosticsBundleBuilder(store, systemSummary: () => "x").Preview(new DiagnosticsBundleOptions()), i => i.Name.StartsWith("logs/", StringComparison.Ordinal));

        Assert.DoesNotContain("ghp_1234567890abcdefghijABCDEFGHIJ0123", log.Content);
    }

    [Fact]
    public void DiagnosticsCode_HasNoNetworkAccess()
    {
        // "No automatic upload": nothing under Services/Diagnostics may open a connection.
        var folder = Path.Combine(RepoRoot(), "AxePower.Core", "Services", "Diagnostics");
        var forbidden = new[] { "HttpClient", "WebRequest", "WebClient", "Socket", "TcpClient", "UdpClient", "WebSocket", "System.Net" };
        foreach (var file in Directory.GetFiles(folder, "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var word in forbidden) Assert.False(text.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(file)} mentions {word}");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("AxePower.sln not found");
    }
}
