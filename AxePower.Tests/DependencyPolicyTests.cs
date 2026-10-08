using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AxePower.Tests;

public class DependencyPolicyTests
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

    [Fact]
    public void DirectoryBuildProps_EnablesRestorePackagesWithLockFile()
    {
        var repoRoot = FindRepoRoot();
        var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
        Assert.True(File.Exists(propsPath), "Directory.Build.props must exist.");

        var content = File.ReadAllText(propsPath);
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", content);
        Assert.Contains("RestoreLockedMode", content);
    }

    [Theory]
    [InlineData("AxePower.App")]
    [InlineData("AxePower.Core")]
    [InlineData("AxePower.Tests")]
    [InlineData("tools/AxePower.UpdateSigning")]
    public void TrackedProjects_HaveValidPackageLockJson(string projectName)
    {
        var repoRoot = FindRepoRoot();
        var lockFilePath = Path.Combine(repoRoot, projectName, "packages.lock.json");

        Assert.True(File.Exists(lockFilePath), $"Project '{projectName}' must have a packages.lock.json committed.");

        var content = File.ReadAllText(lockFilePath);
        Assert.False(string.IsNullOrWhiteSpace(content));

        // Must be valid parseable JSON
        using var jsonDoc = JsonDocument.Parse(content);


        // Must define dependencies
        Assert.True(jsonDoc.RootElement.TryGetProperty("dependencies", out var dependencies) ||
                    jsonDoc.RootElement.TryGetProperty("version", out _));
    }

    [Fact]
    public void CiWorkflow_EnforcesLockedModeRestore()
    {
        var repoRoot = FindRepoRoot();
        var ciPath = Path.Combine(repoRoot, ".github", "workflows", "ci.yml");
        Assert.True(File.Exists(ciPath), ".github/workflows/ci.yml must exist.");

        var content = File.ReadAllText(ciPath);
        Assert.Contains("dotnet restore AxePower.sln --locked-mode", content);
    }

    [Fact]
    public void Dependabot_IsConfiguredWithoutAutoMerge()
    {
        var repoRoot = FindRepoRoot();
        var dependabotPath = Path.Combine(repoRoot, ".github", "dependabot.yml");
        Assert.True(File.Exists(dependabotPath), ".github/dependabot.yml must exist.");

        var content = File.ReadAllText(dependabotPath);
        Assert.Contains(@"package-ecosystem: ""nuget""", content);
        Assert.Contains(@"interval: ""weekly""", content);
        // Ensure no auto-merge is declared
        Assert.DoesNotContain("automerge", content, StringComparison.OrdinalIgnoreCase);
    }
}
