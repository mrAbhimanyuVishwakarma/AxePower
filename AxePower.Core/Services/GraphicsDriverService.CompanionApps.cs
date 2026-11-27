using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Processes;
using AxePower.Core.Services.SoftwareHub;
using Windows.Management.Deployment;

namespace AxePower.Core.Services;

public partial class GraphicsDriverService
{
    private const string NvidiaAppPage = "https://www.nvidia.com/en-us/software/nvidia-app/";
    private const string NvidiaControlPanelStoreId = "9NF8H0H7WMLT";
    private const string NvidiaControlPanelFamily = "NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj";
    private const string IntelGraphicsSoftwareFamily = "AppUp.IntelArcSoftware_8j3eq9eme6ctt";
    private const string IntelLegacyGraphicsPage = "https://www.intel.com/content/www/us/en/download/864990/intel-11th-14th-gen-processor-graphics-windows.html";
    private const string IntelArcGraphicsPage = "https://www.intel.com/content/www/us/en/download/785597/intel-arc-iris-xe-graphics-windows.html";
    private const long MaxInstallerBytes = 2L * 1024 * 1024 * 1024;

    private static readonly string[] NvidiaAppHosts = { "us.download.nvidia.com" };
    private static readonly string[] IntelDownloadHosts = { "downloadmirror.intel.com" };
    private static readonly HttpClient InstallerClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<(bool Success, string Message)> OpenControlPanelAsync(
        GraphicsAdapterInfo adapter, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!adapter.IsNvidia) return (false, "An NVIDIA adapter is required.");

        try
        {
            bool? opened = await TryOpenControlPanelAsync();
            if (opened == true) return (true, "Opened NVIDIA Control Panel.");

            if (opened is null)
            {
                progress?.Report("Installing NVIDIA Control Panel from Microsoft Store...");
                var command = new ProcessCommand(AllowedTool.WinGet, new[]
                {
                    "install", "--id", NvidiaControlPanelStoreId, "--exact", "--source", "msstore",
                    "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
                }, TimeSpan.FromMinutes(10));
                var result = await _runner.RunAsync(command, line => progress?.Report(line.Text), ct);
                ct.ThrowIfCancellationRequested();
                if (result.ExitedWithAny(0) && await OpenAfterSetupAsync(TryOpenControlPanelAsync, ct))
                    return (true, "Installed and opened NVIDIA Control Panel.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logService.LogWarning("[GraphicsDriver] NVIDIA Control Panel could not open or install.", ex);
        }

        bool storeOpened = OpenStore(NvidiaControlPanelStoreId, "NVIDIA Control Panel") ||
            OpenUrl($"https://apps.microsoft.com/detail/{NvidiaControlPanelStoreId}", "NVIDIA Control Panel");
        return storeOpened
            ? (true, "Opened the NVIDIA Control Panel listing in Microsoft Store.")
            : (false, "Could not open NVIDIA Control Panel or its Microsoft Store listing.");
    }

    private async Task<(bool Success, string Message)> OpenVendorAppAsync(
        GraphicsAdapterInfo adapter, IProgress<string>? progress, CancellationToken ct)
    {
        string fallback = adapter.OfficialUrl;
        try
        {
            bool? opened = await TryOpenVendorAppAsync(adapter.Vendor);
            if (opened == true) return (true, $"Opened {adapter.ToolName}.");

            if (opened is null)
            {
                string? intelPage = adapter.Vendor == GpuVendor.Intel && RuntimeInformation.OSArchitecture == Architecture.X64
                    && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19045)
                    ? await Task.Run(() => SelectIntelDriverPage(adapter.Name), ct)
                    : null;
                fallback = intelPage ?? fallback;
                var source = await ResolveCompanionInstallerAsync(adapter, intelPage, ct);
                if (source is not null)
                {
                    string file = await DownloadVerifiedInstallerAsync(source, progress, ct);
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Opening {adapter.ToolName} setup...");
                    using var setup = Process.Start(new ProcessStartInfo(file)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(file)
                    });
                    if (setup is not null)
                    {
                        await setup.WaitForExitAsync(ct);
                        if (setup.ExitCode is 1641 or 3010)
                            return (true, $"Windows needs to restart before {adapter.ToolName} can open.");

                        if (setup.ExitCode == 0 &&
                            await OpenAfterSetupAsync(() => TryOpenVendorAppAsync(adapter.Vendor), ct))
                            return (true, $"Installed and opened {adapter.ToolName}.");

                        _logService.LogWarning($"[GraphicsDriver] {adapter.ToolName} was unavailable after setup (exit code {setup.ExitCode}).");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logService.LogWarning($"[GraphicsDriver] {adapter.ToolName} could not open or install.", ex);
        }

        ct.ThrowIfCancellationRequested();
        return OpenUrl(fallback, adapter.VendorName)
            ? (true, $"{adapter.ToolName} did not open. Opened {adapter.VendorName}'s download page.")
            : (false, $"Could not open {adapter.ToolName} or {adapter.VendorName}'s download page.");
    }

    private Task<bool?> TryOpenVendorAppAsync(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => Task.FromResult(TryOpenProgram(
            FindVendorProgram(@"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe") ??
            FindVendorProgram(@"NVIDIA Corporation\NVIDIA App\NVIDIA App.exe"))),
        GpuVendor.Amd => Task.FromResult(TryOpenProgram(
            FindVendorProgram(@"AMD\CNext\CNext\RadeonSoftware.exe") ??
            FindVendorProgram(@"AMD\CNext\CNext\cnext.exe"))),
        GpuVendor.Intel => TryOpenPackageAsync(IntelGraphicsSoftwareFamily, "INTELGRAPHICSSOFTWARE"),
        _ => Task.FromResult<bool?>(null)
    };

    private async Task<bool?> TryOpenControlPanelAsync()
    {
        bool? packaged = await TryOpenPackageAsync(NvidiaControlPanelFamily);
        if (packaged == true) return true;
        return TryOpenProgram(FindVendorProgram(@"NVIDIA Corporation\Control Panel Client\nvcplui.exe")) ?? packaged;
    }

    private bool? TryOpenProgram(string? path) => path is null ? null : _launcher.OpenProgram(path).Started;

    private static string? FindVendorProgram(string relativePath)
    {
        var folders = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 };
        return folders.Select(Environment.GetFolderPath).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(path, relativePath)).FirstOrDefault(File.Exists);
    }

