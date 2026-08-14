namespace AxePower.Core.Models.Uninstaller;

/// <summary>Backed-up registry key/value before deletion.</summary>
public sealed class RegistryBackupItem
{
    public string SessionId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>Registry hive abbreviation: HKCU, HKLM, HKCR, HKU, HKCC.</summary>
    public string Hive { get; set; } = string.Empty;

    /// <summary>Registry key path below the hive root (without the hive prefix).</summary>
    public string KeyPath { get; set; } = string.Empty;

    /// <summary>Value name, or null if backing up the entire key structure.</summary>
    public string? ValueName { get; set; }

    /// <summary>JSON-serialized value data.</summary>
    public string? ValueData { get; set; }

    /// <summary>Registry value kind as string (String, DWord, QWord, Binary, etc.).</summary>
    public string ValueKind { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.Now;
}
