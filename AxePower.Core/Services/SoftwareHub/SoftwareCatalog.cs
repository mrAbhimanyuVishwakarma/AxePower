using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AxePower.Core.Models;
using AxePower.Core.Services.Processes;

namespace AxePower.Core.Services.SoftwareHub;

/// <summary>
/// The Software Hub's package list and the rules every entry must meet. Entries are checked when
/// the catalog is built, and one that fails (an identifier that could be read as an option, an
/// address that is not https/http, a path that is not fully qualified, an unlisted protocol) is
/// left out and logged rather than offered.
/// </summary>
public sealed class SoftwareCatalog
{
    private readonly List<SoftwarePackageItem> _items;
    private readonly List<(string Id, string Problem)> _rejected = new();

    public SoftwareCatalog(IEnumerable<SoftwarePackageItem> items, LogService? log = null)
    {
        _items = new List<SoftwarePackageItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var problem = Validate(item) ?? (seen.Add(item.Id) ? null : "duplicate id");
            if (problem is null)
            {
                _items.Add(item);
            }
            else
            {
                _rejected.Add((item.Id, problem));
                log?.LogWarning($"[SoftwareHub] Catalog entry '{item.Id}' left out: {problem}.");
            }
        }
    }

    /// <summary>The built-in catalog.</summary>
    public static SoftwareCatalog CreateDefault(LogService? log = null) => new(CreateDefaultItems(), log);

    public IReadOnlyList<SoftwarePackageItem> Items => _items;

    /// <summary>Entries that failed validation, with the reason.</summary>
    public IReadOnlyList<(string Id, string Problem)> Rejected => _rejected;

    /// <summary>The entry whose catalog id or WinGet id matches, or null.</summary>
    public SoftwarePackageItem? Find(string idOrWinGetId) =>
        string.IsNullOrEmpty(idOrWinGetId) ? null :
        _items.FirstOrDefault(p => p.Id.Equals(idOrWinGetId, StringComparison.OrdinalIgnoreCase)) ??
        _items.FirstOrDefault(p => p.WinGetId.Length > 0 && p.WinGetId.Equals(idOrWinGetId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why an entry cannot be offered, or null when it is fine.</summary>
    public static string? Validate(SoftwarePackageItem item)
    {
        if (!PackageInput.IsValidCatalogId(item.Id)) return "the id is not lower-case letters, digits and dashes";
        if (string.IsNullOrWhiteSpace(item.Name)) return "no name";
        if (item.WinGetId.Length > 0 && !PackageInput.IsValidWinGetId(item.WinGetId)) return "the WinGet id has characters that are not allowed";
        if (item.WebUrl.Length > 0 && !PackageInput.TryWebUrl(item.WebUrl, out _)) return "the web address is not a plain https/http address";
        if (item.CustomWebUrl.Length > 0 && !PackageInput.TryWebUrl(item.CustomWebUrl, out _)) return "the custom web address is not a plain https/http address";
        if (item.LocalInstallerPath.Length > 0 && !Path.IsPathFullyQualified(item.LocalInstallerPath)) return "the local installer path is not fully qualified";

        var launch = item.LaunchProtocolOrExe;
        if (launch.Length > 0)
        {
            if (launch.Contains("://", StringComparison.Ordinal))
            {
                if (!UserShellLauncher.TryValidateUri(launch, UserShellLauncher.DefaultSchemes, out _, out var reason)) return "the launch protocol is not allowed (" + reason + ")";
            }
            else if (!Path.IsPathFullyQualified(launch) || !launch.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return "the launch target is neither a listed protocol nor a full .exe path";
            }
        }
        return null;
    }

    public static List<SoftwarePackageItem> CreateDefaultItems()
    {
        var items = new List<SoftwarePackageItem>();


        // 1. Runtimes & DirectX
        items.Add(new SoftwarePackageItem
        {
            Id = "vcredist",
            WinGetId = "Microsoft.VCRedist.2015+.x64",
            Name = "Visual C++ All-in-One (2015-2022)",
            Description = "Required runtime library for almost all modern PC games and 3D engines.",
            Category = PackageCategory.Runtimes,
            Glyph = "\uE8B8",
            WebUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
        });

        items.Add(new SoftwarePackageItem
        {
            Id = "directx",
            WinGetId = "Microsoft.DirectX",
            Name = "DirectX End-User Runtime",
            Description = "Legacy and modern DirectX 9/10/11 libraries for vintage and modern games.",
            Category = PackageCategory.Runtimes,
            Glyph = "\uE8B8",
            WebUrl = "https://www.microsoft.com/en-us/download/details.aspx?id=35"
        });

        items.Add(new SoftwarePackageItem
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
        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
        {
            Id = "wemod",
            WinGetId = "WeMod.WeMod",
            Name = "WeMod",
            Description = "Popular game trainer app with mods, cheats, and unlimited health/ammo for thousands of single-player titles.",
            Category = PackageCategory.Gaming,
            Glyph = "",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeMod", "WeMod.exe"),
            WebUrl = "https://www.wemod.com/"
        });

        // 3. Hardware & Tuning
        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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
        items.Add(new SoftwarePackageItem
        {
            Id = "ninite",
            WinGetId = "",
            Name = "Ninite",
            Description = "Install and update popular Windows apps automatically at once with no toolbars or clicking next.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE896",
            LocalInstallerPath = Path.Combine(AppContext.BaseDirectory, "Ninite Installer.exe"),
            CustomWebUrl = "https://ninite.com/",
            WebUrl = "https://ninite.com/"
        });

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
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

        items.Add(new SoftwarePackageItem
        {
            Id = "camstudio",
            WinGetId = "CamStudio.CamStudio",
            Name = "CamStudio",
            Description = "Open-source screen & gameplay video recorder with AVI/MP4 encoding and microphone capture.",
            Category = PackageCategory.Utilities,
            Glyph = "\uE714",
            LaunchProtocolOrExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "Tools", "CamStudio", "Recorder.exe"),
            WebUrl = "https://camstudio.org/"
        });

        return items;
    }
}
