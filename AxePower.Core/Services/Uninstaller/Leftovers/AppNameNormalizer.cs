// Portions adapted from Bulk Crap Uninstaller (https://github.com/BCUninstaller/Bulk-Crap-Uninstaller)
// Copyright (c) 2017 Marcin Szeniak. Licensed under the Apache License, Version 2.0. Modified for AxePower.

using System.Text.RegularExpressions;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller.Leftovers;

/// <summary>
/// Turns display names and publishers into the names leftovers are actually filed under
/// ("Mozilla Firefox (x64 en-US)" → "Mozilla Firefox", "Google LLC" → "Google"), and matches
/// folder or key names against them with BCU's fuzzy rules.
/// </summary>
public static partial class AppNameNormalizer
{
    private static readonly char[] VersionTail = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ',', '-', '_', ' '];

    private static readonly HashSet<string> CompanySuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "corp", "corporation", "limited", "inc", "incorporated", "llc", "ltd", "gmbh", "co", "s.a", "sa",
        "ag", "plc", "pty", "bv", "b.v", "srl", "s.r.l", "oy", "ab", "kk", "company",
    };

    /// <summary>Folder names that say nothing about which app they belong to.</summary>
    public static readonly HashSet<string> GenericFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "app", "apps", "application", "applications", "program", "programs", "data", "common", "shared",
        "temp", "tmp", "cache", "caches", "logs", "log", "config", "settings", "files", "x64", "x86", "win32", "win64",
        "current", "latest", "release", "main", "tools", "installer", "install", "setup", "uninstall", "uninst",
        "resources", "lib", "libs", "plugins", "update", "updater", "default", "user", "users", "local", "roaming",
    };

    /// <summary>BCU's DisplayNameTrimmed: drop a trailing "(…)", version numbers, bitness and locale tags.</summary>
    public static string TrimDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var original = name.Trim();
        var s = original;

        while (true)
        {
            var before = s;

            if (s.EndsWith(')'))
            {
                var open = s.LastIndexOf('(');
                if (open > 4) s = s[..open].TrimEnd();
            }

            s = s.TrimEnd(VersionTail);
            s = TrailingTokenRegex().Replace(s, string.Empty).TrimEnd();

            if (s == before) break;
        }

        return s.Length > 0 ? s : original;
    }

    /// <summary>BCU's PublisherTrimmed: drop (R)/(TM) and company suffixes such as "Inc." or "LLC".</summary>
    public static string TrimPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return string.Empty;
        var s = publisher.Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
                         .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
                         .Replace("®", string.Empty).Replace("™", string.Empty)
                         .Replace("(c)", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("©", string.Empty)
                         .Trim();

        while (true)
        {
            var before = s;
            s = s.TrimEnd('.', ',', ' ', '-');
            var cut = s.LastIndexOfAny([' ', ',']);
            if (cut > 0 && CompanySuffixes.Contains(s[(cut + 1)..].TrimEnd('.')))
                s = s[..cut];
            if (s == before) break;
        }

        return s.Length > 0 ? s : publisher.Trim();
    }

    /// <summary>
    /// The names the app's leftovers may be filed under: the trimmed display name, the install
    /// folder's name, the uninstaller folder's name, the registry key name and the main exe name.
    /// Names of four characters or fewer are dropped (they never match).
    /// </summary>
    public static IReadOnlyList<string> GetCandidateNames(InstalledApplication app)
    {
        var names = new List<string> { TrimDisplayName(app.DisplayName) };

        AddFolderName(app.InstallLocation);
        if (UninstallEntryClassifier.IsAcceptableDerivedFolder(Path.GetDirectoryName(app.UninstallerPath) ?? string.Empty))
            AddFolderName(Path.GetDirectoryName(app.UninstallerPath));

        var key = app.RegistryKeyName;
        if (key.EndsWith("_is1", StringComparison.OrdinalIgnoreCase)) key = key[..^4];
        if (key.Length > 0 && !LooksLikeId(key)) names.Add(TrimDisplayName(key));

        if (app.IconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            names.Add(TrimDisplayName(Path.GetFileNameWithoutExtension(app.IconPath)));

        return names
            .Where(n => n.Length > 4 && !GenericFolderNames.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        void AddFolderName(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            var dir = folder.TrimEnd('\\');
            // "…\Drive File Stream\131.0.2.0" is named by its parent
            for (int i = 0; i < 2 && dir.Length > 3; i++)
            {
                var name = Path.GetFileName(dir);
                if (name.Length > 0 && !GenericFolderNames.Contains(name) && !VersionLikeRegex().IsMatch(name))
                {
                    names.Add(TrimDisplayName(name));
                    return;
                }
                dir = Path.GetDirectoryName(dir) ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// BCU's MatchStringToProductName: −1 no match, 0–1 perfect, 2 when one name contains the
    /// other, or the distance when it is under a third of the shorter name. Names of four
    /// characters or fewer never match.
    /// </summary>
    public static int Match(string productName, string candidate)
    {
        var product = Clean(productName);
        var str = Clean(candidate);
        var lowest = Math.Min(product.Length, str.Length);
        if (lowest <= 4) return -1;

        // Offset 2 = one character of look-ahead; with the canonical algorithm, 1 has none
        var distance = Sift4.SimplestDistance(product, str, 2);
        if (distance <= 1) return distance;
        if (product.Contains(str, StringComparison.Ordinal) || str.Contains(product, StringComparison.Ordinal)) return 2;
        if (distance < lowest / 3.0) return distance;
        return -1;
    }

    /// <summary>
    /// The best (lowest) match of <paramref name="candidate"/> against any of the app's names,
    /// also trying each name without its publisher ("Google Chrome" → "Chrome"). −1 when none match.
    /// </summary>
    public static int MatchAny(IReadOnlyList<string> productNames, string publisherTrimmed, string candidate)
    {
        var best = -1;
        foreach (var name in productNames)
        {
            Consider(Match(name, candidate));
            if (publisherTrimmed.Length > 0 && name.Contains(publisherTrimmed, StringComparison.OrdinalIgnoreCase))
            {
                var withoutPublisher = name.Replace(publisherTrimmed, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                Consider(Match(withoutPublisher, candidate));
            }
        }
        return best;

        void Consider(int result)
        {
            if (result >= 0 && (best < 0 || result < best)) best = result;
        }
    }

    public static bool MatchesPublisher(string publisherTrimmed, string candidate)
    {
        if (publisherTrimmed.Length == 0) return false;
        var a = Clean(publisherTrimmed);
        var b = Clean(candidate);
        if (a.Equals(b, StringComparison.Ordinal)) return true;
        return Math.Min(a.Length, b.Length) > 4 && Sift4.SimplestDistance(a, b, 2) <= 1;
    }

    public static bool LooksLikeId(string name) =>
        IdLikeRegex().IsMatch(name) || name.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase);

    private static string Clean(string s) => s.Replace('_', ' ').ToLowerInvariant().Trim();

    [GeneratedRegex(@"[\s\-_]+(x64|x86|arm64|amd64|64-?bit|32-?bit|64 bit|32 bit|v|version|[a-z]{2}-[a-z]{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingTokenRegex();

    [GeneratedRegex(@"^v?\d+([.\-_]\d+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLikeRegex();

    /// <summary>GUIDs with or without braces, and electron-builder style hex IDs.</summary>
    [GeneratedRegex(@"^\{?[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}?")]
    private static partial Regex IdLikeRegex();
}
