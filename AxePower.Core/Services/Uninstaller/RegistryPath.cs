using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Registry paths as Uninstaller Pro stores them: "HKLM\SOFTWARE\…" or "HKCU\Software\…",
/// always in the 64-bit view with WOW6432Node written out, so nothing depends on process bitness.
/// </summary>
public static class RegistryPath
{
    public const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string UninstallSubKeyWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Splits "HKLM\SOFTWARE\X" (or the long HKEY_… form) into hive and sub-key.</summary>
    public static bool TryParse(string? fullPath, out RegistryHive hive, out string subKey)
    {
        hive = default;
        subKey = string.Empty;
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        var path = fullPath.Trim().TrimEnd('\\');
        var slash = path.IndexOf('\\');
        var root = slash < 0 ? path : path[..slash];
        subKey = slash < 0 ? string.Empty : path[(slash + 1)..].Trim('\\');

        switch (root.ToUpperInvariant())
        {
            case "HKLM":
            case "HKEY_LOCAL_MACHINE":
                hive = RegistryHive.LocalMachine; return true;
            case "HKCU":
            case "HKEY_CURRENT_USER":
                hive = RegistryHive.CurrentUser; return true;
            case "HKCR":
            case "HKEY_CLASSES_ROOT":
                hive = RegistryHive.ClassesRoot; return true;
            case "HKU":
            case "HKEY_USERS":
                hive = RegistryHive.Users; return true;
            case "HKCC":
            case "HKEY_CURRENT_CONFIG":
                hive = RegistryHive.CurrentConfig; return true;
            default:
                return false;
        }
    }

    public static bool IsRegistryPath(string? path) => TryParse(path, out _, out _);

    public static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.ClassesRoot => "HKCR",
        RegistryHive.Users => "HKU",
        RegistryHive.CurrentConfig => "HKCC",
        _ => hive.ToString(),
    };

    public static string Combine(string keyPath, string child) =>
        keyPath.TrimEnd('\\') + "\\" + child.TrimStart('\\');

    /// <summary>Opens a key in the 64-bit view; null when it doesn't exist or can't be read.</summary>
    public static RegistryKey? OpenKey(string fullPath, bool writable = false)
    {
        if (!TryParse(fullPath, out var hive, out var sub)) return null;
        try
        {
            var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            if (sub.Length == 0) return writable ? null : baseKey;
            using (baseKey)
                return baseKey.OpenSubKey(sub, writable);
        }
        catch (Exception caught) when (ExpectedFailures.Registry(caught))
        {
            // Unopenable counts as missing; deletes re-check existence afterwards.
            return null;
        }
    }

    public static bool KeyExists(string fullPath)
    {
        using var key = OpenKey(fullPath);
        return key is not null;
    }

    public static bool ValueExists(string fullPath, string valueName)
    {
        using var key = OpenKey(fullPath);
        return key is not null && key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads a string value (expanded), or empty.</summary>
    public static string GetString(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) switch
            {
                string s => s,
                string[] a => string.Join(' ', a),
                _ => string.Empty,
            };
        }
        catch (Exception caught) when (ExpectedFailures.Registry(caught))
        {
            // Unreadable value: treated as empty.
            return string.Empty;
        }
    }

    /// <summary>Reads a DWORD that some installers store as QWORD or as a string; null when absent.</summary>
    public static int? GetInt(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s.Trim(), out var parsed) => parsed,
                _ => null,
            };
        }
        catch (Exception caught) when (ExpectedFailures.Registry(caught))
        {
            // Unreadable value: treated as absent.
            return null;
        }
    }
}
