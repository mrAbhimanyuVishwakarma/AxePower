using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace AxePower.Core.Services.Operations;

/// <summary>A network adapter's DNS setting as it is now: the servers, and whether they were typed in by hand.</summary>
public sealed record DnsCurrent(IReadOnlyList<string> Servers, bool IsStatic)
{
    public static DnsCurrent Unknown { get; } = new(Array.Empty<string>(), false);

    public string Describe() => Servers.Count == 0
        ? "unknown"
        : (IsStatic ? "set by hand: " : "automatic (DHCP): ") + string.Join(", ", Servers);

    /// <summary>Reads the adapter's IPv4 DNS servers, and whether they are static (Tcpip NameServer is set).</summary>
    public static DnsCurrent Read(string adapterName)
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
            if (adapter is null) return Unknown;
            var servers = adapter.GetIPProperties().DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString()).ToList();
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapter.Id}");
            bool isStatic = key?.GetValue("NameServer") is string nameServer && nameServer.Trim().Length > 0;
            return new DnsCurrent(servers, isStatic);
        }
        catch (Exception ex) when (ex is NetworkInformationException || ExpectedFailures.Registry(ex))
        {
            return Unknown;
        }
    }
}

/// <summary>
/// The confirmations of AxePower's destructive and system-wide actions, built from each action's own
/// data (the apps, files, script, adapter or packages involved). One place, so the content is the
/// same wherever an action is started and can be tested without showing a dialog.
/// </summary>
public static class ConfirmationCatalog
{
    public const string UninstallerSource = "Uninstaller Pro";

    // -- Uninstall cleanup ------------------------------------------------------------

    public static ActionConfirmation UninstallApps(IReadOnlyList<(string Name, string Method)> apps, IReadOnlyList<string> skipped, bool restorePoint, bool dryRun)
    {
        var affected = apps.Select(a => $"{a.Name}: {a.Method}").ToList();
        affected.AddRange(skipped.Select(s => $"Skipped (protected): {s}"));
        if (dryRun)
        {
            return new ActionConfirmation
            {
                Action = $"Dry run: show what uninstalling {Count(apps.Count, "app")} would do",
                Source = UninstallerSource,
                Affected = affected,
                Risk = RiskLevel.Low,
                Backup = "Nothing is changed in a dry run.",
                Reversibility = Reversibility.FullyReversible,
                ConfirmLabel = "Run the dry run",
            };
        }
        return new ActionConfirmation
        {
            Action = $"Uninstall {Count(apps.Count, "app")}",
            Source = UninstallerSource,
            Affected = affected,
            RequiresAdministrator = true,
            Risk = RiskLevel.High,
            Backup = (restorePoint
                ? "A System Restore point is created first, when Windows allows it. "
                : "No System Restore point is created (turned off in Uninstaller settings). ")
                + "Leftovers are offered for cleanup only after each app is really gone.",
            Restart = RestartNeed.None,
            Reversibility = Reversibility.Irreversible,
            ReversibilityNote = "each app's own uninstaller removes it, and AxePower cannot undo that; reinstall an app to get it back.",
            ConfirmLabel = apps.Count == 1 ? $"Uninstall {apps[0].Name}" : $"Uninstall {apps.Count} apps",
        };
    }

    public static ActionConfirmation CleanLeftovers(IReadOnlyList<string> items, bool dryRun, bool recycle)
    {
        if (dryRun)
        {
            return new ActionConfirmation
            {
                Action = $"Dry run: check {Count(items.Count, "leftover")} without deleting anything",
                Source = UninstallerSource,
                Affected = items,
                Risk = RiskLevel.Low,
                Backup = "Nothing is changed in a dry run.",
                Reversibility = Reversibility.FullyReversible,
                ConfirmLabel = "Run the dry run",
            };
        }
        return new ActionConfirmation
        {
            Action = $"Remove {Count(items.Count, "leftover")} of uninstalled apps",
            Source = UninstallerSource,
            Affected = items,
            RequiresAdministrator = true,
            Risk = RiskLevel.High,
            Backup = "Registry keys and values are exported to .reg backups first; an item whose backup fails is kept. "
                     + (recycle ? "Files and folders go to the Recycle Bin." : "Files and folders are deleted permanently (Recycle Bin is turned off in Uninstaller settings)."),
            Reversibility = Reversibility.PartiallyReversible,
            ReversibilityNote = recycle
                ? "registry items can be restored from Uninstaller history, and files from the Recycle Bin until it is emptied."
                : "registry items can be restored from Uninstaller history; deleted files cannot.",
            ConfirmLabel = $"Remove {Count(items.Count, "leftover")}",
        };
    }

    public static ActionConfirmation RestoreUninstallBackup(string session) => new()
    {
        Action = $"Restore the registry backups from {session}",
        Source = UninstallerSource,
        Affected = new[] { $"Registry keys and values removed during {session} are written back." },
        RequiresAdministrator = true,
        Risk = RiskLevel.Medium,
        Backup = "The .reg backups made before that cleanup are used. HKLM items need administrator rights. Files and folders are restored from the Recycle Bin, not here.",
        Reversibility = Reversibility.PartiallyReversible,
        ReversibilityNote = "restored keys can be removed again with another cleanup; files are restored from the Recycle Bin, not here.",
        ConfirmLabel = "Restore the backups",
    };

