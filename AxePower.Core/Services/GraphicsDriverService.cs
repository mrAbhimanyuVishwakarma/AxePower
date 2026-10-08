using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using AxePower.Core.Services.Processes;
using AxePower.Core.Services.SoftwareHub;

namespace AxePower.Core.Services;

public enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
    Unknown
}

/// <summary>One physical graphics adapter plus the official way to install/update its driver.</summary>
public class GraphicsAdapterInfo
{
    public string Name { get; set; } = string.Empty;
    public GpuVendor Vendor { get; set; }
    public string VendorName { get; set; } = string.Empty;

    /// <summary>Vendor-facing driver version (e.g. "566.36" for NVIDIA), not the raw Windows one.</summary>
    public string DriverVersion { get; set; } = string.Empty;
    public string DriverDate { get; set; } = string.Empty;
    public bool IsDedicated { get; set; }

    /// <summary>Official vendor tool that installs and updates this driver.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>Official vendor page — used only as a fallback when the Store path is unavailable.</summary>
    public string OfficialUrl { get; set; } = string.Empty;

    /// <summary>Microsoft Store product id of the vendor's own app (primary install path).</summary>
    public string StoreId { get; set; } = string.Empty;
    public string StoreAppName { get; set; } = string.Empty;

    /// <summary>Second Store app shipped by the same vendor (NVIDIA Control Panel).</summary>
    public string CompanionStoreId { get; set; } = string.Empty;
    public string CompanionAppName { get; set; } = string.Empty;

    public string AccentColorHex { get; set; } = "#60A5FA";

    /// <summary>Dedicated video memory in bytes; 0 when it could not be determined.</summary>
    public ulong VideoMemoryBytes { get; set; }

    /// <summary>
    /// e.g. "NVIDIA GeForce RTX 4060 8GB". The size is shown only for dedicated cards —
    /// an integrated GPU's figure is shared system memory, not VRAM, so quoting it misleads.
    /// </summary>
    public string NameWithMemory
    {
        get
        {
            if (VideoMemoryBytes == 0 || !IsDedicated) return Name;
            double gb = VideoMemoryBytes / 1024.0 / 1024.0 / 1024.0;
            return gb >= 1
                ? $"{Name} {gb:0.#}GB"
                : $"{Name} {VideoMemoryBytes / 1024.0 / 1024.0:0}MB";
        }
    }

    public bool UsesStore => !string.IsNullOrWhiteSpace(StoreId);
    public bool HasCompanionApp => !string.IsNullOrWhiteSpace(CompanionStoreId);

    /// <summary>True if this adapter is manufactured by NVIDIA.</summary>
    public bool IsNvidia => Vendor == GpuVendor.Nvidia;

    /// <summary>True if this adapter is a mobile/laptop GPU variant.</summary>
    public bool IsNotebook => Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) ||
                              Name.Contains("Notebook", StringComparison.OrdinalIgnoreCase) ||
                              Name.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ||
                              Name.Contains("Max-Q", StringComparison.OrdinalIgnoreCase);

    /// <summary>Shown only when there is no Store path, so the user knows where the driver comes from.</summary>
    public bool ShowOfficialSiteFallback => !UsesStore;
    public string TypeLabel => IsDedicated ? "DEDICATED" : "INTEGRATED";
    public string DriverDisplay => string.IsNullOrWhiteSpace(DriverVersion)
        ? "Driver version unavailable"
        : $"Driver {DriverVersion}";
}

public class NvidiaDriverRelease
{
    public string Version { get; set; } = string.Empty;
    public string ReleaseDate { get; set; } = string.Empty;
    public string Badge { get; set; } = string.Empty;
    public string BadgeColorHex { get; set; } = "#10B981";
    public string Description { get; set; } = string.Empty;
    public string DownloadUrlDesktop { get; set; } = string.Empty;
    public string DownloadUrlNotebook { get; set; } = string.Empty;

    public string GetDownloadUrl(bool isNotebook) => isNotebook ? DownloadUrlNotebook : DownloadUrlDesktop;
}

/// <summary>
/// Detects the machine's real graphics adapters and routes driver installs/updates to
/// official vendor sources only (winget publisher manifests or the vendor's own site).
/// </summary>
public class GraphicsDriverService
{
    private const string AmdDriversPageUrl = "https://www.amd.com/en/support/download/drivers.html";

