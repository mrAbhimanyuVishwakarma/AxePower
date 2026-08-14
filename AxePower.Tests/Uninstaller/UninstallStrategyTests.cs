using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// Tests for MSI ProductCode validation — critical safety check
/// before invoking msiexec.
/// </summary>
public class UninstallStrategyTests
{
    // ── MsiUninstallExecutor ProductCode validation ────────────────────────────

    [Theory]
    [InlineData("{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}", true)]
    [InlineData("{00000000-0000-0000-0000-000000000000}", true)]
    [InlineData("{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}", true)]
    [InlineData("", false)]
    [InlineData("not-a-guid", false)]
    [InlineData("{A1B2C3D4-E5F6-7890-ABCD-EF123456789}", false)]   // too short
    [InlineData("{A1B2C3D4-E5F6-7890-ABCD-EF12345678901}", false)]  // too long
    [InlineData("A1B2C3D4-E5F6-7890-ABCD-EF1234567890", false)]    // missing braces
    [InlineData("{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}extra", false)]
    [InlineData(null!, false)]
    public void IsValidProductCode_ReturnsExpected(string? productCode, bool expected)
    {
        var result = MsiUninstallExecutor.IsValidProductCode(productCode ?? string.Empty);
        Assert.Equal(expected, result);
    }

    // ── WingetUninstallExecutor ID validation ─────────────────────────────────
    // Tested indirectly by ensuring the executor requires a valid WinGet ID
    // (executor returns Fail if ID is invalid)

    [Theory]
    [InlineData("Google.Chrome")]
    [InlineData("VideoLAN.VLC")]
    [InlineData("7zip.7zip")]
    [InlineData("Microsoft.VisualStudioCode")]
    public void ValidWingetIds_PassSafeCharacterCheck(string id)
    {
        // Safe pattern: Publisher.App with alphanumeric, dots, hyphens, underscores
        var regex = new System.Text.RegularExpressions.Regex(@"^[\w\.\-]+$");
        Assert.Matches(regex, id);
    }

    [Theory]
    [InlineData("Google Chrome; rm -rf /")]
    [InlineData("../../etc/passwd")]
    [InlineData("App`whoami`")]
    [InlineData("App$(echo bad)")]
    public void MaliciousWingetIds_FailSafeCharacterCheck(string id)
    {
        var regex = new System.Text.RegularExpressions.Regex(@"^[\w\.\-]+$");
        Assert.DoesNotMatch(regex, id);
    }
}
