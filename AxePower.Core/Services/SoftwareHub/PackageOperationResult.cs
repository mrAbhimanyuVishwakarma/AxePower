using System.Collections.Generic;
using System.Linq;

namespace AxePower.Core.Services.SoftwareHub;

public enum PackageOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    /// <summary>Not run: invalid input or nothing that could be done.</summary>
    Refused,
    /// <summary>Not reached because the batch was cancelled.</summary>
    Skipped
}

/// <param name="OfferedWebUrl">A page the user may want to open instead (a bundled installer that is missing).</param>
public sealed record PackageOperationResult(PackageOutcome Outcome, string? Message = null, string? OfferedWebUrl = null)
{
    public bool Succeeded => Outcome == PackageOutcome.Succeeded;
}

/// <summary>Reported before (Outcome null) and after each package of a batch.</summary>
public sealed record PackageBatchProgress(int Index, int Count, string Id, string Name, PackageOutcome? Outcome);

public sealed record PackageBatchItem(string Id, string Name, PackageOperationResult Result);

public sealed record PackageBatchResult(IReadOnlyList<PackageBatchItem> Items)
{
    public int SucceededCount => Items.Count(i => i.Result.Outcome == PackageOutcome.Succeeded);
    public bool WasCancelled => Items.Any(i => i.Result.Outcome is PackageOutcome.Cancelled or PackageOutcome.Skipped);
}
