using System.Text.RegularExpressions;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Builds the exact command that uninstalls an application. The confirmation dialog shows its
/// description, dry run prints it and the executors run it.
///
/// Switches are only added for kinds whose uninstallers are known to accept them; an
/// <see cref="UninstallerKind.Unknown"/> exe always runs exactly as registered.
/// </summary>
public static partial class UninstallCommandBuilder
{
    /// <param name="msiLogPath">Where msiexec writes its verbose log (MSI only).</param>
    public static UninstallPlan Build(InstalledApplication app, bool preferQuiet, IFileSystemProbe fs, string? msiLogPath = null)
    {
        var kind = app.UninstallerKind;

        switch (kind)
        {
            case UninstallerKind.BrokenEntry:
                return new UninstallPlan
                {
                    Kind = kind,
                    Description = "Remove broken entry (listing only; files are not touched)",
                };

            case UninstallerKind.StoreApp:
                if (!IsValidPackageFullName(app.PackageFullName))
                    return Error(kind, $"Invalid package name '{app.PackageFullName}'.");
                return new UninstallPlan
                {
                    Kind = kind,
                    FileName = PowerShellPath,
                    Arguments = $"Remove-AppxPackage -Package '{app.PackageFullName}'",
                    IsQuiet = true,
                    Description = "Store app (Remove-AppxPackage)",
                };

            case UninstallerKind.Msi:
                if (!MsiProductStateReader.IsProductCode(app.ProductCode))
                    return Error(kind, $"Invalid or missing MSI ProductCode '{app.ProductCode}'.");
                var ui = preferQuiet ? "/qn" : "/qb";
                var args = $"/x {app.ProductCode} {ui} /norestart";
                if (!string.IsNullOrEmpty(msiLogPath)) args += $" /l*v \"{msiLogPath}\"";
                return new UninstallPlan
                {
                    Kind = kind,
                    FileName = MsiExecPath,
                    Arguments = args,
                    IsQuiet = preferQuiet,
                    LogPath = msiLogPath ?? string.Empty,
                    Description = Describe(kind, preferQuiet),
                };
        }

        // Every other kind runs an exe, which must still exist.
        if (string.IsNullOrEmpty(app.UninstallerPath) || !fs.FileExists(app.UninstallerPath))
            return Error(kind, $"The uninstaller is missing: {app.UninstallerPath}");

        // A vendor-provided silent command wins over anything we would generate
        if (preferQuiet &&
            UninstallCommandParser.TryParse(app.QuietUninstallString, fs, out var quiet) && quiet.FileExists)
        {
            return new UninstallPlan
            {
                Kind = kind,
                FileName = quiet.FileName,
                Arguments = quiet.Arguments,
                IsQuiet = true,
                Description = $"{KindName(kind)}, silent (vendor command)",
            };
        }

        var registryArgs = app.UninstallerArguments;
        var (arguments, isQuiet) = kind switch
        {
            UninstallerKind.BurnBundle => preferQuiet
                ? ("/uninstall /quiet /norestart", true)
                : ("/uninstall", false),
            UninstallerKind.InnoSetup => preferQuiet
                ? ("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", true)
                : (string.Empty, false),
            // Registry args such as /allusers still matter for a silent run. Never pass _?=:
            // it stops the uninstaller from deleting its own folder.
            UninstallerKind.Nsis => preferQuiet
                ? (Join(RemoveSwitch(registryArgs, "S"), "/S"), true)
                : (registryArgs, false),
            UninstallerKind.Squirrel => preferQuiet
                ? ("--uninstall -s", true)
                : ("--uninstall", false),
            // InstallShield, PowerShell and Unknown: exactly as registered
            _ => (registryArgs, false),
        };

        return new UninstallPlan
        {
            Kind = kind,
            FileName = app.UninstallerPath,
            Arguments = arguments,
            IsQuiet = isQuiet,
            Description = Describe(kind, isQuiet),
        };
    }

    public static string MsiExecPath => Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    public static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static string KindName(UninstallerKind kind) => kind switch
    {
        UninstallerKind.Msi => "Windows Installer (msiexec)",
        UninstallerKind.BurnBundle => "WiX bundle",
        UninstallerKind.InnoSetup => "Inno Setup",
        UninstallerKind.Nsis => "NSIS",
        UninstallerKind.InstallShield => "InstallShield",
        UninstallerKind.Squirrel => "Squirrel",
        UninstallerKind.PowerShell => "PowerShell script",
        UninstallerKind.StoreApp => "Store app",
        UninstallerKind.BrokenEntry => "Broken entry",
        _ => "Vendor uninstaller",
    };

    /// <summary>
    /// A PackageFullName is only letters, digits, dots, underscores, hyphens and tildes, so it
    /// can sit inside a single-quoted PowerShell string without any escaping.
    /// </summary>
    public static bool IsValidPackageFullName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && PackageNameRegex().IsMatch(name);

    [GeneratedRegex(@"^[\w\.\-~]+$")]
    private static partial Regex PackageNameRegex();

    private static string Describe(UninstallerKind kind, bool quiet) =>
        $"{KindName(kind)}, {(quiet ? "silent" : "interactive")}";

    private static UninstallPlan Error(UninstallerKind kind, string error) =>
        new() { Kind = kind, Error = error, Description = $"{KindName(kind)}: {error}" };

    private static string Join(string a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : $"{a.Trim()} {b}";

    private static string RemoveSwitch(string args, string name) =>
        Regex.Replace(args ?? string.Empty, $@"(^|\s)/{name}(?=\s|$)", " ", RegexOptions.IgnoreCase).Trim();
}
