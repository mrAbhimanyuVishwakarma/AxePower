using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.Tests;

/// <summary>
/// "Use dedicated GPU in Game Mode" against a fake preference store: the real one writes
/// HKCU\Software\Microsoft\DirectX\UserGpuPreferences.
/// </summary>
public class GameModeGpuServiceTests : IDisposable
{
    private const string Game = @"C:\Games\Hades\Hades.exe";
    private const string OtherGame = @"D:\SteamLibrary\steamapps\common\Celeste\Celeste.exe";

    private readonly string _settingsDirectory = Path.Combine(Path.GetTempPath(), $"axepower_gpu_{Guid.NewGuid():N}");
    private readonly LogService _log = new();
    private readonly FakeGpuPreferences _store = new();
    private readonly List<string> _games = new() { Game, OtherGame };
    private readonly SettingsService _settings;
    private readonly GameModeGpuService _service;

    public GameModeGpuServiceTests()
    {
        _settings = new SettingsService(_log, _settingsDirectory);
        _service = new GameModeGpuService(_settings, _log, _store, () => Task.FromResult<IEnumerable<string>>(_games));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_settingsDirectory)) Directory.Delete(_settingsDirectory, true); } catch { }
    }

    private void TurnOn()
    {
        var s = _settings.GetSettings();
        s.GameModeUseDedicatedGpu = true;
        _settings.SaveSettings(s);
    }

    [Fact]
    public async Task ToggleOff_GameModeChangesNothing()
    {
        await _service.OnModeAppliedAsync(PowerMode.GameMode);

        Assert.Empty(_store.Values);
    }

    [Fact]
    public async Task ToggleOn_GameModePinsEveryGameToHighPerformance()
    {
        TurnOn();

        await _service.OnModeAppliedAsync(PowerMode.GameMode);

        Assert.Equal("GpuPreference=2;", _store.Values[Game]);
        Assert.Equal("GpuPreference=2;", _store.Values[OtherGame]);
    }

    [Fact]
    public async Task LeavingGameMode_PutsBackExactlyWhatWasThere()
    {
        _store.Values[OtherGame] = "SwapEffectUpgradeEnable=1;GpuPreference=1;";
        TurnOn();
        await _service.OnModeAppliedAsync(PowerMode.GameMode);
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=2;", _store.Values[OtherGame]);

        await _service.OnModeAppliedAsync(PowerMode.Smart);

        Assert.False(_store.Values.ContainsKey(Game));
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=1;", _store.Values[OtherGame]);
        Assert.Empty(_settings.GetSettings().GpuPreferenceBackups);
    }

    [Fact]
    public async Task APreferenceTheUserChangedDuringGameMode_IsLeftAlone()
    {
        TurnOn();
        await _service.OnModeAppliedAsync(PowerMode.GameMode);
        _store.Values[Game] = "GpuPreference=1;";

        await _service.OnModeAppliedAsync(PowerMode.Eco);

        Assert.Equal("GpuPreference=1;", _store.Values[Game]);
    }

    [Fact]
    public async Task AnInterruptedGameMode_IsRestoredByTheNextStartsModeChange()
    {
        TurnOn();
        await _service.OnModeAppliedAsync(PowerMode.GameMode);

        // AxePower closed or crashed; a new process starts with the same settings file
        var reloaded = new SettingsService(_log, _settingsDirectory);
        reloaded.LoadSettings();
        var afterRestart = new GameModeGpuService(reloaded, _log, _store,
            () => Task.FromResult<IEnumerable<string>>(_games));
        await afterRestart.OnModeAppliedAsync(PowerMode.Smart);

        Assert.Empty(_store.Values);
    }

    [Fact]
    public async Task EnteringGameModeTwice_DoesNotLoseTheOriginalValue()
    {
        _store.Values[Game] = "GpuPreference=0;";
        TurnOn();
        await _service.OnModeAppliedAsync(PowerMode.GameMode);
        await _service.OnModeAppliedAsync(PowerMode.GameMode);

        await _service.OnModeAppliedAsync(PowerMode.Smart);

        Assert.Equal("GpuPreference=0;", _store.Values[Game]);
    }

    [Fact]
    public async Task TurningTheToggleOff_DuringGameMode_RestoresAtOnce()
    {
        TurnOn();
        await _service.OnModeAppliedAsync(PowerMode.GameMode);

        var s = _settings.GetSettings();
        s.GameModeUseDedicatedGpu = false;
        _settings.SaveSettings(s);
        await _service.OnSettingChangedAsync();

        Assert.Empty(_store.Values);
    }

    [Fact]
    public async Task TurningTheToggleOn_DuringGameMode_PinsAtOnce()
    {
        await _service.OnModeAppliedAsync(PowerMode.GameMode);
        TurnOn();

        await _service.OnSettingChangedAsync();

        Assert.Equal(2, _store.Values.Count);
    }

    [Theory]
    [InlineData(null, "GpuPreference=2;")]
    [InlineData("", "GpuPreference=2;")]
    [InlineData("GpuPreference=1;", "GpuPreference=2;")]
    [InlineData("SwapEffectUpgradeEnable=0;", "SwapEffectUpgradeEnable=0;GpuPreference=2;")]
    public void UserGpuPreference_SetsHighPerformanceAndKeepsOtherKeys(string? existing, string expected)
    {
        Assert.Equal(expected, UserGpuPreference.WithHighPerformance(existing));
    }

    private sealed class FakeGpuPreferences : IGpuPreferenceStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string exePath) => Values.TryGetValue(exePath, out var v) ? v : null;

        public bool Set(string exePath, string value)
        {
            Values[exePath] = value;
            return true;
        }

        public bool Delete(string exePath) => Values.Remove(exePath) || true;
    }
}
