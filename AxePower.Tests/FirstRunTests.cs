using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AxePower.App.ViewModels;
using AxePower.Core.Models;
using AxePower.Core.Services;
using Xunit;

namespace AxePower.Tests;

/// <summary>
/// First run and safe defaults (Prompt 31): a fresh install starts with nothing registered,
/// elevated, switched or checked online until the welcome's Continue; existing users keep every
/// setting exactly; and the start-up power-mode change only happens with agreement.
/// </summary>
public sealed class FirstRunTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "axepower-firstrun-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { }
    }

    private SettingsService Load()
    {
        var settings = new SettingsService(new LogService(), _folder);
        settings.LoadSettings();
        return settings;
    }

    [Fact]
    public void AFreshInstall_StartsWithNothingSurprising()
    {
        var s = Load().GetSettings();

        Assert.False(s.FirstRunCompleted);
        Assert.False(s.StartWithWindows);
        Assert.False(s.RunAsAdministrator);
        Assert.False(s.AutoSwitchEnabled);
        Assert.False(s.ApplyExpectedModeOnStartup);
        Assert.False(s.AutoCheckUpdates);
        Assert.False(s.AutoDownloadUpdates);
        Assert.False(s.RemoteAccess.Enabled);
    }

    [Fact]
    public void TheFreshInstallState_IsSaved_SoTheWelcomeShowsAgainUntilContinue()
    {
        Load();
        Assert.False(Load().GetSettings().FirstRunCompleted);
    }

    [Fact]
    public void AnExistingSettingsFile_IsKeptExactly_AndNeverSeesTheWelcome()
    {
        // A settings file as written before Prompt 31: no firstRunCompleted key, the old defaults
        var old = new AppSettings { StartWithWindows = true, RunAsAdministrator = true, AutoSwitchEnabled = true, AutoDownloadUpdates = true, Theme = AppTheme.Light };
        var json = JsonNode.Parse(JsonSerializer.Serialize(old, Options))!.AsObject();
        json.Remove("firstRunCompleted");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "settings.json"), json.ToJsonString());

        var loaded = Load().GetSettings();

        Assert.True(loaded.FirstRunCompleted);
        var before = JsonNode.Parse(JsonSerializer.Serialize(old, Options))!.AsObject();
        var after = JsonNode.Parse(JsonSerializer.Serialize(loaded, Options))!.AsObject();
        before.Remove("firstRunCompleted");
        after.Remove("firstRunCompleted");
        Assert.Equal(before.ToJsonString(), after.ToJsonString());
    }

    [Fact]
    public void ACompletedWelcome_StaysCompleted_AcrossLoads()
    {
        var settings = Load();
        new WelcomeViewModel(settings).Continue();
        Assert.True(Load().GetSettings().FirstRunCompleted);
    }

    [Fact]
    public void TheWelcome_OffersTheRecommendedChoices()
    {
        var vm = new WelcomeViewModel(Load());
        Assert.False(vm.StartWithWindows);
        Assert.True(vm.RunAsAdministrator);
        Assert.True(vm.AutomaticPowerModes);
        Assert.True(vm.CheckForUpdates);
        Assert.False(vm.DownloadUpdatesAutomatically);
    }

    [Fact]
    public void Continue_SavesExactlyWhatWasChosen()
    {
        var settings = Load();
        var vm = new WelcomeViewModel(settings)
        {
            StartWithWindows = true,
            RunAsAdministrator = false,
            AutomaticPowerModes = false,
            CheckForUpdates = true,
            DownloadUpdatesAutomatically = true,
        };
        vm.Continue();

        var s = Load().GetSettings();
        Assert.True(vm.Completed);
        Assert.True(s.FirstRunCompleted);
        Assert.True(s.StartWithWindows);
        Assert.False(s.RunAsAdministrator);
        Assert.False(s.AutoSwitchEnabled);
        Assert.False(s.ApplyExpectedModeOnStartup);
        Assert.True(s.AutoCheckUpdates);
        Assert.True(s.AutoDownloadUpdates);
    }

    [Fact]
    public void TurningOffUpdateChecks_AlsoTurnsOffAutomaticDownloads()
    {
        var vm = new WelcomeViewModel(Load()) { DownloadUpdatesAutomatically = true };
        vm.CheckForUpdates = false;
        Assert.False(vm.DownloadUpdatesAutomatically);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task AutoSwitch_AppliesAModeAtStartup_OnlyWithAgreement(bool agreed, bool expectChange)
    {
        var log = new LogService();
        var settings = Load();
        var s = settings.GetSettings();
        s.FirstRunCompleted = true;
        s.ApplyExpectedModeOnStartup = agreed;
        settings.SaveSettings(s);

        var schemes = new FakePowerSchemes();
        var before = schemes.Active;
        var power = new PowerPlanService(log, new FakeRefreshRate(), settings, schemes);
        var autoSwitch = new AutoSwitchService(new BatteryService(log), power, settings, log, new NotificationService(), new WindowsAlertSoundService(log));
        try
        {
            autoSwitch.Start();
            await Task.Delay(500);
            bool changed = schemes.Active != before || schemes.Duplicates.Count > 0 || schemes.Deleted.Count > 0;
            Assert.Equal(expectChange, changed);
        }
        finally
        {
            autoSwitch.Stop();
        }
    }

    private static readonly JsonSerializerOptions Options = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}
