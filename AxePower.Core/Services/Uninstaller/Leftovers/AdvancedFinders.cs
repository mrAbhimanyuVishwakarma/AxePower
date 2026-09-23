// Portions adapted from Bulk Crap Uninstaller (https://github.com/BCUninstaller/Bulk-Crap-Uninstaller)
// Copyright (c) 2017 Marcin Szeniak. Licensed under the Apache License, Version 2.0. Modified for AxePower.

using System.Runtime.InteropServices;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller.Leftovers;

/// <summary><c>App Paths\&lt;exe&gt;</c> keys whose program lives in the app's folders.</summary>
public sealed class AppPathsFinder : ILeftoverFinder
{
    private static readonly string[] Roots =
    [
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths",
    ];

    public ScanMode MinimumMode => ScanMode.Advanced;

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        if (context.AppFolders.Count == 0) yield break;
        foreach (var root in Roots)
        {
            foreach (var name in SoftwareKeyFinder.SubKeyNames(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = RegistryPath.Combine(root, name);
                string target;
                try
                {
                    using var key = RegistryPath.OpenKey(path);
                    if (key is null) continue;
                    target = RegistryPath.GetString(key, string.Empty).Trim().Trim('"');
                    if (target.Length == 0) target = RegistryPath.GetString(key, "Path").Trim().Trim('"');
                }
                catch
                {
                    continue;
                }

                if (!context.PointsIntoAppFolders(target)) continue;
                var item = context.NewItem(LeftoverItemType.RegistryKey, path);
                context.AddExplicitEvidence(item, target);
                yield return item;
            }
        }
    }
}

/// <summary>Compatibility flags and Program Compatibility Assistant records for programs in the app's folders.</summary>
public sealed class AppCompatFinder : ILeftoverFinder
{
    private static readonly string[] Keys =
    [
        @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
        @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
    ];

