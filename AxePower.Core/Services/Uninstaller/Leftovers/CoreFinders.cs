// Portions adapted from Bulk Crap Uninstaller (https://github.com/BCUninstaller/Bulk-Crap-Uninstaller)
// Copyright (c) 2017 Marcin Szeniak. Licensed under the Apache License, Version 2.0. Modified for AxePower.

using AxePower.Core.Models.Uninstaller;
using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller.Leftovers;

/// <summary>The app's install folder (and uninstaller folder), if they still exist.</summary>
public sealed class InstallLocationFinder : ILeftoverFinder
{
    public ScanMode MinimumMode => ScanMode.Safe;

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        foreach (var folder in context.AppFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder)) continue;

            var item = context.NewItem(LeftoverItemType.Folder, folder);
            item.Evidence.Add(ConfidenceRecords.ExplicitConnection);
            context.AddFolderEvidence(item, cancellationToken);
            yield return item;
        }
    }
}

/// <summary>
/// Folders named after the app, directly in (or one publisher folder below) AppData, LocalLow,
/// ProgramData, Program Files and Common Files (BCU's CommonDriveJunkScanner).
/// </summary>
public sealed class CommonFolderFinder : ILeftoverFinder
{
    private readonly IReadOnlyList<string> _roots;

    public CommonFolderFinder() : this(DefaultRoots()) { }

    public CommonFolderFinder(IReadOnlyList<string> roots)
    {
        _roots = roots;
    }

    public ScanMode MinimumMode => ScanMode.Moderate;

    public static IReadOnlyList<string> DefaultRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            string.IsNullOrEmpty(profile) ? string.Empty : Path.Combine(profile, "AppData", "LocalLow"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
        }
        .Where(r => !string.IsNullOrEmpty(r))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        if (context.CandidateNames.Count == 0) yield break;

        foreach (var root in _roots)
        {
            foreach (var dir in SafeDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                var match = AppNameNormalizer.MatchAny(context.CandidateNames, context.PublisherTrimmed, name);

                if (match >= 0)
                {
                    yield return Create(context, dir, name, match, level: 0, underPublisher: false, cancellationToken);
                    continue;
                }

                if (!AppNameNormalizer.MatchesPublisher(context.PublisherTrimmed, name)) continue;

                foreach (var sub in SafeDirectories(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var subName = Path.GetFileName(sub);
                    var subMatch = AppNameNormalizer.MatchAny(context.CandidateNames, context.PublisherTrimmed, subName);
                    if (subMatch >= 0)
                        yield return Create(context, sub, subName, subMatch, level: 1, underPublisher: true, cancellationToken);
                }
            }
        }
    }

