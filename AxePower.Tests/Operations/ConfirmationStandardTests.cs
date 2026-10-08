using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AxePower.App.ViewModels;
using AxePower.App.Windows;
using AxePower.Core.Services.Operations;
using AxePower.Tests.Shell;
using Xunit;

namespace AxePower.Tests.Operations;

/// <summary>
/// The destructive-action standard (Prompt 27), tested from metadata: every confirmation names the
/// action and scope, everything affected, admin need, risk, backup, restart and reversibility, has
/// a named button, and a high-risk one cannot run from one stray click.
/// </summary>
public class ConfirmationStandardTests
{
    public static TheoryData<string> CatalogNames() => new()
    {
        "uninstall", "uninstall-one", "uninstall-dry", "leftovers-recycle", "leftovers-delete", "leftovers-dry", "restore",
        "debloat", "update-cache", "dns-from-dhcp", "dns-from-static", "dns-reset-static", "install-many", "install-one", "upgrade-all",
    };

    internal static ActionConfirmation Build(string name) => name switch
    {
        "uninstall" => ConfirmationCatalog.UninstallApps(new[] { ("Spotify", "Runs Spotify's uninstaller"), ("7-Zip", "msiexec /x {GUID} /qn") }, new[] { "Microsoft Edge" }, restorePoint: true, dryRun: false),
        "uninstall-one" => ConfirmationCatalog.UninstallApps(new[] { ("Spotify", "Runs Spotify's uninstaller") }, Array.Empty<string>(), restorePoint: false, dryRun: false),
        "uninstall-dry" => ConfirmationCatalog.UninstallApps(new[] { ("Spotify", "Runs Spotify's uninstaller") }, Array.Empty<string>(), restorePoint: true, dryRun: true),
        "leftovers-recycle" => ConfirmationCatalog.CleanLeftovers(new[] { @"Folder: C:\Program Files\Spotify", @"Registry key: HKCU\Software\Spotify" }, dryRun: false, recycle: true),
        "leftovers-delete" => ConfirmationCatalog.CleanLeftovers(new[] { @"Folder: C:\Program Files\Spotify" }, dryRun: false, recycle: false),
        "leftovers-dry" => ConfirmationCatalog.CleanLeftovers(new[] { @"Folder: C:\Program Files\Spotify" }, dryRun: true, recycle: true),
        "restore" => ConfirmationCatalog.RestoreUninstallBackup("Spotify, 6 Oct 2026"),
        "debloat" => ConfirmationCatalog.RunDebloatScript("Win11Debloat", "Raphire", new[] { "Removes pre-installed apps", "Turns off Windows telemetry" }, "irm https://debloat.raphi.re/ | iex"),
        "update-cache" => ConfirmationCatalog.ResetWindowsUpdateCache(AxePower.Core.Services.WindowsUpdateShieldService.UpdateServices),
        "dns-from-dhcp" => ConfirmationCatalog.ChangeDns("Wi-Fi", "Cloudflare", "1.1.1.1, 1.0.0.1", new DnsCurrent(new[] { "192.168.1.1" }, IsStatic: false)),
        "dns-from-static" => ConfirmationCatalog.ChangeDns("Wi-Fi", "Cloudflare", "1.1.1.1, 1.0.0.1", new DnsCurrent(new[] { "9.9.9.9" }, IsStatic: true)),
        "dns-reset-static" => ConfirmationCatalog.ResetDnsToAutomatic("Wi-Fi", new DnsCurrent(new[] { "9.9.9.9" }, IsStatic: true)),
        "install-many" => ConfirmationCatalog.InstallPackages(new[] { "VLC", "7-Zip", "OBS Studio" }),
        "install-one" => ConfirmationCatalog.InstallPackages(new[] { "VLC" }),
        "upgrade-all" => ConfirmationCatalog.UpgradeAllPackages(Array.Empty<string>()),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static readonly Regex Generic = new(@"^(ok|yes|confirm|continue|proceed|apply|run|go|done|accept)$", RegexOptions.IgnoreCase);

    [Theory]
    [MemberData(nameof(CatalogNames))]
    public void EveryConfirmation_MeetsTheStandard(string name)
    {
        var c = Build(name);

        Assert.False(string.IsNullOrWhiteSpace(c.Action));
        Assert.False(string.IsNullOrWhiteSpace(c.Source));
        Assert.NotEmpty(c.Affected);
        Assert.False(string.IsNullOrWhiteSpace(c.Backup));
        Assert.False(string.IsNullOrWhiteSpace(c.RestartText));
        Assert.False(string.IsNullOrWhiteSpace(c.AdministratorText));
        Assert.DoesNotMatch(Generic, c.ConfirmLabel);
        Assert.Contains(' ', c.ConfirmLabel); // a verb and an object, e.g. "Remove 2 leftovers"
        if (c.Reversibility != Reversibility.FullyReversible) Assert.False(string.IsNullOrWhiteSpace(c.ReversibilityNote));
        if (c.Risk == RiskLevel.High) Assert.True(c.RequiresAcknowledgement);
        if (c.Reversibility == Reversibility.Irreversible) Assert.NotEqual(RiskLevel.Low, c.Risk); // never undoable and "low risk" at once

        // No pressure and no generic questions, anywhere in it.
        var all = string.Join("\n", new[] { c.Action, c.Backup, c.ConfirmLabel, c.ReversibilityText, c.RestartText, c.Acknowledgement }.Concat(c.Affected));
        foreach (var word in new[] { "are you sure", "hurry", "now or never", "last chance", "act now", "don't miss", "immediately" })
            Assert.DoesNotContain(word, all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uninstall_ListsEveryAppAndWhatWasSkipped_AndNeverPromisesAnUndo()
    {
        var c = Build("uninstall");

        Assert.Equal("Uninstall 2 apps", c.Action);
        Assert.Contains("Spotify: Runs Spotify's uninstaller", c.Affected);
        Assert.Contains("Skipped (protected): Microsoft Edge", c.Affected);
        Assert.Equal(RiskLevel.High, c.Risk);
        Assert.Equal(Reversibility.Irreversible, c.Reversibility);
        Assert.Contains("Restore point is created first", c.Backup);
        Assert.Equal("Uninstall Spotify", Build("uninstall-one").ConfirmLabel);
        Assert.Contains("No System Restore point", Build("uninstall-one").Backup);
    }

    [Fact]
    public void DryRuns_AreLowRisk_AndSayNothingChanges()
    {
        foreach (var name in new[] { "uninstall-dry", "leftovers-dry" })
        {
            var c = Build(name);
            Assert.Equal(RiskLevel.Low, c.Risk);
            Assert.False(c.RequiresAcknowledgement);
            Assert.Equal("Nothing is changed in a dry run.", c.Backup);
            Assert.Equal("Run the dry run", c.ConfirmLabel);
        }
    }

    [Fact]
    public void Leftovers_ShowEveryItem_AndWhetherFilesCanComeBack()
    {
        var recycle = Build("leftovers-recycle");
        var delete = Build("leftovers-delete");

        Assert.Equal(2, recycle.Affected.Count);
        Assert.Contains("Recycle Bin", recycle.Backup);
        Assert.Contains("Recycle Bin until it is emptied", recycle.ReversibilityNote);
        Assert.Contains("deleted permanently", delete.Backup);
        Assert.Contains("deleted files cannot", delete.ReversibilityNote);
        Assert.True(recycle.RequiresAcknowledgement && delete.RequiresAcknowledgement);
    }

    [Fact]
    public void Debloat_IsHighRiskIrreversibleAndNeedsARestart()
    {
        var c = Build("debloat");

        Assert.Equal(RiskLevel.High, c.Risk);
        Assert.Equal(Reversibility.Irreversible, c.Reversibility);
        Assert.Equal(RestartNeed.Restart, c.Restart);
        Assert.Contains("third-party", c.Action);
        Assert.Contains("Command: irm https://debloat.raphi.re/ | iex", c.Affected);
        Assert.Equal("I understand this cannot be undone from AxePower.", c.Acknowledgement);
    }

    [Fact]
    public void Dns_IsFullyReversibleFromAutomatic_ButWarnsAboutServersSetByHand()
    {
        var fromDhcp = Build("dns-from-dhcp");
        var fromStatic = Build("dns-from-static");

        Assert.Equal(Reversibility.FullyReversible, fromDhcp.Reversibility);
        Assert.Contains("Wi-Fi now: automatic (DHCP): 192.168.1.1", fromDhcp.Affected);
        Assert.Equal(Reversibility.PartiallyReversible, fromStatic.Reversibility);
        Assert.Contains("9.9.9.9", fromStatic.Backup);
        Assert.Equal("Use Cloudflare", fromStatic.ConfirmLabel);
    }

    [Fact]
    public void TheUpdateCacheReset_ListsTheServicesAndTheFolder()
    {
        var c = Build("update-cache");

        foreach (var service in AxePower.Core.Services.WindowsUpdateShieldService.UpdateServices)
            Assert.Contains(c.Affected, a => a.StartsWith($"Service {service}:", StringComparison.Ordinal));
        Assert.Contains(@"%WINDIR%\SoftwareDistribution\Download: emptied", c.Affected);
        Assert.True(c.RequiresAdministrator);
    }

    [Fact]
    public void AHighRiskAction_CannotRunUntilTheBoxIsTicked_AndNothingIsPreTicked()
    {
        var high = new ActionConfirmationViewModel(Build("debloat"));
        var low = new ActionConfirmationViewModel(Build("dns-from-dhcp"));

        Assert.False(high.IsAcknowledged);
        Assert.False(high.CanConfirm);
        high.IsAcknowledged = true;
        Assert.True(high.CanConfirm);
        Assert.False(low.RequiresAcknowledgement);
        Assert.True(low.CanConfirm);
    }

    [Fact]
    public void TheFiveFlows_UseTheCatalog_AndNoGenericQuestionRemains()
    {
        var root = RepoRoot();
        string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { root, "AxePower.App" }.Concat(parts).ToArray()));

        Assert.Contains("ConfirmationCatalog.UninstallApps", Source("Services", "UninstallerPrompts.cs"));
        Assert.Contains("ConfirmationCatalog.CleanLeftovers", Source("Services", "UninstallerPrompts.cs"));
        Assert.Contains("ConfirmationCatalog.RunDebloatScript", Source("ViewModels", "DebloatViewModel.cs"));
        Assert.Contains("ConfirmationCatalog.ResetWindowsUpdateCache", Source("ViewModels", "SoftwareHubViewModel.cs"));
        Assert.Contains("ConfirmationCatalog.InstallPackages", Source("ViewModels", "SoftwareHubViewModel.cs"));
        Assert.Contains("ConfirmationCatalog.UpgradeAllPackages", Source("ViewModels", "SoftwareHubViewModel.cs"));
        Assert.Contains("ConfirmationCatalog.ChangeDns", Source("ViewModels", "SystemDoctorViewModel.cs"));
        Assert.Contains("ConfirmationCatalog.ResetDnsToAutomatic", Source("ViewModels", "SystemDoctorViewModel.cs"));

        var generic = Directory.GetFiles(Path.Combine(root, "AxePower.App"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("are you sure", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(generic.Count == 0, "\"Are you sure\" in: " + string.Join(", ", generic));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("AxePower.sln not found");
    }
}

/// <summary>The confirmation window: Cancel is the default and has focus, and the action button starts disabled for high risk.</summary>
[Collection(WpfCollection.Name)]
public class ConfirmationWindowTests
{
    private readonly WpfApplicationFixture _wpf;

    public ConfirmationWindowTests(WpfApplicationFixture wpf) => _wpf = wpf;

    [Fact]
    public Task CancelIsTheSafeDefault_AndTheActionWaitsForTheTickBox() => _wpf.RunAsync(async () =>
    {
        ShellTestHost.UseAppResources();
        var errors = new List<string>();
        foreach (var skin in new[] { "dark", "light", "glass" })
        {
            using var applied = ShellTestHost.ApplySkin(skin);
            using var bindings = new ShellTestHost.BindingErrors();
            var vm = new ActionConfirmationViewModel(ConfirmationStandardTests.Build("leftovers-recycle"));
            var window = new ActionConfirmationWindow
            {
                DataContext = vm, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
                ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            await Task.Delay(100);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            var buttons = ShellTestHost.Descendants(window).OfType<Button>().ToList();
            var cancel = buttons.Single(b => (b.Content as string) == "Cancel");
            var action = buttons.Single(b => (b.Content as string) == "Remove 2 leftovers");
            Assert.True(cancel.IsDefault && cancel.IsCancel);
            Assert.False(action.IsDefault);
            Assert.False(action.IsEnabled);
            vm.IsAcknowledged = true;
            window.UpdateLayout();
            Assert.True(action.IsEnabled);

            var dir = Environment.GetEnvironmentVariable("AXEPOWER_RENDER_DIR");
            if (!string.IsNullOrEmpty(dir)) ShellTestHost.SavePng((System.Windows.Media.Visual)window.Content, (int)window.ActualWidth, (int)window.ActualHeight, Path.Combine(dir, $"confirm-{skin}.png"));
            errors.AddRange(bindings.Errors.Select(e => $"[{skin}] {e}"));
            window.Close();
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    });
}
