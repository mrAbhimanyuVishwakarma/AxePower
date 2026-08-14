using AxePower.Core.Models.Uninstaller;
using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Scans for application-specific leftover items (registry, files, folders)
/// after an application has been uninstalled.
///
/// IMPORTANT: This is an APPLICATION-SPECIFIC cleaner.
/// It answers: "Does this item probably belong to the app we just removed?"
/// It does NOT perform generic registry cleaning.
/// </summary>
public sealed class LeftoverScannerService : ILeftoverScannerService
{
    private readonly ILeftoverConfidenceService _confidenceService;
    private readonly IProtectionService _protectionService;

    public LeftoverScannerService(
        ILeftoverConfidenceService confidenceService,
        IProtectionService protectionService)
    {
        _confidenceService = confidenceService;
        _protectionService = protectionService;
    }

    public async Task<IReadOnlyList<LeftoverItem>> ScanAsync(
        InstalledApplication application,
        ScanMode scanMode,
        UninstallerSettings settings,
        CancellationToken cancellationToken = default)
    {
        var results = new List<LeftoverItem>();

        await Task.Run(() =>
        {
            // Registry scan
            ScanRegistry(application, scanMode, results, cancellationToken);

            // File/folder scan
            ScanFilesystem(application, scanMode, results, cancellationToken);

            // Startup entries (all modes)
            ScanStartupEntries(application, results, cancellationToken);

            if (scanMode == ScanMode.Advanced)
                ScanAdvancedLocations(application, results, cancellationToken);

        }, cancellationToken);

        // Score each item
        foreach (var item in results)
        {
            var (score, reason, isProtected, isShared) =
                _confidenceService.Score(item, application);

            item.ConfidenceScore = score;
            item.ConfidenceReason = reason;
            item.IsProtected = isProtected || _protectionService.IsPathProtected(item.Path);
            item.IsSharedComponent = isShared;
            item.ScanMode = scanMode.ToString();

            // Auto-select items with High+ confidence that are not protected/shared
            item.IsSelected = score >= 80 && !item.IsProtected && !item.IsSharedComponent;
        }

        // Apply exclusion filter
        results = results
            .Where(item =>
                !settings.ExcludedRegistryPaths.Any(ex =>
                    item.Path.StartsWith(ex, StringComparison.OrdinalIgnoreCase)) &&
                !settings.ExcludedFilePaths.Any(ex =>
                    item.Path.StartsWith(ex, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Remove duplicates
        results = results
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(i => i.ConfidenceScore).First())
            .ToList();

        return results.OrderByDescending(i => i.ConfidenceScore).ToList().AsReadOnly();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Registry Scanning
    // ─────────────────────────────────────────────────────────────────────────

    private void ScanRegistry(
        InstalledApplication app,
        ScanMode mode,
        List<LeftoverItem> results,
        CancellationToken ct)
    {
        // ── Always: exact product code / package identity ────────────────────
        if (!string.IsNullOrEmpty(app.ProductCode))
        {
            ScanRegistryForKey(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData",
                app.ProductCode, LeftoverItemType.RegistryKey, results, app, ct);
        }

        // ── Always: exact app name key under HKCU/HKLM Software ───────────────
        if (!string.IsNullOrEmpty(app.Publisher))
        {
            TryAddRegistryKey($@"HKCU\SOFTWARE\{app.Publisher}\{app.DisplayName}", results, app, ct);
            TryAddRegistryKey($@"HKLM\SOFTWARE\{app.Publisher}\{app.DisplayName}", results, app, ct);
        }
        TryAddRegistryKey($@"HKCU\SOFTWARE\{app.DisplayName}", results, app, ct);
        TryAddRegistryKey($@"HKLM\SOFTWARE\{app.DisplayName}", results, app, ct);

        if (mode == ScanMode.Safe) return;

        // ── Moderate+: WOW6432 equivalents ──────────────────────────────────
        if (!string.IsNullOrEmpty(app.Publisher))
        {
            TryAddRegistryKey($@"HKLM\SOFTWARE\WOW6432Node\{app.Publisher}\{app.DisplayName}", results, app, ct);
        }
        TryAddRegistryKey($@"HKLM\SOFTWARE\WOW6432Node\{app.DisplayName}", results, app, ct);


        // ── Moderate+: Uninstall key leftovers ──────────────────────────────
        if (!string.IsNullOrEmpty(app.ProductCode))
        {
            TryAddRegistryKey(
                $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{app.ProductCode}",
                results, app, ct);
            TryAddRegistryKey(
                $@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{app.ProductCode}",
                results, app, ct);
        }
    }

    private static void TryAddRegistryKey(
        string fullPath,
        List<LeftoverItem> results,
        InstalledApplication app,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (RegistryKeyExists(fullPath))
        {
            results.Add(new LeftoverItem
            {
                ApplicationId = app.Id,
                ApplicationName = app.DisplayName,
                ItemType = LeftoverItemType.RegistryKey,
                Path = fullPath,
            });
        }
    }

    private static void ScanRegistryForKey(
        string basePath, string searchTerm, LeftoverItemType type,
        List<LeftoverItem> results, InstalledApplication app, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var (hive, subPath) = SplitHivePath(basePath);
            using var baseKey = OpenBaseKey(hive);
            if (baseKey is null) return;
            using var subKey = baseKey.OpenSubKey(subPath, false);
            if (subKey is null) return;

            foreach (var name in subKey.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                if (name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new LeftoverItem
                    {
                        ApplicationId = app.Id,
                        ApplicationName = app.DisplayName,
                        ItemType = type,
                        Path = $@"{basePath}\{name}",
                    });
                }
            }
        }
        catch { }
    }

    private static bool RegistryKeyExists(string fullPath)
    {
        try
        {
            var (hive, subPath) = SplitHivePath(fullPath);
            using var baseKey = OpenBaseKey(hive);
            if (baseKey is null) return false;
            using var sub = baseKey.OpenSubKey(subPath, false);
            return sub is not null;
        }
        catch { return false; }
    }

    private static (RegistryHive hive, string subPath) SplitHivePath(string fullPath)
    {
        if (fullPath.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.CurrentUser, fullPath[5..]);
        if (fullPath.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.LocalMachine, fullPath[5..]);
        if (fullPath.StartsWith("HKCR\\", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.ClassesRoot, fullPath[5..]);
        return (RegistryHive.LocalMachine, fullPath);
    }

    private static RegistryKey? OpenBaseKey(RegistryHive hive) =>
        hive switch
        {
            RegistryHive.CurrentUser  => Registry.CurrentUser,
            RegistryHive.LocalMachine => Registry.LocalMachine,
            RegistryHive.ClassesRoot  => Registry.ClassesRoot,
            _ => null,
        };

    // ─────────────────────────────────────────────────────────────────────────
    // Filesystem Scanning
    // ─────────────────────────────────────────────────────────────────────────

    private static void ScanFilesystem(
        InstalledApplication app,
        ScanMode mode,
        List<LeftoverItem> results,
        CancellationToken ct)
    {
        // ── Always: InstallLocation ─────────────────────────────────────────
        if (!string.IsNullOrEmpty(app.InstallLocation) && Directory.Exists(app.InstallLocation))
        {
            AddFolderItem(app.InstallLocation, results, app);
        }

        // ── Always: AppData and LocalAppData with exact app name ────────────
        var appData   = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        TryAddDirectory(Path.Combine(appData,   app.DisplayName), results, app, ct);
        TryAddDirectory(Path.Combine(localData, app.DisplayName), results, app, ct);

        if (mode == ScanMode.Safe) return;

        // ── Moderate+: Publisher subfolders ─────────────────────────────────
        if (!string.IsNullOrEmpty(app.Publisher))
        {
            TryAddDirectory(Path.Combine(appData,   app.Publisher, app.DisplayName), results, app, ct);
            TryAddDirectory(Path.Combine(localData, app.Publisher, app.DisplayName), results, app, ct);

            var progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            TryAddDirectory(Path.Combine(progData, app.Publisher, app.DisplayName), results, app, ct);
            TryAddDirectory(Path.Combine(progData, app.DisplayName), results, app, ct);
        }

        // ── Moderate+: Program Files leftovers ──────────────────────────────
        var pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        TryAddDirectory(Path.Combine(pf,    app.DisplayName), results, app, ct);
        TryAddDirectory(Path.Combine(pfX86, app.DisplayName), results, app, ct);
        if (!string.IsNullOrEmpty(app.Publisher))
        {
            TryAddDirectory(Path.Combine(pf,    app.Publisher, app.DisplayName), results, app, ct);
            TryAddDirectory(Path.Combine(pfX86, app.Publisher, app.DisplayName), results, app, ct);
        }
    }

    private static void TryAddDirectory(
        string path,
        List<LeftoverItem> results,
        InstalledApplication app,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            AddFolderItem(path, results, app);
    }

    private static void AddFolderItem(string path, List<LeftoverItem> results, InstalledApplication app)
    {
        long size = 0;
        try
        {
            size = new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => { try { return f.Length; } catch { return 0L; } });
        }
        catch { }

        results.Add(new LeftoverItem
        {
            ApplicationId = app.Id,
            ApplicationName = app.DisplayName,
            ItemType = LeftoverItemType.Folder,
            Path = path,
            SizeBytes = size,
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Startup Entry Scanning
    // ─────────────────────────────────────────────────────────────────────────

    private static void ScanStartupEntries(
        InstalledApplication app,
        List<LeftoverItem> results,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var startupPaths = new[]
        {
            @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        };

        var appNameLower = app.DisplayName.ToLowerInvariant();
        var installLower = app.InstallLocation.ToLowerInvariant();

        foreach (var regPath in startupPaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (hive, subPath) = SplitHivePath(regPath);
                using var baseKey = OpenBaseKey(hive);
                if (baseKey is null) continue;
                using var key = baseKey.OpenSubKey(subPath, false);
                if (key is null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    ct.ThrowIfCancellationRequested();
                    var valueData = key.GetValue(valueName)?.ToString() ?? string.Empty;
                    var dataLower = valueData.ToLowerInvariant();

                    // Only add if the startup command references the app's install path
                    bool matchesApp = (!string.IsNullOrEmpty(installLower) && dataLower.Contains(installLower)) ||
                                      valueName.ToLowerInvariant().Contains(appNameLower);

                    if (matchesApp)
                    {
                        results.Add(new LeftoverItem
                        {
                            ApplicationId = app.Id,
                            ApplicationName = app.DisplayName,
                            ItemType = LeftoverItemType.StartupEntry,
                            Path = $@"{regPath}\{valueName}",
                            ValueName = valueName,
                        });
                    }
                }
            }
            catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Advanced scan locations
    // ─────────────────────────────────────────────────────────────────────────

    private void ScanAdvancedLocations(
        InstalledApplication app,
        List<LeftoverItem> results,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Scan HKCU\SOFTWARE\Classes for application-specific COM registrations
        if (!string.IsNullOrEmpty(app.DisplayName))
        {
            TryAddRegistryKey($@"HKCU\SOFTWARE\Classes\{app.DisplayName}", results, app, ct);
        }

        // Scheduled tasks that reference the install path
        // (informational — not auto-selected, left for manual review)
        // Service detection is handled as informational in the UI

        // Additional AppData\Local\Low
        var localLow = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow");
        TryAddDirectory(Path.Combine(localLow, app.DisplayName), results, app, ct);
        if (!string.IsNullOrEmpty(app.Publisher))
            TryAddDirectory(Path.Combine(localLow, app.Publisher), results, app, ct);
    }
}
