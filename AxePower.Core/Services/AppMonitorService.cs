using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class AppMonitorService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly AutoSwitchService _autoSwitchService;
    private readonly PowerPlanService _powerPlanService;
    private readonly AppKillerService _appKillerService;
    private readonly LogService _logService;
    private readonly NotificationService _notificationService;

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private bool _isRunning = false;
    private string? _activeTrackedApp = null;

    public AppMonitorService(
        SettingsService settingsService,
        AutoSwitchService autoSwitchService,
        PowerPlanService powerPlanService,
        AppKillerService appKillerService,
        LogService logService,
        NotificationService notificationService)
    {
        _settingsService = settingsService;
        _autoSwitchService = autoSwitchService;
        _powerPlanService = powerPlanService;
        _appKillerService = appKillerService;
        _logService = logService;
        _notificationService = notificationService;
    }

    public void Start()
    {
        if (_isRunning) return;

        _settingsService.OnSettingsChanged += OnSettingsChanged;

        var settings = _settingsService.GetSettings();
        if (settings.AppRulesEnabled)
        {
            StartWatchers();
        }

        _isRunning = true;
        _logService.LogInfo("AppMonitorService started.");
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _settingsService.OnSettingsChanged -= OnSettingsChanged;
        StopWatchers();

        _isRunning = false;
        _logService.LogInfo("AppMonitorService stopped.");
    }

    private void StartWatchers()
    {
        try
        {
            // Watch for Process Starts
            var startQuery = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
            _startWatcher = new ManagementEventWatcher(startQuery);
            _startWatcher.EventArrived += OnProcessStarted;
            _startWatcher.Start();

            // Watch for Process Stops
            var stopQuery = new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace");
            _stopWatcher = new ManagementEventWatcher(stopQuery);
            _stopWatcher.EventArrived += OnProcessStopped;
            _stopWatcher.Start();

            _logService.LogInfo("WMI Process watchers started.");
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to start WMI Process watchers. (May require admin privileges).", ex);
        }
    }

    private void StopWatchers()
    {
        if (_startWatcher != null)
        {
            _startWatcher.EventArrived -= OnProcessStarted;
            _startWatcher.Stop();
            _startWatcher.Dispose();
            _startWatcher = null;
        }

        if (_stopWatcher != null)
        {
            _stopWatcher.EventArrived -= OnProcessStopped;
            _stopWatcher.Stop();
            _stopWatcher.Dispose();
            _stopWatcher = null;
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings e)
    {
        if (e.AppRulesEnabled && _startWatcher == null)
        {
            StartWatchers();
        }
        else if (!e.AppRulesEnabled && _startWatcher != null)
        {
            StopWatchers();
            
            // If we were tracking an app, revert immediately
            if (_activeTrackedApp != null)
            {
                _activeTrackedApp = null;
                _autoSwitchService.Resume();
            }
        }
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        var settings = _settingsService.GetSettings();
        if (!settings.AppRulesEnabled || settings.AppRules.Count == 0) return;

        // If we are already tracking an active app, don't override it until it closes
        if (_activeTrackedApp != null) return;

        string? processName = e.NewEvent.Properties["ProcessName"]?.Value?.ToString();
        if (string.IsNullOrEmpty(processName)) return;

        var rule = settings.AppRules.FirstOrDefault(r => r.ExecutableName.Equals(processName, StringComparison.OrdinalIgnoreCase));
        if (rule != null)
        {
            _logService.LogInfo($"App rule triggered: {processName} started. Applying mode: {rule.TargetMode}.");
            _activeTrackedApp = processName;
            
            // Apply mode
            _autoSwitchService.Pause();
            
            if (settings.GameBoosterEnabled && settings.AppsToClose != null)
            {
                int killedCount = _appKillerService.KillBackgroundApps(settings.AppsToClose);
                if (killedCount > 0)
                {
                    _logService.LogInfo($"Game Booster: Killed {killedCount} background apps.");
                }
            }

            Task.Run(async () => 
            {
                await _powerPlanService.ApplyModeAsync(rule.TargetMode);
                _notificationService.Show($"Game Mode: Switched to {rule.TargetMode}");
            });
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        if (_activeTrackedApp == null) return;

        string? processName = e.NewEvent.Properties["ProcessName"]?.Value?.ToString();
        if (string.IsNullOrEmpty(processName)) return;

        if (processName.Equals(_activeTrackedApp, StringComparison.OrdinalIgnoreCase))
        {
            _logService.LogInfo($"App rule ended: {processName} stopped. Reverting to Smart Mode.");
            _activeTrackedApp = null;
            
            // Resume AutoSwitchService, which will re-evaluate and apply the correct standard mode
            _autoSwitchService.Resume();

            var settings = _settingsService.GetSettings();
            if (settings.GameBoosterEnabled)
            {
                int restartedCount = _appKillerService.RestartBackgroundApps();
                if (restartedCount > 0)
                {
                    _logService.LogInfo($"Game Booster: Restarted {restartedCount} background apps.");
                }
            }

            _notificationService.Show("Game Mode: App closed, reverting power mode.");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
