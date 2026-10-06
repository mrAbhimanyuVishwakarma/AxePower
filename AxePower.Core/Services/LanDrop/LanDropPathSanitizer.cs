using System;
using System.IO;
using System.Security;
using System.Text;

namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// Hardened filename validation, normalization, and path containment helper for LanDrop uploads.
/// Defends against directory traversal, alternate data streams, Windows reserved DOS devices,
/// and filesystem injection attacks.
/// </summary>
public static class LanDropPathSanitizer
{
    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Detects whether a filename string contains path traversal characters, directory separators,
    /// drive letters, alternate data streams, or URL-encoded traversal indicators.
    /// </summary>
    public static bool IsPathTraversalAttempt(string? rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName)) return false;

        string trimmed = rawFileName.Trim();

        // Direct path traversal markers
        if (trimmed.Contains("..", StringComparison.Ordinal)) return true;
        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains(':')) return true;

        // URL encoded traversal markers (%2e = ., %2f = /, %5c = \, %3a = :)
        if (trimmed.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("%3a", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Relative or absolute root indicators
        if (trimmed.StartsWith("~", StringComparison.Ordinal) ||
            trimmed.StartsWith(".", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Normalizes a raw filename to a safe, valid file name on Windows, stripping invalid characters,
    /// disarming reserved DOS device names, and avoiding filesystem quirks.
    /// </summary>
    public static string NormalizeFileName(string? rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return $"drop_{DateTime.UtcNow:yyyyMMdd_HHmmss}.bin";
        }

        // Strip any directory components if present
        string fileName = Path.GetFileName(rawFileName.Trim());

        // Strip invalid characters and control characters
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(fileName.Length);
        foreach (char c in fileName)
        {
            if (char.IsControl(c) || Array.IndexOf(invalidChars, c) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        string sanitized = sb.ToString().Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return $"drop_{DateTime.UtcNow:yyyyMMdd_HHmmss}.bin";
        }

        // Split into base name and extension
        string baseName = Path.GetFileNameWithoutExtension(sanitized);
        string ext = Path.GetExtension(sanitized);

        // Disarm Windows reserved device names (e.g. CON, PRN, AUX, NUL, COM1..9, LPT1..9)
        foreach (var reserved in ReservedDeviceNames)
        {
            if (string.Equals(baseName, reserved, StringComparison.OrdinalIgnoreCase))
            {
                baseName = "_" + baseName;
                break;
            }
        }

        // Bound base name length to avoid MAX_PATH issues
        const int maxBaseLen = 120;
        if (baseName.Length > maxBaseLen)
        {
            baseName = baseName.Substring(0, maxBaseLen);
        }

        // The extension is bounded too: "a." followed by hundreds of characters is otherwise a
        // name longer than Windows' 255-character limit, and the upload fails at the final move.
        const int maxExtLen = 32;
        if (ext.Length > maxExtLen)
        {
            ext = ext.Substring(0, maxExtLen);
        }

        string result = baseName + ext;
        return string.IsNullOrWhiteSpace(result) ? $"drop_{DateTime.UtcNow:yyyyMMdd_HHmmss}.bin" : result;
    }

    /// <summary>
    /// Verifies that the resolved path for a file strictly resides within the target directory.
    /// Throws <see cref="SecurityException"/> if the path escapes containment.
    /// </summary>
    public static string ValidateContainment(string downloadDirectory, string safeFileName)
    {
        string fullDownloadDir = Path.GetFullPath(downloadDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        string targetFullPath = Path.GetFullPath(Path.Combine(downloadDirectory, safeFileName));

        if (!targetFullPath.StartsWith(fullDownloadDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"Target path '{targetFullPath}' escapes download directory containment.");
        }

        return targetFullPath;
    }

    /// <summary>
    /// Generates a unique, non-colliding file path within <paramref name="downloadDirectory"/>
    /// and verifies strict containment.
    /// </summary>
    public static string GetUniqueContainedPath(string downloadDirectory, string safeFileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(safeFileName);
        string ext = Path.GetExtension(safeFileName);
        string currentName = safeFileName;
        string targetFullPath = ValidateContainment(downloadDirectory, currentName);
        int counter = 1;

        while (File.Exists(targetFullPath))
        {
            currentName = $"{baseName} ({counter}){ext}";
            targetFullPath = ValidateContainment(downloadDirectory, currentName);
            counter++;
        }

        return targetFullPath;
    }
}
