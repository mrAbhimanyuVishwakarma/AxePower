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

    /// <summary>What applying a tweak would change (with today's values). Changes nothing.</summary>
    Operations.OperationRecord PlanApply(string tweakId);

    /// <summary>What reverting would change: the rollback of the recorded apply, or Windows' defaults when there is none.</summary>
    Operations.OperationRecord PlanRevert(string tweakId);

    IReadOnlyList<Operations.OperationRecord> PlanApplyRecommended();

    IReadOnlyList<Operations.OperationRecord> PlanRevertAll();

    /// <summary>Runs an approved plan and records it.</summary>
    Task<Operations.OperationRecord> ExecuteAsync(Operations.OperationRecord plan);
}
