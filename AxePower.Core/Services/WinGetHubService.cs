using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class WinGetHubService : IWinGetHubService
{
    private readonly LogService _log;
    private readonly List<SoftwarePackageItem> _packages = new();

    public WinGetHubService(LogService log)
    {
        _log = log;
        InitializeCatalog();
    }

    private void InitializeCatalog()
    {
        _packages.Clear();

        // 1. Runtimes & DirectX
        _packages.Add(new SoftwarePackageItem
        {
            Id = "vcredist",
            WinGetId = "Microsoft.VCRedist.2015+.x64",
            Name = "Visual C++ All-in-One (2015-2022)",
            Description = "Required runtime library for almost all modern PC games and 3D engines.",
            Category = PackageCategory.Runtimes,
            Glyph = "\uE8B8",
            WebUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "directx",
            WinGetId = "Microsoft.DirectX",
            Name = "DirectX End-User Runtime",
            Description = "Legacy and modern DirectX 9/10/11 libraries for vintage and modern games.",
            Category = PackageCategory.Runtimes,
            Glyph = "\uE8B8",
            WebUrl = "https://www.microsoft.com/en-us/download/details.aspx?id=35"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "dotnet8",
            WinGetId = "Microsoft.DotNet.DesktopRuntime.8",
            Name = ".NET 8 Desktop Runtime",
            Description = "High-performance runtime for modern Windows desktop software and tools.",
            Category = PackageCategory.Runtimes,
            Glyph = "\uE8B8",
            WebUrl = "https://dotnet.microsoft.com/download/dotnet/8.0"
        });

        // 2. Launchers & Gaming
        _packages.Add(new SoftwarePackageItem
        {
            Id = "steam",
            WinGetId = "Valve.Steam",
            Name = "Steam",
            Description = "The ultimate digital game distribution and gaming community platform.",
            Category = PackageCategory.Gaming,
            Glyph = "\uE7FC",
            LaunchProtocolOrExe = "steam://",
            WebUrl = "https://store.steampowered.com/about/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "epic",
            WinGetId = "EpicGames.EpicGamesLauncher",
            Name = "Epic Games Launcher",
            Description = "Epic Games storefront, Unreal Engine hub, and weekly free games.",
            Category = PackageCategory.Gaming,
            Glyph = "\uE7FC",
            LaunchProtocolOrExe = "com.epicgames.launcher://",
            WebUrl = "https://store.epicgames.com/download"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "rockstar",
            WinGetId = "RockstarGames.Launcher",
            Name = "Rockstar Games Launcher",
            Description = "Official launcher for Grand Theft Auto, Red Dead Redemption, and Rockstar titles.",
            Category = PackageCategory.Gaming,
            Glyph = "\uE7FC",
            LaunchProtocolOrExe = "rockstargames://",
            WebUrl = "https://www.rockstargames.com/gta-online/launcher"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "ubisoft",
            WinGetId = "Ubisoft.Connect",
            Name = "Ubisoft Connect",
            Description = "Ecosystem of player services and digital storefront for Ubisoft games.",
            Category = PackageCategory.Gaming,
            Glyph = "\uE7FC",
            LaunchProtocolOrExe = "uplay://",
            WebUrl = "https://ubisoftconnect.com/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "battlenet",
            WinGetId = "Blizzard.BattleNet",
            Name = "Battle.net",
            Description = "Blizzard Entertainment and Activision gaming portal for Call of Duty, Diablo, and Overwatch.",
            Category = PackageCategory.Gaming,
            Glyph = "\uE7FC",
            LaunchProtocolOrExe = "battlenet://",
            WebUrl = "https://www.blizzard.com/apps/battle.net/desktop"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "gog",
            WinGetId = "GOG.Galaxy",
            Name = "GOG Galaxy",
            Description = "DRM-free gaming platform that consolidates multiple libraries into one.",
            Category = PackageCategory.Gaming,
            Glyph = "\uE7FC",
            LaunchProtocolOrExe = "goggalaxy://",
            WebUrl = "https://www.gog.com/galaxy"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "ea",
            WinGetId = "ElectronicArts.EADesktop",
            Name = "EA App",
            Description = "Electronic Arts game launcher for Battlefield, Apex Legends, and EA Play.",
            Category = PackageCategory.Gaming,
            Glyph = "\uE7FC",
            LaunchProtocolOrExe = "origin2://",
            WebUrl = "https://www.ea.com/ea-app"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "fitgirl",
            WinGetId = "",
            Name = "FitGirl Repacks",
            Description = "Official web resource for verified lightweight game repacks and community releases.",
            Category = PackageCategory.Gaming,
            Glyph = "\uE71B",
            IsWebLinkOnly = true,
            WebUrl = "https://fitgirl-repacks.site/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "dlss5swapper",
            WinGetId = "rakanki911.DLSS5Swapper",
            Name = "DLSS 5 Swapper",
            Description = "Seamlessly upgrade and swap NVIDIA DLSS, Streamline DLLs, RenoDX add-on, and ReShade silently in modern games.",
            Category = PackageCategory.Gaming,
            Glyph = "\uE7FC",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "Tools", "DLSS5-Swapper", "DLSS5-Swapper-portable.exe"),
            WebUrl = "https://github.com/rakanki911/DLSS5-Swapper/releases"
        });

        // 3. Hardware & Tuning
        _packages.Add(new SoftwarePackageItem
        {
            Id = "afterburner",
            WinGetId = "Guru3D.Afterburner",
            Name = "MSI Afterburner",
            Description = "Industry standard GPU overclocking, voltage control, and RTSS OSD monitor.",
            Category = PackageCategory.Monitoring,
            Glyph = "\uE945",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MSI Afterburner", "MSIAfterburner.exe"),
            WebUrl = "https://www.msi.com/Landing/afterburner/graphics-cards"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "cpuz",
            WinGetId = "CPUID.CPU-Z",
            Name = "CPU-Z",
            Description = "Detailed CPU architectural reporting, clock speeds, cache, and RAM timings.",
            Category = PackageCategory.Monitoring,
            Glyph = "\uE945",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CPUID", "CPU-Z", "cpuz.exe"),
            WebUrl = "https://www.cpuid.com/softwares/cpu-z.html"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "gpuz",
            WinGetId = "TechPowerUp.GPU-Z",
            Name = "GPU-Z",
            Description = "Real-time graphics card sensor monitoring, VRAM bandwidth, and BIOS details.",
            Category = PackageCategory.Monitoring,
            Glyph = "\uE945",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GPU-Z", "GPU-Z.exe"),
            WebUrl = "https://www.techpowerup.com/download/techpowerup-gpu-z/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "hwinfo",
            WinGetId = "REALiX.HWiNFO",
            Name = "HWiNFO",
            Description = "Exhaustive professional hardware diagnostic and telemetry monitor.",
            Category = PackageCategory.Monitoring,
            Glyph = "\uE945",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HWiNFO64", "HWiNFO64.exe"),
            WebUrl = "https://www.hwinfo.com/download/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "crystaldisk",
            WinGetId = "CrystalDewWorld.CrystalDiskInfo",
            Name = "CrystalDiskInfo",
            Description = "NVMe SSD & HDD health monitor, temperature alerts, and S.M.A.R.T. health.",
            Category = PackageCategory.Monitoring,
            Glyph = "\uE945",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CrystalDiskInfo", "DiskInfo64.exe"),
            WebUrl = "https://crystalmark.info/en/software/crystaldiskinfo/"
        });

        // 4. Tools & Productivity
        _packages.Add(new SoftwarePackageItem
        {
            Id = "ninite",
            WinGetId = "",
            Name = "Ninite",
            Description = "Install and update popular Windows apps automatically at once with no toolbars or clicking next.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE896",
            LocalInstallerPath = @"C:\Users\Abhimanyu Vishwakarm\GitHub\AxePower\Ninite Installer.exe",
            CustomWebUrl = "https://ninite.com/",
            WebUrl = "https://ninite.com/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "7zip",
            WinGetId = "7zip.7zip",
            Name = "7-Zip",
            Description = "High compression file archiver with 7z, ZIP, RAR, and TAR support.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE8B7",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7zFM.exe"),
            WebUrl = "https://www.7-zip.org/download.html"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "obs",
            WinGetId = "OBSProject.OBSStudio",
            Name = "OBS Studio",
            Description = "Free and open-source software for video recording and live streaming.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE8B7",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "bin", "64bit", "obs64.exe"),
            WebUrl = "https://obsproject.com/download"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "discord",
            WinGetId = "Discord.Discord",
            Name = "Discord",
            Description = "All-in-one voice, video, and text communication for gamers.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE8B7",
            LaunchProtocolOrExe = "discord://",
            WebUrl = "https://discord.com/download"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "vlc",
            WinGetId = "VideoLAN.VLC",
            Name = "VLC Media Player",
            Description = "Universal media player that plays any video and audio format effortlessly.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE8B7",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe"),
            WebUrl = "https://www.videolan.org/vlc/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "notepadplusplus",
            WinGetId = "Notepad++.Notepad++",
            Name = "Notepad++",
            Description = "Fast, lightweight tabbed code and text editor with syntax highlighting.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE8B7",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notepad++", "notepad++.exe"),
            WebUrl = "https://notepad-plus-plus.org/downloads/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "davinci",
            WinGetId = "BlackmagicDesign.DaVinciResolve",
            Name = "DaVinci Resolve (Free)",
            Description = "Industry-standard Hollywood-grade video editing, color grading, VFX, and Fairlight audio.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE8B7",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blackmagic Design", "DaVinci Resolve", "Resolve.exe"),
            WebUrl = "https://www.blackmagicdesign.com/products/davinciresolve"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "audacity",
            WinGetId = "Audacity.Audacity",
            Name = "Audacity",
            Description = "Free, open source, multi-track audio recorder and waveform editor.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE8D6",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Audacity", "Audacity.exe"),
            WebUrl = "https://www.audacityteam.org/download/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "gimp",
            WinGetId = "GIMP.GIMP",
            Name = "GIMP",
            Description = "Free & open source raster graphics and photo manipulation editor.",
            Category = PackageCategory.Utilities,
            Glyph = "\uEB9F",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GIMP 2", "bin", "gimp-2.10.exe"),
            WebUrl = "https://www.gimp.org/downloads/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "canva",
            WinGetId = "Canva.Canva",
            Name = "Canva",
            Description = "Visual design suite for creating graphics, banners, presentations, and social media content.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE8B7",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Canva", "Canva.exe"),
            WebUrl = "https://www.canva.com/download/windows/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "blender",
            WinGetId = "BlenderFoundation.Blender",
            Name = "Blender",
            Description = "Free open source 3D creation suite for modeling, sculpting, animation, and raytracing.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE8B7",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation", "Blender", "blender.exe"),
            WebUrl = "https://www.blender.org/download/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "handbrake",
            WinGetId = "HandBrake.HandBrake",
            Name = "HandBrake",
            Description = "Open-source video transcoder for converting video formats with AV1, HEVC, and H.264 support.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE8B7",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HandBrake", "HandBrake.exe"),
            WebUrl = "https://handbrake.fr/downloads.php"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "powertoys",
            WinGetId = "Microsoft.PowerToys",
            Name = "Microsoft PowerToys",
            Description = "Windows productivity suite (FancyZones, Text Extractor OCR, ColorPicker, Awake).",
            Category = PackageCategory.Utilities,
            Glyph = "\uE713",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerToys", "PowerToys.exe"),
            WebUrl = "https://github.com/microsoft/PowerToys/releases"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "sharex",
            WinGetId = "ShareX.ShareX",
            Name = "ShareX",
            Description = "Screen capture, GIF/video recorder, OCR tool, and file sharing utility.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE8B7",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ShareX", "ShareX.exe"),
            WebUrl = "https://getsharex.com/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "peace",
            WinGetId = "PeterVerbeek.PeaceEqualizer",
            Name = "Peace Equalizer (Equalizer APO)",
            Description = "System-wide parametric equalizer GUI with auto-EQ headphone tuning, bass boost, and spatial crossfeed.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE767",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO", "config", "Peace.exe"),
            WebUrl = "https://sourceforge.net/projects/peace-equalizer-apo-extension/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "dolby",
            WinGetId = "9N0866FS04B8",
            Name = "Dolby Access (Dolby Atmos)",
            Description = "3D spatial sound virtualization, gaming audio positioning, dynamic studio EQ, and headphone surround.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE767",
            LaunchProtocolOrExe = "ms-windows-store://pdp/?ProductId=9N0866FS04B8",
            WebUrl = "https://www.dolby.com/technologies/dolby-atmos/access/"
        });

        _packages.Add(new SoftwarePackageItem
        {
            Id = "steelseriesgg",
            WinGetId = "SteelSeries.GG",
            Name = "SteelSeries GG (Sonar Audio)",
            Description = "Gaming parametric audio engine, Sonar virtual multi-channel mixer, and AI microphone noise cancellation.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE767",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SteelSeries", "GG", "SteelSeriesGG.exe"),
            WebUrl = "https://steelseries.com/gg/sonar"
        });
    }

    public IReadOnlyList<SoftwarePackageItem> GetAvailablePackages() => _packages.AsReadOnly();

    public async Task RefreshInstallationStatusesAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                // 1. Fast Primary Check: Registry & Custom Protocols & Direct Executables
                foreach (var pkg in _packages)
                {
                    if (pkg.IsWebLinkOnly)
                    {
                        pkg.IsInstalled = false;
                        continue;
                    }

                    pkg.IsInstalled = CheckIsInstalledFast(pkg);
                }

                // 2. Secondary Comprehensive WinGet list check
                var installedWingetOutput = RunSilentCli("winget.exe", "list");
                if (!string.IsNullOrWhiteSpace(installedWingetOutput))
                {
                    foreach (var pkg in _packages)
                    {
                        if (pkg.IsWebLinkOnly) continue;

                        if (!pkg.IsInstalled && !string.IsNullOrEmpty(pkg.WinGetId))
                        {
                            bool found = installedWingetOutput.Contains(pkg.WinGetId, StringComparison.OrdinalIgnoreCase) ||
                                         installedWingetOutput.Contains(pkg.Name, StringComparison.OrdinalIgnoreCase);
                            if (found) pkg.IsInstalled = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[WinGetHub] Failed to refresh package list: {ex.Message}");
            }
        });
    }

    public static bool CheckIsInstalledFast(SoftwarePackageItem pkg)
    {
        try
        {
            // Direct file existence check if specified
            if (!string.IsNullOrEmpty(pkg.LaunchProtocolOrExe) && !pkg.LaunchProtocolOrExe.Contains("://"))
            {
                if (File.Exists(pkg.LaunchProtocolOrExe)) return true;
            }

            // Check protocol registration (e.g. steam://, com.epicgames.launcher://, uplay://, rockstargames://, battlenet://)
            if (!string.IsNullOrEmpty(pkg.LaunchProtocolOrExe) && pkg.LaunchProtocolOrExe.EndsWith("://"))
            {
                string proto = pkg.LaunchProtocolOrExe.TrimEnd('/', ':');
                using var key = Registry.ClassesRoot.OpenSubKey(proto);
                if (key != null) return true;
            }

            // Check well-known registry entries for launchers & tools
            switch (pkg.Id)
            {
                case "steam":
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                        if (k != null) return true;
                    break;
                case "epic":
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher"))
                        if (k != null) return true;
                    break;
                case "rockstar":
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Rockstar Games\Launcher"))
                        if (k != null) return true;
                    break;
                case "ubisoft":
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher"))
                        if (k != null) return true;
                    break;
                case "battlenet":
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Blizzard Entertainment\Battle.net"))
                        if (k != null) return true;
                    break;
                case "afterburner":
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\MSI\Afterburner"))
                        if (k != null) return true;
                    break;
                case "dlss5swapper":
                    string dlssExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "Tools", "DLSS5-Swapper", "DLSS5-Swapper-portable.exe");
                    if (File.Exists(dlssExe))
                    {
                        pkg.LaunchProtocolOrExe = dlssExe;
                        return true;
                    }
                    break;
            }
        }
        catch { }

        return false;
    }

    // Microsoft Store product IDs (e.g. 9N0866FS04B8) only exist in the msstore source
    private static string GetWinGetSource(string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(id, "^9[A-Z0-9]{11}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? "msstore" : "winget";

    public async Task<bool> InstallPackageAsync(string wingetId, IProgress<string> outputProgress)
    {
        var pkg = _packages.FirstOrDefault(p => p.WinGetId.Equals(wingetId, StringComparison.OrdinalIgnoreCase) || p.Id.Equals(wingetId, StringComparison.OrdinalIgnoreCase));
        string targetId = pkg?.WinGetId ?? wingetId;
        string displayName = pkg?.Name ?? targetId;
        string webUrl = pkg?.WebUrl ?? string.Empty;

        if (pkg != null && pkg.Id == "dlss5swapper")
        {
            return await DownloadAndRunDlss5SwapperAsync(pkg, outputProgress);
        }

        outputProgress.Report($"\n[Install] > {displayName} ({targetId})");
        _log.LogInfo($"[WinGetHub] Starting 1-click install for '{targetId}'...");

        bool success = false;

        if (!string.IsNullOrEmpty(targetId))
        {
            success = await ExecuteWinGetProcessAsync(
                $"install --id \"{targetId}\" --exact --source {GetWinGetSource(targetId)} --accept-source-agreements --accept-package-agreements --silent",
                outputProgress, 300000);

            // The exit code can lie for wrapped installers; trust an on-disk check before declaring failure
            if (!success && pkg != null && CheckIsInstalledFast(pkg))
                success = true;

            if (success && pkg != null)
                pkg.IsInstalled = true;
        }

        // Fallback: if the install failed (or there is no winget ID), open the official portal in the browser
        if (!success && !string.IsNullOrEmpty(webUrl))
        {
            outputProgress.Report($"\n[Direct Web Setup] Opening official download portal for {displayName}: {webUrl}");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = webUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                outputProgress.Report($"[Browser Error] {ex.Message}");
            }
        }

        return success;
    }

    public async Task<bool> UpgradePackageAsync(string wingetId, IProgress<string> outputProgress)
    {
        var pkg = _packages.FirstOrDefault(p => p.WinGetId.Equals(wingetId, StringComparison.OrdinalIgnoreCase) || p.Id.Equals(wingetId, StringComparison.OrdinalIgnoreCase));
        string targetId = pkg?.WinGetId ?? wingetId;
        string displayName = pkg?.Name ?? targetId;

        if (pkg != null && pkg.Id == "dlss5swapper")
        {
            return await DownloadAndRunDlss5SwapperAsync(pkg, outputProgress);
        }

        outputProgress.Report($"\n[Update] > {displayName} ({targetId})");
        _log.LogInfo($"[WinGetHub] Starting update for '{targetId}'...");

        if (string.IsNullOrEmpty(targetId))
            return false;

        return await ExecuteWinGetProcessAsync(
            $"upgrade --id \"{targetId}\" --exact --source {GetWinGetSource(targetId)} --accept-source-agreements --accept-package-agreements --silent",
            outputProgress, 300000);
    }

    public async Task<bool> UninstallPackageAsync(string wingetId, IProgress<string> outputProgress)
    {
        var pkg = _packages.FirstOrDefault(p => p.WinGetId.Equals(wingetId, StringComparison.OrdinalIgnoreCase) || p.Id.Equals(wingetId, StringComparison.OrdinalIgnoreCase));
        string targetId = pkg?.WinGetId ?? wingetId;

        if (pkg != null && pkg.Id == "dlss5swapper")
        {
            string toolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "Tools", "DLSS5-Swapper");
            try
            {
                if (Directory.Exists(toolsDir))
                {
                    Directory.Delete(toolsDir, true);
                }
                pkg.IsInstalled = false;
                outputProgress.Report("\n[DLSS 5 Swapper] ✓ Cleanly removed portable files from local tools directory.");
                _log.LogInfo("[WinGetHub] Removed DLSS 5 Swapper portable directory.");
                return true;
            }
            catch (Exception ex)
            {
                outputProgress.Report($"\n[DLSS 5 Swapper] Could not remove portable files: {ex.Message}");
                return false;
            }
        }

        outputProgress.Report($"\n[Starting PowerShell Uninstall] > winget uninstall --id {targetId} --silent");
        _log.LogInfo($"[WinGetHub] Uninstalling package '{targetId}' via PowerShell...");

        bool success = await ExecuteWinGetProcessAsync($"uninstall --id \"{targetId}\" --silent", outputProgress, 35000);

        if (!success)
        {
            outputProgress.Report($"\n[Notice] Silent uninstall non-zero. Attempting interactive uninstall...");
            success = await ExecuteWinGetProcessAsync($"uninstall --id \"{targetId}\"", outputProgress, 35000);
        }

        if (pkg != null && success)
        {
            pkg.IsInstalled = false;
        }

        return success;
    }

    private async Task<bool> DownloadAndRunDlss5SwapperAsync(SoftwarePackageItem pkg, IProgress<string> outputProgress)
    {
        string toolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "Tools", "DLSS5-Swapper");
        Directory.CreateDirectory(toolsDir);
        string exePath = Path.Combine(toolsDir, "DLSS5-Swapper-portable.exe");

        outputProgress.Report("\n[DLSS 5 Swapper] Querying GitHub releases for latest portable executable...");
        string downloadUrl = "https://github.com/rakanki911/DLSS5-Swapper/releases/download/v2.2.4/DLSS5-Swapper-2.2.4-portable.exe";
        string versionTag = "v2.2.4";

        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AxePower-Client");
            client.Timeout = TimeSpan.FromSeconds(10);
            string apiJson = await client.GetStringAsync("https://api.github.com/repos/rakanki911/DLSS5-Swapper/releases/latest");
            using var doc = System.Text.Json.JsonDocument.Parse(apiJson);
            if (doc.RootElement.TryGetProperty("tag_name", out var tagProp))
            {
                versionTag = tagProp.GetString() ?? versionTag;
            }
            if (doc.RootElement.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    string assetName = asset.GetProperty("name").GetString() ?? "";
                    if (assetName.Contains("portable", StringComparison.OrdinalIgnoreCase) && assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? downloadUrl;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[WinGetHub] GitHub API lookup for DLSS5-Swapper: {ex.Message}. Using direct release fallback.");
        }

        outputProgress.Report($"[DLSS 5 Swapper] Target release: {versionTag}");
        outputProgress.Report($"[DLSS 5 Swapper] Downloading portable build from: {downloadUrl}");

        try
        {
            using var downloadClient = new System.Net.Http.HttpClient();
            downloadClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            downloadClient.Timeout = TimeSpan.FromMinutes(10);

            using var response = await downloadClient.GetAsync(downloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var file = new FileStream(exePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int read;
            int lastReport = -1;

            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    int percent = (int)((totalRead * 100) / totalBytes.Value);
                    if (percent != lastReport && percent % 15 == 0)
                    {
                        lastReport = percent;
                        outputProgress.Report($"[Download Progress] {percent}% ({totalRead / (1024 * 1024)} MB / {totalBytes.Value / (1024 * 1024)} MB)");
                    }
                }
            }

            file.Flush();
            file.Close();

            outputProgress.Report($"\n[DLSS 5 Swapper] ✓ Download completed successfully ({totalRead / (1024 * 1024)} MB)!");
            outputProgress.Report($"[DLSS 5 Swapper] 🚀 Launching DLSS 5 Swapper portable ({versionTag})...");

            pkg.IsInstalled = true;
            pkg.LaunchProtocolOrExe = exePath;

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WorkingDirectory = toolsDir
            });

            _log.LogInfo($"[WinGetHub] Successfully downloaded and launched DLSS 5 Swapper from {exePath}");
            return true;
        }
        catch (Exception ex)
        {
            outputProgress.Report($"\n[ERROR] DLSS 5 Swapper download failed: {ex.Message}");
            _log.LogError("[WinGetHub] DLSS 5 Swapper download failed", ex);
            return false;
        }
    }

    public async Task<bool> UpgradeAllPackagesAsync(IProgress<string> outputProgress)
    {
        outputProgress.Report("\n=== Starting Batch Upgrade via PowerShell (winget upgrade --all) ===");
        _log.LogInfo("[WinGetHub] Executing PowerShell batch upgrade for all installed packages...");

        bool success = await ExecuteWinGetProcessAsync("upgrade --all --include-unknown --accept-source-agreements --accept-package-agreements", outputProgress, 300000);

        outputProgress.Report("\n=== PowerShell Batch Upgrade Completed ===");
        await RefreshInstallationStatusesAsync();
        return success;
    }

    private static string RunSilentCli(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            string output = proc?.StandardOutput.ReadToEnd() ?? string.Empty;
            proc?.WaitForExit(5000);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<bool> ExecuteWinGetProcessAsync(string arguments, IProgress<string> outputProgress, int timeoutMs = 45000)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget.exe",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };

                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        outputProgress.Report(e.Data);
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        outputProgress.Report($"[ERROR] {e.Data}");
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = process.WaitForExit(timeoutMs);
                if (!exited)
                {
                    try { process.Kill(true); } catch { }
                    outputProgress.Report($"[TIMEOUT] Command timed out after {timeoutMs / 1000}s.");
                    return false;
                }

                int code = process.ExitCode;
                outputProgress.Report($"[DONE] Process exited with code: {code}");
                return (code == 0 || code == -1978335189);
            }
            catch (Exception ex)
            {
                outputProgress.Report($"[EXCEPTION] Failed to run winget: {ex.Message}");
                return false;
            }
        });
    }
}
