namespace AxePower.Core.Models.Uninstaller;

/// <summary>
/// Scan depth for leftover detection.
/// </summary>
public enum ScanMode
{
    /// <summary>Only exact known application-specific locations. Fastest, lowest risk.</summary>
    Safe,

    /// <summary>
    /// Safe locations plus publisher subtrees, AppData, ProgramData name matches.
    /// Recommended default.
    /// </summary>
    Moderate,

    /// <summary>
    /// Moderate locations plus CLSIDs, GUIDs, shell integration, service references,
    /// startup entries. Every result still passes the confidence filter.
    /// </summary>
    Advanced,
}

/// <summary>Persistent settings for Uninstaller Pro.</summary>
public sealed class UninstallerSettings
{
    public ScanMode DefaultScanMode { get; set; } = ScanMode.Moderate;

    /// <summary>Create a System Restore Point before batch uninstall.</summary>
    public bool CreateSystemRestorePoint { get; set; } = true;

    /// <summary>Back up registry keys before deletion.</summary>
    public bool BackupRegistryChanges { get; set; } = true;

    /// <summary>Move deleted files to Recycle Bin instead of permanent delete.</summary>
    public bool MoveToRecycleBin { get; set; } = true;

    /// <summary>Automatically scan for leftovers after each application is removed.</summary>
    public bool AutoScanLeftovers { get; set; } = true;

    /// <summary>Include protected applications in the list (shown but not deletable).</summary>
    public bool ShowProtectedApplications { get; set; } = false;

    /// <summary>Show orphaned applications in the list.</summary>
    public bool ShowOrphanedApplications { get; set; } = true;

    /// <summary>
    /// Allow advanced scan mode in the UI.
    /// Advanced scan is more thorough but carries higher false-positive risk.
    /// </summary>
    public bool AllowAdvancedScan { get; set; } = true;

    /// <summary>
    /// SAFETY: When true, no real uninstall commands or deletions are executed.
    /// All operations are logged as "Would execute".
    /// </summary>
    public bool DryRunMode { get; set; } = false;

    /// <summary>Registry paths explicitly excluded from leftover suggestions.</summary>
    public List<string> ExcludedRegistryPaths { get; set; } = new();

    /// <summary>File system paths explicitly excluded from leftover suggestions.</summary>
    public List<string> ExcludedFilePaths { get; set; } = new();
}
