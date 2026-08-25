using System;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class DynamicModeAutomationService : IDynamicModeAutomationService
{
    private readonly IServiceThrottlerService _serviceThrottler;
    private readonly IMemoryOptimizerService _memoryOptimizer;
    private readonly NotificationService _notificationService;
    private readonly LogService _log;

    public DynamicAutomationConfig Config { get; } = new();

    public DynamicModeAutomationService(
        IServiceThrottlerService serviceThrottler,
        IMemoryOptimizerService memoryOptimizer,
        NotificationService notificationService,
        LogService log)
    {
        _serviceThrottler = serviceThrottler;
        _memoryOptimizer = memoryOptimizer;
        _notificationService = notificationService;
        _log = log;
    }

    public async Task OnModeChangedAsync(string newMode)
    {
        _log.LogInfo($"[DynamicModeAutomation] Mode changed event received: '{newMode}'");

        bool isHighPerformanceMode = string.Equals(newMode, "Beast", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(newMode, "GameMode", StringComparison.OrdinalIgnoreCase);

        bool isEcoOrBalanced = string.Equals(newMode, "Eco", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(newMode, "Balanced", StringComparison.OrdinalIgnoreCase);

        if (isHighPerformanceMode)
        {
            if (Config.AutoPauseServicesInBeast && !_serviceThrottler.AreServicesThrottled)
            {
                int throttled = await _serviceThrottler.ThrottleHeavyServicesAsync();
                if (throttled > 0)
                {
                    _notificationService.Show($"⚡ Dynamic Mode: Paused {throttled} non-essential background service(s) for maximum performance!");
                }
            }

            if (Config.AutoFlushRamOnGameLaunch)
            {
                double freedGb = await _memoryOptimizer.PurgeStandbyMemoryAsync();
                if (freedGb > 0.05)
                {
                    _notificationService.Show($"🧹 Memory Tuned: Cleared ~{freedGb * 1024:F0} MB of standby RAM cache.");
                }
            }
        }
        else if (isEcoOrBalanced)
        {
            if (Config.AutoRestoreOnEco && _serviceThrottler.AreServicesThrottled)
            {
                int restored = await _serviceThrottler.RestoreThrottledServicesAsync();
                if (restored > 0)
                {
                    _notificationService.Show($"🌱 Eco/Balanced Mode: Restored {restored} background service(s).");
                }
            }
        }
    }

    public async Task<int> ForceThrottleServicesAsync()
    {
        return await _serviceThrottler.ThrottleHeavyServicesAsync();
    }

    public async Task<int> ForceRestoreServicesAsync()
    {
        return await _serviceThrottler.RestoreThrottledServicesAsync();
    }

    public async Task<double> OptimizeMemoryNowAsync()
    {
        return await _memoryOptimizer.PurgeStandbyMemoryAsync();
    }
}
