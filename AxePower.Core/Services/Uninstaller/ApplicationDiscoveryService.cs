using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AxePower.Core.Models.Uninstaller;
using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Discovers installed applications from the uninstall registry keys and Store packages.
/// The registry walk only reads values; every classification rule lives in
/// <see cref="UninstallEntryClassifier"/>, and protection comes only from
/// <see cref="IProtectionService"/>.
/// </summary>
public sealed partial class ApplicationDiscoveryService : IApplicationDiscoveryService
{
    private static readonly (RegistryHive Hive, string SubKey, string Prefix)[] RegistrySources =
    [
        (RegistryHive.LocalMachine, RegistryPath.UninstallSubKey, "HKLM"),
        (RegistryHive.LocalMachine, RegistryPath.UninstallSubKeyWow, "HKLM"),
        (RegistryHive.CurrentUser, RegistryPath.UninstallSubKey, "HKCU"),
    ];

    private static readonly TimeSpan AppxTimeout = TimeSpan.FromSeconds(30);

    private readonly IProtectionService _protection;
    private readonly IFileSystemProbe _fs;
    private readonly IMsiProductStateReader _msi;

    public ApplicationDiscoveryService()
        : this(new ProtectionService(), new FileSystemProbe(), new MsiProductStateReader())
    {
    }

    public ApplicationDiscoveryService(IProtectionService protection, IFileSystemProbe fs, IMsiProductStateReader msi)
    {
        _protection = protection;
        _fs = fs;
        _msi = msi;
    }

    public async Task<IReadOnlyList<InstalledApplication>> DiscoverApplicationsAsync(
        CancellationToken cancellationToken = default)
    {
        var registryTask = Task.Run(() => ReadRegistry(cancellationToken), cancellationToken);
        var appxTask = DiscoverAppxPackagesAsync(cancellationToken);
        await Task.WhenAll(registryTask, appxTask).ConfigureAwait(false);

        var results = await registryTask.ConfigureAwait(false);
        foreach (var package in await appxTask.ConfigureAwait(false))
        {
            // Store packages only deduplicate against each other
            if (!results.Any(a => a.PackageFullName.Equals(package.PackageFullName, StringComparison.OrdinalIgnoreCase)))
                results.Add(package);
        }
        return Finish(results);
    }

    public async Task<IReadOnlyList<InstalledApplication>> DiscoverRegistryApplicationsAsync(
        CancellationToken cancellationToken = default)
    {
        var results = await Task.Run(() => ReadRegistry(cancellationToken), cancellationToken).ConfigureAwait(false);
        return Finish(results);
    }

    private IReadOnlyList<InstalledApplication> Finish(List<InstalledApplication> results)
    {
        foreach (var app in results)
            app.IsProtected = _protection.IsApplicationProtected(app);

        // Broken entries at the bottom, then by name
        return results
            .OrderBy(a => a.IsBrokenEntry ? 1 : 0)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Registry
    // ─────────────────────────────────────────────────────────────────────────

    private List<InstalledApplication> ReadRegistry(CancellationToken ct)
    {
        var results = new List<InstalledApplication>();

        foreach (var (hive, subKey, prefix) in RegistrySources)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var uninstallKey = baseKey.OpenSubKey(subKey, writable: false);
                if (uninstallKey is null) continue;

                foreach (var keyName in uninstallKey.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var appKey = uninstallKey.OpenSubKey(keyName, writable: false);
                        if (appKey is null) continue;

                        var values = UninstallEntryValues.FromRegistryKey(appKey);
                        var app = UninstallEntryClassifier.Classify(values, keyName, $@"{prefix}\{subKey}\{keyName}", _fs, _msi);
                        if (app is not null) MergeOrAdd(results, app);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        // One unreadable entry doesn't stop discovery
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Hive not accessible — skip
            }
        }

        ClearSharedDerivedLocations(results);
        return results;
    }

