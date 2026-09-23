using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.App.Views;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;
using AxePower.Core.Services.Uninstaller.Leftovers;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// Lays out UninstallerView against a populated view model — details, settings and history
/// panels, results and a leftover review — and fails on any WPF binding error.
/// </summary>
[Collection(WpfCollection.Name)]
public class UninstallerViewBindingTests
{
    private readonly WpfApplicationFixture _wpf;

    public UninstallerViewBindingTests(WpfApplicationFixture wpf) => _wpf = wpf;

    private sealed class ErrorCollector : TraceListener
    {
        public List<string> Errors { get; } = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message is not null) Errors.Add(message);
        }
    }

    private sealed class SilentPrompts : IUninstallerPrompts
    {
        public bool ConfirmBatch(IReadOnlyList<(InstalledApplication App, UninstallPlan Plan)> plans, IReadOnlyList<(InstalledApplication App, string Reason)> skipped, bool restorePoint, bool dryRun) => false;
        public RunningAppDecision AskRunningApp(InstalledApplication app, IReadOnlyList<RunningProcessInfo> processes, bool closeAttemptFailed) => RunningAppDecision.Skip;
        public StopWaitingChoice AskStopWaiting(string applicationName, bool isQuiet) => StopWaitingChoice.KeepWaiting;
        public bool AskStopStalledUninstaller(InstalledApplication app, TimeSpan elapsed) => false;
        public bool ConfirmCleanLeftovers(int count, int needsAdmin, bool dryRun, bool recycle) => false;
        public bool ConfirmRestore(string sessionDescription) => false;
        public bool ConfirmRelaunchAsAdmin(int itemsNeedingAdmin) => false;
    }

    [Fact]
    public async Task EveryPanel_BindsWithoutErrors()
    {
        await _wpf.RunAsync(async () =>
        {
            var collector = new ErrorCollector();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(collector);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

            try
            {
                using var tempFolder = new TempFolder();
                var temp = tempFolder.Path;
                var log = new LogService();
                var discovery = new FakeDiscovery();
                discovery.Apps.AddRange(
                [
                    new InstalledApplication { Id = "a", DisplayName = "Normal App", Publisher = "Vendor", UninstallerKind = UninstallerKind.Nsis, RegistryKeyPath = @"HKCU\X\a", InstallLocation = @"C:\Program Files\Normal" },
                    new InstalledApplication { Id = "b", DisplayName = "Broken App", UninstallerKind = UninstallerKind.BrokenEntry, ApplicationType = ApplicationType.Orphaned, RegistryKeyPath = @"HKCU\X\b" },
                ]);

                var history = new InMemoryHistory();
                var session = new UninstallSession { ApplicationNames = ["Normal App"] };
                session.Results.Add(new UninstallResult { ApplicationId = "a", ApplicationName = "Normal App", Outcome = UninstallOutcome.Removed, LogPath = @"C:\x.log" });
                session.Results.Add(new UninstallResult { ApplicationId = "b", ApplicationName = "Broken App", Outcome = UninstallOutcome.StillInstalled, OfferRemoveBrokenEntry = true, ErrorMessage = "Still installed" });
                var leftover = new LeftoverItem
                {
                    ApplicationId = "a", ApplicationName = "Normal App", ItemType = LeftoverItemType.Folder,
                    Path = @"C:\Users\u\AppData\Roaming\Normal", Evidence = [ConfidenceRecords.ExplicitConnection], ConfidenceScore = 90,
                };
                session.LeftoverItems.Add(leftover);
                session.LeftoverItems.Add(new LeftoverItem { ApplicationName = "Normal App", ItemType = LeftoverItemType.RegistryKey, Path = @"HKLM\SOFTWARE\Normal", RequiresElevation = true });
                session.LeftoverItems.Add(new LeftoverItem { ApplicationName = "Normal App", ItemType = LeftoverItemType.Service, Path = @"HKLM\SYSTEM\x", BlockedReason = "info" });
                await history.SaveSessionAsync(session);

                var manifests = new BackupManifestStore(temp);
                var backup = new RegistryBackupService(manifests);
                var protection = new ProtectionService();
                var vm = new UninstallerViewModel(discovery, new BatchUninstallServiceStub(), new FakeScanner(),
                    new LeftoverCleanupService(protection, backup, new FileCleanupService(), manifests),
                    history, backup, protection, new FakeVerifier(), new FakeFileSystemProbe(), manifests,
                    new SettingsService(log, Path.Combine(temp, "settings")), new ElevationService(), new SilentPrompts(),
                    new NotificationService(), log);

                // Canary: prove the listener sees binding errors before trusting an empty list
                var canary = new TextBlock { DataContext = vm };
                canary.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NoSuchProperty"));
                canary.Measure(new Size(100, 100));
                Assert.NotEmpty(collector.Errors);
                collector.Errors.Clear();

                var view = new UninstallerView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1400, Height = 1000 };

                void Layout()
                {
                    host.Measure(new Size(1400, 1000));
                    host.Arrange(new Rect(0, 0, 1400, 1000));
                    host.UpdateLayout();
                }

                await vm.RefreshCommand.ExecuteAsync(null);
                vm.SelectedApplication = vm.ApplicationsView.Cast<InstalledApplicationViewModel>().First();
                vm.LastSession = session;
                foreach (var r in session.Results) vm.BatchResults.Add(r);
                vm.IsResultsVisible = true;
                vm.ResumeReviewCommand.Execute(new UninstallSessionViewModel(session, temp));
                vm.IsProgressVisible = true;
                Layout();

                vm.ToggleSettingsCommand.Execute(null);
                Layout();

                await vm.ToggleHistoryCommand.ExecuteAsync(null);
                Layout();

                Assert.True(vm.IsLeftoverReviewVisible);
                Assert.True(collector.Errors.Count == 0, string.Join(Environment.NewLine, collector.Errors));
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(collector);
            }
        });
    }

    private sealed class BatchUninstallServiceStub : IBatchUninstallService
    {
        public Task<UninstallSession> ExecuteBatchAsync(IReadOnlyList<InstalledApplication> applications, UninstallerSettings settings,
            IProgress<UninstallProgress>? progress = null, IUninstallInteraction? interaction = null, UninstallBatchControl? control = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new UninstallSession());
    }
}