    public ScanMode MinimumMode => ScanMode.Advanced;

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        if (context.AppFolders.Count == 0) yield break;
        foreach (var keyPath in Keys)
        {
            foreach (var valueName in ValueNames(keyPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!context.PointsIntoAppFolders(valueName)) continue;
                var item = context.NewItem(LeftoverItemType.RegistryValue, keyPath, valueName);
                context.AddExplicitEvidence(item, valueName);
                yield return item;
            }
        }
    }

    internal static string[] ValueNames(string keyPath)
    {
        try
        {
            using var key = RegistryPath.OpenKey(keyPath);
            return key?.GetValueNames() ?? [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>Explorer's MUI cache entries ("C:\App\app.exe.FriendlyAppName") for programs in the app's folders.</summary>
public sealed class MuiCacheFinder : ILeftoverFinder
{
    private const string MuiCacheKey = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";

    public ScanMode MinimumMode => ScanMode.Advanced;

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        if (context.AppFolders.Count == 0) yield break;
        foreach (var valueName in AppCompatFinder.ValueNames(MuiCacheKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exeEnd = valueName.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            var program = exeEnd > 0 ? valueName[..(exeEnd + 4)] : valueName;
            if (!context.PointsIntoAppFolders(program)) continue;

            var item = context.NewItem(LeftoverItemType.RegistryValue, MuiCacheKey, valueName);
            context.AddExplicitEvidence(item, program);
            yield return item;
        }
    }
}

/// <summary>COM classes (CLSID\{guid}) whose every server lives in the app's folders.</summary>
public sealed class ComFinder : ILeftoverFinder
{
    private static readonly string[] ServerKeys = ["InprocServer32", "LocalServer32", "InprocHandler32"];
    private readonly Func<bool> _isElevated;

    public ComFinder(Func<bool> isElevated)
    {
        _isElevated = isElevated;
    }

    public ScanMode MinimumMode => ScanMode.Advanced;

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        if (context.AppFolders.Count == 0) yield break;

        var roots = new List<string> { @"HKCU\Software\Classes\CLSID" };
        if (_isElevated())
        {
            roots.Add(@"HKLM\SOFTWARE\Classes\CLSID");
            roots.Add(@"HKLM\SOFTWARE\WOW6432Node\Classes\CLSID");
        }

        foreach (var root in roots)
        {
            foreach (var clsid in SoftwareKeyFinder.SubKeyNames(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = RegistryPath.Combine(root, clsid);
                var servers = new List<string>();
                try
                {
                    foreach (var serverKey in ServerKeys)
                    {
                        using var key = RegistryPath.OpenKey(RegistryPath.Combine(path, serverKey));
                        if (key is null) continue;
                        var server = RegistryPath.GetString(key, string.Empty).Trim();
                        if (server.Length == 0) continue;
                        servers.Add(server.StartsWith('"') ? server.Trim('"').Split('"')[0] : server.Split(" /", 2)[0].Split(" -", 2)[0]);
                    }
                }
                catch
                {
                    continue;
                }

                if (servers.Count == 0 || !servers.All(context.PointsIntoAppFolders)) continue;
                var item = context.NewItem(LeftoverItemType.RegistryKey, path);
                context.AddExplicitEvidence(item, servers[0]);
                yield return item;
            }
        }
    }
}

/// <summary>Scheduled tasks whose actions start a program from the app's folders.</summary>
public sealed class ScheduledTaskFinder : ILeftoverFinder
{
    public ScanMode MinimumMode => ScanMode.Advanced;

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        if (context.AppFolders.Count == 0) yield break;
        foreach (var task in ScheduledTaskService.EnumerateTasks(cancellationToken))
        {
            var target = task.ExecPaths.FirstOrDefault(context.PointsIntoAppFolders);
            if (target is null) continue;

            var item = context.NewItem(LeftoverItemType.ScheduledTask, task.Path);
            item.RequiresElevation = task.RequiresElevation;
            context.AddExplicitEvidence(item, target);
            yield return item;
        }
    }
}

/// <summary>Services whose binary is in the app's folders. Shown for information only.</summary>
public sealed class ServiceInfoFinder : ILeftoverFinder
{
    private const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services";
    private readonly IFileSystemProbe _fs;

    public ServiceInfoFinder(IFileSystemProbe fs)
    {
        _fs = fs;
    }

    public ScanMode MinimumMode => ScanMode.Advanced;

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        if (context.AppFolders.Count == 0) yield break;
        foreach (var name in SoftwareKeyFinder.SubKeyNames(ServicesKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string imagePath;
            try
            {
                using var key = RegistryPath.OpenKey(RegistryPath.Combine(ServicesKey, name));
                if (key is null) continue;
                imagePath = RegistryPath.GetString(key, "ImagePath");
            }
            catch
            {
                continue;
            }

            if (imagePath.Length == 0) continue;
            UninstallCommandParser.TryParse(imagePath, _fs, out var command);
            if (!context.PointsIntoAppFolders(command?.FileName)) continue;

            var item = context.NewItem(LeftoverItemType.Service, RegistryPath.Combine(ServicesKey, name));
            item.Evidence.Add(ConfidenceRecords.ExplicitConnection);
            item.ConfidenceReason = $"Service '{name}' runs {command!.FileName}";
            yield return item;
        }
    }
}

/// <summary>Windows Firewall rules for programs in the app's folders. Shown for information only.</summary>
public sealed class FirewallRuleFinder : ILeftoverFinder
{
    public ScanMode MinimumMode => ScanMode.Advanced;

    public IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken)
    {
        if (context.AppFolders.Count == 0) return [];
        var results = new List<LeftoverItem>();
        object? policy = null;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return results;
            policy = Activator.CreateInstance(type);
            dynamic rules = ((dynamic)policy!).Rules;
            foreach (dynamic rule in rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? program = rule.ApplicationName;
                if (string.IsNullOrEmpty(program)) continue;
                program = Environment.ExpandEnvironmentVariables(program);
                if (!context.PointsIntoAppFolders(program)) continue;

                var item = context.NewItem(LeftoverItemType.FirewallRule, program, (string)rule.Name);
                item.Evidence.Add(ConfidenceRecords.ExplicitConnection);
                results.Add(item);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
        }
        return results;
    }
}
