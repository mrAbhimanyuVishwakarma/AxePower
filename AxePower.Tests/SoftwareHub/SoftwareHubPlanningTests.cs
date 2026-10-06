using System;
using System.IO;
using System.Linq;
using AxePower.Core.Models;
using AxePower.Core.Services.Processes;
using AxePower.Core.Services.SoftwareHub;
using Xunit;

namespace AxePower.Tests.SoftwareHub;

/// <summary>
/// The decisions made before anything runs: which identifiers, addresses and file names are
/// accepted, what winget is asked to do, the fallbacks, and which rights each step gets.
/// </summary>
public class SoftwareHubPlanningTests
{
    public static TheoryData<string> MaliciousIds => new()
    {
        "--override",                                   // read as an option
        "-h",
        "Valve.Steam --override \"/S /D=C:\\evil\"",     // extra arguments smuggled in one value
        "Valve.Steam\" --source evil \"",
        "Valve.Steam --source=evil",
        "Valve.Steam&calc",
        "Valve.Steam;calc",
        "Valve.Steam|calc",
        "Valve.Steam`ncalc",
        "Valve.Steam$(calc)",
        "Valve.Steam\ncalc",
        "Valve.Steam\rcalc",
        "Valve.Steam\tx",
        "Valve Steam",
        "..\\..\\Windows\\System32\\calc",
        "Valve..Steam",
        "Valve.Steam*",
        "%COMSPEC%",
        "Ｖalve.Steam",                                  // full-width look-alike
        "Valve.Steam\u0000x",
        "",
        new string('a', 129),
    };

    [Theory]
    [MemberData(nameof(MaliciousIds))]
    public void MaliciousIdentifier_IsRejected_AndPlansNothing(string id)
    {
        Assert.False(PackageInput.IsValidWinGetId(id));

        var planner = new InstallPlanner(SoftwareCatalog.CreateDefault(), Path.GetTempPath(), Path.GetTempPath());
        foreach (var action in Enum.GetValues<PackageAction>())
        {
            var plan = planner.Plan(action, id);
            Assert.True(plan.IsRefused, $"{action} '{id}' was planned");
            Assert.Empty(plan.Steps);
        }
        Assert.Throws<ArgumentException>(() => InstallPlanner.WinGetInstall(id));
    }

    [Fact]
    public void EveryBuiltInEntry_PassesValidation()
    {
        var catalog = SoftwareCatalog.CreateDefault();
        Assert.Empty(catalog.Rejected);
        Assert.Equal(SoftwareCatalog.CreateDefaultItems().Count, catalog.Items.Count);
        Assert.All(catalog.Items.Where(i => i.WinGetId.Length > 0), i => Assert.True(PackageInput.IsValidWinGetId(i.WinGetId), i.WinGetId));
    }

    [Fact]
    public void CatalogEntryWithHostileData_IsLeftOut_NotOffered()
    {
        SoftwarePackageItem Item(string id, Action<SoftwarePackageItem> change)
        {
            var item = new SoftwarePackageItem { Id = id, Name = id, WinGetId = "Good.Package" };
            change(item);
            return item;
        }

        var catalog = new SoftwareCatalog(new[]
        {
            Item("ok", _ => { }),
            Item("bad-id", i => i.WinGetId = "--override x"),
            Item("bad-url", i => i.WebUrl = "javascript:alert(1)"),
            Item("file-url", i => i.WebUrl = "file:///C:/Windows/System32/calc.exe"),
            Item("bad-protocol", i => i.LaunchProtocolOrExe = "ms-msdt://evil"),
            Item("relative-exe", i => i.LaunchProtocolOrExe = "tools\\x.exe"),
            Item("batch-file", i => i.LaunchProtocolOrExe = "C:\\x\\run.bat"),
            Item("relative-installer", i => i.LocalInstallerPath = "setup.exe"),
            Item("Bad Catalog Id", _ => { }),
            Item("ok", i => i.Name = "duplicate"),
        });

        Assert.Equal(new[] { "ok" }, catalog.Items.Select(i => i.Id));
        Assert.Equal(9, catalog.Rejected.Count);
    }

