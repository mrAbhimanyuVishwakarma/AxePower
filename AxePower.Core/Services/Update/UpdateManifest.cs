using System;
using System.Text;
using System.Text.Json.Serialization;

namespace AxePower.Core.Services.Update;

/// <summary>
/// Represents a cryptographically signed AxePower release manifest.
/// </summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("assetName")]
    public string AssetName { get; set; } = string.Empty;

    [JsonPropertyName("byteLength")]
    public long ByteLength { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    /// <summary>
    /// Gets deterministic canonical UTF-8 payload bytes for digital signature creation and verification.
    /// Format: "{Version}\n{AssetName}\n{ByteLength}\n{Sha256InLowercase}"
    /// </summary>
    public byte[] GetCanonicalPayloadBytes()
    {
        var canonical = $"{Version.Trim()}\n{AssetName.Trim()}\n{ByteLength}\n{Sha256.Trim().ToLowerInvariant()}";
        return Encoding.UTF8.GetBytes(canonical);
    }
}
