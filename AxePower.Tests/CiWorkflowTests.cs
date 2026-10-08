using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace AxePower.Tests;

public class CiWorkflowTests
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

    private static string GetWorkflowContent()
    {
        var repoRoot = FindRepoRoot();
        var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "ci.yml");
        Assert.True(File.Exists(workflowPath), ".github/workflows/ci.yml must exist.");
        return File.ReadAllText(workflowPath);
    }

    [Fact]
    public void CiWorkflow_ExistsAndIsNonEmpty()
    {
        var content = GetWorkflowContent();
        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.Contains("name: AxePower CI", content);
    }

    [Fact]
    public void CiWorkflow_HasLeastPrivilegePermissions()
    {
        var content = GetWorkflowContent();

        // Must specify contents: read
        Assert.Contains("contents: read", content);

        // Must not grant write permissions to contents or id-token
        Assert.DoesNotContain("contents: write", content);
        Assert.DoesNotContain("id-token: write", content);
    }

    [Fact]
    public void CiWorkflow_ContainsNoRepositorySecrets()
    {
        var content = GetWorkflowContent();

        // Must not reference any secrets (signing certs, passwords, etc.)
        var secretMatches = Regex.Matches(content, @"secrets\.([a-zA-Z0-9_]+)");
        Assert.Empty(secretMatches);
    }

    [Fact]
    public void CiWorkflow_PinsWindowsRunnerAndDotNetSdk()
    {
        var content = GetWorkflowContent();

        // Pinned Windows runner
        Assert.Contains("runs-on: windows-2022", content);

        // Pinned .NET 8 SDK
        Assert.Contains("dotnet-version: '8.0.403'", content);
    }

    [Fact]
    public void CiWorkflow_CollectsCoverageAndUploadsArtifacts()
    {
        var content = GetWorkflowContent();

        Assert.Contains(@"--collect:""XPlat Code Coverage""", content);
        Assert.Contains("name: test-results", content);
        Assert.Contains("name: code-coverage", content);
        Assert.Contains("actions/upload-artifact@", content);
    }

    [Fact]
    public void CiWorkflow_AuditsPackageVulnerabilities()
    {
        var content = GetWorkflowContent();
        Assert.Contains("audit-dependencies.ps1", content);

        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "audit-dependencies.ps1");
        Assert.True(File.Exists(scriptPath), "scripts/audit-dependencies.ps1 must exist.");

        var scriptContent = File.ReadAllText(scriptPath);
        Assert.Contains("--vulnerable", scriptContent);
        Assert.Contains("--include-transitive", scriptContent);
        Assert.Contains("(Critical|High)", scriptContent);
    }
}