    // Launch through Windows package activation with the current user's package registration.
    // A missing registration can be installed. An installed app that fails activation goes to the vendor page.
    private static async Task<bool?> TryOpenPackageAsync(string family, string? applicationId = null)
    {
        foreach (var package in new PackageManager().FindPackagesForUser(string.Empty, family))
        {
            var apps = await package.GetAppListEntriesAsync();
            var app = apps.FirstOrDefault(entry => applicationId is null ||
                entry.AppUserModelId.Equals($"{family}!{applicationId}", StringComparison.OrdinalIgnoreCase));
            if (app is not null) return await app.LaunchAsync();
        }
        return null;
    }

    private static async Task<bool> OpenAfterSetupAsync(Func<Task<bool?>> open, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (await open() == true) return true;
            if (attempt < 5) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return false;
    }

    private sealed record InstallerSource(string Url, string Page, string[] Hosts, string Publisher);

    private async Task<InstallerSource?> ResolveCompanionInstallerAsync(
        GraphicsAdapterInfo adapter, string? intelPage, CancellationToken ct)
    {
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookup.CancelAfter(TimeSpan.FromSeconds(20));
        switch (adapter.Vendor)
        {
            case GpuVendor.Nvidia:
                string architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
                string nvidiaHtml = await HttpClient.GetStringAsync(NvidiaAppPage + "#download-now", lookup.Token);
                string nvidiaUrl = FindInstallerLink(nvidiaHtml,
                    $@"https://us\.download\.nvidia\.com/nvapp/client/[^""'\s<>]+/NVIDIA_app_v[\d.]+_{architecture}\.exe");
                return nvidiaUrl.Length == 0 ? null : new(nvidiaUrl, NvidiaAppPage, NvidiaAppHosts, "NVIDIA Corporation");

            case GpuVendor.Amd when RuntimeInformation.OSArchitecture == Architecture.X64:
                string? amdUrl = await ResolveLatestAmdInstallerUrlAsync(lookup.Token);
                return amdUrl is null ? null : new(amdUrl, AmdDriversPageUrl, AmdDownloadHosts, "Advanced Micro Devices");

            case GpuVendor.Intel when intelPage is not null:
                string intelHtml = await GetIntelDriverPageAsync(intelPage, lookup.Token);
                string? intelUrl = FindIntelInstallerLink(intelHtml, adapter.DriverVersion);
                return intelUrl is null ? null : new(intelUrl, intelPage, IntelDownloadHosts, "Intel Corporation");

            default:
                return null;
        }
    }