    // -- Debloat ----------------------------------------------------------------------

    public static ActionConfirmation RunDebloatScript(string tool, string author, IReadOnlyList<string> whatItDoes, string command) => new()
    {
        Action = $"Run {tool} by {author} (third-party script)",
        Source = "Debloat",
        Affected = whatItDoes.Concat(new[] { $"Command: {command}" }).ToList(),
        RequiresAdministrator = true,
        Risk = RiskLevel.High,
        Backup = "AxePower makes no backup of what the script changes. Create a restore point first (System Optimizer); the script may offer its own.",
        Restart = RestartNeed.Restart,
        Reversibility = Reversibility.Irreversible,
        ReversibilityNote = "the script runs on its own, so AxePower cannot see, list or undo its changes. Removed apps can usually be reinstalled from the Microsoft Store.",
        ConfirmLabel = $"Run {tool}",
    };

    // -- Windows Update repair --------------------------------------------------------

    public static ActionConfirmation ResetWindowsUpdateCache(IReadOnlyList<string> services) => new()
    {
        Action = "Reset the Windows Update download cache",
        Source = "Update PC",
        Affected = services.Select(s => $"Service {s}: stopped if running, then started again").Concat(new[] { @"%WINDIR%\SoftwareDistribution\Download: emptied" }).ToList(),
        RequiresAdministrator = true,
        Risk = RiskLevel.Medium,
        Backup = "None needed: the deleted files are only downloads, and Windows Update fetches them again. Your update history stays.",
        Reversibility = Reversibility.PartiallyReversible,
        ReversibilityNote = "services are put back as they were; the deleted downloads are fetched again rather than restored.",
        ConfirmLabel = "Reset the update cache",
    };

    // -- DNS --------------------------------------------------------------------------

    public static ActionConfirmation ChangeDns(string adapter, string provider, string newServers, DnsCurrent current)
    {
        bool customNow = current.IsStatic;
        return new ActionConfirmation
        {
            Action = $"Use {provider} DNS on {adapter}",
            Source = "System Doctor › DNS",
            Affected = new[] { $"{adapter} now: {current.Describe()}", $"{adapter} after: set by hand: {newServers}" },
            RequiresAdministrator = true,
            Risk = RiskLevel.Low,
            Backup = customNow
                ? $"Note your current servers ({string.Join(", ", current.Servers)}): \"Automatic (DHCP)\" does not bring back servers you typed in yourself."
                : "Switching back to \"Automatic (DHCP)\" returns to what you have now.",
            Reversibility = customNow ? Reversibility.PartiallyReversible : Reversibility.FullyReversible,
            ReversibilityNote = customNow ? "your own DNS servers have to be typed in again by hand." : null,
            ConfirmLabel = $"Use {provider}",
        };
    }

    public static ActionConfirmation ResetDnsToAutomatic(string adapter, DnsCurrent current) => new()
    {
        Action = $"Set DNS on {adapter} back to automatic (DHCP)",
        Source = "System Doctor › DNS",
        Affected = new[] { $"{adapter} now: {current.Describe()}", $"{adapter} after: automatic (DHCP)" },
        RequiresAdministrator = true,
        Risk = RiskLevel.Low,
        Backup = current.IsStatic ? $"Note your current servers ({string.Join(", ", current.Servers)}) if you want them again." : "Nothing to keep: it is automatic already.",
        Reversibility = current.IsStatic ? Reversibility.PartiallyReversible : Reversibility.FullyReversible,
        ReversibilityNote = current.IsStatic ? "the servers set by hand are not remembered." : null,
        ConfirmLabel = "Use automatic DNS",
    };

    // -- Bulk software operations --------------------------------------------------------

    public static ActionConfirmation InstallPackages(IReadOnlyList<string> names) => new()
    {
        Action = $"Install {Count(names.Count, "app")} with WinGet",
        Source = "Software Hub",
        Affected = names,
        RequiresAdministrator = true,
        Risk = names.Count > 1 ? RiskLevel.Medium : RiskLevel.Low,
        Backup = "None needed: nothing is removed. Each installer comes from WinGet's source or the publisher.",
        Reversibility = Reversibility.PartiallyReversible,
        ReversibilityNote = "each app can be uninstalled again (Software Hub or Uninstaller Pro), but what its installer set up may stay.",
        ConfirmLabel = names.Count == 1 ? $"Install {names[0]}" : $"Install {names.Count} apps",
    };

    public static ActionConfirmation UpgradeAllPackages(IReadOnlyList<string> knownUpgrades) => new()
    {
        Action = "Update every app WinGet has an update for",
        Source = "Software Hub",
        Affected = knownUpgrades.Count > 0 ? knownUpgrades : new[] { "Every installed app that WinGet finds a newer version of (the list is decided by WinGet when it runs)." },
        RequiresAdministrator = true,
        Risk = RiskLevel.Medium,
        Backup = "None: earlier versions are not kept.",
        Restart = RestartNeed.None,
        Reversibility = Reversibility.Irreversible,
        ReversibilityNote = "AxePower cannot go back to an earlier version of an app.",
        ConfirmLabel = "Update all apps",
    };

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
