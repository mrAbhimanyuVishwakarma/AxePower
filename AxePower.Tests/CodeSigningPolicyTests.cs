using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace AxePower.Tests;

public class CodeSigningPolicyTests
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
    public void Repository_DoesNotContainCertificateOrPrivateKeyFiles()
    {
        var sensitiveExtensions = new[] { ".pfx", ".p12", ".snk", ".key", ".pem" };
        var repoDir = new DirectoryInfo(RepoRoot);

        // Exclude ignored external/cloned reference directories if present
        var excludedDirs = new[] { ".git", ".vs", "bin", "obj", "packages", "scratch", "LenovoLegionToolkit", "winutil" };

        var sensitiveFiles = repoDir.EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(f => sensitiveExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .Where(f => !excludedDirs.Any(ex => f.FullName.Split(Path.DirectorySeparatorChar).Contains(ex)))
            .Select(f => f.FullName)
            .ToList();

        Assert.Empty(sensitiveFiles);
    }

    [Fact]
    public void SignFileScript_ExistsAndExposesExpectedParameters()
    {
        string scriptPath = Path.Combine(RepoRoot, "scripts", "sign-file.ps1");
        Assert.True(File.Exists(scriptPath), "scripts/sign-file.ps1 must exist.");

        string content = File.ReadAllText(scriptPath);
        Assert.Contains("$FilePath", content);
        Assert.Contains("$CertPath", content);
        Assert.Contains("$CertBase64", content);
        Assert.Contains("$CertPassword", content);
        Assert.Contains("$CertThumbprint", content);
        Assert.Contains("$TimestampServer", content);
        Assert.Contains("$RequireSigned", content);
        Assert.Contains("http://timestamp.digicert.com", content);
    }

    [Fact]
    public void VerifySignatureScript_ExistsAndValidatesAuthenticodeAndTimestamp()
    {
        string scriptPath = Path.Combine(RepoRoot, "scripts", "verify-signature.ps1");
        Assert.True(File.Exists(scriptPath), "scripts/verify-signature.ps1 must exist.");

        string content = File.ReadAllText(scriptPath);
        Assert.Contains("Get-AuthenticodeSignature", content);
        Assert.Contains("$ExpectedSubject", content);
        Assert.Contains("TimeStamperCertificate", content);
        Assert.Contains("signtool", content);
    }

    [Fact]
    public void ReleaseGateScript_ExistsAndEnforcesFailClosedBehavior()
    {
        string scriptPath = Path.Combine(RepoRoot, "scripts", "release-gate.ps1");
        Assert.True(File.Exists(scriptPath), "scripts/release-gate.ps1 must exist.");

        string content = File.ReadAllText(scriptPath);
        Assert.Contains("$RequireSigned", content);
        Assert.Contains("verify-signature.ps1", content);
        Assert.Contains("RELEASE GATE FAILED", content);
        Assert.Contains("exit 1", content);
    }

    [Fact]
    public void InstallerScript_ConfiguresSignedUninstallerAndSignTool()
    {
        string issPath = Path.Combine(RepoRoot, "Installer", "AxePower.iss");
        Assert.True(File.Exists(issPath), "Installer/AxePower.iss must exist.");

        string content = File.ReadAllText(issPath);
        Assert.Contains("SignedUninstaller=yes", content);
        Assert.Contains("SignTool", content);
    }

    [Fact]
    public void CodeSigningDocumentation_ExistsAndDetailsSingleOwnerAction()
    {
        string docPath = Path.Combine(RepoRoot, "docs", "security", "CODE_SIGNING.md");
        Assert.True(File.Exists(docPath), "docs/security/CODE_SIGNING.md must exist.");

        string content = File.ReadAllText(docPath);
        Assert.Contains("Single External Action Required By Owner", content);
        Assert.Contains("SIGNING_CERT_BASE64", content);
        Assert.Contains("SIGNING_CERT_PASSWORD", content);
        Assert.Contains("RFC 3161", content);
        Assert.Contains("Get-AuthenticodeSignature", content);
        Assert.Contains("signtool verify", content);
    }

    [Fact]
    public void SignFileScript_InDevModeWithoutCertificate_SkipsGracefullyWithExitZero()
    {
        string scriptPath = Path.Combine(RepoRoot, "scripts", "sign-file.ps1");
        string dummyFile = Path.Combine(Path.GetTempPath(), $"axepower_test_{Guid.NewGuid():N}.tmp");
        File.WriteAllText(dummyFile, "test content");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -FilePath \"{dummyFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["SIGNING_CERT_PATH"] = "";
            psi.EnvironmentVariables["SIGNING_CERT_BASE64"] = "";
            psi.EnvironmentVariables["SIGNING_CERT_THUMBPRINT"] = "";
            psi.EnvironmentVariables["AXEPOWER_RELEASE_MODE"] = "0";

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("[SKIP]", stdout);
        }
        finally
        {
            if (File.Exists(dummyFile))
            {
                File.Delete(dummyFile);
            }
        }
    }

    [Fact]
    public void SignFileScript_InReleaseModeWithoutCertificate_FailsClosed()
    {
        string scriptPath = Path.Combine(RepoRoot, "scripts", "sign-file.ps1");
        string dummyFile = Path.Combine(Path.GetTempPath(), $"axepower_test_{Guid.NewGuid():N}.tmp");
        File.WriteAllText(dummyFile, "test content");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -FilePath \"{dummyFile}\" -RequireSigned",
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
            Assert.Contains("FAIL-CLOSED", stderr);
        }
        finally
        {
            if (File.Exists(dummyFile))
            {
                File.Delete(dummyFile);
            }
        }
    }

    [Fact]
    public void VerifySignatureScript_OnUnsignedFile_FailsClosedWhenRequired()
    {
        string scriptPath = Path.Combine(RepoRoot, "scripts", "verify-signature.ps1");
        string dummyFile = Path.Combine(Path.GetTempPath(), $"axepower_test_{Guid.NewGuid():N}.tmp");
        File.WriteAllText(dummyFile, "test content");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -FilePath \"{dummyFile}\" -FailIfUnsigned",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("VERIFICATION FAILED", stderr);
        }
        finally
        {
            if (File.Exists(dummyFile))
            {
                File.Delete(dummyFile);
            }
        }
    }
}
