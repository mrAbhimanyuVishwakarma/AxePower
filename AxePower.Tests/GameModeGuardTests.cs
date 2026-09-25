using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.Tests;

/// <summary>Game Mode is held while a known game runs. Fakes only: no real processes or plans.</summary>
public class GameModeGuardTests : IDisposable
{
    private readonly string _settingsDirectory = Path.Combine(Path.GetTempPath(), $"axepower_guard_{Guid.NewGuid():N}");
    private readonly LogService _log = new();
    private readonly SettingsService _settings;
    private readonly PowerPlanService _powerPlan;
    private readonly List<string> _running = new() { "explorer", "chrome" };
    private readonly List<string> _library = new() { @"C:\Games\Hades\Hades.exe" };
    private readonly GameModeGuard _guard;

    public GameModeGuardTests()
    {
        _settings = new SettingsService(_log, _settingsDirectory);
        _powerPlan = TestPowerPlan.Create(_log, _settings);
        _guard = new GameModeGuard(_powerPlan, _settings,
            () => Task.FromResult<IEnumerable<string>>(_library), () => _running);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_settingsDirectory)) Directory.Delete(_settingsDirectory, true); } catch { }
    }

    [Fact]
    public async Task InGameMode_WithALibraryGameRunning_BlocksAnotherMode()
    {
        await _powerPlan.ApplyModeAsync(PowerMode.GameMode);
        _running.Add("Hades");

        Assert.Equal("Hades", await _guard.GetBlockingGameAsync(PowerMode.Eco));
    }

    [Fact]
    public async Task InGameMode_WithAnAppRuleGameRunning_BlocksAnotherMode()
    {
        var s = _settings.GetSettings();
        s.AppRules.Add(new AppRule { ExecutableName = "Cyberpunk2077.exe", TargetMode = PowerMode.GameMode });
        _settings.SaveSettings(s);
        await _powerPlan.ApplyModeAsync(PowerMode.GameMode);
        _running.Add("Cyberpunk2077");

        Assert.Equal("Cyberpunk2077", await _guard.GetBlockingGameAsync(PowerMode.Smart));
    }

    [Fact]
    public async Task InGameMode_WithNoGameRunning_DoesNotBlock()
    {
        await _powerPlan.ApplyModeAsync(PowerMode.GameMode);

        Assert.Null(await _guard.GetBlockingGameAsync(PowerMode.Eco));
    }

    [Fact]
    public async Task OutsideGameMode_ARunningGameDoesNotBlock()
    {
        await _powerPlan.ApplyModeAsync(PowerMode.Beast);
        _running.Add("Hades");

        Assert.Null(await _guard.GetBlockingGameAsync(PowerMode.Eco));
    }

    [Fact]
    public async Task StayingInGameMode_IsNeverBlocked()
    {
        await _powerPlan.ApplyModeAsync(PowerMode.GameMode);
        _running.Add("Hades");

        Assert.Null(await _guard.GetBlockingGameAsync(PowerMode.GameMode));
    }
}
