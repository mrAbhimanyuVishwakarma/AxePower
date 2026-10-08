using System;
using System.Collections.Generic;
using System.IO;
using AxePower.Core.Models;
using AxePower.Core.Services.Processes;

namespace AxePower.Core.Services.SoftwareHub;

public enum PackageAction
{
    Install,
    Upgrade,
    Uninstall
}

public enum StepCondition
{
    Always,
    /// <summary>Only when no earlier step has succeeded (a fallback).</summary>
    IfNotYetSucceeded
}

/// <summary>One thing a plan does, and the rights it runs with.</summary>
public abstract record PlanStep(StepCondition When, RunAs RunAs);

/// <summary>Runs winget with fixed, separately passed arguments.</summary>
public sealed record WinGetStep(ProcessCommand Command, string? Notice = null, StepCondition When = StepCondition.Always)
    : PlanStep(When, RunAs.CurrentProcess);

/// <summary>Succeeds when the on-disk check finds the package (the exit code can lie for wrapped installers).</summary>
public sealed record CheckInstalledStep(StepCondition When, bool ReportWhenFound = false)
    : PlanStep(When, RunAs.CurrentProcess);

/// <summary>Opens an official page with the user's normal rights. Never counts as success.</summary>
public sealed record OpenWebPageStep(string Url, IReadOnlyList<string> Messages, StepCondition When)
    : PlanStep(When, RunAs.StandardUser);

/// <summary>Downloads a portable tool and starts it with the user's normal rights.</summary>
public sealed record DownloadPortableToolStep(PortableToolSpec Spec, string Folder)
    : PlanStep(StepCondition.Always, RunAs.StandardUser);

/// <summary>Deletes a portable tool's folder under the AxePower tools folder.</summary>
public sealed record RemoveToolFolderStep(string Folder, bool MissingCountsAsRemoved, string Label)
    : PlanStep(StepCondition.Always, RunAs.CurrentProcess);

/// <summary>Starts a bundled installer that sits next to AxePower; offers its web page when it is missing.</summary>
public sealed record LocalInstallerStep(string InstallerPath, string FallbackUrl)
    : PlanStep(StepCondition.Always, RunAs.CurrentProcess);

/// <summary>What will be done for one package action, decided before anything runs.</summary>
public sealed record InstallPlan(
    PackageAction Action,
    string TargetId,
    string DisplayName,
    SoftwarePackageItem? Package,
    IReadOnlyList<PlanStep> Steps,
    string? RefusedReason = null)
{
    public bool IsRefused => RefusedReason != null;
}

/// <summary>
/// Turns "install/update/uninstall this" into an <see cref="InstallPlan"/>. Pure: no process,
/// file or network access, so every decision (arguments, fallbacks, refusals, rights) is testable.
/// An identifier that fails <see cref="PackageInput.IsValidWinGetId"/> is refused here, before any
/// command exists.
/// </summary>
public sealed class InstallPlanner
{
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan UninstallTimeout = TimeSpan.FromSeconds(35);
    public static readonly TimeSpan UpgradeAllTimeout = TimeSpan.FromMinutes(5);

    private readonly SoftwareCatalog _catalog;
    private readonly string _toolsRoot;
    private readonly string _appDirectory;

    public InstallPlanner(SoftwareCatalog catalog, string? toolsRoot = null, string? appDirectory = null)
    {
        _catalog = catalog;
        _toolsRoot = toolsRoot ?? PackageDiscovery.DefaultToolsRoot;
        _appDirectory = appDirectory ?? AppContext.BaseDirectory;
    }

    public string ToolsRoot => _toolsRoot;

