namespace AxePower.Core.Models.Uninstaller;

/// <summary>
/// Unified model representing an installed application discovered from one or more sources.
/// </summary>
public sealed class InstalledApplication
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Internal stable ID (derived from ProductCode, PackageFullName, or a hash).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Publisher / developer name.</summary>
    public string Publisher { get; set; } = string.Empty;

    /// <summary>Installed version string.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Install date in yyyyMMdd format (from registry) or null.</summary>
    public string? InstallDate { get; set; }

    /// <summary>Root installation directory.</summary>
    public string InstallLocation { get; set; } = string.Empty;

    /// <summary>Estimated install size in kilobytes (from registry EstimatedSize).</summary>
    public long EstimatedSizeKb { get; set; }

    // ── Uninstall metadata ────────────────────────────────────────────────────

    /// <summary>The raw uninstall command string from the registry.</summary>
    public string UninstallString { get; set; } = string.Empty;

    /// <summary>Silent uninstall command (QuietUninstallString from registry).</summary>
    public string QuietUninstallString { get; set; } = string.Empty;

    /// <summary>Modify/repair command.</summary>
    public string ModifyPath { get; set; } = string.Empty;

    // ── MSI-specific ──────────────────────────────────────────────────────────

    /// <summary>MSI ProductCode GUID, e.g. {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}.</summary>
    public string ProductCode { get; set; } = string.Empty;

    // ── AppX/MSIX-specific ────────────────────────────────────────────────────

    /// <summary>Full package name for AppX/MSIX packages.</summary>
    public string PackageFullName { get; set; } = string.Empty;

    /// <summary>Package family name.</summary>
    public string PackageFamilyName { get; set; } = string.Empty;

    // ── WinGet-specific ───────────────────────────────────────────────────────

    /// <summary>WinGet package ID (exact match), e.g. "Google.Chrome".</summary>
    public string WingetId { get; set; } = string.Empty;

    /// <summary>WinGet source name, e.g. "winget" or "msstore".</summary>
    public string WingetSource { get; set; } = string.Empty;

    // ── Environment ───────────────────────────────────────────────────────────

    /// <summary>CPU architecture (x64, x86, arm64, neutral).</summary>
    public string Architecture { get; set; } = string.Empty;

    /// <summary>Installation scope: Machine or User.</summary>
    public string Scope { get; set; } = "Machine";

    /// <summary>Path to the application's icon or executable for icon extraction.</summary>
    public string IconPath { get; set; } = string.Empty;

    // ── Classification ────────────────────────────────────────────────────────

    /// <summary>Primary application type.</summary>
    public ApplicationType ApplicationType { get; set; } = ApplicationType.Win32;

    /// <summary>All discovery sources that found this application (may be multiple after deduplication).</summary>
    public DiscoverySource DiscoverySources { get; set; } = DiscoverySource.None;

    // ── Flags ─────────────────────────────────────────────────────────────────

    /// <summary>SystemComponent=1 in registry (hidden by default in Programs & Features).</summary>
    public bool IsSystemComponent { get; set; }

    /// <summary>Protected by AxePower's protection layer — cannot be removed via Uninstaller Pro.</summary>
    public bool IsProtected { get; set; }

    /// <summary>Published by Microsoft (used to offer extra caution).</summary>
    public bool IsMicrosoftComponent { get; set; }

    /// <summary>A process from this application's InstallLocation is currently running.</summary>
    public bool IsRunning { get; set; }

    /// <summary>Whether a valid uninstall path exists.</summary>
    public bool CanUninstall { get; set; } = true;

    /// <summary>Whether the uninstall operation typically requires administrator rights.</summary>
    public bool RequiresElevation { get; set; }

    /// <summary>
    /// Confidence that the uninstall metadata is valid and complete (0–100).
    /// Orphaned apps will have lower confidence.
    /// </summary>
    public int UninstallerConfidence { get; set; } = 100;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Human-readable size string (e.g. "1.27 GB", "450 MB").</summary>
    public string DisplaySize
    {
        get
        {
            if (EstimatedSizeKb <= 0) return string.Empty;
            double mb = EstimatedSizeKb / 1024.0;
            if (mb >= 1024)
                return $"{mb / 1024:F2} GB";
            if (mb >= 1)
                return $"{mb:F0} MB";
            return $"{EstimatedSizeKb} KB";
        }
    }

    /// <summary>Formatted install date or empty string.</summary>
    public string DisplayInstallDate
    {
        get
        {
            if (string.IsNullOrWhiteSpace(InstallDate) || InstallDate.Length != 8)
                return string.Empty;
            if (DateTime.TryParseExact(InstallDate, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt.ToString("MMM d, yyyy");
            return InstallDate;
        }
    }

    public override string ToString() => $"{DisplayName} ({Version}) [{ApplicationType}]";
}
