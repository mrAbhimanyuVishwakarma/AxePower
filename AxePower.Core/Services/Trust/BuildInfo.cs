using System.Reflection;
using AxePower.Core.Services.Update;

namespace AxePower.Core.Services.Trust;

/// <summary>What this build is: version, the commit it was built from, and how it is signed.</summary>
public static class BuildInfo
{
    private static readonly string Informational =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString() ?? "unknown";

    public static string Version => Informational.Split('+')[0];

    /// <summary>The git commit the build started from (12 characters), or null when it was built without git.</summary>
    public static string? Commit => Informational.Contains('+') ? Informational[(Informational.IndexOf('+') + 1)..] : null;

    public const string UpdateRepository = "github.com/mrAbhimanyuVishwakarma/AxePower_Download";

    /// <summary>Whether update manifests are checked against a pinned key, in plain words.</summary>
    public static string UpdateVerification => string.IsNullOrEmpty(UpdateSigningKey.Pinned)
        ? "Not set up: this build accepts no automatic updates."
        : UpdateSigningKey.ReleasesAreAuthenticodeSigned
            ? "Each update's manifest must be signed with AxePower's key, and the installer must carry AxePower's code signature."
            : "Each update's manifest must be signed with AxePower's key, and the installer must match the signed size and SHA-256.";

    /// <summary>The running program's Windows code signature, in plain words.</summary>
    public static string ExecutableSignature(IAuthenticodeVerifier? verifier = null)
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "Unknown (the program file could not be found).";
        var result = (verifier ?? new WindowsAuthenticodeVerifier(requireTrustedRoot: true)).Verify(path, expectedPublisher: string.Empty);
        return result.IsValid
            ? $"Signed by {result.Subject}."
            : "Not code-signed. Windows SmartScreen may warn when it is installed; updates are still checked as described below.";
    }
}
