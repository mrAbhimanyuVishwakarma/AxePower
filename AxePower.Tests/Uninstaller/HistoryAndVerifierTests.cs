using System.IO;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller;
using Microsoft.Win32;

namespace AxePower.Tests.Uninstaller;

/// <summary>Session history (legacy loading, upsert) and install-state verification.</summary>
public class HistoryAndVerifierTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly TestRegistryKey _key = new();

    public void Dispose()
    {
        _key.Dispose();
        _temp.Dispose();
    }

    // ── History ──────────────────────────────────────────────────────────────

    [Fact]
    public void LegacyHistory_WithoutOutcomes_MapsFromTheOldFlags()
    {
        const string json = """
        [ { "SessionId": "11111111-1111-1111-1111-111111111111", "StartTime": "2026-08-01T10:00:00",
            "Results": [
              { "ApplicationName": "Removed", "Success": true,  "WasSkipped": false, "WasDryRun": false, "ExitCode": 0, "ApplicationType": 5 },
              { "ApplicationName": "Failed",  "Success": false, "WasSkipped": false, "WasDryRun": false, "ExitCode": 1603 },
              { "ApplicationName": "Skipped", "Success": false, "WasSkipped": true,  "WasDryRun": false },
              { "ApplicationName": "Dry",     "Success": true,  "WasSkipped": false, "WasDryRun": true }
            ] } ]
        """;

        var session = Assert.Single(UninstallHistoryService.Parse(json));

        Assert.Equal(UninstallOutcome.Removed, session.Results[0].Outcome);
        Assert.Equal(ApplicationType.Orphaned, session.Results[0].ApplicationType);
        Assert.Equal(UninstallOutcome.Failed, session.Results[1].Outcome);
        Assert.Equal(UninstallOutcome.Skipped, session.Results[2].Outcome);
        Assert.Equal(UninstallOutcome.DryRun, session.Results[3].Outcome);
    }

    [Fact]
    public async Task SavingTheSameSessionTwice_UpdatesIt()
    {
        var history = new UninstallHistoryService(_temp.Combine("history"));
        var session = new UninstallSession { ApplicationNames = ["App"] };
        session.Results.Add(new UninstallResult { ApplicationName = "App", Outcome = UninstallOutcome.Removed });

        await history.SaveSessionAsync(session);
        session.LeftoverReviewDone = true;
        await history.SaveSessionAsync(session);

        var loaded = Assert.Single(await history.GetSessionsAsync());
        Assert.True(loaded.LeftoverReviewDone);
        Assert.Equal(UninstallOutcome.Removed, Assert.Single(loaded.Results).Outcome);
        Assert.False(File.Exists(_temp.Combine("history", "sessions.json.tmp")));
    }

    [Fact]
    public async Task CorruptHistory_LoadsEmpty()
    {
        Directory.CreateDirectory(_temp.Combine("history"));
        await File.WriteAllTextAsync(_temp.Combine("history", "sessions.json"), "{ not json");
        Assert.Empty(await new UninstallHistoryService(_temp.Combine("history")).GetSessionsAsync());
    }

    // ── Verifier ─────────────────────────────────────────────────────────────

    private string CreateUninstallKey(string name, string uninstallString)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{_key.SubKey}\{name}", true);
        key.SetValue("DisplayName", name);
        key.SetValue("UninstallString", uninstallString);
        return $@"{_key.FullPath}\{name}";
    }

    [Fact]
    public async Task RegistryEntry_IsInstalledUntilItsKeyIsGone()
    {
        var exe = _temp.CreateFile("uninst.exe");
        var path = CreateUninstallKey("App", $"\"{exe}\"");
        var app = new InstalledApplication { RegistryKeyPath = path, UninstallerKind = UninstallerKind.Unknown };
        var verifier = new InstallStateVerifier(new FakeMsiReader(), new FileSystemProbe());

        Assert.True(await verifier.IsStillInstalledAsync(app, CancellationToken.None));

        Registry.CurrentUser.DeleteSubKeyTree($@"{_key.SubKey}\App");
        Assert.False(await verifier.IsStillInstalledAsync(app, CancellationToken.None));
    }

    [Fact]
    public async Task StaleSecondRegistration_DoesNotCountAsInstalled_ButAWorkingOneDoes()
    {
        var exe = _temp.CreateFile("uninst.exe");
        var stale = CreateUninstallKey("Stale", "\"C:\\DoesNotExist\\uninst.exe\"");
        var working = CreateUninstallKey("Working", $"\"{exe}\"");
        var verifier = new InstallStateVerifier(new FakeMsiReader(), new FileSystemProbe());

        var withStale = new InstalledApplication { RegistryKeyPath = $@"{_key.FullPath}\Gone", RelatedKeyPaths = [stale] };
        var withWorking = new InstalledApplication { RegistryKeyPath = $@"{_key.FullPath}\Gone", RelatedKeyPaths = [working] };

        Assert.False(await verifier.IsStillInstalledAsync(withStale, CancellationToken.None));
        Assert.True(await verifier.IsStillInstalledAsync(withWorking, CancellationToken.None));
    }

    [Fact]
    public async Task MsiProduct_CountsAsInstalledWhileWindowsInstallerKnowsIt()
    {
        const string pc = "{11111111-2222-3333-4444-555555555555}";
        var msi = new FakeMsiReader();
        var app = new InstalledApplication { RegistryKeyPath = $@"{_key.FullPath}\Gone", UninstallerKind = UninstallerKind.Msi, ProductCode = pc };
        var verifier = new InstallStateVerifier(msi, new FileSystemProbe());

        Assert.True(await verifier.IsStillInstalledAsync(app, CancellationToken.None));

        msi.States[pc] = MsiProductStateReader.InstallStateUnknown;
        Assert.False(await verifier.IsStillInstalledAsync(app, CancellationToken.None));
    }

    [Fact]
    public async Task AppWithoutAKey_IsNeverReportedRemoved()
    {
        var verifier = new InstallStateVerifier(new FakeMsiReader(), new FileSystemProbe());
        Assert.True(await verifier.IsStillInstalledAsync(new InstalledApplication(), CancellationToken.None));
    }
}
