using System.Runtime.InteropServices;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// File-system protection rules for leftover cleanup. Two kinds of folder are protected:
/// <list type="bullet">
/// <item><b>Roots</b>: the folder itself and every ancestor of it are blocked (a drive root,
/// Program Files, AppData, Documents…), but app folders inside them are not.</item>
/// <item><b>Trees</b>: the folder and everything inside it are blocked (Windows, WindowsApps,
/// the Microsoft folders, shared runtimes…).</item>
/// </list>
/// </summary>
public static class ProtectedPaths
{
    private static readonly Lazy<string[]> Roots = new(BuildRoots);
    private static readonly Lazy<string[]> Trees = new(BuildTrees);
    private static readonly Lazy<string[]> ShortcutFolders = new(BuildShortcutFolders);
    private static readonly Lazy<string[]> WindowsStartMenuFolders = new(BuildWindowsStartMenuFolders);

    /// <summary>Why the folder or file may not be deleted, or null when these rules allow it.</summary>
    public static string? GetBlockReason(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Empty path";
        var p = Normalize(path);
        if (p is null) return "Not a full path";

        if (IsDriveRoot(p)) return "A drive root";

        // The Start Menu and Startup folders live inside the Microsoft trees, but the shortcuts
        // and app folders inside them belong to apps (the folders themselves stay protected)
        var inShortcutFolder = ShortcutFolders.Value.Any(f => IsStrictlyUnder(p, f));
        foreach (var windowsFolder in WindowsStartMenuFolders.Value)
            if (IsSameOrUnder(p, windowsFolder))
                return $"A Windows Start Menu folder ({windowsFolder})";

        foreach (var tree in Trees.Value)
            if (IsSameOrUnder(p, tree) && !inShortcutFolder)
                return $"Inside a protected Windows folder ({tree})";

        foreach (var root in Roots.Value)
            if (IsSameOrUnder(root, p))
                return p.Equals(root, StringComparison.OrdinalIgnoreCase)
                    ? $"A shared system folder ({root})"
                    : $"Contains a shared system folder ({root})";

        if (IsOneDriveRoot(p)) return "A OneDrive folder";

        return null;
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="folder"/> or is inside it.</summary>
    public static bool IsSameOrUnder(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        if (p is null || f is null) return false;
        return p.Equals(f, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrictlyUnder(string path, string folder) =>
        IsSameOrUnder(path, folder) && !string.Equals(Normalize(path), Normalize(folder), StringComparison.OrdinalIgnoreCase);

    /// <summary>Full path without a trailing separator; null when it isn't a full path.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var trimmed = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(trimmed)) return null;
            var full = Path.GetFullPath(trimmed);
            return IsDriveRootRaw(full) ? full.TrimEnd('\\') + "\\" : full.TrimEnd('\\', '/');
        }
        catch (Exception caught) when (ExpectedFailures.Path(caught))
        {
            // Not a usable path: GetBlockReason then blocks it ("Not a full path").
            return null;
        }
    }

    public static bool IsDriveRoot(string path)
    {
        var p = Normalize(path);
        return p is not null && IsDriveRootRaw(p);
    }

    private static bool IsDriveRootRaw(string full)
    {
        var root = Path.GetPathRoot(full);
        return !string.IsNullOrEmpty(root)
            && full.TrimEnd('\\', '/').Equals(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOneDriveRoot(string p)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile)) return false;
        var parent = Path.GetDirectoryName(p);
        return parent is not null
            && parent.Equals(profile.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(p).StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static string[] BuildRoots()
    {
        var list = new List<string?>();
        void Add(Environment.SpecialFolder f) => list.Add(Environment.GetFolderPath(f));

        foreach (var f in new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu,
            Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms,
            Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup,
            Environment.SpecialFolder.Templates, Environment.SpecialFolder.CommonTemplates,
            Environment.SpecialFolder.Fonts, Environment.SpecialFolder.Favorites,
            Environment.SpecialFolder.SendTo, Environment.SpecialFolder.Recent,
            Environment.SpecialFolder.InternetCache, Environment.SpecialFolder.Cookies,
            Environment.SpecialFolder.History,
        })
            Add(f);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        list.Add(GetDownloadsFolder());
        list.Add(string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "AppData"));
        list.Add(string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "AppData", "LocalLow"));
        list.Add(string.IsNullOrEmpty(profile) ? null : Path.GetDirectoryName(profile)); // C:\Users
        list.Add(Environment.GetEnvironmentVariable("PUBLIC"));
        list.Add(Path.GetTempPath());
        list.Add(string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Temp"));
        list.Add(string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Programs"));
        list.Add(string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Package Cache"));
        list.Add(string.IsNullOrEmpty(programData) ? null : Path.Combine(programData, "Package Cache"));
        list.Add(string.IsNullOrEmpty(windows) ? null : Path.Combine(windows, "Installer"));

        return list
            .Select(Normalize)
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BuildTrees()
    {
        var list = new List<string?>();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        list.Add(windows);
        list.Add(string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Packages"));
        list.Add(string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Microsoft"));
        list.Add(string.IsNullOrEmpty(roaming) ? null : Path.Combine(roaming, "Microsoft"));
        list.Add(string.IsNullOrEmpty(programData) ? null : Path.Combine(programData, "Microsoft"));

        foreach (var pf in ProgramFilesFolders())
        {
            list.Add(Path.Combine(pf, "WindowsApps"));
            list.Add(Path.Combine(pf, "ModifiableWindowsApps"));
            list.Add(Path.Combine(pf, "Microsoft"));
            list.Add(Path.Combine(pf, "Common Files", "microsoft shared"));
            list.Add(Path.Combine(pf, "Common Files", "System"));
            list.Add(Path.Combine(pf, "Internet Explorer"));
            list.Add(Path.Combine(pf, "dotnet"));
            list.Add(Path.Combine(pf, "Reference Assemblies"));
            list.Add(Path.Combine(pf, "MSBuild"));
            list.Add(Path.Combine(pf, "WindowsPowerShell"));
            list.Add(Path.Combine(pf, "Uninstall Information"));

            // Windows NT, Windows Defender*, Windows Mail, Windows Media Player, Windows Kits…
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(pf, "Windows*"))
                    list.Add(dir);
            }
            catch (Exception caught) when (ExpectedFailures.FileSystem(caught)) { /* The explicit Windows* names below still protect the usual folders. */ }
            // Named as well, so a failed enumeration above cannot leave them unprotected.
            foreach (var name in new[]
            {
                "Windows NT", "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail",
                "Windows Media Player", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security",
                "Windows Sidebar", "Windows Kits", "Windows Journal", "Windows Multimedia Platform",
            })
                list.Add(Path.Combine(pf, name));
        }

        return list
            .Select(Normalize)
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BuildShortcutFolders() =>
        new[]
        {
            Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms,
            Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup,
        }
        .Select(f => Normalize(Environment.GetFolderPath(f)))
        .Where(p => p is not null)
        .Select(p => p!)
        .ToArray();

    /// <summary>Windows' own Start Menu groups, protected even though the Start Menu is not.</summary>
    private static string[] BuildWindowsStartMenuFolders()
    {
        string[] groups = ["Accessories", "Accessibility", "Administrative Tools", "Maintenance", "System Tools",
                           "Windows Accessories", "Windows Administrative Tools", "Windows Ease of Access",
                           "Windows PowerShell", "Windows System", "Windows Tools"];
        return new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms }
            .Select(f => Environment.GetFolderPath(f))
            .Where(p => !string.IsNullOrEmpty(p))
            .SelectMany(p => groups.Select(g => Normalize(Path.Combine(p, g))))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToArray();
    }

    private static IEnumerable<string> ProgramFilesFolders()
    {
        foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var path = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(path)) yield return path;
        }
    }

    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    private static string? GetDownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var ptr) == 0)
            {
                try { return Marshal.PtrToStringUni(ptr); }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
        }
        catch { /* Falls back to the profile Downloads folder below, which stays protected. */ }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads");
    }
}
