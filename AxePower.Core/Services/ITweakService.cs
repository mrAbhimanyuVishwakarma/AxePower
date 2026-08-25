using System.Collections.Generic;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public interface ITweakService
{
    IReadOnlyList<SystemTweakItem> GetAllTweaks();
    Task RefreshAllTweakStatusesAsync();
    Task<bool> ApplyTweakAsync(string tweakId);
    Task<bool> RevertTweakAsync(string tweakId);
    Task<int> ApplyAllRecommendedAsync();
    Task<int> RevertAllAsync();
}