    private static LeftoverItem Create(LeftoverScanContext context, string dir, string name, int match, int level,
        bool underPublisher, CancellationToken cancellationToken)
    {
        var item = context.NewItem(LeftoverItemType.Folder, dir);
        context.AddNameEvidence(item, name, match, level, underPublisher);
        if (level == 0) item.Evidence.Add(ConfidenceRecords.DirectlyInsideKnownFolder);
        if (context.PointsIntoAppFolders(dir)) item.Evidence.Add(ConfidenceRecords.ExplicitConnection);
        context.AddFolderEvidence(item, cancellationToken);
        return item;
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>Software keys named after the app (or under its publisher) in HKCU and HKLM (BCU's SoftwareRegKeyScanner).</summary>
public sealed class SoftwareKeyFinder : ILeftoverFinder
{
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "WOW6432Node", "Clients", "RegisteredApplications", "ODBC", "Windows",
        "AppDataLow", "Khronos",
    };

    private readonly IReadOnlyList<string> _roots;

    public SoftwareKeyFinder() : this([@"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node"]) { }

    public SoftwareKeyFinder(IReadOnlyList<string> roots)
    {
        _roots = roots;
    }

    public ScanMode MinimumMode => ScanMode.Moderate;

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        if (context.CandidateNames.Count == 0) yield break;

        foreach (var root in _roots)
        {
            foreach (var name in SubKeyNames(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SkipNames.Contains(name)) continue;

                var path = RegistryPath.Combine(root, name);
                var match = AppNameNormalizer.MatchAny(context.CandidateNames, context.PublisherTrimmed, name);
                if (match >= 0)
                {
                    yield return Create(context, path, name, match, 0, false);
                    continue;
                }

                if (!AppNameNormalizer.MatchesPublisher(context.PublisherTrimmed, name)) continue;

                foreach (var subName in SubKeyNames(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var subMatch = AppNameNormalizer.MatchAny(context.CandidateNames, context.PublisherTrimmed, subName);
                    if (subMatch >= 0)
                        yield return Create(context, RegistryPath.Combine(path, subName), subName, subMatch, 1, true);
                }
            }
        }
    }

    private static LeftoverItem Create(LeftoverScanContext context, string path, string name, int match, int level, bool underPublisher)
    {
        var item = context.NewItem(LeftoverItemType.RegistryKey, path);
        context.AddNameEvidence(item, name, match, level, underPublisher);
        if (ValuesPointIntoAppFolders(context, path)) item.Evidence.Add(ConfidenceRecords.ExplicitConnection);
        return item;
    }

    /// <summary>True when one of the key's own string values names a path in the app's folders.</summary>
    private static bool ValuesPointIntoAppFolders(LeftoverScanContext context, string path)
    {
        if (context.AppFolders.Count == 0) return false;
        try
        {
            using var key = RegistryPath.OpenKey(path);
            if (key is null) return false;
            foreach (var valueName in key.GetValueNames().Take(200))
            {
                if (key.GetValue(valueName) is not string value) continue;
                var candidate = value.Trim().Trim('"');
                if (context.PointsIntoAppFolders(candidate)) return true;
            }
        }
        catch { }
        return false;
    }

    internal static string[] SubKeyNames(string path)
    {
        try
        {
            using var key = RegistryPath.OpenKey(path);
            return key?.GetSubKeyNames() ?? [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>
/// Uninstall entries tied to the removed app whose uninstaller is gone: the app's own keys that
/// survived, and other entries pointing into its folders (BCU's UninstallerKeySearcher).
/// </summary>
public sealed class UninstallKeyFinder : ILeftoverFinder
{
    private readonly IFileSystemProbe _fs;
    private readonly IMsiProductStateReader _msi;

    public UninstallKeyFinder(IFileSystemProbe fs, IMsiProductStateReader msi)
    {
        _fs = fs;
        _msi = msi;
    }

    public ScanMode MinimumMode => ScanMode.Safe;

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        var ownKeys = context.Application.AllRegistryKeyPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { $@"HKLM\{RegistryPath.UninstallSubKey}", $@"HKLM\{RegistryPath.UninstallSubKeyWow}", $@"HKCU\{RegistryPath.UninstallSubKey}" })
        {
            foreach (var name in SoftwareKeyFinder.SubKeyNames(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = RegistryPath.Combine(root, name);

                InstalledApplication? entry;
                try
                {
                    using var key = RegistryPath.OpenKey(path);
                    if (key is null) continue;
                    entry = UninstallEntryClassifier.Classify(UninstallEntryValues.FromRegistryKey(key), name, path, _fs, _msi);
                }
                catch
                {
                    continue;
                }

                var isOwn = ownKeys.Contains(path);
                var pointsIn = entry is not null &&
                               (context.PointsIntoAppFolders(entry.InstallLocation) && !entry.IsInstallLocationDerived ||
                                context.PointsIntoAppFolders(entry.UninstallerPath) ||
                                context.PointsIntoAppFolders(entry.IconPath));

                if (!isOwn && !pointsIn) continue;
                // A working uninstaller means the entry still belongs to something installed
                if (entry is not null && entry.UninstallerKind != UninstallerKind.BrokenEntry) continue;

                var item = context.NewItem(LeftoverItemType.RegistryKey, path);
                item.Evidence.Add(ConfidenceRecords.IsUninstallerRegistryKey);
                if (pointsIn) item.Evidence.Add(ConfidenceRecords.ExplicitConnection);
                yield return item;
            }
        }
    }
}

/// <summary>Run / RunOnce values and Startup-folder shortcuts that start something from the app's folders.</summary>
public sealed class StartupFinder : ILeftoverFinder
{
    private static readonly string[] RunKeys =
    [
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
    ];

    private readonly IFileSystemProbe _fs;

    public StartupFinder(IFileSystemProbe fs)
    {
        _fs = fs;
    }

    public ScanMode MinimumMode => ScanMode.Safe;

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        foreach (var runKey in RunKeys)
        {
            foreach (var (valueName, data) in ReadValues(runKey))
            {
                cancellationToken.ThrowIfCancellationRequested();
                UninstallCommandParser.TryParse(data, _fs, out var command);
                var target = command?.FileName;

                if (context.PointsIntoAppFolders(target))
                {
                    var item = context.NewItem(LeftoverItemType.StartupEntry, runKey, valueName);
                    context.AddExplicitEvidence(item, target);
                    yield return item;
                }
                else if (context.Mode >= ScanMode.Moderate)
                {
                    var match = AppNameNormalizer.MatchAny(context.CandidateNames, context.PublisherTrimmed, valueName);
                    if (match < 0) continue;
                    var item = context.NewItem(LeftoverItemType.StartupEntry, runKey, valueName);
                    context.AddNameEvidence(item, valueName, match, 0, false);
                    if (target is not null && _fs.FileExists(target)) item.Evidence.Add(ConfidenceRecords.TargetStillExists);
                    yield return item;
                }
            }
        }

        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                 })
        {
            foreach (var item in ShortcutFinder.FindShortcuts(context, folder, recursive: false, cancellationToken))
                yield return item;
        }
    }

    private static IEnumerable<(string Name, string Data)> ReadValues(string keyPath)
    {
        var values = new List<(string, string)>();
        try
        {
            using var key = RegistryPath.OpenKey(keyPath);
            if (key is null) return values;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (key.GetValue(name) is string data && data.Length > 0) values.Add((name, data));
            }
        }
        catch { }
        return values;
    }
}

/// <summary>
/// Start Menu and Desktop shortcuts that point into the app's folders. A Start Menu folder that
/// holds nothing but such shortcuts is offered as one folder.
/// </summary>
public sealed class ShortcutFinder : ILeftoverFinder
{
    public ScanMode MinimumMode => ScanMode.Safe;

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        var programsRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };
        foreach (var root in programsRoots)
            foreach (var item in FindShortcuts(context, root, recursive: true, cancellationToken))
                yield return item;