    // The auto-detect installer is ~48 MB, so allow a generous transfer window
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(15) };

    /// <summary>Driver installers may only be fetched from the vendors' own download hosts.</summary>
    internal static readonly string[] NvidiaDownloadHosts = { "download.nvidia.com" };
    internal static readonly string[] AmdDownloadHosts = { "drivers.amd.com" };

    private readonly LogService _logService;
    private readonly IUserShellLauncher _launcher;

    public GraphicsDriverService(LogService logService, IUserShellLauncher? launcher = null)
    {
        _logService = logService;
        _launcher = launcher ?? new UserShellLauncher(logService);
    }

    /// <summary>
    /// Enumerates physical display adapters. Virtual adapters (spacedesk, Remote Desktop,
    /// Basic Display) are excluded so only hardware that has a vendor driver is offered.
    /// </summary>
    public List<GraphicsAdapterInfo> DetectAdapters()
    {
        var adapters = new List<GraphicsAdapterInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, DriverVersion, DriverDate FROM Win32_VideoController");

            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                string pnpId = obj["PNPDeviceID"]?.ToString() ?? string.Empty;
                string rawDriver = obj["DriverVersion"]?.ToString() ?? string.Empty;
                string rawDate = obj["DriverDate"]?.ToString() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(name)) continue;

                // Only real PCI display hardware has a vendor driver to update
                if (!pnpId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;

                var vendor = ResolveVendor(pnpId, name);
                if (vendor == GpuVendor.Unknown) continue;

                if (adapters.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

                var info = BuildAdapterInfo(vendor, name);
                info.DriverVersion = FormatDriverVersion(vendor, rawDriver);
                info.DriverDate = FormatDriverDate(rawDate);
                info.IsDedicated = IsDedicatedAdapter(vendor, name);
                info.VideoMemoryBytes = GetVideoMemoryBytes(name);
                adapters.Add(info);
            }
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[GraphicsDriver] WMI adapter detection failed: {ex.Message}");
        }

        // Fallback: If WMI fails (e.g. HRESULT 0x80010114) or returns no adapters, detect via Registry
        if (adapters.Count == 0)
        {
            DetectAdaptersFromRegistry(adapters);
        }

        // Dedicated GPU first — it is the one users update most often
        return adapters.OrderByDescending(a => a.IsDedicated).ThenBy(a => a.VendorName).ToList();
    }

    private void DetectAdaptersFromRegistry(List<GraphicsAdapterInfo> adapters)
    {
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(classKey);
            if (root == null) return;

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                // Driver instances are numeric subkeys (0000, 0001, ...)
                if (subKeyName.Length != 4 || !subKeyName.All(char.IsDigit)) continue;

                using var instance = root.OpenSubKey(subKeyName);
                if (instance == null) continue;

                string name = instance.GetValue("DriverDesc") as string ?? string.Empty;
                string pnpId = instance.GetValue("MatchingDeviceId") as string ?? string.Empty;
                string rawDriver = instance.GetValue("DriverVersion") as string ?? string.Empty;
                string rawDate = instance.GetValue("DriverDate") as string ?? string.Empty;

                if (string.IsNullOrWhiteSpace(name)) continue;

                // Only real PCI display hardware has a vendor driver to update
                if (!pnpId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;

                var vendor = ResolveVendor(pnpId, name);
                if (vendor == GpuVendor.Unknown) continue;

                if (adapters.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

                var info = BuildAdapterInfo(vendor, name);
                info.DriverVersion = FormatDriverVersion(vendor, rawDriver);
                info.DriverDate = FormatDriverDate(rawDate);
                info.IsDedicated = IsDedicatedAdapter(vendor, name);
                info.VideoMemoryBytes = GetVideoMemoryBytes(name);
                adapters.Add(info);
            }
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[GraphicsDriver] Registry adapter detection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads dedicated VRAM from the display class registry key. Win32_VideoController's
    /// AdapterRAM is a 32-bit value and wraps on cards with 4 GB or more, so the driver's
    /// own qwMemorySize entry is used instead.
    /// </summary>
    private static ulong GetVideoMemoryBytes(string adapterName)
    {
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(classKey);
            if (root == null) return 0;

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                // Driver instances are the numeric subkeys (0000, 0001, ...)
                if (subKeyName.Length != 4 || !subKeyName.All(char.IsDigit)) continue;

                using var instance = root.OpenSubKey(subKeyName);
                if (instance == null) continue;

                string? desc = instance.GetValue("DriverDesc") as string;
                if (string.IsNullOrEmpty(desc) || !desc.Equals(adapterName, StringComparison.OrdinalIgnoreCase))
                    continue;

                object? size = instance.GetValue("HardwareInformation.qwMemorySize");
                if (size is long qword && qword > 0) return (ulong)qword;

                // Older drivers store a 32-bit MemorySize instead
                object? legacy = instance.GetValue("HardwareInformation.MemorySize");
                if (legacy is byte[] bytes && bytes.Length >= 4)
                    return BitConverter.ToUInt32(bytes, 0);
                if (legacy is int dword && dword > 0) return (uint)dword;
            }
        }
        catch (Exception caught) when (ExpectedFailures.Registry(caught)) { /* Unreadable: the adapter's memory is shown as unknown. */ }

        return 0;
    }

    private static GpuVendor ResolveVendor(string pnpId, string name)
    {
        // PCI vendor IDs are authoritative; the adapter name is only a fallback
        if (pnpId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Nvidia;
        if (pnpId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ||
            pnpId.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Amd;
        if (pnpId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Intel;

        if (Regex.IsMatch(name, "NVIDIA|GeForce|RTX|GTX|Quadro", RegexOptions.IgnoreCase)) return GpuVendor.Nvidia;
        if (Regex.IsMatch(name, @"\bAMD\b|Radeon|Vega|FirePro", RegexOptions.IgnoreCase)) return GpuVendor.Amd;
        if (Regex.IsMatch(name, @"Intel|\bIris\b|\bUHD\b|\bHD Graphics\b", RegexOptions.IgnoreCase)) return GpuVendor.Intel;

        return GpuVendor.Unknown;
    }

    private static bool IsDedicatedAdapter(GpuVendor vendor, string name) => vendor switch
    {
        GpuVendor.Nvidia => true,
        GpuVendor.Intel => Regex.IsMatch(name, @"\bArc\b", RegexOptions.IgnoreCase),
        // AMD ships both: RX/Pro cards are discrete, APU graphics are integrated
        GpuVendor.Amd => Regex.IsMatch(name, @"\bRX\b|Radeon Pro|FirePro", RegexOptions.IgnoreCase),
        _ => false
    };

    private static GraphicsAdapterInfo BuildAdapterInfo(GpuVendor vendor, string name) => vendor switch
    {
        GpuVendor.Nvidia => new GraphicsAdapterInfo
        {
            Name = name,
            Vendor = vendor,
            VendorName = "NVIDIA",
            ToolName = "NVIDIA App",
            StoreId = "XP8CLZL93F5Z4P",              // NVIDIA App
            StoreAppName = "NVIDIA App",
            CompanionStoreId = "9NF8H0H7WMLT",       // NVIDIA Control Panel
            CompanionAppName = "NVIDIA Control Panel",
            OfficialUrl = "https://www.nvidia.com/en-us/software/nvidia-app/",
            AccentColorHex = "#76B900"
        },
        GpuVendor.Amd => new GraphicsAdapterInfo
        {
            Name = name,
            Vendor = vendor,
            // AMD publishes no Microsoft Store app, so this one uses AMD's official
            // Auto-Detect and Install tool, which picks the right Adrenalin driver itself
            VendorName = "AMD",
            ToolName = "AMD Auto-Detect and Install",
            OfficialUrl = "https://www.amd.com/en/support/download/drivers.html",
            AccentColorHex = "#ED1C24"
        },
        GpuVendor.Intel => new GraphicsAdapterInfo
        {
            Name = name,
            Vendor = vendor,
            VendorName = "Intel",
            ToolName = "Intel Graphics Command Center",
            StoreId = "9PLFNLNT3G5G",                // Intel Graphics Command Center
            StoreAppName = "Intel Graphics Command Center",
            OfficialUrl = "https://www.intel.com/content/www/us/en/support/detect.html",
            AccentColorHex = "#0068B5"
        },
        _ => new GraphicsAdapterInfo { Name = name, Vendor = vendor, VendorName = "Graphics" }
    };

    /// <summary>
    /// NVIDIA's Windows driver version encodes the marketing version in its last five digits
    /// (32.0.15.6636 -> 566.36). Other vendors use their raw version as-is.
    /// </summary>
    private static string FormatDriverVersion(GpuVendor vendor, string rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion)) return string.Empty;
        if (vendor != GpuVendor.Nvidia) return rawVersion;

        string digits = rawVersion.Replace(".", string.Empty);
        if (digits.Length < 5 || !digits.All(char.IsDigit)) return rawVersion;

        string last5 = digits[^5..];
        return $"{last5[..3]}.{last5[3..]}";
    }

    /// <summary>Formats driver date from WMI (yyyyMMdd...) or Registry (e.g. 8-20-2026).</summary>
    private static string FormatDriverDate(string rawDate)
    {
        if (string.IsNullOrWhiteSpace(rawDate)) return string.Empty;

        // WMI returns CIM_DATETIME ("20240917000000.000000-000")
        if (rawDate.Length >= 8 && rawDate[..8].All(char.IsDigit))
        {
            if (DateTime.TryParseExact(rawDate[..8], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var wmiParsed))
            {
                return wmiParsed.ToString("MMM dd, yyyy");
            }
        }

        // Registry returns formats like "8-20-2026" or "2026-08-20"
        if (DateTime.TryParse(rawDate, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var regParsed))
        {
            return regParsed.ToString("MMM dd, yyyy");
        }

        return rawDate;
    }

    /// <summary>
    /// Installs or updates the vendor's graphics app. Every vendor with a Microsoft Store app
    /// goes through the Store; AMD (which ships none) downloads and runs its official
    /// Auto-Detect and Install tool. The vendor's web page is used only if those fail.
    /// </summary>
    public async Task<(bool Success, string Message)> InstallOrUpdateAsync(
        GraphicsAdapterInfo adapter, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (adapter.UsesStore)
        {
            if (OpenStore(adapter.StoreId, adapter.StoreAppName))
                return (true, $"Opening {adapter.StoreAppName} in the Microsoft Store — install or update it there.");

            // Store unavailable (not installed / protocol unregistered) — fall back to the official page
            bool opened = OpenUrl(adapter.OfficialUrl, adapter.VendorName);
            return opened
                ? (true, $"Microsoft Store unavailable. Opened the official {adapter.VendorName} download page instead.")
                : (false, $"Could not reach the Microsoft Store or the official {adapter.VendorName} page.");
        }

        return await RunAmdAutoDetectAsync(adapter, progress, ct);
    }

    /// <summary>
    /// AMD ships no Store app, so this fetches the current Auto-Detect and Install tool
    /// straight from AMD's official download page and runs it. The URL is always read from
    /// the page (never hardcoded) so the newest build is used as AMD publishes it.
    /// </summary>
    private async Task<(bool Success, string Message)> RunAmdAutoDetectAsync(
        GraphicsAdapterInfo adapter, IProgress<string>? progress, CancellationToken ct)
    {
        string? installerUrl = null;
        try
        {
            progress?.Report("\n[Graphics Drivers] Looking up AMD's current Auto-Detect and Install tool...");
            installerUrl = await ResolveLatestAmdInstallerUrlAsync(ct);
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[GraphicsDriver] AMD installer lookup failed: {ex.Message}");
        }

        // The page is untrusted input: only an https installer on AMD's driver host, with a plain
        // .exe file name, is downloaded.
        if (!PackageInput.TryDownloadUrl(installerUrl, AmdDownloadHosts, out var installerUri) ||
            PackageInput.SafeDownloadFileName(installerUri, ".exe") is not string fileName)
        {
            OpenUrl(adapter.OfficialUrl, adapter.VendorName);
            return (true, "Could not read AMD's download page. Opened the official AMD driver site instead.");
        }

        try
        {
            string targetPath = Path.Combine(Path.GetTempPath(), fileName);

            progress?.Report($"[Graphics Drivers] Downloading {fileName} from AMD...");

            using (var response = await SendAmdRequestAsync(installerUri.AbsoluteUri, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(targetPath);
                await source.CopyToAsync(target, ct);
            }

            // AMD's CDN answers unknown paths with an HTML page and a 200 status, so confirm
            // this really is a Windows executable before running anything
            if (!IsWindowsExecutable(targetPath))
            {
                try { File.Delete(targetPath); } catch (Exception caught) when (ExpectedFailures.FileSystem(caught)) { /* Best-effort: the rejected download stays in the temp folder. */ }
                OpenUrl(adapter.OfficialUrl, adapter.VendorName);
                return (false, "AMD returned an unexpected file. Opened the official AMD driver page instead.");
            }

            progress?.Report($"[Graphics Drivers] Launching the AMD Auto-Detect and Install tool...");
            var launched = _launcher.RunInstaller(targetPath);
            if (!launched.Started)
            {
                OpenUrl(adapter.OfficialUrl, adapter.VendorName);
                return (false, $"The AMD tool did not start ({launched.Message}). Opened the official AMD driver page instead.");
            }

            _logService.LogInfo($"[GraphicsDriver] Launched AMD auto-detect installer: {installerUrl}");
            return (true, "AMD Auto-Detect and Install tool launched — it will fetch the latest driver for your GPU.");
        }
        catch (Exception ex)
        {
            _logService.LogError("[GraphicsDriver] AMD auto-detect download failed.", ex);
            OpenUrl(adapter.OfficialUrl, adapter.VendorName);
            return (false, $"AMD download failed ({ex.Message}). Opened the official AMD driver page instead.");
        }
    }

    /// <summary>Reads AMD's official drivers page and returns the current auto-detect installer URL.</summary>
    private async Task<string?> ResolveLatestAmdInstallerUrlAsync(CancellationToken ct)
    {
        using var response = await SendAmdRequestAsync(AmdDriversPageUrl, ct);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(ct);

        var matches = Regex.Matches(html, @"https://drivers\.amd\.com/drivers/[^""'\s<>]+\.exe");
        if (matches.Count == 0) return null;

        // Prefer the auto-detect ("minimalsetup") build, else the first installer offered
        var autoDetect = matches.Select(m => m.Value)
            .FirstOrDefault(v => v.Contains("minimalsetup", StringComparison.OrdinalIgnoreCase));
        return autoDetect ?? matches[0].Value;
    }

    /// <summary>AMD's CDN rejects requests without a browser user agent and page referer.</summary>
    private static Task<HttpResponseMessage> SendAmdRequestAsync(string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Referer", AmdDriversPageUrl);
        return HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>True when the file starts with the "MZ" PE signature.</summary>
    private static bool IsWindowsExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    public void OpenCompanionApp(GraphicsAdapterInfo adapter)
    {
        if (!adapter.HasCompanionApp) return;
        if (!OpenStore(adapter.CompanionStoreId, adapter.CompanionAppName))
            OpenUrl(adapter.OfficialUrl, adapter.VendorName);
    }

    public void OpenOfficialPage(GraphicsAdapterInfo adapter) => OpenUrl(adapter.OfficialUrl, adapter.VendorName);

    private bool OpenStore(string productId, string label)
    {
        if (string.IsNullOrWhiteSpace(productId) || !Regex.IsMatch(productId, "^[A-Za-z0-9]{12}$")) return false;
        var result = _launcher.OpenUri($"ms-windows-store://pdp/?ProductId={productId}");
        if (result.Started)
        {
            _logService.LogInfo($"[GraphicsDriver] Opened Microsoft Store for {label} ({productId}).");
            return true;
        }
        // Store app missing (LTSC/Server) or protocol not registered
        _logService.LogWarning($"[GraphicsDriver] Microsoft Store unavailable for {label}: {result.Message}");
        return false;
    }

    private bool OpenUrl(string url, string label)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            var result = _launcher.OpenUri(url);
            if (!result.Started) _logService.LogWarning($"[GraphicsDriver] Could not open {label}: {result.Message}");
            else _logService.LogInfo($"[GraphicsDriver] Opened {label}: {url}");
            return result.Started;
        }
        catch (Exception ex)
        {
            _logService.LogError($"[GraphicsDriver] Could not open {label} ({url}).", ex);
            return false;
        }
    }

    /// <summary>
    /// Returns a list of curated stable releases and popular rollback versions for NVIDIA GeForce cards.
    /// </summary>
    public IReadOnlyList<NvidiaDriverRelease> GetCuratedNvidiaDrivers()
    {
        return new List<NvidiaDriverRelease>
        {
            new NvidiaDriverRelease
            {
                Version = "572.70",
                ReleaseDate = "Feb 2025",
                Badge = "LATEST GAME READY",
                BadgeColorHex = "#3B82F6",
                Description = "Latest Game Ready driver supporting RTX 50 series, Monster Hunter Wilds & DLSS 4 updates.",
                DownloadUrlDesktop = "https://us.download.nvidia.com/Windows/572.70/572.70-desktop-win10-win11-64bit-international-dch-whql.exe",
                DownloadUrlNotebook = "https://us.download.nvidia.com/Windows/572.70/572.70-notebook-win10-win11-64bit-international-dch-whql.exe"
            },
            new NvidiaDriverRelease
            {
                Version = "566.36",
                ReleaseDate = "Dec 2024",
                Badge = "RECOMMENDED STABLE",
                BadgeColorHex = "#10B981",
                Description = "Widely tested rock-solid stable release. Minimal micro-stuttering and optimal frame pacing.",
                DownloadUrlDesktop = "https://us.download.nvidia.com/Windows/566.36/566.36-desktop-win10-win11-64bit-international-dch-whql.exe",
                DownloadUrlNotebook = "https://us.download.nvidia.com/Windows/566.36/566.36-notebook-win10-win11-64bit-international-dch-whql.exe"
            },
            new NvidiaDriverRelease
            {
                Version = "566.14",
                ReleaseDate = "Nov 2024",
                Badge = "PREVIOUS STABLE",
                BadgeColorHex = "#8B5CF6",
                Description = "STALKER 2 & Microsoft Flight Simulator 2024 release with DLSS 3 frame generation fixes.",
                DownloadUrlDesktop = "https://us.download.nvidia.com/Windows/566.14/566.14-desktop-win10-win11-64bit-international-dch-whql.exe",
                DownloadUrlNotebook = "https://us.download.nvidia.com/Windows/566.14/566.14-notebook-win10-win11-64bit-international-dch-whql.exe"
            },
            new NvidiaDriverRelease
            {
                Version = "561.09",
                ReleaseDate = "Sep 2024",
                Badge = "MILESTONE BUILD",
                BadgeColorHex = "#06B6D4",
                Description = "Final Fantasy XVI & God of War Ragnarok driver. Highly stable baseline for RTX 30/40 cards.",
                DownloadUrlDesktop = "https://us.download.nvidia.com/Windows/561.09/561.09-desktop-win10-win11-64bit-international-dch-whql.exe",
                DownloadUrlNotebook = "https://us.download.nvidia.com/Windows/561.09/561.09-notebook-win10-win11-64bit-international-dch-whql.exe"
            },
            new NvidiaDriverRelease
            {
                Version = "560.94",
                ReleaseDate = "Aug 2024",
                Badge = "POPULAR ROLLBACK",
                BadgeColorHex = "#F59E0B",
                Description = "Black Myth: Wukong & Star Wars Outlaws benchmark driver with proven reliability.",
                DownloadUrlDesktop = "https://us.download.nvidia.com/Windows/560.94/560.94-desktop-win10-win11-64bit-international-dch-whql.exe",
                DownloadUrlNotebook = "https://us.download.nvidia.com/Windows/560.94/560.94-notebook-win10-win11-64bit-international-dch-whql.exe"
            },
            new NvidiaDriverRelease
            {
                Version = "552.22",
                ReleaseDate = "Apr 2024",
                Badge = "LOW-LATENCY CLASSIC",
                BadgeColorHex = "#EC4899",
                Description = "Renowned low DPC latency build. Highly favored for older titles, emulation, and VR stability.",
                DownloadUrlDesktop = "https://us.download.nvidia.com/Windows/552.22/552.22-desktop-win10-win11-64bit-international-dch-whql.exe",
                DownloadUrlNotebook = "https://us.download.nvidia.com/Windows/552.22/552.22-notebook-win10-win11-64bit-international-dch-whql.exe"
            },
            new NvidiaDriverRelease
            {
                Version = "537.58",
                ReleaseDate = "Oct 2023",
                Badge = "ESPORTS BENCHMARK",
                BadgeColorHex = "#EF4444",
                Description = "Legendary CS2 / Valorant competitive driver praised for maximum 1% low FPS consistency.",
                DownloadUrlDesktop = "https://us.download.nvidia.com/Windows/537.58/537.58-desktop-win10-win11-64bit-international-dch-whql.exe",
                DownloadUrlNotebook = "https://us.download.nvidia.com/Windows/537.58/537.58-notebook-win10-win11-64bit-international-dch-whql.exe"
            }
        };
    }

    /// <summary>
    /// Opens the official NVIDIA Advanced Driver Search page for beta, archive, and legacy drivers.
    /// </summary>
    public void OpenNvidiaAdvancedSearch()
    {
        OpenUrl("https://www.nvidia.com/download/find.aspx", "NVIDIA Advanced Driver Search");
    }

    /// <summary>
    /// Downloads an official NVIDIA driver installer from official NVIDIA servers with progress reporting and runs it.
    /// </summary>
    public async Task<(bool Success, string Message)> DownloadAndRunNvidiaDriverAsync(
        string downloadUrl, string version, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!PackageInput.TryDownloadUrl(downloadUrl, NvidiaDownloadHosts, out var downloadUri) ||
            PackageInput.SafeDownloadFileName(downloadUri, ".exe") is not string fileName)
            return (false, "Invalid download URL.");
        downloadUrl = downloadUri.AbsoluteUri;

        string downloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "Downloads");
        Directory.CreateDirectory(downloadDir);
        string targetPath = Path.Combine(downloadDir, fileName);

        try
        {
            if (File.Exists(targetPath))
            {
                progress?.Report($"\n[NVIDIA Driver] Found already downloaded installer: {fileName}");
                progress?.Report($"[NVIDIA Driver] Launching NVIDIA {version} installer...");
                var cached = _launcher.RunInstaller(targetPath);
                return cached.Started
                    ? (true, $"Launched NVIDIA Driver {version} installer.")
                    : (false, $"The NVIDIA installer did not start: {cached.Message}");
            }

            progress?.Report($"\n[NVIDIA Driver] Initiating official download for NVIDIA {version} from NVIDIA CDN...");
            progress?.Report($"[Source] {downloadUrl}");

            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var sourceStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            int lastReportPercent = -1;

            while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    int percent = (int)((totalRead * 100) / totalBytes.Value);
                    if (percent != lastReportPercent && percent % 10 == 0)
                    {
                        lastReportPercent = percent;
                        double mbRead = totalRead / (1024.0 * 1024.0);
                        double mbTotal = totalBytes.Value / (1024.0 * 1024.0);
                        progress?.Report($"[Download Progress] {percent}% ({mbRead:0.#} MB / {mbTotal:0.#} MB)");
                    }
                }
            }

            fileStream.Flush();
            fileStream.Close();

            if (!IsWindowsExecutable(targetPath))
            {
                try { File.Delete(targetPath); } catch (Exception caught) when (ExpectedFailures.FileSystem(caught)) { /* Best-effort: the rejected download stays in AxePower's Downloads folder. */ }
                return (false, "Downloaded file failed PE verification.");
            }

            progress?.Report($"\n[NVIDIA Driver] ✓ Download completed ({totalRead / (1024 * 1024)} MB).");
            progress?.Report($"[NVIDIA Driver] Launching NVIDIA {version} installer setup...");
            var launched = _launcher.RunInstaller(targetPath);
            if (!launched.Started) return (false, $"Downloaded, but the NVIDIA installer did not start: {launched.Message}");

            _logService.LogInfo($"[GraphicsDriver] Launched NVIDIA driver installer: {targetPath}");
            return (true, $"Downloaded and launched NVIDIA Driver {version} installer.");
        }
        catch (Exception ex)
        {
            _logService.LogError($"[GraphicsDriver] Failed downloading NVIDIA driver {version}: {ex.Message}", ex);
            progress?.Report($"\n[ERROR] Download failed: {ex.Message}");
            return (false, $"Download failed: {ex.Message}");
        }
    }
}
