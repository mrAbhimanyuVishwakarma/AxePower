using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AxePower.Tests;

/// <summary>
/// Release-candidate checks on the shipping installer script (Prompt 33): Setup.exe carries a
/// version resource matching the app, and the license, security policy, privacy policy and
/// third-party notices are installed with AxePower.
/// </summary>
public class InstallerContentsTests
{
    private static string[] Script()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        return File.ReadAllLines(Path.Combine(dir!.FullName, "Installer", "AxePower.iss"));
    }

    [Theory]
    [InlineData("VersionInfoVersion={#AppVersion}")]
    [InlineData("VersionInfoProductVersion={#AppVersion}")]
    [InlineData("VersionInfoCompany={#AppPublisher}")]
    public void SetupExe_CarriesTheAppVersion(string line) => Assert.Contains(line, Script());

    [Theory]
    [InlineData(@"..\LICENSE")]
    [InlineData(@"..\SECURITY.md")]
    [InlineData(@"..\docs\security\PRIVACY.md")]
    [InlineData(@"..\THIRD_PARTY_NOTICES.md")]
    public void LegalAndSecurityDocuments_AreInstalled(string source) =>
        Assert.Contains(Script(), l => l.TrimStart().StartsWith("Source:") && l.Contains($"\"{source}\"") && l.Contains("DestDir: \"{app}\""));
}
