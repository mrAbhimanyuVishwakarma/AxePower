using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// Tests for the leftover confidence scoring service.
/// These verify the core safety principle: the confidence scorer
/// must correctly distinguish app-specific leftovers from system paths.
/// </summary>
public class ConfidenceScoringTests
{
    private readonly LeftoverConfidenceService _service = new();

    private static InstalledApplication MakeApp(
        string name = "TestApp",
        string publisher = "TestCorp",
        string installLocation = @"C:\Program Files\TestCorp\TestApp",
        string productCode = "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}") =>
        new()
        {
            Id = "test-id",
            DisplayName = name,
            Publisher = publisher,
            InstallLocation = installLocation,
            ProductCode = productCode,
        };

    // ── Protected path tests ──────────────────────────────────────────────────

    [Fact]
    public void WindowsSystemPath_Returns_LowScore_And_Protected()
    {
        var item = new LeftoverItem
        {
            Path = @"C:\Windows\System32\SomeFile.dll",
            ItemType = LeftoverItemType.File,
        };

        var (score, _, isProtected, _) = _service.Score(item, MakeApp());

        Assert.True(isProtected, "System32 files must be marked protected");
        Assert.True(score < 20, $"Expected score < 20 for system path, got {score}");
    }

    [Fact]
    public void WindowsDirectory_Returns_LowScore_And_Protected()
    {
        var item = new LeftoverItem
        {
            Path = @"C:\Windows\SomeComponent",
            ItemType = LeftoverItemType.Folder,
        };

        var (score, _, isProtected, _) = _service.Score(item, MakeApp());

        Assert.True(isProtected, "Windows directory items must be protected");
        Assert.True(score < 20, $"Expected score < 20 for Windows path, got {score}");
    }

    // ── High confidence tests ─────────────────────────────────────────────────

    [Fact]
    public void InstallLocation_SubPath_Returns_HighScore()
    {
        var app = MakeApp(installLocation: @"C:\Program Files\TestCorp\TestApp");
        var item = new LeftoverItem
        {
            Path = @"C:\Program Files\TestCorp\TestApp\Leftover",
            ItemType = LeftoverItemType.Folder,
        };

        var (score, _, isProtected, _) = _service.Score(item, app);

        Assert.False(isProtected, "InstallLocation subpath should not be protected");
        Assert.True(score >= 50, $"Expected high confidence for install location, got {score}");
    }

    [Fact]
    public void ProductCode_In_Path_Returns_HighScore()
    {
        var pc = "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}";
        var app = MakeApp(productCode: pc);
        var item = new LeftoverItem
        {
            Path = $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\{pc}",
            ItemType = LeftoverItemType.RegistryKey,
        };

        var (score, _, _, _) = _service.Score(item, app);

        Assert.True(score >= 40, $"Expected high confidence for ProductCode path, got {score}");
    }

    // ── Shared component tests ────────────────────────────────────────────────

    [Fact]
    public void VCRedist_Path_Returns_IsShared()
    {
        var item = new LeftoverItem
        {
            Path = @"C:\Program Files\Common Files\Microsoft Visual C++ Redist",
            ItemType = LeftoverItemType.Folder,
        };

        var (score, _, _, isShared) = _service.Score(item, MakeApp());

        Assert.True(isShared, "VC Redist paths must be marked as shared components");
        Assert.True(score < 50, $"Shared component score should be low, got {score}");
    }

    [Fact]
    public void DotNetPath_Returns_IsShared()
    {
        var item = new LeftoverItem
        {
            Path = @"C:\Program Files\dotnet\sdk\SomeFile",
            ItemType = LeftoverItemType.File,
        };

        var (score, _, _, isShared) = _service.Score(item, MakeApp());

        Assert.True(isShared, "dotnet SDK paths must be marked as shared components");
    }

    // ── Confidence label tests ─────────────────────────────────────────────────

    [Theory]
    [InlineData(95, "Very High")]
    [InlineData(85, "High")]
    [InlineData(70, "Medium")]
    [InlineData(45, "Low")]
    [InlineData(20, "Very Low")]
    public void ConfidenceLabel_MatchesExpected(int score, string expected)
    {
        var item = new LeftoverItem { ConfidenceScore = score };
        Assert.Equal(expected, item.ConfidenceLabel);
    }
}
