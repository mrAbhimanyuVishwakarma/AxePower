using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AxePower.Core.Services.Update;
using Xunit;

namespace AxePower.Tests;

/// <summary>
/// The owner's update-signing setup: the key tool pins a public key in the source, signs manifests
/// in exactly the format the app verifies, and the app's defaults follow the pinned key and the
/// "releases are unsigned" decision.
/// </summary>
public class UpdateSigningSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_UpdateSigning_" + Guid.NewGuid().ToString("N"));

    public UpdateSigningSetupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* test cleanup */ }
    }

    private static string RepoRoot()
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "AxePower.sln"))) root = Path.GetDirectoryName(root);
        return root ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string KeySource => File.ReadAllText(Path.Combine(RepoRoot(), "AxePower.Core", "Services", "Update", "UpdateSigningKey.cs"));

    private static string PinnedFrom(string source) =>
        Regex.Match(source, "public const string PublicKeyPem = \"(?<pem>[^\"]*)\";").Groups["pem"].Value.Replace("\\n", "\n");

    [Fact]
    public void RewriteSource_PinsThePublicKey_AndCanReplaceItLater()
    {
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var once = UpdateSigningKey.RewriteSource(KeySource, first.ExportSubjectPublicKeyInfoPem());
        Assert.Equal(first.ExportSubjectPublicKeyInfoPem().Trim(), PinnedFrom(once).Trim());

        var twice = UpdateSigningKey.RewriteSource(once, second.ExportSubjectPublicKeyInfoPem());
        Assert.Equal(second.ExportSubjectPublicKeyInfoPem().Trim(), PinnedFrom(twice).Trim());
        Assert.Equal(KeySource.Split('\n').Length, twice.Split('\n').Length); // only the one line changed
    }

    [Fact]
    public void RewriteSource_RefusesAPrivateKeyAndOtherCurves()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<ArgumentException>(() => UpdateSigningKey.RewriteSource(KeySource, key.ExportPkcs8PrivateKeyPem()));

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() => UpdateSigningKey.RewriteSource(KeySource, p384.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void SignedManifest_RoundTripsThroughJson_AndVerifiesOnlyAsSigned()
    {
        var installer = Path.Combine(_dir, "AxePower-v2.1.2-Setup.exe");
        File.WriteAllBytes(installer, RandomNumberGenerator.GetBytes(4096));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pinned = key.ExportSubjectPublicKeyInfoPem();

        var manifest = ManifestSigner.CreateSignedManifest(installer, "2.1.2", key);
        Assert.Equal("AxePower-v2.1.2-Setup.exe", manifest.AssetName);
        Assert.Equal(4096, manifest.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(installer))).ToLowerInvariant(), manifest.Sha256);

        // The app reads it with these options (UpdateService.DownloadManifestAsync).
        var json = JsonSerializer.Serialize(manifest);
        var read = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var verifier = new EcdsaManifestVerifier(pinned);
        Assert.True(verifier.VerifyManifest(read, read.Signature!, out var error), error);

        read.Sha256 = new string('0', 64);
        Assert.False(verifier.VerifyManifest(read, read.Signature!, out _));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(new EcdsaManifestVerifier(other.ExportSubjectPublicKeyInfoPem()).VerifyManifest(manifest, manifest.Signature!, out _));
    }

    [Fact]
    public void CreateSignedManifest_RefusesABadVersionOrMissingFile()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var installer = Path.Combine(_dir, "setup.exe");
        File.WriteAllBytes(installer, new byte[10]);
        Assert.Throws<ArgumentException>(() => ManifestSigner.CreateSignedManifest(installer, "v2.1.2", key));
        Assert.Throws<FileNotFoundException>(() => ManifestSigner.CreateSignedManifest(Path.Combine(_dir, "missing.exe"), "2.1.2", key));
    }

    [Fact]
    public void AppDefaults_FollowThePinnedKey_AndTheUnsignedReleaseDecision()
    {
        var options = new UpdateSecurityOptions();
        Assert.Equal(UpdateSigningKey.Pinned, options.PinnedPublicKeyPem);
        Assert.Equal(UpdateSigningKey.ReleasesAreAuthenticodeSigned, options.RequireAuthenticode);
        Assert.True(options.FailClosedIfKeyMissing);
        Assert.True(options.EnforceDowngradeProtection);

        // Whatever is pinned in the source is a usable P-256 public key (or nothing yet).
        var pinned = PinnedFrom(KeySource);
        if (pinned.Length > 0)
        {
            using var check = ECDsa.Create();
            check.ImportFromPem(pinned);
            Assert.Equal(256, check.KeySize);
            Assert.DoesNotContain("PRIVATE", pinned);
        }
    }

    [Fact]
    public void NoPrivateKeyIsCheckedIn()
    {
        var root = RepoRoot();
        var suspects = new[] { "AxePower.App", "AxePower.Core", "AxePower.Tests", "tools", "scripts", "Installer", "docs", ".github" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => f.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(suspects.Count == 0, "Key files must stay outside the repository:\n" + string.Join("\n", suspects));
    }
}
