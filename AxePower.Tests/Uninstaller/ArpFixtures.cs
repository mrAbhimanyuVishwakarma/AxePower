using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// The uninstall entries captured from the audit PC (Fixtures/arp-entries.json, user name
/// redacted), turned back into classifier input.
/// </summary>
internal static partial class ArpFixtures
{
    public sealed record Entry(string KeyName, string RegistryKeyPath, UninstallEntryValues Values);

    private static readonly Lazy<IReadOnlyList<Entry>> Cached = new(LoadEntries);

    public static IReadOnlyList<Entry> Entries => Cached.Value;

    public static Entry Find(string displayName, string? keyName = null) =>
        Entries.First(e => e.Values.DisplayName == displayName && (keyName is null || e.KeyName == keyName));

    /// <summary>A file system in which every uninstaller the fixtures name exists (with Inno .dat files and rundll32 DLLs).</summary>
    public static FakeFileSystemProbe CompleteFileSystem()
    {
        var fs = new FakeFileSystemProbe();
        foreach (var entry in Entries)
        {
            var command = entry.Values.UninstallString;
            var exe = ExePath(command);
            if (exe is not null)
            {
                fs.AddFile(exe);
                if (InnoRegex().IsMatch(Path.GetFileName(exe)))
                    fs.AddFile(Path.ChangeExtension(exe, ".dat"));
            }
            foreach (Match dll in DllRegex().Matches(command))
                fs.AddFile(dll.Groups[1].Value);
        }
        return fs;
    }

    public static InstalledApplication? Classify(Entry entry, IFileSystemProbe fs, IMsiProductStateReader msi) =>
        UninstallEntryClassifier.Classify(entry.Values, entry.KeyName, entry.RegistryKeyPath, fs, msi);

    /// <summary>The program path a fixture command names, the way a human reads it.</summary>
    public static string? ExePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            var quoted = end > 0 ? command[1..end] : command[1..];
            return Path.IsPathFullyQualified(quoted) ? quoted : null;
        }
        var match = UnquotedExeRegex().Match(command);
        return match.Success && Path.IsPathFullyQualified(match.Value) ? match.Value : null;
    }

    private static IReadOnlyList<Entry> LoadEntries()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Uninstaller", "Fixtures", "arp-entries.json");
        var json = File.ReadAllText(path).Replace("<user>", "fixtureuser");
        using var doc = JsonDocument.Parse(json);

        var list = new List<Entry>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string S(string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
            int? I(string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

            var parent = S("ParentKey")
                .Replace("HKEY_LOCAL_MACHINE", "HKLM", StringComparison.OrdinalIgnoreCase)
                .Replace("HKEY_CURRENT_USER", "HKCU", StringComparison.OrdinalIgnoreCase);
            var keyName = S("KeyName");

            list.Add(new Entry(keyName, $@"{parent}\{keyName}", new UninstallEntryValues
            {
                DisplayName = S("DisplayName"),
                Publisher = S("Publisher"),
                DisplayVersion = S("DisplayVersion"),
                InstallLocation = S("InstallLocation"),
                DisplayIcon = S("DisplayIcon"),
                UninstallString = S("UninstallString"),
                QuietUninstallString = S("QuietUninstallString"),
                ParentKeyName = S("ParentKeyName"),
                ReleaseType = S("ReleaseType"),
                BundleCachePath = S("BundleCachePath"),
                BundleProviderKey = S("BundleProviderKey"),
                WindowsInstaller = I("WindowsInstaller"),
                SystemComponent = I("SystemComponent"),
                NoRemove = I("NoRemove"),
            }));
        }
        return list;
    }

    [GeneratedRegex(@"^[A-Za-z]:\\.*?\.exe(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex UnquotedExeRegex();

    [GeneratedRegex(@"""([^""]+\.dll)""", RegexOptions.IgnoreCase)]
    private static partial Regex DllRegex();

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InnoRegex();
}
