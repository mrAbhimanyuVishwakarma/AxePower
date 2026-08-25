using System.Collections.Generic;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public interface IServiceThrottlerService
{
    IReadOnlyList<ManagedServiceItem> GetManagedServices();
    Task<int> ThrottleHeavyServicesAsync();
    Task<int> RestoreThrottledServicesAsync();
    bool AreServicesThrottled { get; }
}
