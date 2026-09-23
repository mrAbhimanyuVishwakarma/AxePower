using System.IO;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// Tests for the ProtectionService — verifies that AxePower itself
/// and Windows system components can never be uninstalled.
/// </summary>
public class ProtectionServiceTests
{
    private readonly ProtectionService _service = new();

    // ── AxePower protection ────────────────────────────────────────────────────

    [Fact]
    public void AxePower_IsAlwaysProtected()
    {
        var app = new InstalledApplication
        {
            DisplayName = "AxePower",
            Publisher = "Abhimanyu Vishwakarma",
            CanUninstall = true,
        };

        Assert.True(_service.IsApplicationProtected(app));
        Assert.NotNull(_service.GetProtectionReason(app));
    }

    [Fact]
    public void AxePowerSuffix_IsProtected()
    {
        var app = new InstalledApplication
        {
            DisplayName = "AxePower Desktop",
            Publisher = "Test Publisher",
            CanUninstall = true,
        };

        Assert.True(_service.IsApplicationProtected(app));
    }

    // ── System component protection ────────────────────────────────────────────

    [Fact]
    public void SystemComponent_IsProtected()
    {
        var app = new InstalledApplication
        {
            DisplayName = "Microsoft Visual C++ 2022 Redistributable",
            Publisher = "Microsoft Corporation",
            IsSystemComponent = true,
            CanUninstall = true,
        };

        Assert.True(_service.IsApplicationProtected(app));
    }

    [Fact]
    public void WindowsPrefixApp_IsProtected()
    {
        var app = new InstalledApplication
        {
            DisplayName = "Windows Subsystem for Linux",
            Publisher = "Microsoft Corporation",
            CanUninstall = true,
        };

        Assert.True(_service.IsApplicationProtected(app));
    }

    // ── Normal app is NOT protected ────────────────────────────────────────────

    [Fact]
    public void NormalApp_IsNotProtected()
    {
        var app = new InstalledApplication
        {
            DisplayName = "VLC Media Player",
            Publisher = "VideoLAN",
            CanUninstall = true,
            IsSystemComponent = false,
        };

        Assert.False(_service.IsApplicationProtected(app));
        Assert.Null(_service.GetProtectionReason(app));
    }

    // ── NonRemovable AppX ─────────────────────────────────────────────────────

    [Fact]
    public void NonRemovableAppX_IsProtected()
    {
        var app = new InstalledApplication
        {
            DisplayName = "Windows Store",
            Publisher = "Microsoft Corporation",
            ApplicationType = ApplicationType.AppX,
            CanUninstall = false,
        };

        Assert.True(_service.IsApplicationProtected(app));
    }

    // ── Path protection ────────────────────────────────────────────────────────

    [Fact]
    public void WindowsDir_IsPathProtected()
    {
        Assert.True(_service.IsPathProtected(@"C:\Windows\System32\kernel32.dll"));
        Assert.True(_service.IsPathProtected(@"C:\Windows\SomeDir"));
    }

    [Fact]
    public void NormalAppDir_IsNotPathProtected()
    {
        Assert.False(_service.IsPathProtected(@"C:\Program Files\VLC\vlc.exe"));
    }

    // ── File-system rails (D7) ───────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:")]
    public void DriveRoots_AreBlocked(string path)
    {
        Assert.NotNull(_service.GetBlockReason(path, []));
    }

