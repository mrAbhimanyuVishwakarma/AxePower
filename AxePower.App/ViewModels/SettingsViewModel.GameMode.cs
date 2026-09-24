using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AxePower.App.Services;
using AxePower.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels;

public sealed record DisplayModeOption(GpuDisplayMode Mode, string DisplayName);

/// <summary>The Game Mode card: dedicated GPU in Game Mode, and the Optimus / dGPU-only display mode.</summary>
public partial class SettingsViewModel
{
    private GameModeGpuService? _gameModeGpu;
    private IGpuDisplayModeController? _displayModeController;
    private IGameModePrompts? _gameModePrompts;
    private GpuControlAppService? _gpuControl;

    /// <summary>Set while a change is being rolled back, so the rollback does not ask again.</summary>
    private bool _suppressGameModePrompts;

    public IReadOnlyList<DisplayModeOption> DisplayModeOptions { get; } = new[]
    {
        new DisplayModeOption(GpuDisplayMode.Optimus, "Optimus (Hybrid)"),
        new DisplayModeOption(GpuDisplayMode.DedicatedOnly, "Dedicated GPU only"),
    };

    [ObservableProperty]
    private bool _gameModeUseDedicatedGpu;

    /// <summary>True once the firmware answered; false shows the NVIDIA panel button instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisplayModeUnsupported))]
    private bool _isDisplayModeSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisplayModeUnsupported))]
    private bool _isDisplayModeChecked;

    public bool IsDisplayModeUnsupported => IsDisplayModeChecked && !IsDisplayModeSupported;

    [ObservableProperty]
    private DisplayModeOption? _selectedDisplayMode;

    [ObservableProperty]
    private bool _displayModeRestartRequired;

    private void InitializeGameMode(GameModeGpuService? gameModeGpu, IGpuDisplayModeController? displayMode,
        IGameModePrompts? prompts, GpuControlAppService? gpuControl)
    {
        _gameModeGpu = gameModeGpu;
        _displayModeController = displayMode;
        _gameModePrompts = prompts;
        _gpuControl = gpuControl;
        _gameModeUseDedicatedGpu = _currentSettings.GameModeUseDedicatedGpu;

        if (_displayModeController != null)
            _ = LoadDisplayModeAsync();
        else
            IsDisplayModeChecked = true;
    }

    private async Task LoadDisplayModeAsync()
    {
        try
        {
            var controller = _displayModeController!;
            bool supported = await controller.IsSupportedAsync();
            var mode = supported ? await controller.GetModeAsync() : null;

            _suppressGameModePrompts = true;
            try
            {
                IsDisplayModeSupported = supported && mode != null;
                SelectedDisplayMode = mode == null ? null : Option(mode.Value);
                DisplayModeRestartRequired = controller.RestartRequired;
            }
            finally
            {
                _suppressGameModePrompts = false;
            }
        }
        catch
        {
            IsDisplayModeSupported = false;
        }
        finally
        {
            IsDisplayModeChecked = true;
        }
    }

    partial void OnGameModeUseDedicatedGpuChanged(bool value)
    {
        if (CurrentSettings == null || _suppressGameModePrompts) return;

        // Turning it on is the risky direction; turning it off only puts things back
        if (value && _gameModePrompts != null && !_gameModePrompts.ConfirmDedicatedGpu())
        {
            Rollback(() => GameModeUseDedicatedGpu = false);
            return;
        }

        CurrentSettings.GameModeUseDedicatedGpu = value;
        SaveCurrentSettings();
        if (_gameModeGpu != null) _ = _gameModeGpu.OnSettingChangedAsync();
        _notificationService.Show(value
            ? "Game Mode will use the dedicated GPU for your games."
            : "Game Mode no longer changes the GPU your games use.");
    }

    partial void OnSelectedDisplayModeChanged(DisplayModeOption? oldValue, DisplayModeOption? newValue)
    {
        if (_suppressGameModePrompts || newValue == null || oldValue == null || oldValue.Mode == newValue.Mode) return;
        _ = ChangeDisplayModeAsync(oldValue, newValue);
    }

    private async Task ChangeDisplayModeAsync(DisplayModeOption previous, DisplayModeOption requested)
    {
        if (_displayModeController == null) return;

        if (_gameModePrompts != null && !_gameModePrompts.ConfirmDisplayMode(requested.Mode))
        {
            Rollback(() => SelectedDisplayMode = previous);
            return;
        }

        if (!await _displayModeController.SetModeAsync(requested.Mode))
        {
            _notificationService.Show("Could not change the display mode. Is AxePower running as administrator?");
            Rollback(() => SelectedDisplayMode = previous);
            return;
        }

        DisplayModeRestartRequired = _displayModeController.RestartRequired;
        if (DisplayModeRestartRequired && _gameModePrompts != null && _gameModePrompts.AskRestartNow(requested.Mode))
            _gameModePrompts.RestartWindows();
    }

    [RelayCommand]
    private void RestartForDisplayMode()
    {
        if (_gameModePrompts != null && SelectedDisplayMode != null && _gameModePrompts.AskRestartNow(SelectedDisplayMode.Mode))
            _gameModePrompts.RestartWindows();
    }

    [RelayCommand]
    private void OpenGpuControlPanel()
    {
        if (_gpuControl?.OpenGraphicsSettings() != true)
            _notificationService.Show("Could not open the graphics control panel.");
    }

    private DisplayModeOption Option(GpuDisplayMode mode) =>
        mode == GpuDisplayMode.DedicatedOnly ? DisplayModeOptions[1] : DisplayModeOptions[0];

    private void Rollback(Action undo)
    {
        _suppressGameModePrompts = true;
        try { undo(); }
        finally { _suppressGameModePrompts = false; }
    }
}
