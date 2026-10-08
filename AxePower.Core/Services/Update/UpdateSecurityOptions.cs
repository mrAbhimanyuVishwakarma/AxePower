using System.Collections.Generic;

namespace AxePower.Core.Services.Update;

public sealed class UpdateSecurityOptions
{
    /// <summary>
    /// Trusted hosts allowed for update download requests and redirects.
    /// </summary>
    public HashSet<string> AllowedHosts { get; } = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com",
        // Where GitHub now redirects release downloads (seen 2026-10-08: without it every update
        // was refused). The file is still checked against the signed manifest's size and SHA-256.
        "release-assets.githubusercontent.com"
    };

    /// <summary>
    /// Trusted repository paths on GitHub.
    /// </summary>
    public HashSet<string> AllowedRepositories { get; } = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "mrAbhimanyuVishwakarma/AxePower",
        "mrAbhimanyuVishwakarma/AxePower_Download"
    };

    /// <summary>
    /// Pinned ECDSA P-256 public key (in PEM format or SubjectPublicKeyInfo Base64)
    /// used to verify the digital signature of the update manifest. Defaults to the key pinned in
    /// <see cref="UpdateSigningKey"/> (null until one is generated, so updates fail closed).
    /// </summary>
    public string? PinnedPublicKeyPem { get; set; } = UpdateSigningKey.Pinned;

    /// <summary>
    /// Expected signer identity (subject substring) for Authenticode-signed executables.
    /// </summary>
    public string ExpectedPublisher { get; set; } = "Abhimanyu Vishwakarma";

    /// <summary>
    /// Whether to fail closed when no pinned public key is configured.
    /// Default is true.
    /// </summary>
    public bool FailClosedIfKeyMissing { get; set; } = true;

    /// <summary>
    /// Whether to require a valid Authenticode signature on downloaded installers. Follows
    /// <see cref="UpdateSigningKey.ReleasesAreAuthenticodeSigned"/>: off while releases are unsigned,
    /// when the pinned manifest signature plus the signed size and SHA-256 decide.
    /// </summary>
    public bool RequireAuthenticode { get; set; } = UpdateSigningKey.ReleasesAreAuthenticodeSigned;

    /// <summary>
    /// Whether WinVerifyTrust must build to a trusted root CA.
    /// Default is true for production.
    /// </summary>
    public bool RequireTrustedRoot { get; set; } = true;

    /// <summary>
    /// Whether to reject updates with a version less than or equal to the currently installed version.
    /// Default is true.
    /// </summary>
    public bool EnforceDowngradeProtection { get; set; } = true;

    /// <summary>
    /// Testing-only switch allowing loopback HTTP endpoints. Must remain false in production.
    /// </summary>
    public bool AllowHttpForTesting { get; set; } = false;
}