    public static IEnumerable<object[]> SpecialFolders() =>
        new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.Programs,
            Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Startup,
            Environment.SpecialFolder.Fonts, Environment.SpecialFolder.CommonProgramFilesX86,
        }
        .Select(f => Environment.GetFolderPath(f))
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(SpecialFolders))]
    public void SpecialFolders_AndTheirAncestors_AreBlocked(string folder)
    {
        Assert.NotNull(_service.GetBlockReason(folder, []));
        var parent = Path.GetDirectoryName(folder);
        if (parent is not null) Assert.NotNull(_service.GetBlockReason(parent, []));
    }

    [Fact]
    public void AppFolderInsideASpecialFolder_IsAllowed()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Null(_service.GetBlockReason(Path.Combine(appData, "Notepad++"), []));
    }

    [Fact]
    public void MicrosoftFoldersAndPackageCache_AreBlocked()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Assert.NotNull(_service.GetBlockReason(Path.Combine(local, "Microsoft"), []));
        Assert.NotNull(_service.GetBlockReason(Path.Combine(local, "Microsoft", "OneDrive"), []));
        Assert.NotNull(_service.GetBlockReason(Path.Combine(programData, "Package Cache"), []));
        Assert.NotNull(_service.GetBlockReason(Path.Combine(local, "Packages", "Some.App_8wekyb3d8bbwe"), []));
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        Assert.NotNull(_service.GetBlockReason(Path.Combine(windowsApps, "App_1.0"), []));
    }

    [Fact]
    public void StartMenuShortcuts_AreAllowed_ButTheStartMenuAndWindowsGroupsAreNot()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);

        Assert.Null(_service.GetBlockReason(Path.Combine(programs, "Notepad++.lnk"), []));
        Assert.Null(_service.GetBlockReason(Path.Combine(commonPrograms, "Discord Inc"), []));
        Assert.NotNull(_service.GetBlockReason(programs, []));
        Assert.NotNull(_service.GetBlockReason(Path.Combine(programs, "Windows Tools"), []));
        Assert.NotNull(_service.GetBlockReason(Path.Combine(commonPrograms, "Accessories", "Paint.lnk"), []));
        // Other things in the same Microsoft tree stay protected
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.NotNull(_service.GetBlockReason(Path.Combine(roaming, "Microsoft", "Windows", "Themes"), []));
    }

    [Fact]
    public void ParentOfAnotherAppsInstallFolder_IsBlocked()
    {
        var others = new[] { @"C:\Program Files\Epic Games\Fortnite" };
        Assert.NotNull(_service.GetBlockReason(@"C:\Program Files\Epic Games", others));
        Assert.NotNull(_service.GetBlockReason(@"C:\Program Files\Epic Games\Fortnite", others));
        Assert.Null(_service.GetBlockReason(@"C:\Program Files\Epic Games\Launcher", others));
    }

    // ── Registry rails ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Microsoft")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft")]
    [InlineData(@"HKCU\Software\Microsoft")]
    [InlineData(@"HKCU\Software\Classes")]
    [InlineData(@"HKCU\Software\Classes\.txt")]
    [InlineData(@"HKLM\SOFTWARE\Classes")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Foo")]
    [InlineData(@"HKLM\SAM\SAM")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Vendor")]
    [InlineData(@"HKCU\Software\Policies\Vendor")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData")]
    [InlineData(@"HKCR\Installer\Products")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData(@"HKLM\SOFTWARE")]
    [InlineData(@"HKCU\Environment")]
    public void SystemRegistryAreas_AreBlocked(string path)
    {
        Assert.NotNull(_service.GetBlockReason(path, []));
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Notepad++")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{CD24A421-5DFB-4EC7-9E1A-7930D58ADA49}")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\app.exe")]
    [InlineData(@"HKCU\Software\Classes\CLSID\{11111111-2222-3333-4444-555555555555}")]
    [InlineData(@"HKCU\Software\Notepad++")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Vendor\App")]
    public void AppSpecificKeys_AreAllowed(string path)
    {
        Assert.Null(_service.GetBlockReason(path, []));
    }

    [Fact]
    public void RunValues_AreAllowed_ButNotTheRunKeyItself()
    {
        const string run = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
        Assert.Null(ProtectionService.GetRegistryBlockReason(run, "SomeApp"));
        Assert.NotNull(ProtectionService.GetRegistryBlockReason(run, null));
    }

    [Fact]
    public void InformationalItems_AreNeverDeletable()
    {
        Assert.NotNull(_service.GetBlockReason(new LeftoverItem { ItemType = LeftoverItemType.Service, Path = @"HKLM\SYSTEM\CurrentControlSet\Services\x" }, []));
        Assert.NotNull(_service.GetBlockReason(new LeftoverItem { ItemType = LeftoverItemType.FirewallRule, Path = @"C:\App\app.exe" }, []));
        Assert.NotNull(_service.GetBlockReason(new LeftoverItem { ItemType = LeftoverItemType.ScheduledTask, Path = @"\Microsoft\Windows\Defrag\ScheduledDefrag" }, []));
        Assert.Null(_service.GetBlockReason(new LeftoverItem { ItemType = LeftoverItemType.ScheduledTask, Path = @"\GoogleUpdateTaskMachineUA" }, []));
    }

    [Fact]
    public void Elevation_IsNeededForMachineLocations()
    {
        Assert.True(_service.RequiresElevation(new LeftoverItem { ItemType = LeftoverItemType.RegistryKey, Path = @"HKLM\SOFTWARE\Vendor" }));
        Assert.False(_service.RequiresElevation(new LeftoverItem { ItemType = LeftoverItemType.RegistryKey, Path = @"HKCU\Software\Vendor" }));
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(_service.RequiresElevation(new LeftoverItem { ItemType = LeftoverItemType.Folder, Path = Path.Combine(pf, "App") }));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.False(_service.RequiresElevation(new LeftoverItem { ItemType = LeftoverItemType.Folder, Path = Path.Combine(appData, "App") }));
    }

    [Fact]
    public void OneRuleSet_ProtectsHiddenMicrosoftComponentsAndAxePower()
    {
        var hidden = new InstalledApplication { DisplayName = "Some Runtime", IsSystemComponent = true };
        Assert.True(_service.IsApplicationProtected(hidden));
        var store = new InstalledApplication { DisplayName = "Store Thing", UninstallerKind = UninstallerKind.StoreApp, CanUninstall = false };
        Assert.True(_service.IsApplicationProtected(store));
    }
}
