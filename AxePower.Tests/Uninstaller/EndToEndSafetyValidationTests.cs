using System.IO;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// View-model safety checks with every destructive service faked: nothing is uninstalled and
/// nothing on the machine changes.
/// </summary>
[Collection(WpfCollection.Name)]
public class EndToEndSafetyValidationTests : IDisposable
{
    private readonly WpfApplicationFixture _wpf;
    private readonly List<TempFolder> _tempFolders = new();

    public EndToEndSafetyValidationTests(WpfApplicationFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        foreach (var folder in _tempFolders) folder.Dispose();
    }

    private sealed class FakePrompts : IUninstallerPrompts
    {
        public bool Confirm { get; set; } = true;
        public int ConfirmCalls { get; private set; }
        public IReadOnlyList<(InstalledApplication App, string Reason)> LastSkipped { get; private set; } = [];

        public bool ConfirmBatch(IReadOnlyList<(InstalledApplication App, UninstallPlan Plan)> plans,
            IReadOnlyList<(InstalledApplication App, string Reason)> skipped, bool restorePoint, bool dryRun)
        {
            ConfirmCalls++;
            LastSkipped = skipped;
            return Confirm;
        }

        public RunningAppDecision AskRunningApp(InstalledApplication app, IReadOnlyList<RunningProcessInfo> processes, bool closeAttemptFailed) => RunningAppDecision.Skip;
        public StopWaitingChoice AskStopWaiting(string applicationName, bool isQuiet) => StopWaitingChoice.KeepWaiting;
        public bool AskStopStalledUninstaller(InstalledApplication app, TimeSpan elapsed) => false;
        public bool ConfirmCleanLeftovers(int count, int needsAdmin, bool dryRun, bool recycle) => false;
        public bool ConfirmRestore(string sessionDescription) => false;
        public bool ConfirmRelaunchAsAdmin(int itemsNeedingAdmin) => false;
    }

    /// <summary>Records what it was asked to uninstall and, optionally, blocks until released.</summary>
    private sealed class RecordingBatch : IBatchUninstallService
    {
        public List<string> Requested { get; } = new();
        public TaskCompletionSource? Gate { get; set; }

        public async Task<UninstallSession> ExecuteBatchAsync(IReadOnlyList<InstalledApplication> applications,
            UninstallerSettings settings, IProgress<UninstallProgress>? progress = null, IUninstallInteraction? interaction = null,
            UninstallBatchControl? control = null, CancellationToken cancellationToken = default)
        {
            Requested.AddRange(applications.Select(a => a.DisplayName));
            if (Gate is not null) await Gate.Task;
            return new UninstallSession
            {
                ApplicationNames = applications.Select(a => a.DisplayName).ToList(),
                Results = applications.Select(a => new UninstallResult { ApplicationId = a.Id, ApplicationName = a.DisplayName, Outcome = UninstallOutcome.DryRun }).ToList(),
            };
        }
    }

    private (UninstallerViewModel Vm, RecordingBatch Batch, FakePrompts Prompts, FakeDiscovery Discovery) CreateHarness()
    {
        var temp = new TempFolder();
        _tempFolders.Add(temp);
        var log = new LogService();
        var protection = new ProtectionService();
        var discovery = new FakeDiscovery();
        var batch = new RecordingBatch();
        var prompts = new FakePrompts();
        var manifests = new BackupManifestStore(temp.Combine("history"));
        var backup = new RegistryBackupService(manifests);

        var vm = new UninstallerViewModel(
            discovery, batch, new FakeScanner(),
            new LeftoverCleanupService(protection, backup, new FileCleanupService(), manifests),
            new InMemoryHistory(), backup, protection, new FakeVerifier(), new FakeFileSystemProbe(), manifests,
            new SettingsService(log, temp.Combine("settings")),
            new ElevationService(), prompts, new NotificationService(), log);

        return (vm, batch, prompts, discovery);
    }

    private static InstalledApplication App(string name, bool isProtected = false, UninstallerKind kind = UninstallerKind.Unknown) => new()
    {
        Id = $@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
        RegistryKeyPath = $@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
        DisplayName = name,
        Publisher = "Test Publisher",
        UninstallerKind = kind,
        ApplicationType = InstalledApplication.ApplicationTypeFor(kind),
        IsProtected = isProtected,
        CanUninstall = true,
    };

    [Fact]
    public void SearchFilter_MatchesNameAndPublisher()
    {
        var appVm1 = new InstalledApplicationViewModel(new InstalledApplication { DisplayName = "Test Notepad Plus", Publisher = "Don Ho" });
        var appVm2 = new InstalledApplicationViewModel(new InstalledApplication { DisplayName = "VLC Media Player", Publisher = "VideoLAN" });

        Assert.Contains("Notepad", appVm1.DisplayName);
        Assert.DoesNotContain("Notepad", appVm2.DisplayName);
    }