    private static async Task<string> GetIntelDriverPageAsync(string page, CancellationToken ct)
    {
        try
        {
            using var primary = CancellationTokenSource.CreateLinkedTokenSource(ct);
            primary.CancelAfter(TimeSpan.FromSeconds(8));
            return await HttpClient.GetStringAsync(page, primary.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Intel publishes the same download record on its localized site. The installer still comes
            // from Intel's download host and must pass the same publisher verification.
            string localized = page.Replace("www.intel.com/content/www/us/en/", "www.intel.cn/content/www/cn/zh/", StringComparison.Ordinal);
            return await HttpClient.GetStringAsync(localized, ct);
        }
    }

    internal static string? FindIntelInstallerLink(string html, string installedDriver)
    {
        Version? floor = Version.TryParse(installedDriver, out var installed) && installed.Revision >= 0
            ? new Version(installed.Build, installed.Revision)
            : null;

        return Regex.Matches(html, @"https://downloadmirror\.intel\.com/\d+/gfx_win_(?<version>\d+\.\d+)\.exe",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => (Url: match.Value, Version: Version.Parse(match.Groups["version"].Value)))
            .Where(match => floor is null || match.Version >= floor)
            .OrderByDescending(match => match.Version)
            .Select(match => match.Url)
            .FirstOrDefault();
    }

    private static string? SelectIntelDriverPage(string adapterName)
    {
        if (Regex.IsMatch(adapterName, @"\bArc\b", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(adapterName, @"\bArc(?:\(TM\)|™)?\s+Pro\b", RegexOptions.IgnoreCase))
            return IntelArcGraphicsPage;

        if (!Regex.IsMatch(adapterName, @"\b(?:UHD Graphics|Iris(?:\(R\))?\s+Xe)\b", RegexOptions.IgnoreCase))
            return null;

        using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
        using var processors = searcher.Get();
        foreach (ManagementObject processor in processors)
        {
            using (processor)
            {
                if (Regex.IsMatch(processor["Name"]?.ToString() ?? "", @"\bi[3579]-1[1-4]\d{3}", RegexOptions.IgnoreCase))
                    return IntelLegacyGraphicsPage;
            }
        }
        return null;
    }

    private static string FindInstallerLink(string html, string pattern) =>
        Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Value;

    private async Task<string> DownloadVerifiedInstallerAsync(
        InstallerSource source, IProgress<string>? progress, CancellationToken ct)
    {
        if (!PackageInput.TryDownloadUrl(source.Url, source.Hosts, out var uri) ||
            PackageInput.SafeDownloadFileName(uri, ".exe") is not string fileName)
            throw new InvalidDataException("The vendor returned an invalid installer URL.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri(source.Page);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0");
        using var response = await InstallerClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? expectedBytes = response.Content.Headers.ContentLength;
        if (expectedBytes is <= 0 or > MaxInstallerBytes)
            throw new InvalidDataException("The installer download has an unexpected size.");

        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AxePower", "Downloads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        try
        {
            using var download = CancellationTokenSource.CreateLinkedTokenSource(ct);
            download.CancelAfter(TimeSpan.FromMinutes(15));
            progress?.Report($"Downloading {fileName}...");
            long copied = 0;
            int reported = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(download.Token))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), download.Token)) != 0)
                {
                    copied += read;
                    if (copied > MaxInstallerBytes) throw new InvalidDataException("The installer exceeded the download size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), download.Token);
                    if (expectedBytes is long size && copied * 100 / size >= reported + 10)
                    {
                        reported = (int)(copied * 100 / size);
                        progress?.Report($"Downloaded {reported}%...");
                    }
                }
            }

            if (expectedBytes is long length && copied != length)
                throw new InvalidDataException("The installer download did not complete.");
            var signature = await Task.Run(() => _authenticode.Verify(path, source.Publisher), ct);
            if (!signature.IsValid)
                throw new InvalidDataException($"The installer signature could not be verified: {signature.FailureReason}");

            return path;
        }
        catch
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ExpectedFailures.FileSystem(ex))
            {
                // A locked temporary installer can be left behind without masking the original failure.
            }
            throw;
        }
    }
}
