using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AxePower.Core.Services.Update;

/// <summary>
/// The public half of AxePower's update-signing key, pinned into the app. Only manifests signed
/// with the matching private key are trusted. <c>scripts/new-update-signing-key.ps1</c> writes the
/// key between the markers below; while it is empty, updates stay off (fail closed).
/// </summary>
public static class UpdateSigningKey
{
    // BEGIN GENERATED PUBLIC KEY (scripts/new-update-signing-key.ps1)
    public const string PublicKeyPem = "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjALCgqh/gA/YMfvMSsZvc6kyoPBG\nOWr5cdkZBgPm9oKnv32AEoEG0YtBrz5YcbYv+S5Inxyp2QOdzTDRoKb0ow==\n-----END PUBLIC KEY-----\n";
    // END GENERATED PUBLIC KEY

    /// <summary>
    /// Whether releases are Authenticode-signed. Releases are unsigned for now (no code-signing
    /// certificate), so updates are trusted on the pinned manifest signature plus the signed size
    /// and SHA-256. Set to true once installers are code-signed, and the Authenticode check returns.
    /// </summary>
    public const bool ReleasesAreAuthenticodeSigned = false;

    /// <summary>The pinned key, or null when none has been generated yet.</summary>
    public static string? Pinned => string.IsNullOrWhiteSpace(PublicKeyPem) ? null : PublicKeyPem;

    private static readonly Regex GeneratedBlock = new(
        @"(// BEGIN GENERATED PUBLIC KEY[^\n]*\n)(?<body>.*?)(\s*// END GENERATED PUBLIC KEY)",
        RegexOptions.Singleline);

    /// <summary>
    /// Returns <paramref name="source"/> (this file's text) with the pinned key replaced by
    /// <paramref name="publicKeyPem"/>. Used by the key-generation tool.
    /// </summary>
    public static string RewriteSource(string source, string publicKeyPem)
    {
        if (publicKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("That is a private key; only the public key may be pinned in the source.", nameof(publicKeyPem));
        using (var check = ECDsa.Create())
        {
            check.ImportFromPem(publicKeyPem); // throws on anything that is not a public key PEM
            if (check.KeySize != 256) throw new ArgumentException("The update-signing key must be ECDSA P-256.", nameof(publicKeyPem));
        }
        var literal = publicKeyPem.Trim().Replace("\r\n", "\n").Replace("\n", "\\n");
        var match = GeneratedBlock.Match(source);
        if (!match.Success) throw new InvalidOperationException("The generated public-key markers were not found.");
        var body = match.Groups["body"];
        return source[..body.Index] + $"    public const string PublicKeyPem = \"{literal}\\n\";" + source[(body.Index + body.Length)..];
    }
}
