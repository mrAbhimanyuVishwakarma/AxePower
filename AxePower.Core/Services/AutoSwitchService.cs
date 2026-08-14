using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class AutoSwitchService : IDisposable
{
    private readonly BatteryService _batteryService;
    private readonly PowerPlanService _powerPlanService;
    private readonly SettingsService _settingsService;
    private readonly LogService _logService;
    private readonly NotificationService _notificationService;

    private CancellationTokenSource? _serviceCts;
    private CancellationTokenSource? _transitionCts;
    private readonly SemaphoreSlim _transitionLock = new(1, 1);
    
    private bool? _lastPluggedInState = null;
    private bool _inLowBatteryState = false;
    private bool _isPaused = false;

    public AutoSwitchService(
        BatteryService batteryService,
        PowerPlanService powerPlanService,
        SettingsService settingsService,
        LogService logService,
        NotificationService notificationService)
    {
        _batteryService = batteryService;
        _powerPlanService = powerPlanService;
        _settingsService = settingsService;
        _logService = logService;
        _notificationService = notificationService;
    }

    public void Start()
    {
        _serviceCts = new CancellationTokenSource();
        
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _settingsService.OnSettingsChanged += OnSettingsChanged;

        // Start fallback loop
        Task.Run(() => FallbackMonitorLoop(_serviceCts.Token));
        
        _logService.LogInfo("AutoSwitchService started.");
        
        // Trigger initial evaluation or apply remembered last manual mode if configured
        var settings = _settingsService.GetSettings();
        if (settings.StartWithLastSelectedMode)
        {
            _logService.LogInfo($"StartWithLastSelectedMode is enabled. Applying remembered mode: {settings.LastSelectedPowerMode}");
            var status = _batteryService.GetCurrentStatus();
            _lastPluggedInState = status.IsPluggedIn;
            _inLowBatteryState = settings.LowBatteryRuleEnabled && status.BatteryPercentage <= settings.LowBatteryThresholdPercent;
            _ = _powerPlanService.ApplyModeAsync(settings.LastSelectedPowerMode);
        }
        else
        {
            EvaluatePowerStateAsync().ConfigureAwait(false);
        }
    }

    public void Stop()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _settingsService.OnSettingsChanged -= OnSettingsChanged;
        
        _serviceCts?.Cancel();
        _transitionCts?.Cancel();
        
        _logService.LogInfo("AutoSwitchService stopped.");
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        _logService.LogInfo($"System power mode changed: {e.Mode}");
        EvaluatePowerStateAsync().ConfigureAwait(false);
    }

    private void OnSettingsChanged(object? sender, AppSettings e)
    {
        EvaluatePowerStateAsync().ConfigureAwait(false);
    }

    private async Task FallbackMonitorLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), token);
                await EvaluatePowerStateAsync();
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _logService.LogError("Error in AutoSwitchService fallback monitor loop.", ex);
            }
        }
    }

    public async Task EvaluatePowerStateAsync()
    {
        if (_isPaused)
        {
            return;
        }

        var settings = _settingsService.GetSettings();
        if (!settings.AutoSwitchEnabled)
        {
            return;
        }

        var status = _batteryService.GetCurrentStatus();
        
        // Hysteresis calculation for low battery
        bool isLowBattery = _inLowBatteryState;
        if (settings.LowBatteryRuleEnabled)
        {
            if (status.BatteryPercentage <= settings.LowBatteryThresholdPercent)
            {
                isLowBattery = true;
            }
            else if (status.BatteryPercentage >= settings.LowBatteryThresholdPercent + 5)
            {
                // Must charge 5% above threshold to exit low battery mode
                isLowBattery = false;
            }
        }
        else
        {
            isLowBattery = false;
        }

        bool powerSourceChanged = !_lastPluggedInState.HasValue || _lastPluggedInState.Value != status.IsPluggedIn;
        bool lowBatteryStateChanged = _inLowBatteryState != isLowBattery;

        if (powerSourceChanged || lowBatteryStateChanged)
        {
            _logService.LogInfo($"State change detected. PluggedIn: {status.IsPluggedIn}, LowBattery: {isLowBattery}");
            
            // Cancel any pending transition
            _transitionCts?.Cancel();
            _transitionCts = new CancellationTokenSource();
            var token = _transitionCts.Token;

            // Update state
            _lastPluggedInState = status.IsPluggedIn;
            _inLowBatteryState = isLowBattery;

            // Fire and forget the transition
            _ = ExecuteTransitionAsync(settings, status.IsPluggedIn, isLowBattery, token);
        }
    }

    private async Task ExecuteTransitionAsync(AppSettings settings, bool isPluggedIn, bool isLowBattery, CancellationToken token)
    {
        try
        {
            // Acquire lock to serialize transitions
            await _transitionLock.WaitAsync(token);

            try
            {
                if (settings.SwitchDelaySeconds > 0)
                {
                    _logService.LogInfo($"Waiting {settings.SwitchDelaySeconds} seconds before switching...");
                    await Task.Delay(settings.SwitchDelaySeconds * 1000, token);
                }

                // If cancelled during delay, abort
                token.ThrowIfCancellationRequested();

                // Determine target mode
                PowerMode targetMode;
                if (isPluggedIn)
                {
                    targetMode = settings.PluggedInMode;
                }
                else
                {
                    targetMode = isLowBattery ? settings.LowBatteryMode : settings.UnpluggedMode;
                }

                var currentMode = await _powerPlanService.GetActivePowerModeAsync();
                if (currentMode != targetMode)
                {
                    _logService.LogInfo($"Applying target mode: {targetMode}");
                    var success = await _powerPlanService.ApplyModeAsync(targetMode);
                    if (success && settings.ShowNotifications)
                    {
                        _notificationService.Show($"AxePower switched to {targetMode} mode.");
                    }
                }
                else
                {
                    _logService.LogInfo($"Target mode {targetMode} is already active.");
                }
            }
            finally
            {
                _transitionLock.Release();
            }
        }
        catch (TaskCanceledException)
        {
            _logService.LogInfo("Pending power mode transition was cancelled.");
        }
        catch (Exception ex)
        {
            _logService.LogError("Error during power mode transition.", ex);
        }
    }

    public void CancelPendingTransition()
    {
        _transitionCts?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        _transitionLock.Dispose();
        _serviceCts?.Dispose();
        _transitionCts?.Dispose();
    }

    public void Pause()
    {
        _isPaused = true;
        _logService.LogInfo("AutoSwitchService is now paused (e.g. by Game Mode).");
    }

    public void Resume()
    {
        _isPaused = false;
        _logService.LogInfo("AutoSwitchService is now resumed.");
        EvaluatePowerStateAsync().ConfigureAwait(false);
    }
}
