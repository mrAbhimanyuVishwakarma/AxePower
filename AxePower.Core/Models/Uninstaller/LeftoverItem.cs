namespace AxePower.Core.Models.Uninstaller;

/// <summary>
/// Type of leftover item found after an application is removed. Saved history stores these as
/// numbers, so new values are only ever appended.
/// </summary>
public enum LeftoverItemType
{
    RegistryKey,
    RegistryValue,
    File,
    Folder,

    /// <summary>A Windows service whose binary is in the removed app's folder. Informational only, never deletable.</summary>
    Service,

    /// <summary>A Run / RunOnce registry value.</summary>
    StartupEntry,

    /// <summary>A .lnk file (Start Menu, Desktop or Startup folder).</summary>
    Shortcut,

    /// <summary>A Task Scheduler task; <see cref="LeftoverItem.Path"/> is the task path.</summary>
    ScheduledTask,

    /// <summary>A firewall rule for a program in the removed app's folder. Informational only.</summary>
    FirewallRule,
}

/// <summary>
/// A single leftover item found after an application is removed.
/// Every item carries the evidence that ties it to the removed application.
/// </summary>
public sealed class LeftoverItem
{
    public string ItemId { get; set; } = Guid.NewGuid().ToString();
    public string ApplicationId { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = string.Empty;
    public LeftoverItemType ItemType { get; set; }

    /// <summary>
    /// File or folder path, registry key path (64-bit view, WOW6432Node spelled out) or task path.
    /// For registry values it is the containing key.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Registry value name (for RegistryValue and StartupEntry items).</summary>
    public string? ValueName { get; set; }

    /// <summary>Confidence score 0–100, derived from <see cref="Evidence"/>.</summary>
    public int ConfidenceScore { get; set; }

    /// <summary>Human-readable confidence label.</summary>
    public string ConfidenceLabel => ConfidenceScore switch
    {
        >= 95 => "Very High",
        >= 80 => "High",
        >= 60 => "Medium",
        >= 40 => "Low",
        _      => "Very Low",
    };

    /// <summary>Explanation of why this item is considered a leftover.</summary>
    public string ConfidenceReason { get; set; } = string.Empty;

    /// <summary>The signed evidence records behind the score.</summary>
    public List<ConfidenceRecord> Evidence { get; set; } = new();

    /// <summary>File/folder size in bytes (0 for registry items).</summary>
    public long SizeBytes { get; set; }

    public string DisplaySize
    {
        get
        {
            if (SizeBytes <= 0) return string.Empty;
            double mb = SizeBytes / (1024.0 * 1024.0);
            if (mb >= 1024) return $"{mb / 1024:F2} GB";
            if (mb >= 1)    return $"{mb:F1} MB";
            double kb = SizeBytes / 1024.0;
            if (kb >= 1)    return $"{kb:F0} KB";
            return $"{SizeBytes} B";
        }
    }

    /// <summary>Whether the item is selected (pre-ticked) for deletion.</summary>
    public bool IsSelected { get; set; }

    /// <summary>Protected — must not be deleted.</summary>
    public bool IsProtected { get; set; }

    /// <summary>Why the item can't be selected (protected path, used by another app…); null when it can.</summary>
    public string? BlockedReason { get; set; }

    /// <summary>Deleting it needs administrator rights (HKLM, Program Files, ProgramData, machine tasks).</summary>
    public bool RequiresElevation { get; set; }

    /// <summary>Appears to be shared with other applications.</summary>
    public bool IsSharedComponent { get; set; }

    /// <summary>Which scan mode found this item.</summary>
    public string ScanMode { get; set; } = "Moderate";

    /// <summary>File is locked by a running process.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Shown for information only (services, firewall rules); never deletable.</summary>
    public bool IsInformational => ItemType is LeftoverItemType.Service or LeftoverItemType.FirewallRule;

    public bool HasEvidence(string recordName) =>
        Evidence.Any(e => string.Equals(e.Name, recordName, StringComparison.Ordinal));

    /// <summary>Short display label for the UI (filename or last registry path segment).</summary>
    public string DisplayLabel
    {
        get
        {
            if (!string.IsNullOrEmpty(ValueName)) return ValueName;
            if (string.IsNullOrEmpty(Path)) return string.Empty;
            return System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } name
                ? name
                : Path;
        }
    }

    /// <summary>Path plus value name, as shown in the review list.</summary>
    public string DisplayPath => string.IsNullOrEmpty(ValueName) ? Path : $"{Path}  →  {ValueName}";
}
