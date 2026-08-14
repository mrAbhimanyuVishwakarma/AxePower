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
}