    [Fact]
    public void InstallCommand_IsFixedVerbsAndFlags_WithTheIdAsOneArgument()
    {
        var command = InstallPlanner.WinGetInstall("Microsoft.VCRedist.2015+.x64");
        Assert.Equal(AllowedTool.WinGet, command.Tool);
        Assert.Equal(new[]
        {
            "install", "--id", "Microsoft.VCRedist.2015+.x64", "--exact", "--source", "winget",
            "--accept-source-agreements", "--accept-package-agreements", "--silent"
        }, command.Arguments);
        Assert.Equal(InstallPlanner.InstallTimeout, command.Timeout);

        Assert.Equal("msstore", InstallPlanner.WinGetInstall("9N0866FS04B8").Arguments[5]);
        Assert.Equal(new[] { "uninstall", "--id", "Valve.Steam", "--exact", "--silent" }, InstallPlanner.WinGetUninstall("Valve.Steam", silent: true).Arguments);
        Assert.Equal(new[] { "uninstall", "--id", "Valve.Steam", "--exact" }, InstallPlanner.WinGetUninstall("Valve.Steam", silent: false).Arguments);
    }

    [Fact]
    public void ProcessCommand_RejectsNulAndNull_AndDisplayIsOnlyForLogs()
    {
        Assert.Throws<ArgumentException>(() => new ProcessCommand(AllowedTool.WinGet, new[] { "a\0b" }, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new ProcessCommand(AllowedTool.WinGet, new string[] { null! }, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessCommand(AllowedTool.WinGet, Array.Empty<string>(), TimeSpan.Zero));
        Assert.Equal("winget list \"a b\" \"\"", new ProcessCommand(AllowedTool.WinGet, new[] { "list", "a b", "" }, TimeSpan.FromSeconds(1)).Display);
    }

    [Fact]
    public void Plans_HaveTheExpectedStepsFallbacksAndRights()
    {
        var root = Path.Combine(Path.GetTempPath(), "AxePower_Plan_" + Guid.NewGuid().ToString("N"));
        var planner = new InstallPlanner(SoftwareCatalog.CreateDefault(), Path.Combine(root, "Tools"), Path.Combine(root, "App"));

        // Install: winget, then the on-disk check, then the official page, each only if the one before failed.
        var install = planner.Plan(PackageAction.Install, "steam");
        Assert.Equal("Valve.Steam", install.TargetId);
        Assert.Collection(install.Steps,
            s => Assert.True(s is WinGetStep { When: StepCondition.Always, RunAs: RunAs.CurrentProcess }),
            s => Assert.True(s is CheckInstalledStep { When: StepCondition.IfNotYetSucceeded }),
            s => Assert.True(s is OpenWebPageStep { When: StepCondition.IfNotYetSucceeded, RunAs: RunAs.StandardUser }));

        // Lookup by WinGet id finds the same entry.
        Assert.Equal("steam", planner.Plan(PackageAction.Install, "valve.steam").Package!.Id);

        // Uninstall: silent, then interactive only if silent failed.
        var uninstall = planner.Plan(PackageAction.Uninstall, "steam");
        Assert.Collection(uninstall.Steps,
            s => Assert.Contains("--silent", ((WinGetStep)s).Command.Arguments),
            s => Assert.True(s is WinGetStep { When: StepCondition.IfNotYetSucceeded } w && !w.Command.Arguments.Contains("--silent")));

        // CamStudio is not on WinGet: check the disk, then its page. No winget at all.
        var cam = planner.Plan(PackageAction.Install, "camstudio");
        Assert.DoesNotContain(cam.Steps, s => s is WinGetStep);
        Assert.Equal("https://camstudio.org/", Assert.Single(cam.Steps.OfType<OpenWebPageStep>()).Url);

        // DLSS 5 Swapper: a portable download that starts as the signed-in user; uninstall removes its folder.
        var dlss = Assert.IsType<DownloadPortableToolStep>(Assert.Single(planner.Plan(PackageAction.Upgrade, "dlss5swapper").Steps));
        Assert.Equal(RunAs.StandardUser, dlss.RunAs);
        Assert.Equal(Path.Combine(root, "Tools", PortableTools.Dlss5SwapperFolder), dlss.Folder);
        var remove = Assert.IsType<RemoveToolFolderStep>(Assert.Single(planner.Plan(PackageAction.Uninstall, "dlss5swapper").Steps));
        Assert.True(remove.MissingCountsAsRemoved);

        // Ninite's installer path is next to the real app, not this test's app folder: not used.
        var ninite = Assert.IsType<LocalInstallerStep>(Assert.Single(planner.Plan(PackageAction.Install, "ninite").Steps));
        Assert.Equal(string.Empty, ninite.InstallerPath);
        Assert.Equal("https://ninite.com/", ninite.FallbackUrl);

        // An id the catalog does not know but that is well formed (Phone Hub's Spacedesk) is still allowed.
        Assert.Equal("Datronicsoft.SpacedeskDriver.Server", Assert.IsType<WinGetStep>(planner.Plan(PackageAction.Install, "Datronicsoft.SpacedeskDriver.Server").Steps[0]).Command.Arguments[2]);
    }

    [Fact]
    public void LocalInstaller_IsUsedOnlyInsideTheAppFolder()
    {
        var app = Path.Combine(Path.GetTempPath(), "AxePower_App_" + Guid.NewGuid().ToString("N"));
        SoftwarePackageItem Ninite(string path) => new() { Id = "ninite", Name = "Ninite", LocalInstallerPath = path, CustomWebUrl = "https://ninite.com/" };

        var inside = new InstallPlanner(new SoftwareCatalog(new[] { Ninite(Path.Combine(app, "Ninite Installer.exe")) }), app, app);
        Assert.Equal(Path.Combine(app, "Ninite Installer.exe"), Assert.IsType<LocalInstallerStep>(inside.Plan(PackageAction.Install, "ninite").Steps[0]).InstallerPath);

        var escaping = new InstallPlanner(new SoftwareCatalog(new[] { Ninite(Path.Combine(app, "..", "Downloads", "Ninite Installer.exe")) }), app, app);
        Assert.Equal(string.Empty, Assert.IsType<LocalInstallerStep>(escaping.Plan(PackageAction.Install, "ninite").Steps[0]).InstallerPath);
    }

    [Theory]
    [InlineData(RunAs.StandardUser, true, LaunchRoute.ViaUserShell)]
    [InlineData(RunAs.StandardUser, false, LaunchRoute.Direct)]
    [InlineData(RunAs.CurrentProcess, true, LaunchRoute.Direct)]
    [InlineData(RunAs.CurrentProcess, false, LaunchRoute.Direct)]
    [InlineData(RunAs.Administrator, true, LaunchRoute.Direct)]
    [InlineData(RunAs.Administrator, false, LaunchRoute.Refuse)]
    public void ElevationDecision_Matrix(RunAs need, bool elevated, LaunchRoute expected) =>
        Assert.Equal(expected, ElevationPolicy.Decide(need, elevated));

    [Theory]
    [InlineData("https://ninite.com/", true)]
    [InlineData("http://example.com/a?b=c&d=e", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("https://user:pw@example.com/", false)]
    [InlineData("https://exa mple.com/", false)]
    [InlineData("https://example.com/\"x", false)]
    [InlineData("//example.com/", false)]
    [InlineData("C:\\Windows\\System32\\calc.exe", false)]
    public void WebUrls(string url, bool ok) => Assert.Equal(ok, PackageInput.TryWebUrl(url, out _));

    [Theory]
    [InlineData("https://us.download.nvidia.com/Windows/572.70/x.exe", true)]
    [InlineData("https://download.nvidia.com/x.exe", true)]
    [InlineData("http://us.download.nvidia.com/x.exe", false)]
    [InlineData("https://download.nvidia.com.evil.example/x.exe", false)]
    [InlineData("https://evildownload.nvidia.com/x.exe", false)]
    [InlineData("https://evil.example/download.nvidia.com/x.exe", false)]
    public void DownloadHosts(string url, bool ok) => Assert.Equal(ok, PackageInput.TryDownloadUrl(url, new[] { "download.nvidia.com" }, out _));

    [Theory]
    [InlineData("https://h/a/setup.exe", "setup.exe")]
    [InlineData("https://h/a/Setup%20One.EXE", "Setup One.EXE")]
    [InlineData("https://h/a/..%2F..%2Fevil.exe", null)]
    [InlineData("https://h/a/..%5Cevil.exe", null)]
    [InlineData("https://h/a/CON.exe", null)]
    [InlineData("https://h/a/.exe", null)]
    [InlineData("https://h/a/setup.exe.", null)]
    [InlineData("https://h/a/setup.msi", null)]
    [InlineData("https://h/a/setup.exe%3A.txt", null)]
    [InlineData("https://h/a/", null)]
    public void DownloadFileNames(string url, string? expected) =>
        Assert.Equal(expected, PackageInput.SafeDownloadFileName(new Uri(url), ".exe"));

    [Fact]
    public void StrictlyInside_RejectsTheRootItselfTraversalAndPrefixTricks()
    {
        var root = Path.Combine(Path.GetTempPath(), "Tools");
        Assert.True(PackageInput.IsStrictlyInside(Path.Combine(root, "DLSS5-Swapper"), root));
        Assert.False(PackageInput.IsStrictlyInside(root, root));
        Assert.False(PackageInput.IsStrictlyInside(Path.Combine(root, "..", "Windows"), root));
        Assert.False(PackageInput.IsStrictlyInside(root + "Evil" + Path.DirectorySeparatorChar + "x", root));
    }
}
