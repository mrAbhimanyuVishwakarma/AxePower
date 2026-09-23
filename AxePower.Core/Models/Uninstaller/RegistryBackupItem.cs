namespace AxePower.Core.Models.Uninstaller;

/// <summary>
/// One registry backup taken before a delete. A key backup is a .reg file exported with
/// <c>reg export</c>; a value backup keeps the value's kind and its data as typed JSON.
/// </summary>
public sealed class RegistryBackupItem
{
    public string SessionId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>Registry hive abbreviation: HKCU or HKLM.</summary>
    public string Hive { get; set; } = string.Empty;

    /// <summary>Registry key path below the hive root (64-bit view, WOW6432Node spelled out).</summary>
    public string KeyPath { get; set; } = string.Empty;

    /// <summary>Value name, or null for a whole-key backup.</summary>
    public string? ValueName { get; set; }

    /// <summary>
    /// JSON of the value data, typed by <see cref="ValueKind"/>: a string (String/ExpandString,
    /// unexpanded), a number (DWord/QWord), base64 (Binary/None) or a string array (MultiString).
    /// </summary>
    public string? ValueData { get; set; }

    /// <summary>Registry value kind as string (String, DWord, QWord, Binary, etc.).</summary>
    public string ValueKind { get; set; } = string.Empty;

    /// <summary>For key backups: the .reg file name inside the session folder.</summary>
    public string BackupFile { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string FullKeyPath => string.IsNullOrEmpty(Hive) ? KeyPath : $"{Hive}\\{KeyPath}";
}