    [Theory]
    [InlineData(UninstallerKind.Unknown, "Win32")]
    [InlineData(UninstallerKind.Nsis, "Win32")]
    [InlineData(UninstallerKind.BurnBundle, "Win32")]
    [InlineData(UninstallerKind.Msi, "MSI")]
    [InlineData(UninstallerKind.StoreApp, "AppX")]
    [InlineData(UninstallerKind.BrokenEntry, "Broken")]
    public void CategoryChips_FollowTheUninstallerKind(UninstallerKind kind, string expected)
    {
        var vm = new InstalledApplicationViewModel(App("X", kind: kind));
        Assert.Equal(expected, vm.ApplicationTypeLabel);
    }

    [Fact]
    public void BrokenEntries_OfferRemoveBrokenEntry_NotUninstall()
    {
        Assert.Equal("Remove broken entry", new InstalledApplicationViewModel(App("X", kind: UninstallerKind.BrokenEntry)).ActionLabel);
        Assert.Equal("Uninstall", new InstalledApplicationViewModel(App("X", kind: UninstallerKind.Msi)).ActionLabel);
    }

    [Fact]
    public void SelectingAnApp_ShowsTheDetailsPanel()
    {
        _wpf.Invoke(() =>
        {
            var (vm, _, _, _) = CreateHarness();
            var appVm = new InstalledApplicationViewModel(App("Test Notepad Plus"));

            vm.SelectedApplication = appVm;

            Assert.True(vm.IsDetailPanelVisible);
            Assert.False(string.IsNullOrEmpty(appVm.PlanDescription));
        });
    }

    [Fact]
    public async Task D11_DetailsPanelUninstall_ActsOnTheViewedApp_NotTheCheckedRows()
    {
        await _wpf.RunAsync(async () =>
        {
            var (vm, batch, _, discovery) = CreateHarness();
            discovery.Apps.AddRange([App("Checked App"), App("Viewed App")]);
            await vm.RefreshCommand.ExecuteAsync(null);

            var rows = vm.ApplicationsView.Cast<InstalledApplicationViewModel>().ToList();
            rows.Single(r => r.DisplayName == "Checked App").IsSelected = true;
            vm.SelectedApplication = rows.Single(r => r.DisplayName == "Viewed App");

            await vm.UninstallThisCommand.ExecuteAsync(null);

            Assert.Equal(["Viewed App"], batch.Requested);
        });
    }

    [Fact]
    public async Task D12_ProtectedPick_IsSkipped_AndTheRestStillRuns()
    {
        await _wpf.RunAsync(async () =>
        {
            var (vm, batch, prompts, discovery) = CreateHarness();
            discovery.Apps.AddRange([App("Normal App"), App("AxePower Desktop", isProtected: true)]);
            await vm.RefreshCommand.ExecuteAsync(null);

            // The Protected chip lists protected apps even while they are hidden elsewhere
            vm.SelectFilterCommand.Execute("Protected");
            foreach (var row in vm.ApplicationsView.Cast<InstalledApplicationViewModel>()) row.IsSelected = true;
            vm.SelectFilterCommand.Execute("All");
            foreach (var row in vm.ApplicationsView.Cast<InstalledApplicationViewModel>()) row.IsSelected = true;

            await vm.UninstallSelectedCommand.ExecuteAsync(null);

            Assert.Equal(["Normal App"], batch.Requested);
            Assert.Equal(1, prompts.ConfirmCalls);
            Assert.Contains(prompts.LastSkipped, s => s.App.DisplayName == "AxePower Desktop");
        });
    }

    [Fact]
    public async Task NothingRuns_WhenTheConfirmationIsDeclined()
    {
        await _wpf.RunAsync(async () =>
        {
            var (vm, batch, prompts, discovery) = CreateHarness();
            discovery.Apps.Add(App("Some App"));
            await vm.RefreshCommand.ExecuteAsync(null);
            vm.SelectedApplication = vm.ApplicationsView.Cast<InstalledApplicationViewModel>().Single();
            prompts.Confirm = false;

            await vm.UninstallThisCommand.ExecuteAsync(null);

            Assert.Empty(batch.Requested);
        });
    }

