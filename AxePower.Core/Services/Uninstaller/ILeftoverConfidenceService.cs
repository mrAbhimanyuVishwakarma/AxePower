using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Scores leftover items by confidence that they belong to the removed application.</summary>
public interface ILeftoverConfidenceService
{
    /// <summary>
    /// Assigns a confidence score (0–100) and reason to the given leftover item,
    /// given the application that was removed.
    /// </summary>
    (int Score, string Reason, bool IsProtected, bool IsShared) Score(
        LeftoverItem item,
        InstalledApplication application);
}
