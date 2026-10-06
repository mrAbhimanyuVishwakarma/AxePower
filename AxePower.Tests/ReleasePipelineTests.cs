using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Xunit;

namespace AxePower.Tests;

public class ReleasePipelineTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AxePower.sln")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new InvalidOperationException("Could not locate AxePower.sln root.");
    }

    [Fact]
    public void Changelog_ExistsAndFollowsKeepAChangelogStandard()
    {
        string changelogPath = Path.Combine(RepoRoot, "CHANGELOG.md");
        Assert.True(File.Exists(changelogPath), "CHANGELOG.md must exist in repo root.");

        string content = File.ReadAllText(changelogPath);
        Assert.Contains("# Changelog", content);
        Assert.Contains("## [Unreleased]", content);
        Assert.Contains("## [2.1.0]", content);
        Assert.Contains("Keep a Changelog", content);
    }

    [Fact]
    public void GenerateSbomScript_ProducesValidCycloneDxJson()
    {
        string scriptPath = Path.Combine(RepoRoot, "scripts", "generate-sbom.ps1");
        Assert.True(File.Exists(scriptPath), "scripts/generate-sbom.ps1 must exist.");

        string tempSbom = Path.Combine(Path.GetTempPath(), $"axepower_test_sbom_{Guid.NewGuid():N}.json");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -OutputPath \"{tempSbom}\" -ProductVersion \"2.1.0\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(tempSbom), $"SBOM output was not created. Stderr: {stderr}");

            using var jsonDoc = JsonDocument.Parse(File.ReadAllText(tempSbom));
            var root = jsonDoc.RootElement;

            Assert.Equal("CycloneDX", root.GetProperty("bomFormat").GetString());
            Assert.Equal("1.5", root.GetProperty("specVersion").GetString());

            var metadata = root.GetProperty("metadata");
            var component = metadata.GetProperty("component");
            Assert.Equal("AxePower", component.GetProperty("name").GetString());
            Assert.Equal("2.1.0", component.GetProperty("version").GetString());

            var components = root.GetProperty("components");
            Assert.True(components.GetArrayLength() > 0, "SBOM components list must not be empty.");

            bool foundMvvm = false;
            foreach (var comp in components.EnumerateArray())
            {
                if (comp.GetProperty("name").GetString() == "CommunityToolkit.Mvvm")
                {
                    foundMvvm = true;
                    Assert.Contains("pkg:nuget/CommunityToolkit.Mvvm", comp.GetProperty("purl").GetString());
                    break;
                }
            }
            Assert.True(foundMvvm, "CommunityToolkit.Mvvm was expected in CycloneDX SBOM.");
        }
        finally
        {
            if (File.Exists(tempSbom))
            {
                File.Delete(tempSbom);
            }
        }
    }

    [Fact]
    public void GenerateProvenanceScript_ProducesMachineReadableProvenance()
    {
        string scriptPath = Path.Combine(RepoRoot, "scripts", "generate-provenance.ps1");
        Assert.True(File.Exists(scriptPath), "scripts/generate-provenance.ps1 must exist.");

        string tempProv = Path.Combine(Path.GetTempPath(), $"axepower_test_prov_{Guid.NewGuid():N}.json");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -OutputPath \"{tempProv}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(tempProv), $"Provenance file was not created. Stderr: {stderr}");

            using var jsonDoc = JsonDocument.Parse(File.ReadAllText(tempProv));
            var root = jsonDoc.RootElement;

            Assert.True(root.TryGetProperty("timestamp", out _));
            var buildDef = root.GetProperty("buildDefinition");
            Assert.True(buildDef.GetProperty("deterministic").GetBoolean());

            var env = buildDef.GetProperty("environment");
            Assert.True(env.TryGetProperty("dotnetSdkVersion", out var sdkVer));
            Assert.False(string.IsNullOrWhiteSpace(sdkVer.GetString()));

            Assert.True(env.TryGetProperty("dependencyLockSha256", out var lockSha));
            Assert.Equal(64, lockSha.GetString()?.Length);

            var workflow = buildDef.GetProperty("workflow");
            Assert.True(workflow.TryGetProperty("system", out _));
        }
        finally
        {
            if (File.Exists(tempProv))
            {
                File.Delete(tempProv);
            }
        }
    }

    [Fact]
    public void GenerateReleaseNotesScript_ExtractsTargetVersionAndAppendsVerification()
    {
        string scriptPath = Path.Combine(RepoRoot, "scripts", "generate-release-notes.ps1");
        Assert.True(File.Exists(scriptPath), "scripts/generate-release-notes.ps1 must exist.");

        string tempNotes = Path.Combine(Path.GetTempPath(), $"axepower_test_notes_{Guid.NewGuid():N}.md");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -TargetVersion \"2.1.0\" -OutputPath \"{tempNotes}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(tempNotes), $"Release notes file was not created. Stderr: {stderr}");

            string notesContent = File.ReadAllText(tempNotes);
            Assert.Contains("# AxePower v2.1.0", notesContent);
            Assert.Contains("Security & Verification", notesContent);
            Assert.Contains("sbom.cdx.json", notesContent);
            Assert.Contains("provenance.json", notesContent);
            Assert.Contains("Get-AuthenticodeSignature", notesContent);
        }
        finally
        {
            if (File.Exists(tempNotes))
            {
                File.Delete(tempNotes);
            }
        }
    }

    [Fact]
    public void ReleaseCandidateWorkflow_ConfiguredCorrectly()
    {
        string workflowPath = Path.Combine(RepoRoot, ".github", "workflows", "release-candidate.yml");
        Assert.True(File.Exists(workflowPath), "release-candidate.yml must exist.");

        string content = File.ReadAllText(workflowPath);
        Assert.Contains("tags:", content);
        Assert.Contains("- 'v*'", content);
        Assert.Contains("draft: true", content);
        Assert.Contains("prerelease: true", content);
        Assert.Contains("environment: release", content);
        Assert.Contains("package-release.ps1", content);
        Assert.Contains("-RequireSigned", content);
    }

    [Fact]
    public void PackageReleaseScript_ExistsAndExposesExpectedParameters()
    {
        string scriptPath = Path.Combine(RepoRoot, "scripts", "package-release.ps1");
        Assert.True(File.Exists(scriptPath), "scripts/package-release.ps1 must exist.");

        string content = File.ReadAllText(scriptPath);
        Assert.Contains("$Version", content);
        Assert.Contains("$OutputDir", content);
        Assert.Contains("$RequireSigned", content);
        Assert.Contains("$SkipTests", content);
        Assert.Contains("generate-sbom.ps1", content);
        Assert.Contains("generate-provenance.ps1", content);
        Assert.Contains("generate-release-notes.ps1", content);
        Assert.Contains("release-gate.ps1", content);
    }

    [Fact]
    public void PackageReleaseScript_InReleaseModeWithoutCertificate_FailsClosed()
    {
        string scriptPath = Path.Combine(RepoRoot, "scripts", "package-release.ps1");
        string testOut = Path.Combine(Path.GetTempPath(), $"axepower_failclosed_{Guid.NewGuid():N}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -OutputDir \"{testOut}\" -RequireSigned -SkipTests -SkipRestore -SkipBuild",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["SIGNING_CERT_PATH"] = "";
            psi.EnvironmentVariables["SIGNING_CERT_BASE64"] = "";
            psi.EnvironmentVariables["SIGNING_CERT_THUMBPRINT"] = "";

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.NotEqual(0, process.ExitCode);
            string combined = stdout + "\n" + stderr;
            Assert.True(
                combined.Contains("FAIL-CLOSED", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Signing failed", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Release gate rejected", StringComparison.OrdinalIgnoreCase),
                $"Expected fail-closed error in output. Output: {combined}");
        }
        finally
        {
            if (Directory.Exists(testOut))
            {
                Directory.Delete(testOut, true);
            }
        }
    }
}
