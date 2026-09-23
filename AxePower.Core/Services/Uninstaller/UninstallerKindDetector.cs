// Portions adapted from Bulk Crap Uninstaller (https://github.com/BCUninstaller/Bulk-Crap-Uninstaller)
// Copyright (c) 2017 Marcin Szeniak. Licensed under the Apache License, Version 2.0. Modified for AxePower.

using System.Text.RegularExpressions;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Decides what an uninstaller really is (BCU's <c>UninstallerTypeAdder.GetUninstallerType</c>,
/// extended with Burn bundles and Squirrel). The key name alone never makes an entry MSI: a GUID
/// key name only supplies the ProductCode when the entry also says <c>WindowsInstaller=1</c>.
/// </summary>
public static partial class UninstallerKindDetector
{
    /// <summary>NSIS detection scans at most this much of the uninstaller.</summary>
    public const long NsisScanLimitBytes = 16L * 1024 * 1024;

    public sealed record Detection(UninstallerKind Kind, string ProductCode, int MsiState);

    public static Detection Detect(
        UninstallCommand? command,
        UninstallEntryValues values,
        string keyName,
        IFileSystemProbe fs,
        IMsiProductStateReader msi)
    {
        var isWindowsInstaller = values.WindowsInstaller == 1;
        var pointsToMsiExec = command is not null && IsMsiExec(command.ExeName);

        // ── Rule 1: Windows Installer ───────────────────────────────────────
        var productCode = pointsToMsiExec || isWindowsInstaller
            ? ExtractProductCode(command?.Arguments)
            : string.Empty;
        if (productCode.Length == 0 && isWindowsInstaller && MsiProductStateReader.IsProductCode(keyName))
            productCode = keyName;

        if (productCode.Length > 0)
        {
            var state = msi.QueryProductState(productCode);
            if (state != MsiProductStateReader.InstallStateUnknown)
                return new Detection(UninstallerKind.Msi, productCode.ToUpperInvariant(), state);

            // Not registered any more. An entry that also names a real exe uninstaller keeps
            // that; otherwise nothing can uninstall it.
            if (command is null || pointsToMsiExec || !command.FileExists)
                return new Detection(UninstallerKind.BrokenEntry, string.Empty, state);
        }

        // ── Rule 8 (checked first for everything else): the uninstaller is missing ──
        if (command is null)
            return new Detection(UninstallerKind.BrokenEntry, string.Empty, int.MinValue);

        // msiexec with no ProductCode (e.g. "/x file.msi"): run it as registered
        if (pointsToMsiExec)
            return new Detection(UninstallerKind.Unknown, string.Empty, int.MinValue);

        if (!command.FileExists)
            return new Detection(UninstallerKind.BrokenEntry, string.Empty, int.MinValue);

        return new Detection(DetectExeKind(command, values, fs), string.Empty, int.MinValue);
    }

    private static UninstallerKind DetectExeKind(UninstallCommand command, UninstallEntryValues values, IFileSystemProbe fs)
    {
        var raw = command.Raw;
        var exe = command.ExeName;

        // Rule 2: WiX Burn bundle
        if (!string.IsNullOrWhiteSpace(values.BundleCachePath) ||
            !string.IsNullOrWhiteSpace(values.BundleProviderKey) ||
            IsPackageCacheBundle(command.FileName))
            return UninstallerKind.BurnBundle;

        // Rule 3: InstallShield
        if (raw.Contains(@"InstallShield Installation Information\{", StringComparison.OrdinalIgnoreCase))
            return UninstallerKind.InstallShield;

        // Rule 4: PowerShell
        if (exe.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
            exe.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains(".ps1", StringComparison.OrdinalIgnoreCase))
            return UninstallerKind.PowerShell;

        // Rule 5: Squirrel
        if (exe.Equals("Update.exe", StringComparison.OrdinalIgnoreCase) &&
            command.Arguments.Contains("--uninstall", StringComparison.OrdinalIgnoreCase))
            return UninstallerKind.Squirrel;

        // Rule 6: Inno Setup (unins###.exe with its .dat next to it)
        if (InnoExeRegex().IsMatch(exe) && fs.FileExists(Path.ChangeExtension(command.FileName, ".dat")))
            return UninstallerKind.InnoSetup;

        // Rule 7: NSIS
        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            fs.FileContainsAscii(command.FileName, "Nullsoft", NsisScanLimitBytes))
            return UninstallerKind.Nsis;

        return UninstallerKind.Unknown;
    }

    /// <summary>The ProductCode after /X or /I in an msiexec command, upper-cased; empty if absent.</summary>
    public static string ExtractProductCode(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return string.Empty;
        var match = ProductCodeArgRegex().Match(arguments);
        return match.Success && MsiProductStateReader.IsProductCode(match.Groups[1].Value)
            ? match.Groups[1].Value.ToUpperInvariant()
            : string.Empty;
    }

    public static bool IsMsiExec(string exeName) =>
        exeName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
        exeName.Equals("msiexec", StringComparison.OrdinalIgnoreCase);

    /// <summary>"…\Package Cache\{guid}\setup.exe" — how Burn caches bundles (machine or per-user).</summary>
    public static bool IsPackageCacheBundle(string path) =>
        PackageCacheRegex().IsMatch(path);

    [GeneratedRegex(@"[/-][XxIi]\s*(\{[0-9A-Fa-f-]{36}\})")]
    private static partial Regex ProductCodeArgRegex();

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InnoExeRegex();

    [GeneratedRegex(@"\\Package Cache\\\{[0-9A-Fa-f-]{36}\}[^\\]*\\[^\\]+\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex PackageCacheRegex();
}
