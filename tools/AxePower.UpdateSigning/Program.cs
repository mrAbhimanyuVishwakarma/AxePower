using System.Security.Cryptography;
using System.Text.Json;
using AxePower.Core.Services.Update;

namespace AxePower.UpdateSigning;

/// <summary>
/// new-key: creates the update-signing key pair, saves the private key where you say, and pins the
///          public key in AxePower.Core/Services/Update/UpdateSigningKey.cs.
/// sign:    writes update-manifest.json for an installer, signed with the private key, and checks
///          the result against the pinned public key before writing it.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "new-key" => NewKey(Options(args)),
                "sign" => Sign(Options(args)),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or CryptographicException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              new-key --key-file <path to save the PRIVATE key> --repo <repository root> [--force]
              sign    --installer <setup.exe> --version <x.y.z> --key-file <private key> --repo <repository root> [--out <update-manifest.json>]
            The private key may also come from the UPDATE_SIGNING_KEY environment variable (PEM text).
            """);
        return 2;
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            var name = args[i][2..];
            options[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }
        return options;
    }

    private static string Require(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"--{name} is required.");

    private static string KeySourceFile(string repo) => Path.Combine(repo, "AxePower.Core", "Services", "Update", "UpdateSigningKey.cs");

    private static int NewKey(Dictionary<string, string> options)
    {
        var keyFile = Path.GetFullPath(Require(options, "key-file"));
        var repo = Path.GetFullPath(Require(options, "repo"));
        var sourceFile = KeySourceFile(repo);
        if (!File.Exists(sourceFile)) throw new IOException($"{sourceFile} not found; pass the repository root as --repo.");
        if (File.Exists(keyFile) && !options.ContainsKey("force"))
            throw new IOException($"{keyFile} already exists. A new key would make updates signed with the old one untrusted; pass --force only if you mean it.");
        if (keyFile.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Save the private key outside the repository, so it can never be committed.");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem());
        var publicPem = key.ExportSubjectPublicKeyInfoPem();

        var source = File.ReadAllText(sourceFile);
        File.WriteAllText(sourceFile, UpdateSigningKey.RewriteSource(source, publicPem));

        Console.WriteLine($"Private key saved to: {keyFile}");
        Console.WriteLine("  Keep it secret. Store a copy in your password manager; anyone with it can sign updates.");
        Console.WriteLine($"Public key pinned in: {sourceFile}");
        Console.WriteLine("  Commit that file and build: from then on the app only accepts manifests signed with this key.");
        Console.WriteLine();
        Console.WriteLine(publicPem);
        return 0;
    }

    private static int Sign(Dictionary<string, string> options)
    {
        var installer = Path.GetFullPath(Require(options, "installer"));
        var version = Require(options, "version");
        var repo = Path.GetFullPath(Require(options, "repo"));
        var outPath = Path.GetFullPath(options.TryGetValue("out", out var o) ? o : Path.Combine(Path.GetDirectoryName(installer)!, "update-manifest.json"));

        // Checked against the key pinned in the source, so a wrong or old key cannot produce a
        // manifest that every installed copy would then reject.
        var pinned = PinnedKey(KeySourceFile(repo));

        var pem = Environment.GetEnvironmentVariable("UPDATE_SIGNING_KEY");
        if (string.IsNullOrWhiteSpace(pem)) pem = File.ReadAllText(Require(options, "key-file"));
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        var manifest = ManifestSigner.CreateSignedManifest(installer, version, key);
        if (!new EcdsaManifestVerifier(pinned).VerifyManifest(manifest, manifest.Signature!, out var failure))
            throw new InvalidOperationException("The signature does not verify with the public key pinned in the app: " + failure);

        File.WriteAllText(outPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine($"Wrote {outPath}");
        Console.WriteLine($"  {manifest.AssetName}, {manifest.ByteLength} bytes, sha256 {manifest.Sha256}");
        Console.WriteLine("Upload update-manifest.json to the same GitHub release as the installer.");
        return 0;
    }

    /// <summary>The pinned key as written in the source file (the running tool may be older).</summary>
    private static string PinnedKey(string sourceFile)
    {
        var text = File.ReadAllText(sourceFile);
        var match = System.Text.RegularExpressions.Regex.Match(text, "public const string PublicKeyPem = \"(?<pem>[^\"]*)\";");
        var pem = match.Success ? match.Groups["pem"].Value.Replace("\\n", "\n") : string.Empty;
        if (string.IsNullOrWhiteSpace(pem)) throw new InvalidOperationException("No public key is pinned yet. Run scripts/new-update-signing-key.ps1 first.");
        return pem;
    }
}