        foreach (var desktop in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                 })
            foreach (var item in FindShortcuts(context, desktop, recursive: false, cancellationToken))
                yield return item;
    }

    internal static IEnumerable<LeftoverItem> FindShortcuts(LeftoverScanContext context, string root, bool recursive,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root) || context.AppFolders.Count == 0 && context.CandidateNames.Count == 0)
            yield break;

        string[] links;
        try
        {
            links = Directory.GetFiles(root, "*.lnk", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            });
        }
        catch
        {
            yield break;
        }

        var matched = new List<(string Link, string? Target)>();
        foreach (var link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ShellLink.GetTarget(link);
            if (context.PointsIntoAppFolders(target))
                matched.Add((link, target));
        }

        // A Start Menu sub-folder holding only this app's shortcuts goes as one item
        var normalizedRoot = ProtectedPaths.Normalize(root);
        foreach (var group in matched.GroupBy(m => Path.GetDirectoryName(m.Link) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var folder = group.Key;
            var wholeFolder = recursive &&
                              !string.Equals(ProtectedPaths.Normalize(folder), normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                              FolderHoldsOnly(folder, group.Select(g => g.Link));

            if (wholeFolder)
            {
                var item = context.NewItem(LeftoverItemType.Folder, folder);
                context.AddExplicitEvidence(item, group.Select(g => g.Target).FirstOrDefault(t => t is not null && File.Exists(t)));
                context.AddFolderEvidence(item, cancellationToken);
                yield return item;
                continue;
            }

            foreach (var (link, target) in group)
            {
                var item = context.NewItem(LeftoverItemType.Shortcut, link);
                context.AddExplicitEvidence(item, target);
                try { item.SizeBytes = new FileInfo(link).Length; } catch { }
                yield return item;
            }
        }
    }

    private static bool FolderHoldsOnly(string folder, IEnumerable<string> links)
    {
        try
        {
            if (Directory.EnumerateDirectories(folder).Any()) return false;
            var files = Directory.GetFiles(folder)
                .Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return files.SetEquals(links);
        }
        catch
        {
            return false;
        }
    }
}