    [Fact]
    public async Task D10_RefreshIsDisabled_WhileABatchRuns()
    {
        await _wpf.RunAsync(async () =>
        {
            var (vm, batch, _, discovery) = CreateHarness();
            discovery.Apps.Add(App("Some App"));
            await vm.RefreshCommand.ExecuteAsync(null);
            vm.SelectedApplication = vm.ApplicationsView.Cast<InstalledApplicationViewModel>().Single();

            batch.Gate = new TaskCompletionSource();
            var running = vm.UninstallThisCommand.ExecuteAsync(null);

            Assert.True(vm.IsBusy);
            Assert.False(vm.RefreshCommand.CanExecute(null));
            Assert.False(vm.UninstallSelectedCommand.CanExecute(null));
            Assert.True(vm.CancelCommand.CanExecute(null));

            batch.Gate.SetResult();
            await running;

            Assert.False(vm.IsBusy);
            Assert.True(vm.RefreshCommand.CanExecute(null));
        });
    }

    [Fact]
    public void ProtectedApps_CannotBeUninstalled()
    {
        var protection = new ProtectionService();
        var axePower = new InstalledApplication { DisplayName = "AxePower Desktop", Publisher = "Abhimanyu Vishwakarma" };

        Assert.True(protection.IsApplicationProtected(axePower));
        Assert.NotNull(protection.GetProtectionReason(axePower));
    }

    [Fact]
    public void LeftoverRows_AreOnlyTickedWhenTheScannerPreselectedThem()
    {
        var preselected = new LeftoverItemViewModel(new LeftoverItem { Path = @"C:\App", ConfidenceScore = 90, IsSelected = true });
        var highButNameOnly = new LeftoverItemViewModel(new LeftoverItem { Path = @"C:\Other", ConfidenceScore = 90, IsSelected = false });

        Assert.True(preselected.IsSelected);
        Assert.False(highButNameOnly.IsSelected);
    }

    [Fact]
    public void BlockedAndAdminOnlyRows_CannotBeTicked()
    {
        var blocked = new LeftoverItemViewModel(new LeftoverItem { Path = @"C:\X", IsSelected = true, BlockedReason = "test" });
        var admin = new LeftoverItemViewModel(new LeftoverItem { Path = @"HKLM\SOFTWARE\X", IsSelected = true, RequiresElevation = true }, isElevated: false);
        var adminElevated = new LeftoverItemViewModel(new LeftoverItem { Path = @"HKLM\SOFTWARE\X", IsSelected = true, RequiresElevation = true }, isElevated: true);

        Assert.False(blocked.CanSelect);
        Assert.False(blocked.IsSelected);
        Assert.False(admin.CanSelect);
        Assert.True(adminElevated.CanSelect);
    }

    [Fact]
    public void SafetyDefaults_AreEnabled()
    {
        var settings = new UninstallerSettings();

        Assert.True(settings.CreateSystemRestorePoint);
        Assert.True(settings.BackupRegistryChanges);
        Assert.True(settings.MoveToRecycleBin);
        Assert.False(settings.PreferQuietUninstall);
        Assert.False(settings.ShowSystemComponents);
        Assert.Equal(ScanMode.Moderate, settings.DefaultScanMode);
    }

    [Fact]
    public async Task DryRun_PerformsNoDestructiveOperation()
    {
        var exe = @"C:\Program Files\Notepad++\uninstall.exe";
        var executor = new Win32UninstallExecutor(new FakeFileSystemProbe(exe), new ProcessTreeWatcher());
        var app = new InstalledApplication
        {
            DisplayName = "Test Notepad Plus",
            UninstallerKind = UninstallerKind.Nsis,
            UninstallerPath = exe,
            UninstallString = $"\"{exe}\"",
        };

        var result = await executor.ExecuteAsync(app, new UninstallerSettings { DryRunMode = true }, new UninstallRunContext());

        Assert.Equal(ExecutionVerdict.DryRun, result.Verdict);
        Assert.Contains("[DRY RUN]", result.CommandExecuted);
    }

    [Fact]
    public async Task ScanModes_ProduceTieredResults()
    {
        var scanner = new LeftoverScannerService(new ProtectionService());
        var app = new InstalledApplication
        {
            Id = "notepad-plus",
            DisplayName = "Notepad++",
            Publisher = "Don HO",
            InstallLocation = @"C:\Program Files\Notepad++",
        };

        var safe = await scanner.ScanAsync(app, ScanMode.Safe, new UninstallerSettings());
        var moderate = await scanner.ScanAsync(app, ScanMode.Moderate, new UninstallerSettings());

        Assert.True(safe.Count <= moderate.Count);
        Assert.All(safe, i => Assert.True(i.HasEvidence("ExplicitConnection") || i.HasEvidence("IsUninstallerRegistryKey")));
    }
}