    public InstallPlan Plan(PackageAction action, string requestedId)
    {
        requestedId ??= string.Empty;
        var pkg = _catalog.Find(requestedId);
        var targetId = pkg?.WinGetId ?? requestedId;
        var name = pkg?.Name ?? (targetId.Length > 0 ? targetId : requestedId);

        InstallPlan Make(params PlanStep[] steps) => new(action, targetId, name, pkg, steps);
        InstallPlan Refuse(string reason) => new(action, targetId, name, pkg, Array.Empty<PlanStep>(), reason);

        switch (pkg?.Id)
        {
            case "dlss5swapper" when action is PackageAction.Install or PackageAction.Upgrade:
                return Make(new DownloadPortableToolStep(PortableTools.Dlss5Swapper, Path.Combine(_toolsRoot, PortableTools.Dlss5SwapperFolder)));
            case "dlss5swapper":
                return Make(new RemoveToolFolderStep(Path.Combine(_toolsRoot, PortableTools.Dlss5SwapperFolder), true, "DLSS 5 Swapper"));
            case "camstudio" when action == PackageAction.Install:
                // Not published on WinGet, so a winget attempt can only fail. Send the user to the
                // official download instead; the card flips to Open once the install is detected.
                return Make(
                    new CheckInstalledStep(StepCondition.Always, ReportWhenFound: true),
                    new OpenWebPageStep(pkg!.WebUrl, new[]
                    {
                        $"\n[Install] {name} is not available on WinGet. Opening the official download page: {pkg.WebUrl}",
                        "After installing it, use Refresh and AxePower will pick it up."
                    }, StepCondition.IfNotYetSucceeded));
            case "camstudio" when action == PackageAction.Uninstall:
                if (!PackageInput.IsValidWinGetId(targetId)) return Refuse(InvalidId(targetId));
                return Make(
                    new RemoveToolFolderStep(Path.Combine(_toolsRoot, "CamStudio"), false, "CamStudio"),
                    new WinGetStep(WinGetUninstall(targetId, silent: true), When: StepCondition.IfNotYetSucceeded),
                    new WinGetStep(WinGetUninstall(targetId, silent: false), SilentUninstallFailedNotice, StepCondition.IfNotYetSucceeded));
        }

        if (action == PackageAction.Install && pkg is { HasCustomInstaller: true })
        {
            var fallback = pkg.CustomWebUrl.Length > 0 ? pkg.CustomWebUrl : pkg.WebUrl;
            return pkg.LocalInstallerPath.Length > 0 && PackageInput.IsStrictlyInside(pkg.LocalInstallerPath, _appDirectory)
                ? Make(new LocalInstallerStep(pkg.LocalInstallerPath, fallback))
                : Make(new LocalInstallerStep(string.Empty, fallback));
        }

        if (targetId.Length == 0)
        {
            if (action == PackageAction.Install && pkg is { WebUrl.Length: > 0 })
                return Make(new OpenWebPageStep(pkg.WebUrl, new[] { DirectWebSetup(name, pkg.WebUrl) }, StepCondition.Always));
            return Refuse($"{name} has no WinGet package.");
        }
        if (!PackageInput.IsValidWinGetId(targetId)) return Refuse(InvalidId(targetId));

        switch (action)
        {
            case PackageAction.Install:
                var steps = new List<PlanStep> { new WinGetStep(WinGetInstall(targetId)) };
                if (pkg != null) steps.Add(new CheckInstalledStep(StepCondition.IfNotYetSucceeded));
                if (pkg is { WebUrl.Length: > 0 })
                    steps.Add(new OpenWebPageStep(pkg.WebUrl, new[] { DirectWebSetup(name, pkg.WebUrl) }, StepCondition.IfNotYetSucceeded));
                return Make(steps.ToArray());
            case PackageAction.Upgrade:
                return Make(new WinGetStep(WinGetUpgrade(targetId)));
            default:
                return Make(
                    new WinGetStep(WinGetUninstall(targetId, silent: true)),
                    new WinGetStep(WinGetUninstall(targetId, silent: false), SilentUninstallFailedNotice, StepCondition.IfNotYetSucceeded));
        }
    }

    private const string SilentUninstallFailedNotice = "\n[Notice] Silent uninstall non-zero. Attempting interactive uninstall...";

    private static string InvalidId(string id) => $"'{id}' is not a valid package identifier, so nothing was run.";

    private static string DirectWebSetup(string name, string url) => $"\n[Direct Web Setup] Opening official download portal for {name}: {url}";

    // -- winget command lines: fixed verbs and flags, the identifier as its own argument ----------

    public static ProcessCommand WinGetInstall(string id) => new(AllowedTool.WinGet, new[]
    {
        "install", "--id", RequireValid(id), "--exact", "--source", PackageInput.WinGetSource(id),
        "--accept-source-agreements", "--accept-package-agreements", "--silent"
    }, InstallTimeout);

    public static ProcessCommand WinGetUpgrade(string id) => new(AllowedTool.WinGet, new[]
    {
        "upgrade", "--id", RequireValid(id), "--exact", "--source", PackageInput.WinGetSource(id),
        "--accept-source-agreements", "--accept-package-agreements", "--silent"
    }, InstallTimeout);

    public static ProcessCommand WinGetUninstall(string id, bool silent)
    {
        var args = new List<string> { "uninstall", "--id", RequireValid(id), "--exact" };
        if (silent) args.Add("--silent");
        return new ProcessCommand(AllowedTool.WinGet, args, UninstallTimeout);
    }

    public static ProcessCommand WinGetUpgradeAll() => new(AllowedTool.WinGet, new[]
    {
        "upgrade", "--all", "--include-unknown", "--accept-source-agreements", "--accept-package-agreements"
    }, UpgradeAllTimeout);

    private static string RequireValid(string id) =>
        PackageInput.IsValidWinGetId(id) ? id : throw new ArgumentException($"'{id}' is not a valid package identifier.", nameof(id));
}
