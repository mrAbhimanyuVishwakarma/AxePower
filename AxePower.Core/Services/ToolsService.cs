using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace AxePower.Core.Services;

/// <summary>A third-party app on the Tools page. AxePower opens or installs it; it never ships it.</summary>
public sealed record ToolDefinition(
    string Id,
    string Name,
    string Purpose,
    string Group,
    string WinGetId,
    string WebUrl,
    string[] ExeNames,
    string[] KnownPaths,
    string UninstallName);

/// <summary>
/// Finds the Tools page's apps wherever they were installed: the publisher's usual folder, the
/// Windows installed-apps list (any drive or folder the user picked), and WinGet's own folders
/// for portable installs. Installing goes through WinGet from the publisher's official source.
/// </summary>
public class ToolsService
{
    public const string Monitoring = "Monitoring";
    public const string HardwareInfo = "Hardware info";
    public const string Cleanup = "Cleanup";

    /// <summary>The page's group order. The page fills two columns in this order, each group going to the shorter one.</summary>
    public static IReadOnlyList<string> GroupOrder { get; } = new[] { Monitoring, Cleanup, HardwareInfo };

    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static IReadOnlyList<ToolDefinition> Tools { get; } = new[]
    {
        new ToolDefinition("afterburner", "MSI Afterburner", "FPS overlay, GPU clocks and fan curves. Opens with AxePower's monitoring set up.",
            Monitoring, "Guru3D.Afterburner", "https://www.msi.com/Landing/afterburner/graphics-cards",
            new[] { "MSIAfterburner.exe" },
            new[] { Path.Combine(ProgramFilesX86, "MSI Afterburner", "MSIAfterburner.exe"), Path.Combine(ProgramFiles, "MSI Afterburner", "MSIAfterburner.exe") },
            "MSI Afterburner"),
        new ToolDefinition("cpuz", "CPU-Z", "Exact CPU, memory and motherboard details.",
            HardwareInfo, "CPUID.CPU-Z", "https://www.cpuid.com/softwares/cpu-z.html",
            new[] { "cpuz.exe", "cpuz_x64.exe" },
            new[] { Path.Combine(ProgramFiles, "CPUID", "CPU-Z", "cpuz.exe"), Path.Combine(ProgramFilesX86, "CPUID", "CPU-Z", "cpuz.exe") },
            "CPU-Z"),
        new ToolDefinition("gpuz", "GPU-Z", "Graphics card model, driver, clocks and live sensors.",
            HardwareInfo, "TechPowerUp.GPU-Z", "https://www.techpowerup.com/gpuz/",
            new[] { "GPU-Z.exe" },
            new[] { Path.Combine(ProgramFilesX86, "GPU-Z", "GPU-Z.exe"), Path.Combine(ProgramFiles, "GPU-Z", "GPU-Z.exe") },
            "GPU-Z"),
        new ToolDefinition("crystaldisk", "CrystalDiskInfo", "Drive health, temperature and hours used, before a drive fails.",
            HardwareInfo, "CrystalDewWorld.CrystalDiskInfo", "https://crystalmark.info/en/software/crystaldiskinfo/",
            new[] { "DiskInfo64.exe", "DiskInfo32.exe", "DiskInfo.exe" },
            new[] { Path.Combine(ProgramFiles, "CrystalDiskInfo", "DiskInfo64.exe"), Path.Combine(ProgramFilesX86, "CrystalDiskInfo", "DiskInfo64.exe") },
            "CrystalDiskInfo"),
        new ToolDefinition("revo", "Revo Uninstaller", "The classic free uninstaller: removes apps and sweeps the leftovers they leave behind.",
            Cleanup, "RevoUninstaller.RevoUninstaller", "https://www.revouninstaller.com/products/revo-uninstaller-free/",
            new[] { "RevoUnin.exe" },
            new[] { Path.Combine(ProgramFiles, "VS Revo Group", "Revo Uninstaller", "RevoUnin.exe"), Path.Combine(ProgramFilesX86, "VS Revo Group", "Revo Uninstaller", "RevoUnin.exe") },
            "Revo Uninstaller"),
        new ToolDefinition("wiztree", "WizTree", "Shows what is filling your drives in seconds, biggest folders and files first.",
            Cleanup, "AntibodySoftware.WizTree", "https://diskanalyzer.com/",
            new[] { "WizTree64.exe", "WizTree.exe" },
            new[] { Path.Combine(ProgramFiles, "WizTree", "WizTree64.exe"), Path.Combine(ProgramFilesX86, "WizTree", "WizTree.exe") },
            "WizTree"),
    };

    public static ToolDefinition Get(string id) => Tools.First(t => t.Id == id);

    /// <summary>The app's program file, or null when it isn't installed.</summary>
    public virtual string? FindExe(ToolDefinition tool)
    {
        foreach (var path in tool.KnownPaths)
            if (File.Exists(path)) return path;

        try { if (FromInstalledApps(tool) is { } fromList) return fromList; } catch { }
        try { if (FromWinGetFolders(tool) is { } fromWinGet) return fromWinGet; } catch { }
        return null;
    }

    public virtual bool IsRunning(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    public virtual void Launch(string exePath) =>
        Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath)! });

    public virtual void OpenFolder(string exePath) =>
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{exePath}\"", UseShellExecute = true });

    // ── Where apps can be ──────────────────────────────────────────────────

    private static readonly (RegistryKey Hive, string Path)[] UninstallKeys =
    {
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    };

    /// <summary>The Windows installed-apps list: finds the app even when installed to another folder or drive.</summary>
    private static string? FromInstalledApps(ToolDefinition tool)
    {
        foreach (var (hive, path) in UninstallKeys)
        {
            using var root = hive.OpenSubKey(path);
            if (root == null) continue;
            foreach (var subName in root.GetSubKeyNames())
            {
                using var app = root.OpenSubKey(subName);
                if (app?.GetValue("DisplayName") is not string name || !name.Contains(tool.UninstallName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var folders = new List<string>();
                if (app.GetValue("InstallLocation") is string location && location.Trim('"', ' ').Length > 0)
                    folders.Add(location.Trim('"', ' '));
                if (app.GetValue("DisplayIcon") is string icon && ExePathOf(icon) is { } iconExe)
                {
                    if (tool.ExeNames.Contains(Path.GetFileName(iconExe), StringComparer.OrdinalIgnoreCase) && File.Exists(iconExe)) return iconExe;
                    folders.Add(Path.GetDirectoryName(iconExe)!);
                }
                foreach (var folder in folders)
                    foreach (var exe in tool.ExeNames)
                        if (File.Exists(Path.Combine(folder, exe))) return Path.Combine(folder, exe);
            }
        }
        return null;
    }

    /// <summary>WinGet puts portable apps under its own Packages folder, one folder per package.</summary>
    private static string? FromWinGetFolders(ToolDefinition tool)
    {
        var packages = Path.Combine(LocalAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packages)) return null;
        foreach (var folder in Directory.EnumerateDirectories(packages, tool.WinGetId + "_*"))
            foreach (var exe in tool.ExeNames)
            {
                var found = Directory.EnumerateFiles(folder, exe, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive })
                    .FirstOrDefault();
                if (found != null) return found;
            }
        return null;
    }

    /// <summary>The path in a DisplayIcon value such as "\"C:\\App\\app.exe\",0".</summary>
    public static string? ExePathOf(string displayIcon)
    {
        var value = displayIcon.Trim();
        int comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out _)) value = value[..comma];
        value = value.Trim().Trim('"');
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : null;
    }
}
