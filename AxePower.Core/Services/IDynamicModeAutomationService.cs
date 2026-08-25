using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public interface IDynamicModeAutomationService
{
    DynamicAutomationConfig Config { get; }
    Task OnModeChangedAsync(string newMode);
    Task<int> ForceThrottleServicesAsync();
    Task<int> ForceRestoreServicesAsync();
    Task<double> OptimizeMemoryNowAsync();
}
