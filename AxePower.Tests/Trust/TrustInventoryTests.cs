using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.Core.Services.Trust;
using Xunit;

namespace AxePower.Tests.Trust;

/// <summary>
/// The inventory behind Trust & Permissions must cover the code: every file that opens a network
/// connection or a listener is disclosed, and every folder AxePower keeps data in is listed.
/// A new network call or data folder without an inventory entry fails here.
/// </summary>
public class TrustInventoryTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("AxePower.sln not found");
    }

    private static string[] SourceFiles() =>
        new[] { "AxePower.Core", "AxePower.App" }
            .SelectMany(p => Directory.GetFiles(Path.Combine(RepoRoot(), p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();

    [Fact]
    public void EveryFileThatTalksToTheNetwork_IsDisclosed()
    {
        var networking = new Regex(@"\bnew HttpClient\b|\bHttpClient\s+_|\bClientWebSocket\b|\bYoutubeClient\b|\bTcpListener\b|\bHttpListener\b|\bnew TcpClient\b|\bUdpClient\b");
        var disclosed = TrustInventory.Connections.SelectMany(c => c.CodeFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var undisclosed = SourceFiles()
            .Where(f => networking.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Where(name => !disclosed.Contains(name!))
            .ToList();

        Assert.True(undisclosed.Count == 0, "Not in TrustInventory.Connections: " + string.Join(", ", undisclosed));
    }

    [Fact]
    public void EveryDisclosedFile_StillExists()
    {
        var files = SourceFiles().Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = TrustInventory.Connections.SelectMany(c => c.CodeFiles).Where(f => !files.Contains(f)).ToList();
        Assert.True(missing.Count == 0, "Listed but gone: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryAppDataFolder_IsListed()
    {
        var folder = new Regex(@"""AxePower"",\s*""(?<name>[A-Za-z0-9_]+)""");
        var listed = string.Join("\n", TrustInventory.DataStores.Select(s => s.PathDisplay));

        var names = SourceFiles().SelectMany(f => folder.Matches(File.ReadAllText(f)).Select(m => m.Groups["name"].Value)).Distinct(StringComparer.OrdinalIgnoreCase);
        var unlisted = names.Where(n => !Regex.IsMatch(listed, $@"(\\AxePower\\{n}\b)|(, {n}\b)", RegexOptions.IgnoreCase)).ToList();

        Assert.True(unlisted.Count == 0, "Not in TrustInventory.DataStores: " + string.Join(", ", unlisted));
    }

    [Fact]
    public void OnlyLogsCachesAndHistories_CanBeDeleted()
    {
        // Owner's choice (2026-10-06): files, backups, settings, undo data and sessions are open-only.
        foreach (var id in new[] { "settings", "operations", "uninstaller", "received", "account", "tools" })
            Assert.False(TrustInventory.DataStores.Single(s => s.Id == id).Deletable, id);
        foreach (var id in new[] { "diagnostics", "music-history", "music-cache", "chat", "battery", "game-art" })
            Assert.True(TrustInventory.DataStores.Single(s => s.Id == id).Deletable, id);
    }

    [Fact]
    public void TheInventory_IsComplete_AndUnique()
    {
        Assert.All(TrustInventory.Connections, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Trigger), c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.DataSent), c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.Control), c.Id);
        });
        Assert.Equal(TrustInventory.Connections.Count, TrustInventory.Connections.Select(c => c.Id).Distinct().Count());
        Assert.Equal(TrustInventory.DataStores.Count, TrustInventory.DataStores.Select(c => c.Id).Distinct().Count());
        Assert.Contains(TrustInventory.CloudData, c => c.Id == "invites" && c.Retention.Contains("10 minutes"));
    }

    [Fact]
    public void RemoteAccess_StaysOff_UntilItsChannelIsPrivate()
    {
        Assert.False(CloudBridgeService.IsAvailable);
        Assert.False(new AppSettings().RemoteAccess.Enabled);
        Assert.Contains("turned off", TrustInventory.Connections.Single(c => c.Id == "remote-access").Trigger, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheBuild_RecordsItsCommit()
    {
        // Built from a git checkout (as the tests are), the commit is in the version.
        Assert.Matches("^[0-9a-f]{12}$", BuildInfo.Commit ?? "");
        Assert.DoesNotContain("+", BuildInfo.Version);
    }
}
