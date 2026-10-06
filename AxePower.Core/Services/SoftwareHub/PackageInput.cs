using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AxePower.Core.Services.SoftwareHub;

/// <summary>
/// Checks for every value that ends up in a command, a download or a file path: package
/// identifiers, URLs and file names are untrusted even when they come from the built-in catalog.
/// </summary>
public static class PackageInput
{
    // Letters, digits and . + _ - only, starting with a letter or digit: covers every WinGet and
    // Store identifier in use, and rules out a leading dash (read as an option), spaces, quotes,
    // separators and wildcards.
    private static readonly Regex WinGetIdPattern = new("^[A-Za-z0-9][A-Za-z0-9.+_-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex StoreIdPattern = new("^9[A-Z0-9]{11}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex CatalogIdPattern = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool IsValidWinGetId(string? id) => id != null && WinGetIdPattern.IsMatch(id) && !id.Contains("..", StringComparison.Ordinal);

    public static bool IsValidCatalogId(string? id) => id != null && CatalogIdPattern.IsMatch(id);

    /// <summary>Microsoft Store product IDs (9N0866FS04B8) live only in the msstore source.</summary>
    public static string WinGetSource(string id) => StoreIdPattern.IsMatch(id) ? "msstore" : "winget";

    /// <summary>An absolute https (or http) address with a host and no user info.</summary>
    public static bool TryWebUrl(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '"')) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("https" or "http") || string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>An https address on one of <paramref name="allowedHosts"/> (or a subdomain of one).</summary>
    public static bool TryDownloadUrl(string? value, IReadOnlyCollection<string> allowedHosts, out Uri uri)
    {
        if (!TryWebUrl(value, out uri) || uri.Scheme != "https") return false;
        var host = uri.IdnHost;
        var allowed = allowedHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
        if (!allowed) uri = null!;
        return allowed;
    }

    /// <summary>
    /// The last path segment of a download address as a safe local file name with the required
    /// extension, or null. Rejects separators (also encoded ones), invalid characters, device names,
    /// leading dots and over-long names, so a download can only land in the folder chosen for it.
    /// </summary>
    public static string? SafeDownloadFileName(Uri uri, string requiredExtension)
    {
        string segment;
        try { segment = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').LastOrDefault() ?? string.Empty); }
        catch (Exception) { /* Fails closed: a name that cannot be decoded is refused. */ return null; }

        if (segment.Length is 0 or > 120) return null;
        if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Contains('/') || segment.Contains('\\')) return null;
        if (segment.StartsWith('.') || segment.EndsWith('.') || segment.EndsWith(' ') || segment.StartsWith(' ')) return null;
        if (!string.Equals(Path.GetExtension(segment), requiredExtension, StringComparison.OrdinalIgnoreCase)) return null;
        var stem = Path.GetFileNameWithoutExtension(segment).Split('.')[0];
        if (ReservedNames.Contains(stem)) return null;
        return segment;
    }

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/> after normalisation.</summary>
    public static bool IsStrictlyInside(string path, string root)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && full.Length > rootFull.Length;
        }
        catch (Exception)
        {
            // Fails closed: a path that cannot be normalised is not inside the folder.
            return false;
        }
    }
}
