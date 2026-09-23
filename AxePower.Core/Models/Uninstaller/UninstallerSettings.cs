using System.Text.Json.Serialization;

namespace AxePower.Core.Models.Uninstaller;

/// <summary>
/// Scan depth for leftover detection.
/// </summary>
public enum ScanMode
{
    /// <summary>Only items with explicit evidence (they point into the app's folder). Fastest, lowest risk.</summary>
    Safe,

    /// <summary>
    /// Safe finders plus name-matched folders and registry keys (AppData, ProgramData, Program Files,
    /// Software keys). Recommended default.
    /// </summary>
    Moderate,

    /// <summary>
    /// Moderate plus App Paths, compatibility flags, MUI cache, COM classes, scheduled tasks, and
    /// (for information) services and firewall rules. Every result still carries evidence.
    /// </summary>
    Advanced,
}

/// <summary>
/// Persistent settings for Uninstaller Pro, stored under the <c>uninstaller</c> key of settings.json.
/// Every property needs a default and a line in <see cref="Clone"/>, or its value is lost.
/// </summary>
public sealed class UninstallerSettings
{
    [JsonPropertyName("defaultScanMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScanMode DefaultScanMode { get; set; } = ScanMode.Moderate;

    /// <summary>Use silent commands where the uninstaller kind supports them. Off: vendors' prompts stay visible.</summary>
    [JsonPropertyName("preferQuietUninstall")]
    public bool PreferQuietUninstall { get; set; } = false;

    /// <summary>Create a System Restore Point before batch uninstall.</summary>
    [JsonPropertyName("createSystemRestorePoint")]
    public bool CreateSystemRestorePoint { get; set; } = true;

    /// <summary>Back up HKCU registry keys before deletion. HKLM keys are always backed up.</summary>
    [JsonPropertyName("backupRegistryChanges")]
    public bool BackupRegistryChanges { get; set; } = true;

    /// <summary>Move deleted files to Recycle Bin instead of permanent delete.</summary>
    [JsonPropertyName("moveToRecycleBin")]
    public bool MoveToRecycleBin { get; set; } = true;

    /// <summary>Scan for leftovers after each verified removal.</summary>
    [JsonPropertyName("autoScanLeftovers")]
    public bool AutoScanLeftovers { get; set; } = true;

    /// <summary>Include protected applications in the list (shown but not removable).</summary>
    [JsonPropertyName("showProtectedApplications")]
    public bool ShowProtectedApplications { get; set; } = false;

    /// <summary>Show broken entries (uninstaller missing) in the list.</summary>
    [JsonPropertyName("showOrphanedApplications")]
    public bool ShowOrphanedApplications { get; set; } = true;

    /// <summary>Show SystemComponent=1 entries, which Windows Settings hides.</summary>
    [JsonPropertyName("showSystemComponents")]
    public bool ShowSystemComponents { get; set; } = false;

    /// <summary>Show updates, hotfixes and child components (ParentKeyName / ReleaseType).</summary>
    [JsonPropertyName("showUpdates")]
    public bool ShowUpdates { get; set; } = false;

    /// <summary>Show leftovers the protection rules refuse, disabled, with the reason.</summary>
    [JsonPropertyName("showBlockedLeftovers")]
    public bool ShowBlockedLeftovers { get; set; } = false;

    /// <summary>Ask whether to keep waiting once a silent uninstall has run this long.</summary>
    [JsonPropertyName("quietStallMinutes")]
    public int QuietStallMinutes { get; set; } = 10;

    /// <summary>
    /// Allow advanced scan mode in the UI.
    /// Advanced scan is more thorough but carries higher false-positive risk.
    /// </summary>
    [JsonPropertyName("allowAdvancedScan")]
    public bool AllowAdvancedScan { get; set; } = true;

    /// <summary>
    /// SAFETY: When true, no real uninstall commands or deletions are executed.
    /// All operations are logged as "Would execute".
    /// </summary>
    [JsonPropertyName("dryRunMode")]
    public bool DryRunMode { get; set; } = false;

    /// <summary>Registry paths explicitly excluded from leftover suggestions.</summary>
    [JsonPropertyName("excludedRegistryPaths")]
    public List<string> ExcludedRegistryPaths { get; set; } = new();

    /// <summary>File system paths explicitly excluded from leftover suggestions.</summary>
    [JsonPropertyName("excludedFilePaths")]
    public List<string> ExcludedFilePaths { get; set; } = new();

    public UninstallerSettings Clone() => new()
    {
        DefaultScanMode = DefaultScanMode,
        PreferQuietUninstall = PreferQuietUninstall,
        CreateSystemRestorePoint = CreateSystemRestorePoint,
        BackupRegistryChanges = BackupRegistryChanges,
        MoveToRecycleBin = MoveToRecycleBin,
        AutoScanLeftovers = AutoScanLeftovers,
        ShowProtectedApplications = ShowProtectedApplications,
        ShowOrphanedApplications = ShowOrphanedApplications,
        ShowSystemComponents = ShowSystemComponents,
        ShowUpdates = ShowUpdates,
        ShowBlockedLeftovers = ShowBlockedLeftovers,
        QuietStallMinutes = QuietStallMinutes,
        AllowAdvancedScan = AllowAdvancedScan,
        DryRunMode = DryRunMode,
        // A settings file with "excludedRegistryPaths": null must not leave callers with a null list
        ExcludedRegistryPaths = new List<string>(ExcludedRegistryPaths ?? new()),
        ExcludedFilePaths = new List<string>(ExcludedFilePaths ?? new()),
    };
}
