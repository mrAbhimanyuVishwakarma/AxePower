namespace AxePower.Core.Models.Uninstaller;

/// <summary>
/// Type of leftover item found after an application is removed.
/// </summary>
public enum LeftoverItemType
{
    RegistryKey,
    RegistryValue,
    File,
    Folder,
    Service,
    StartupEntry,
}

/// <summary>
/// A single leftover item found after an application is removed.
/// Every item has a confidence score and reason explaining WHY it is associated with the removed application.
/// </summary>
public sealed class LeftoverItem
{
    public string ItemId { get; set; } = Guid.NewGuid().ToString();
    public string ApplicationId { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = string.Empty;
    public LeftoverItemType ItemType { get; set; }
    public string Path { get; set; } = string.Empty;

    /// <summary>Registry value name (for RegistryValue type).</summary>
    public string? ValueName { get; set; }

    /// <summary>Confidence score 0–100.</summary>
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

    /// <summary>Whether the item is selected for deletion.</summary>
    public bool IsSelected { get; set; }

    /// <summary>Protected — must not be deleted.</summary>
    public bool IsProtected { get; set; }

    /// <summary>Appears to be shared with other applications.</summary>
    public bool IsSharedComponent { get; set; }

    /// <summary>Which scan mode found this item.</summary>
    public string ScanMode { get; set; } = "Moderate";

    /// <summary>File is locked by a running process.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Short display label for the UI (filename or last registry path segment).</summary>
    public string DisplayLabel
    {
        get
        {
            if (string.IsNullOrEmpty(Path)) return string.Empty;
            return System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } name
                ? name
                : Path;
        }
    }
}