    /// <summary>
    /// A derived install folder is only trusted when no other entry shares it or the same
    /// uninstaller: Steam games all "uninstall" through steam.exe, so its folder is not theirs.
    /// </summary>
    internal static void ClearSharedDerivedLocations(List<InstalledApplication> apps)
    {
        foreach (var app in apps.Where(a => a.IsInstallLocationDerived).ToList())
        {
            var shared = apps.Any(other => !ReferenceEquals(other, app) &&
                (other.InstallLocation.Equals(app.InstallLocation, StringComparison.OrdinalIgnoreCase) ||
                 other.UninstallerPath.Length > 0 &&
                 other.UninstallerPath.Equals(app.UninstallerPath, StringComparison.OrdinalIgnoreCase)));
            if (!shared) continue;

            app.InstallLocation = string.Empty;
            app.IsInstallLocationDerived = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Deduplication / merging
    // ─────────────────────────────────────────────────────────────────────────

    internal static void MergeOrAdd(List<InstalledApplication> existing, InstalledApplication incoming)
    {
        var match = FindDuplicate(existing, incoming);
        if (match is null)
            existing.Add(incoming);
        else
            Merge(match, incoming);
    }

    /// <summary>
    /// Priority: ProductCode → UninstallString → InstallLocation (same publisher) →
    /// name + publisher + version.
    /// </summary>
    private static InstalledApplication? FindDuplicate(List<InstalledApplication> existing, InstalledApplication candidate)
    {
        if (!string.IsNullOrEmpty(candidate.ProductCode))
        {
            var m = existing.FirstOrDefault(a =>
                a.ProductCode.Equals(candidate.ProductCode, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }

        if (!string.IsNullOrWhiteSpace(candidate.UninstallString))
        {
            var m = existing.FirstOrDefault(a =>
                a.UninstallString.Trim().Equals(candidate.UninstallString.Trim(), StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }

        if (!string.IsNullOrEmpty(candidate.InstallLocation) && !candidate.IsInstallLocationDerived)
        {
            var publisher = Normalize(candidate.Publisher);
            var m = existing.FirstOrDefault(a =>
                !a.IsInstallLocationDerived &&
                a.InstallLocation.Equals(candidate.InstallLocation, StringComparison.OrdinalIgnoreCase) &&
                publisher.Length > 0 && Normalize(a.Publisher) == publisher);
            if (m is not null) return m;
        }

        var name = Normalize(candidate.DisplayName);
        if (name.Length > 0)
        {
            var publisher = Normalize(candidate.Publisher);
            var version = Normalize(candidate.Version);
            var m = existing.FirstOrDefault(a =>
                Normalize(a.DisplayName) == name &&
                (publisher.Length == 0 || Normalize(a.Publisher) == publisher) &&
                (version.Length == 0 || Normalize(a.Version) == version));
            if (m is not null) return m;
        }

        return null;
    }

    /// <summary>
    /// One product registered twice (RustDesk has a "RustDesk" key and its MSI key). The entry
    /// whose uninstaller should run becomes the primary: a visible entry over a hidden one, a
    /// Burn bundle over the MSIs it chains, MSI over an exe, anything over a broken entry.
    /// Every key is kept so verification can check that all of them are gone.
    /// </summary>
    private static void Merge(InstalledApplication target, InstalledApplication incoming)
    {
        target.DiscoverySources |= incoming.DiscoverySources;

        if (Rank(incoming) > Rank(target))
        {
            var takeName = IsHidden(target) && !IsHidden(incoming);
            SwapUninstaller(target, incoming);
            if (takeName)
            {
                target.DisplayName = incoming.DisplayName;
                if (!string.IsNullOrEmpty(incoming.Version)) target.Version = incoming.Version;
                if (!string.IsNullOrEmpty(incoming.Publisher)) target.Publisher = incoming.Publisher;
            }
        }

        foreach (var key in incoming.AllRegistryKeyPaths)
        {
            if (!key.Equals(target.RegistryKeyPath, StringComparison.OrdinalIgnoreCase) &&
                !target.RelatedKeyPaths.Contains(key, StringComparer.OrdinalIgnoreCase))
                target.RelatedKeyPaths.Add(key);
        }

        if (!string.IsNullOrEmpty(incoming.InstallLocation) &&
            (string.IsNullOrEmpty(target.InstallLocation) || target.IsInstallLocationDerived && !incoming.IsInstallLocationDerived))
        {
            target.InstallLocation = incoming.InstallLocation;
            target.IsInstallLocationDerived = incoming.IsInstallLocationDerived;
        }

        if (target.EstimatedSizeKb <= 0) target.EstimatedSizeKb = incoming.EstimatedSizeKb;
        if (string.IsNullOrEmpty(target.IconPath)) target.IconPath = incoming.IconPath;
        if (string.IsNullOrEmpty(target.Publisher)) target.Publisher = incoming.Publisher;
        if (string.IsNullOrEmpty(target.Version)) target.Version = incoming.Version;
        if (string.IsNullOrEmpty(target.InstallDate)) target.InstallDate = incoming.InstallDate;

        // Hidden only when every registration is hidden
        target.IsSystemComponent &= incoming.IsSystemComponent;
        target.IsUpdate &= incoming.IsUpdate;
        target.CanUninstall &= incoming.CanUninstall;
    }

    private static bool IsHidden(InstalledApplication app) => app.IsSystemComponent || app.IsUpdate;

    private static int Rank(InstalledApplication app) =>
        (IsHidden(app) ? 0 : 100) + app.UninstallerKind switch
        {
            UninstallerKind.BurnBundle => 20,
            UninstallerKind.Msi => 10,
            UninstallerKind.BrokenEntry => -50,
            _ => 0,
        };

    /// <summary>Exchanges the "what uninstalls this" fields, so <paramref name="target"/> runs <paramref name="source"/>'s uninstaller.</summary>
    private static void SwapUninstaller(InstalledApplication target, InstalledApplication source)
    {
        (target.RegistryKeyPath, source.RegistryKeyPath) = (source.RegistryKeyPath, target.RegistryKeyPath);
        (target.RegistryKeyName, source.RegistryKeyName) = (source.RegistryKeyName, target.RegistryKeyName);
        (target.UninstallString, source.UninstallString) = (source.UninstallString, target.UninstallString);
        (target.QuietUninstallString, source.QuietUninstallString) = (source.QuietUninstallString, target.QuietUninstallString);
        (target.UninstallerKind, source.UninstallerKind) = (source.UninstallerKind, target.UninstallerKind);
        (target.UninstallerPath, source.UninstallerPath) = (source.UninstallerPath, target.UninstallerPath);
        (target.UninstallerArguments, source.UninstallerArguments) = (source.UninstallerArguments, target.UninstallerArguments);
        (target.ProductCode, source.ProductCode) = (source.ProductCode, target.ProductCode);
        (target.MsiProductState, source.MsiProductState) = (source.MsiProductState, target.MsiProductState);
        (target.ApplicationType, source.ApplicationType) = (source.ApplicationType, target.ApplicationType);
        (target.Scope, source.Scope) = (source.Scope, target.Scope);
        (target.Architecture, source.Architecture) = (source.Architecture, target.Architecture);
    }

    private static string Normalize(string? s) =>
        string.IsNullOrWhiteSpace(s) ? string.Empty : NormalizeRegex().Replace(s.ToLowerInvariant().Trim(), string.Empty);

    [GeneratedRegex(@"[\s\-_\.\(\)]+")]
    private static partial Regex NormalizeRegex();

    // ─────────────────────────────────────────────────────────────────────────
    // Store packages
    // ─────────────────────────────────────────────────────────────────────────

    private const string AppxScript =
        "$p = @(Get-AppxPackage -PackageTypeFilter Main | Where-Object { -not $_.IsFramework } | " +
        "Select-Object Name, PackageFullName, PackageFamilyName, Version, Publisher, InstallLocation, NonRemovable, " +
        "@{n='SignatureKind';e={[string]$_.SignatureKind}}); " +
        "ConvertTo-Json -InputObject $p -Compress -Depth 2";

    private static async Task<List<InstalledApplication>> DiscoverAppxPackagesAsync(CancellationToken ct)
    {
        var results = new List<InstalledApplication>();
        try
        {
            var run = await PowerShellRunner.RunAsync(AppxScript, AppxTimeout, ct).ConfigureAwait(false);
            if (run.TimedOut || string.IsNullOrWhiteSpace(run.StandardOutput)) return results;

            using var doc = JsonDocument.Parse(run.StandardOutput.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var package = ParseAppxPackage(item);
                if (package is not null) results.Add(package);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Store discovery failure is non-fatal
        }
        return results;
    }

    internal static InstalledApplication? ParseAppxPackage(JsonElement item)
    {
        string Str(string name) =>
            item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        bool Bool(string name) =>
            item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        var name = Str("Name");
        var fullName = Str("PackageFullName");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(fullName)) return null;
        // Inbox and system-signed packages are part of Windows
        if (Str("SignatureKind") is "System" or "Inbox") return null;

        var installLocation = Str("InstallLocation");
        var (displayName, publisherName) = ReadManifestNames(installLocation, name, fullName);
        var fullNameParts = fullName.Split('_');

        return new InstalledApplication
        {
            Id = fullName,
            DisplayName = displayName,
            Publisher = publisherName.Length > 0 ? publisherName : PublisherCommonName(Str("Publisher")),
            Version = Str("Version"),
            InstallLocation = installLocation,
            PackageFullName = fullName,
            PackageFamilyName = Str("PackageFamilyName"),
            UninstallerKind = UninstallerKind.StoreApp,
            ApplicationType = ApplicationType.AppX,
            DiscoverySources = DiscoverySource.AppX,
            CanUninstall = !Bool("NonRemovable"),
            Architecture = fullNameParts.Length > 2 ? fullNameParts[2] : string.Empty,
            Scope = "User",
        };
    }

    /// <summary>DisplayName and PublisherDisplayName from AppxManifest.xml, resolving ms-resource: strings.</summary>
    private static (string DisplayName, string Publisher) ReadManifestNames(string installLocation, string packageName, string fullName)
    {
        var display = string.Empty;
        var publisher = string.Empty;
        try
        {
            var manifest = Path.Combine(installLocation, "AppxManifest.xml");
            if (File.Exists(manifest))
            {
                var properties = XDocument.Load(manifest).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
                display = Resolve(properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value);
                publisher = Resolve(properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value);
            }
        }
        catch { }

        return (display.Length > 0 ? display : FormatPackageName(packageName), publisher);

        string Resolve(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            value = value.Trim();
            if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return value;
            return ResolveResourceString(fullName, packageName, value) ?? string.Empty;
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    private static string? ResolveResourceString(string fullName, string packageName, string resource)
    {
        var key = resource["ms-resource:".Length..];
        var candidates = new List<string>();
        if (key.StartsWith("//", StringComparison.Ordinal))
            candidates.Add("ms-resource:" + key);
        else if (key.StartsWith('/'))
            candidates.Add($"ms-resource://{packageName}{key}");
        else
        {
            candidates.Add($"ms-resource://{packageName}/Resources/{key}");
            candidates.Add($"ms-resource://{packageName}/{key}");
        }

        foreach (var uri in candidates)
        {
            try
            {
                var buffer = new StringBuilder(1024);
                if (SHLoadIndirectString($"@{{{fullName}?{uri}}}", buffer, buffer.Capacity, IntPtr.Zero) == 0 &&
                    buffer.Length > 0 && !buffer.ToString().StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                    return buffer.ToString();
            }
            catch { }
        }
        return null;
    }

    /// <summary>"Microsoft.WindowsCalculator" → "Windows Calculator" (fallback only).</summary>
    private static string FormatPackageName(string packageName)
    {
        var parts = packageName.Split('.');
        var meaningful = parts.Length > 1 ? parts[^1] : packageName;
        return Regex.Replace(meaningful, @"(?<=[a-z])(?=[A-Z])", " ");
    }

    private static string PublisherCommonName(string publisher) =>
        Regex.Replace(publisher, @"CN=([^,]+).*", "$1").Trim().Trim('"');
}
