using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Scores leftover items by confidence that they belong to the removed application.
/// This is an application-specific cleaner — NOT a generic registry cleaner.
/// Every result must answer: "Does this item probably belong to the app we just removed?"
/// </summary>
public sealed class LeftoverConfidenceService : ILeftoverConfidenceService
{
    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
    private static readonly string System32Dir =
        Environment.GetFolderPath(Environment.SpecialFolder.System).ToLowerInvariant();
    private static readonly string ProgramFilesDir =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).ToLowerInvariant();
    private static readonly string WindowsAppsDir =
        Path.Combine(ProgramFilesDir, "windowsapps");

    // Known shared component patterns — presence indicates shared, not app-specific
    private static readonly string[] SharedComponentPatterns =
    [
        "microsoft visual c++",
        "microsoft .net",
        "directx",
        "vcredist",
        "vc_redist",
        "dotnet",
        "windowsapps",
        "windows kits",
        "common files",
    ];

    public (int Score, string Reason, bool IsProtected, bool IsShared) Score(
        LeftoverItem item,
        InstalledApplication application)
    {
        var pathLower = item.Path.ToLowerInvariant();
        bool isProtected = false;
        bool isShared = false;
        int score = 0;
        var reasons = new List<string>();

        // ── Protected path check (overrides everything) ─────────────────────
        if (IsSystemPath(pathLower))
        {
            return (Score: 5, Reason: "System-protected path — not associated with this application.",
                    IsProtected: true, IsShared: false);
        }

        // ── Shared component check ───────────────────────────────────────────
        if (IsSharedComponent(pathLower))
        {
            isShared = true;
            return (Score: 20, Reason: "Appears to be a shared component — not exclusively associated with this application.",
                    IsProtected: false, IsShared: true);
        }

        // ── Direct InstallLocation match ─────────────────────────────────────
        if (!string.IsNullOrEmpty(application.InstallLocation))
        {
            var installLower = application.InstallLocation.ToLowerInvariant().TrimEnd('\\', '/');
            if (pathLower.StartsWith(installLower))
            {
                score += 50;
                reasons.Add("Path is within the application's install directory");
            }
        }

        // ── ProductCode / Package identity ───────────────────────────────────
        if (!string.IsNullOrEmpty(application.ProductCode))
        {
            if (pathLower.Contains(application.ProductCode.ToLowerInvariant()))
            {
                score += 40;
                reasons.Add("Exact ProductCode match");
            }
        }
        if (!string.IsNullOrEmpty(application.PackageFamilyName))
        {
            if (pathLower.Contains(application.PackageFamilyName.ToLowerInvariant()))
            {
                score += 40;
                reasons.Add("Exact PackageFamilyName match");
            }
        }

        // ── Exact publisher + app name folder ────────────────────────────────
        var appNameClean = (application.DisplayName ?? string.Empty).ToLowerInvariant().Trim();
        var appNameNorm  = Normalize(application.DisplayName);
        var pubClean     = (application.Publisher ?? string.Empty).ToLowerInvariant().Trim();
        var pubNorm      = Normalize(application.Publisher);

        var pathNorm = Normalize(pathLower);

        bool hasAppName = (!string.IsNullOrEmpty(appNameClean) && pathLower.Contains(appNameClean)) ||
                          (!string.IsNullOrEmpty(appNameNorm) && pathNorm.Contains(appNameNorm));

        bool hasPub     = (!string.IsNullOrEmpty(pubClean) && pathLower.Contains(pubClean)) ||
                          (!string.IsNullOrEmpty(pubNorm) && pathNorm.Contains(pubNorm));

        if (hasAppName && hasPub)
        {
            score += 35;
            reasons.Add("Exact publisher + application name match");
        }
        else if (hasAppName)
        {
            score += 20;
            reasons.Add("Application name match");
        }
        else if (hasPub)
        {
            score += 10;
            reasons.Add("Publisher folder match only");
        }

        // ── AppData / ProgramData match ──────────────────────────────────────
        var appData     = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).ToLowerInvariant();
        var localData   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).ToLowerInvariant();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).ToLowerInvariant();

        if ((pathLower.StartsWith(appData) || pathLower.StartsWith(localData) || pathLower.StartsWith(programData))
            && hasAppName)
        {
            score += 10;
            reasons.Add("Application-specific AppData/ProgramData location");
        }

        // ── Registry: exact uninstall key ────────────────────────────────────
        if (item.ItemType == LeftoverItemType.RegistryKey ||
            item.ItemType == LeftoverItemType.RegistryValue)
        {
            if (!string.IsNullOrEmpty(application.ProductCode) &&
                pathLower.Contains(application.ProductCode.ToLowerInvariant()))
            {
                score += 40;
                reasons.Add("Registry key contains exact ProductCode");
            }
        }

        // ── Cap score ────────────────────────────────────────────────────────
        score = Math.Min(score, 100);

        var reason = reasons.Count > 0
            ? string.Join("; ", reasons)
            : "Weak association by path heuristic";

        return (score, reason, isProtected, isShared);
    }

    private static bool IsSystemPath(string pathLower)
    {
        return pathLower.StartsWith(WindowsDir) ||
               pathLower.StartsWith(System32Dir) ||
               pathLower.StartsWith(WindowsAppsDir) ||
               pathLower.Contains(@"\windows\system32\") ||
               pathLower.Contains(@"\windows\syswow64\");
    }

    private static bool IsSharedComponent(string pathLower)
    {
        foreach (var pattern in SharedComponentPatterns)
            if (pathLower.Contains(pattern)) return true;
        return false;
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        return s.ToLowerInvariant().Trim().Replace(" ", "").Replace("-", "").Replace("_", "");
    }
}
