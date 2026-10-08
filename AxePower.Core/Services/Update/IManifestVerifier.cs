using System;
using System.Security.Cryptography;

namespace AxePower.Core.Services.Update;

public interface IManifestVerifier
{
    bool VerifyManifest(UpdateManifest manifest, string signatureBase64, out string? failureReason);
}

/// <summary>
/// Verifies digital signatures on UpdateManifest using an ECDSA P-256 public key.
/// Fails closed if no trusted public key is configured.
/// </summary>
public sealed class EcdsaManifestVerifier : IManifestVerifier
{
    private readonly string? _pinnedPublicKeyPem;

    public EcdsaManifestVerifier(string? pinnedPublicKeyPem = null)
    {
        _pinnedPublicKeyPem = pinnedPublicKeyPem;
    }

    public bool VerifyManifest(UpdateManifest manifest, string signatureBase64, out string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(_pinnedPublicKeyPem))
        {
            failureReason = "No trusted manifest signing public key is configured. Production updates fail closed.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(signatureBase64))
        {
            failureReason = "Manifest signature is missing or empty.";
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            
            if (_pinnedPublicKeyPem.Contains("BEGIN PUBLIC KEY", StringComparison.OrdinalIgnoreCase))
            {
                ecdsa.ImportFromPem(_pinnedPublicKeyPem);
            }
            else
            {
                var keyBytes = Convert.FromBase64String(_pinnedPublicKeyPem);
                ecdsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
            }

            var payloadBytes = manifest.GetCanonicalPayloadBytes();
            var signatureBytes = Convert.FromBase64String(signatureBase64);

            bool isValid = ecdsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256);
            if (!isValid)
            {
                failureReason = "Cryptographic signature does not match manifest contents or pinned public key.";
                return false;
            }

            failureReason = null;
            return true;
        }
        catch (Exception ex)
        {
            // Fails closed with the reason, which the update flow logs and shows.
            failureReason = $"Manifest signature verification error: {ex.Message}";
            return false;
        }
    }
}

/// <summary>
/// Cryptographic helper for signing update manifests.
/// </summary>
public static class ManifestSigner
{
    public static string Sign(UpdateManifest manifest, ECDsa privateKey)
    {
        var payload = manifest.GetCanonicalPayloadBytes();
        var signature = privateKey.SignData(payload, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Builds the signed manifest for an installer: its file name, exact size and SHA-256, signed
    /// with <paramref name="privateKey"/>. The app downloads the asset with this name and refuses it
    /// unless the size and hash match.
    /// </summary>
    public static UpdateManifest CreateSignedManifest(string installerPath, string version, ECDsa privateKey, DateTimeOffset? publishedAt = null)
    {
        if (!System.Version.TryParse(version, out _)) throw new ArgumentException($"'{version}' is not a version number like 2.1.2.", nameof(version));
        var info = new System.IO.FileInfo(installerPath);
        if (!info.Exists) throw new System.IO.FileNotFoundException("Installer not found.", installerPath);

        string sha256;
        using (var stream = info.OpenRead())
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        var manifest = new UpdateManifest
        {
            Version = version,
            AssetName = info.Name,
            ByteLength = info.Length,
            Sha256 = sha256,
            PublishedAt = publishedAt ?? DateTimeOffset.UtcNow,
        };
        manifest.Signature = Sign(manifest, privateKey);
        return manifest;
    }
}
