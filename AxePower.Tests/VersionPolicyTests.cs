using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using AxePower.Core.Services;
using Xunit;

namespace AxePower.Tests;

public class VersionPolicyTests
{
    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Directory.Build.props")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing Directory.Build.props.");
    }

    private static string GetAuthoritativeProductVersion(string repoRoot)
    {
        var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
        Assert.True(File.Exists(propsPath), "Directory.Build.props must exist at repository root.");

        var content = File.ReadAllText(propsPath);
        var match = Regex.Match(content, @"<ProductVersion>([^<]+)</ProductVersion>");
        Assert.True(match.Success, "Directory.Build.props must define <ProductVersion>.");
        return match.Groups[1].Value.Trim();
    }

    [Fact]
    public void DirectoryBuildProps_DefinesValidAuthoritativeProductVersion()
    {
        var repoRoot = FindRepoRoot();
        var version = GetAuthoritativeProductVersion(repoRoot);

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.True(Version.TryParse(version, out var parsed), $"ProductVersion '{version}' must parse as a valid System.Version.");
        Assert.True(parsed.Major >= 2, "ProductVersion major version must be at least 2.");
    }

    [Fact]
    public void Assemblies_ReflectAuthoritativeProductVersion()
    {
        var repoRoot = FindRepoRoot();
        var expectedVersionString = GetAuthoritativeProductVersion(repoRoot);
        var expectedVersion = Version.Parse(expectedVersionString);

        var coreVersion = typeof(UpdateService).Assembly.GetName().Version;
        Assert.NotNull(coreVersion);
        Assert.Equal(expectedVersion.Major, coreVersion.Major);
        Assert.Equal(expectedVersion.Minor, coreVersion.Minor);
        Assert.Equal(expectedVersion.Build, coreVersion.Build);
    }

    [Fact]
    public void UpdateService_FallbackVersion_MatchesAuthoritativeProductVersion()
    {
        var repoRoot = FindRepoRoot();
        var expectedVersion = GetAuthoritativeProductVersion(repoRoot);

        Assert.Equal(UpdateService.FallbackVersion, expectedVersion);
    }

    [Fact]
    public void InstallerScripts_ConsumeAppVersionIss()
    {
        var repoRoot = FindRepoRoot();
        var expectedVersion = GetAuthoritativeProductVersion(repoRoot);

        // 1. AppVersion.iss
        var appVersionIssPath = Path.Combine(repoRoot, "Installer", "AppVersion.iss");
        Assert.True(File.Exists(appVersionIssPath), "Installer/AppVersion.iss must exist.");
        var appVersionContent = File.ReadAllText(appVersionIssPath);
        var appVersionMatch = Regex.Match(appVersionContent, @"#define\s+AppVersion\s+""([^""]+)""");
        Assert.True(appVersionMatch.Success, "Installer/AppVersion.iss must contain #define AppVersion.");
        Assert.Equal(expectedVersion, appVersionMatch.Groups[1].Value.Trim());

        // 2. AxePower.iss
        var axePowerIssPath = Path.Combine(repoRoot, "Installer", "AxePower.iss");
        Assert.True(File.Exists(axePowerIssPath), "Installer/AxePower.iss must exist.");
        var axePowerContent = File.ReadAllText(axePowerIssPath);
        Assert.Contains(@"#include ""AppVersion.iss""", axePowerContent);
        var axePowerDefineMatch = Regex.Match(axePowerContent, @"^\s*#define\s+AppVersion\s+""([^""]+)""", RegexOptions.Multiline);
        Assert.False(axePowerDefineMatch.Success, "Installer/AxePower.iss must not hardcode #define AppVersion; it must include AppVersion.iss.");

        // 3. AxePowerSetup.iss
        var setupIssPath = Path.Combine(repoRoot, "Installer", "AxePowerSetup.iss");
        Assert.True(File.Exists(setupIssPath), "Installer/AxePowerSetup.iss must exist.");
        var setupContent = File.ReadAllText(setupIssPath);
        Assert.Contains(@"#include ""AppVersion.iss""", setupContent);
        Assert.Contains("AppVersion={#AppVersion}", setupContent);
        Assert.Contains("OutputBaseFilename=AxePower-v{#AppVersion}-Setup", setupContent);
    }

    [Theory]
    [InlineData("2.1.0", "2.1.0", true)]
    [InlineData("2.1.0", "2.0.3", false)]
    [InlineData("2.1.0", "9.9.9", false)]
    [InlineData("2.1.0", "", false)]
    public void VersionValidationLogic_DetectsDeliberateMismatches(string appVersion, string installerVersion, bool shouldPass)
    {
        bool matches = !string.IsNullOrEmpty(installerVersion) && string.Equals(appVersion, installerVersion, StringComparison.Ordinal);
        Assert.Equal(shouldPass, matches);
    }
}
