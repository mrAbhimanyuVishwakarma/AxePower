using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AxePower.Core.Models.Uninstaller;
using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Discovers installed applications from the Windows registry, AppX packages, and WinGet.
/// Deduplicates across all sources and detects orphaned/broken entries.
/// </summary>
public sealed class ApplicationDiscoveryService : IApplicationDiscoveryService
{
    private static readonly string[] RegistryUninstallPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    private const string HkcuUninstallPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // Known Microsoft/Windows publisher prefixes for component flagging
    private static readonly string[] MicrosoftPublisherPrefixes =
    [
        "Microsoft", "Microsoft Corporation", "Windows", "Microsoft Windows"
    ];

    // Values that indicate a hidden/system-only registry entry
    private static readonly string[] SystemComponentIndicators =
    [
        "Windows ", "Microsoft Visual C++", "Microsoft .NET",
        "Microsoft DirectX", "NVIDIA FrameView", "Intel(R) ", "AMD ",
    ];

    public async Task<IReadOnlyList<InstalledApplication>> DiscoverApplicationsAsync(
        CancellationToken cancellationToken = default)
    {
        var registryTask = Task.Run(() =>
        {
            var regResults = new List<InstalledApplication>();
            ReadRegistryHive(RegistryHive.LocalMachine, RegistryView.Registry64,
                RegistryUninstallPaths[0], DiscoverySource.RegistryHKLM, regResults, cancellationToken);
            ReadRegistryHive(RegistryHive.LocalMachine, RegistryView.Registry32,
                RegistryUninstallPaths[0], DiscoverySource.RegistryWOW6432, regResults, cancellationToken);
            ReadRegistryHive(RegistryHive.CurrentUser, RegistryView.Default,
                HkcuUninstallPath, DiscoverySource.RegistryHKCU, regResults, cancellationToken);
            return regResults;
        }, cancellationToken);

        var appxTask = DiscoverAppxPackagesAsync(cancellationToken);
        var wingetTask = DiscoverWingetPackagesAsync(cancellationToken);

        await Task.WhenAll(registryTask, appxTask, wingetTask).ConfigureAwait(false);

        var results = new List<InstalledApplication>(await registryTask.ConfigureAwait(false));
        MergeOrAdd(results, await appxTask.ConfigureAwait(false));
        MergeOrAdd(results, await wingetTask.ConfigureAwait(false));

        foreach (var app in results)
        {
            app.IsProtected = IsProtectedApplication(app);
            app.IsMicrosoftComponent = IsMicrosoftPublisher(app.Publisher);
            app.UninstallerConfidence = ComputeUninstallerConfidence(app);
        }

        // Sort: name alphabetically, orphans at the bottom
        return results
            .OrderBy(a => a.ApplicationType == ApplicationType.Orphaned ? 1 : 0)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Registry reading
    // ─────────────────────────────────────────────────────────────────────────

    private static void ReadRegistryHive(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        DiscoverySource source,
        List<InstalledApplication> results,
        CancellationToken ct)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(subKeyPath, writable: false);
            if (uninstallKey is null) return;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var appKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
                    if (appKey is null) continue;

                    var app = ReadApplicationFromKey(appKey, subKeyName, source, hive);
                    if (app is null) continue;

                    MergeOrAdd(results, [app]);
                }
                catch (Exception)
                {
                    // Ignore individual key errors — move on to next entry
                }
            }
        }
        catch (Exception)
        {
            // Hive not accessible — skip silently
        }
    }

    private static InstalledApplication? ReadApplicationFromKey(
        RegistryKey key,
        string keyName,
        DiscoverySource source,
        RegistryHive hive)
    {
        var displayName = GetString(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        // Skip entries that set NoDisplay=1 unless they have a ProductCode
        var noDisplay = GetDword(key, "NoDisplay");
        var systemComponent = GetDword(key, "SystemComponent");
        var noRemove = GetDword(key, "NoRemove");

        // Hidden system components — skip unless they have a GUID key name (MSI)
        if ((noDisplay == 1 || systemComponent == 1) && !IsGuid(keyName))
            return null;

        var uninstallString = GetString(key, "UninstallString");
        var quietUninstall  = GetString(key, "QuietUninstallString");
        var isWindowsInstaller = GetDword(key, "WindowsInstaller") == 1;
        var productCode = GetString(key, "ProductCode");

        // Determine type
        ApplicationType appType;
        if (isWindowsInstaller || IsGuid(productCode) || IsGuid(keyName))
            appType = ApplicationType.MSI;
        else
            appType = ApplicationType.Win32;

        // Detect orphaned entries
        bool isOrphaned = false;
        if (!string.IsNullOrWhiteSpace(uninstallString))
        {
            var execPath = ExtractExecutablePath(uninstallString);
            if (!string.IsNullOrWhiteSpace(execPath) &&
                !execPath.Equals("msiexec", StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(execPath))
            {
                isOrphaned = true;
            }
        }
        else if (appType != ApplicationType.MSI)
        {
            isOrphaned = true; // No uninstall string and not MSI
        }

        if (isOrphaned)
            appType = ApplicationType.Orphaned;

        var pc = IsGuid(productCode) ? productCode
               : IsGuid(keyName)     ? keyName
               : string.Empty;

        var installLocation = GetString(key, "InstallLocation").TrimEnd('\\');
        var sizeKb = (long)GetDword(key, "EstimatedSize");

        var app = new InstalledApplication
        {
            Id = GenerateId(pc, keyName, displayName),
            DisplayName = displayName,
            Publisher = GetString(key, "Publisher"),
            Version = GetString(key, "DisplayVersion"),
            InstallDate = GetString(key, "InstallDate"),
            InstallLocation = installLocation,
            EstimatedSizeKb = sizeKb,
            UninstallString = uninstallString,
            QuietUninstallString = quietUninstall,
            ModifyPath = GetString(key, "ModifyPath"),
            ProductCode = pc,
            ApplicationType = appType,
            DiscoverySources = source,
            IsSystemComponent = systemComponent == 1 || noDisplay == 1,
            CanUninstall = noRemove != 1 && !string.IsNullOrEmpty(uninstallString),
            Architecture = source == DiscoverySource.RegistryWOW6432 ? "x86" : "x64",
            Scope = hive == RegistryHive.CurrentUser ? "User" : "Machine",
        };

        // Icon: try DisplayIcon value
        var displayIcon = GetString(key, "DisplayIcon");
        if (!string.IsNullOrEmpty(displayIcon))
            app.IconPath = ExtractExecutablePath(displayIcon);

        return app;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AppX / MSIX discovery via PowerShell
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<InstalledApplication>> DiscoverAppxPackagesAsync(
        CancellationToken ct)
    {
        var results = new List<InstalledApplication>();
        try
        {
            // Use PowerShell to get AppX packages — avoids COM interop issues in non-admin context
            const string script =
                "Get-AppxPackage | Select-Object -Property Name,PackageFullName,PackageFamilyName," +
                "Version,Publisher,InstallLocation,IsFramework,NonRemovable,SignatureKind | " +
                "ConvertTo-Csv -NoTypeInformation";

            var output = await RunPowerShellAsync(script, ct);
            if (string.IsNullOrWhiteSpace(output)) return results;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return results;

            // Skip header line
            foreach (var line in lines.Skip(1))
            {
                ct.ThrowIfCancellationRequested();
                var parts = ParseCsvLine(line);
                if (parts.Length < 9) continue;

                var name            = parts[0].Trim('"');
                var packageFullName = parts[1].Trim('"');
                var packageFamily   = parts[2].Trim('"');
                var version         = parts[3].Trim('"');
                var publisher       = parts[4].Trim('"');
                var installLocation = parts[5].Trim('"');
                var isFramework     = parts[6].Trim('"').Equals("True", StringComparison.OrdinalIgnoreCase);
                var nonRemovable    = parts[7].Trim('"').Equals("True", StringComparison.OrdinalIgnoreCase);
                var signatureKind   = parts[8].Trim('"'); // Store, System, Developer, Enterprise, Inbox

                if (string.IsNullOrWhiteSpace(name)) continue;
                // Skip framework packages and system packages
                if (isFramework) continue;
                if (signatureKind is "System" or "Inbox") continue;

                // Clean up publisher OID if present
                var cleanPublisher = Regex.Replace(publisher, @"CN=([^,]+).*", "$1").Trim();

                var app = new InstalledApplication
                {
                    Id = GenerateId(packageFullName, packageFamily, name),
                    DisplayName = FormatAppxDisplayName(name),
                    Publisher = cleanPublisher,
                    Version = version,
                    InstallLocation = installLocation,
                    PackageFullName = packageFullName,
                    PackageFamilyName = packageFamily,
                    ApplicationType = ApplicationType.AppX,
                    DiscoverySources = DiscoverySource.AppX,
                    IsSystemComponent = isFramework || nonRemovable,
                    CanUninstall = !nonRemovable,
                    Architecture = "x64",
                    Scope = "Machine",
                };
                results.Add(app);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // AppX discovery failure is non-fatal
        }
        return results;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WinGet discovery
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<InstalledApplication>> DiscoverWingetPackagesAsync(
        CancellationToken ct)
    {
        var results = new List<InstalledApplication>();
        try
        {
            var wingetPath = FindWinget();
            if (wingetPath is null) return results;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var psi = new ProcessStartInfo
            {
                FileName = wingetPath,
                Arguments = "list --accept-source-agreements --disable-interactivity",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(psi);
            if (process is null) return results;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);
            var output = await stdoutTask;

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return results;

            // WinGet list output: "Name  Id  Version  Source"
            // Parse the tabular output (not CSV — uses fixed-width columns with separators)
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // Find header line to determine column positions
            int headerIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("Id") && lines[i].Contains("Version") && lines[i].Contains("Source"))
                {
                    headerIdx = i;
                    break;
                }
            }
            if (headerIdx < 0) return results;

            // Determine column positions from the separator line (usually the line after header)
            int nameStart = 0, idStart = -1, versionStart = -1, sourceStart = -1;
            var header = lines[headerIdx];
            idStart      = header.IndexOf("Id",      StringComparison.Ordinal);
            versionStart = header.IndexOf("Version", StringComparison.Ordinal);
            sourceStart  = header.IndexOf("Source",  StringComparison.Ordinal);

            if (idStart < 0 || versionStart < 0) return results;

            for (int i = headerIdx + 2; i < lines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var line = lines[i];
                if (line.Length < idStart) continue;

                try
                {
                    var name    = line[nameStart..idStart].Trim();
                    var id      = versionStart > idStart && line.Length > versionStart
                                  ? line[idStart..versionStart].Trim()
                                  : line[idStart..].Trim();
                    var version = sourceStart > 0 && line.Length > sourceStart
                                  ? line[versionStart..sourceStart].Trim()
                                  : (line.Length > versionStart ? line[versionStart..].Trim() : string.Empty);
                    var source  = sourceStart > 0 && line.Length > sourceStart
                                  ? line[sourceStart..].Trim()
                                  : string.Empty;

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;

                    var app = new InstalledApplication
                    {
                        Id = GenerateId(id, string.Empty, name),
                        DisplayName = name,
                        Version = version,
                        WingetId = id,
                        WingetSource = source,
                        ApplicationType = ApplicationType.WinGet,
                        DiscoverySources = DiscoverySource.WinGet,
                        CanUninstall = true,
                        Architecture = string.Empty,
                        Scope = "Machine",
                    };
                    results.Add(app);
                }
                catch { /* skip malformed line */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // WinGet discovery failure is non-fatal
        }
        return results;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Deduplication / merging
    // ─────────────────────────────────────────────────────────────────────────

    private static void MergeOrAdd(
        List<InstalledApplication> existing,
        IEnumerable<InstalledApplication> incoming)
    {
        foreach (var app in incoming)
        {
            var match = FindDuplicate(existing, app);
            if (match is not null)
            {
                // Merge: add source flags and enrich missing fields
                match.DiscoverySources |= app.DiscoverySources;

                if (string.IsNullOrEmpty(match.WingetId) && !string.IsNullOrEmpty(app.WingetId))
                    match.WingetId = app.WingetId;
                if (string.IsNullOrEmpty(match.PackageFullName) && !string.IsNullOrEmpty(app.PackageFullName))
                    match.PackageFullName = app.PackageFullName;
                if (string.IsNullOrEmpty(match.ProductCode) && !string.IsNullOrEmpty(app.ProductCode))
                    match.ProductCode = app.ProductCode;
                if (match.EstimatedSizeKb <= 0 && app.EstimatedSizeKb > 0)
                    match.EstimatedSizeKb = app.EstimatedSizeKb;
            }
            else
            {
                existing.Add(app);
            }
        }
    }

    private static InstalledApplication? FindDuplicate(
        List<InstalledApplication> existing,
        InstalledApplication candidate)
    {
        // Priority 1: ProductCode GUID
        if (!string.IsNullOrEmpty(candidate.ProductCode))
        {
            var m = existing.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.ProductCode) &&
                a.ProductCode.Equals(candidate.ProductCode, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }

        // Priority 2: PackageFullName
        if (!string.IsNullOrEmpty(candidate.PackageFullName))
        {
            var m = existing.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.PackageFullName) &&
                a.PackageFullName.Equals(candidate.PackageFullName, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }

        // Priority 3: WinGet ID
        if (!string.IsNullOrEmpty(candidate.WingetId))
        {
            var m = existing.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.WingetId) &&
                a.WingetId.Equals(candidate.WingetId, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }

        // Priority 4: UninstallString identity
        if (!string.IsNullOrEmpty(candidate.UninstallString))
        {
            var m = existing.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.UninstallString) &&
                a.UninstallString.Equals(candidate.UninstallString, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }

        // Priority 5: InstallLocation
        if (!string.IsNullOrEmpty(candidate.InstallLocation))
        {
            var m = existing.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.InstallLocation) &&
                a.InstallLocation.Equals(candidate.InstallLocation, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }

        // Priority 6: Normalized name + publisher + version
        var normalizedName      = Normalize(candidate.DisplayName);
        var normalizedPublisher = Normalize(candidate.Publisher);
        var normalizedVersion   = Normalize(candidate.Version);

        if (!string.IsNullOrEmpty(normalizedName))
        {
            var m = existing.FirstOrDefault(a =>
                Normalize(a.DisplayName) == normalizedName &&
                (string.IsNullOrEmpty(normalizedPublisher) || Normalize(a.Publisher) == normalizedPublisher) &&
                (string.IsNullOrEmpty(normalizedVersion)   || Normalize(a.Version)   == normalizedVersion));
            if (m is not null) return m;
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Protection & metadata
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsProtectedApplication(InstalledApplication app)
    {
        // AxePower itself
        if (app.DisplayName.Contains("AxePower", StringComparison.OrdinalIgnoreCase))
            return true;
        // Non-removable AppX
        if (app.ApplicationType is ApplicationType.AppX or ApplicationType.MSIX &&
            !app.CanUninstall)
            return true;
        // Windows system components
        if (app.IsSystemComponent &&
            (app.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
             string.IsNullOrEmpty(app.Publisher)))
            return true;

        return false;
    }

    private static bool IsMicrosoftPublisher(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return false;
        return MicrosoftPublisherPrefixes.Any(p =>
            publisher.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static int ComputeUninstallerConfidence(InstalledApplication app)
    {
        if (app.ApplicationType == ApplicationType.Orphaned) return 30;
        if (app.ApplicationType == ApplicationType.MSI && !string.IsNullOrEmpty(app.ProductCode)) return 100;
        if (app.ApplicationType is ApplicationType.AppX or ApplicationType.MSIX) return 95;
        if (!string.IsNullOrEmpty(app.QuietUninstallString)) return 90;
        if (!string.IsNullOrEmpty(app.UninstallString)) return 75;
        return 50;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string GetString(RegistryKey key, string name)
        => key.GetValue(name) as string ?? string.Empty;

    private static int GetDword(RegistryKey key, string name)
        => key.GetValue(name) is int v ? v : 0;

    private static bool IsGuid(string s)
        => !string.IsNullOrWhiteSpace(s) &&
           Regex.IsMatch(s, @"^\{[0-9A-Fa-f\-]{36}\}$");

    private static string ExtractExecutablePath(string rawCommand)
    {
        if (string.IsNullOrWhiteSpace(rawCommand)) return string.Empty;
        rawCommand = rawCommand.Trim();

        // Quoted path
        if (rawCommand.StartsWith('"'))
        {
            var end = rawCommand.IndexOf('"', 1);
            return end > 1 ? rawCommand[1..end] : string.Empty;
        }

        // Unquoted: check for .exe in path if path has spaces
        var exeIdx = rawCommand.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
        {
            var candidate = rawCommand[..(exeIdx + 4)].Trim();
            if (File.Exists(candidate))
                return candidate;
        }

        // Unquoted fallback — take first token (up to first space)
        var space = rawCommand.IndexOf(' ');
        return space > 0 ? rawCommand[..space] : rawCommand;
    }

    private static string GenerateId(string primary, string secondary, string tertiary)
    {
        if (!string.IsNullOrEmpty(primary)) return primary;
        if (!string.IsNullOrEmpty(secondary)) return secondary;
        return string.IsNullOrEmpty(tertiary)
            ? Guid.NewGuid().ToString()
            : $"app-{Normalize(tertiary)}";
    }

    private static string Normalize(string s)
        => s is null ? string.Empty :
           Regex.Replace(s.ToLowerInvariant().Trim(), @"[\s\-_\.\(\)]+", "");

    private static string FormatAppxDisplayName(string packageName)
    {
        // Convert "Microsoft.WindowsCalculator" → "Windows Calculator"
        var parts = packageName.Split('.');
        if (parts.Length > 1)
        {
            var meaningful = parts.Last();
            // Insert spaces before capital letters
            return Regex.Replace(meaningful, @"(?<=[a-z])(?=[A-Z])", " ");
        }
        return packageName;
    }

    private static string? FindWinget()
    {
        // Check common winget locations
        var candidates = new[]
        {
            @"C:\Users\" + Environment.UserName +
            @"\AppData\Local\Microsoft\WindowsApps\winget.exe",
            @"C:\Program Files\WindowsApps\" +
            "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\\winget.exe",
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        // Try PATH
        try
        {
            var psi = new ProcessStartInfo("where", "winget")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(output) && File.Exists(output))
                return output;
        }
        catch { }

        return null;
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(7));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NonInteractive -NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(psi);
            if (process is null) return string.Empty;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);
            return await stdoutTask;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string[] ParseCsvLine(string line)
    {
        // Simple CSV parser — handles quoted fields
        var fields = new List<string>();
        bool inQuotes = false;
        var current = new StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
