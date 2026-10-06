using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AxePower.App.ViewModels.ShareScreen;
using AxePower.App.Views.ShareScreen;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.ShareScreen;
using Xunit;
using static AxePower.Tests.ShareScreen.ShareScreenViewModelCharacterizationTests;

namespace AxePower.Tests.ShareScreen;

/// <summary>
/// Lays out the Friends page against its view model in the states the refactor touched (chat,
/// files, an open room, the untrusted-network banner, the join dialog) and fails on any WPF
/// binding error, so moving logic out of the view model cannot quietly break a binding. The stage
/// stays inactive, so no WebView2 is created.
/// </summary>
[Collection(WpfCollection.Name)]
public class ShareScreenViewBindingTests
{
    private readonly WpfApplicationFixture _wpf;

    public ShareScreenViewBindingTests(WpfApplicationFixture wpf) => _wpf = wpf;

    private sealed class ErrorCollector : TraceListener
    {
        public List<string> Errors { get; } = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message is not null) Errors.Add(message);
        }
    }

    private static byte[] Protect(byte[] bytes) => bytes.Select(b => (byte)(b ^ 0x5A)).ToArray();

    [Fact]
    public async Task FriendsPage_BindsWithoutErrors()
    {
        await _wpf.RunAsync(async () =>
        {
            var collector = new ErrorCollector();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(collector);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

            var dir = Path.Combine(Path.GetTempPath(), "AxePower_ShareViewBind_" + Guid.NewGuid().ToString("N"));
            ShareScreenViewModel? vm = null;
            try
            {
                var log = new LogService();
                var settingsService = new SettingsService(log, Path.Combine(dir, "settings"));
                var settingsVm = new ShareScreenSettingsViewModel(new Friends.FriendsHubTests.FakeDeviceProvider(), new Friends.FriendsHubTests.FakeToolProvider(), settingsService, log);
                var service = new RecordingShareService();
                vm = new ShareScreenViewModel(service, settingsVm, new NotificationService(), log,
                    new ShareChatStore(Path.Combine(dir, "chat"), log, Protect, Protect),
                    new ShareAttachmentStore(Path.Combine(dir, "att"), log, Protect, Protect),
                    new RecordingVoice());

                // The canary proves the listener sees binding errors at all.
                var canary = new TextBlock { DataContext = vm };
                canary.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NoSuchProperty"));
                canary.Measure(new Size(100, 100));
                Assert.NotEmpty(collector.Errors);
                collector.Errors.Clear();

                var view = new ShareScreenView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1500, Height = 1000 };
                void Layout()
                {
                    host.Measure(new Size(1500, 1000));
                    host.Arrange(new Rect(0, 0, 1500, 1000));
                    host.UpdateLayout();
                }
                Layout();

                // Chat with a text message and a received file.
                service.RaiseChat(new ShareChatMessage("t1", "Sam", "hello", DateTime.Now));
                var bytes = new byte[300];
                var id = Guid.NewGuid().ToString("N");
                var offer = new ShareChatMessage("f1", "Sam", "", DateTime.Now, false, ShareChatKinds.FileOffer,
                    id, "notes.txt", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 0, 1);
                service.RaiseChat(offer);
                service.RaiseChat(offer with { Kind = ShareChatKinds.FileChunk, ChunkData = Convert.ToBase64String(bytes) });
                service.RaiseChat(offer with { Kind = ShareChatKinds.FileComplete });
                await WaitAsync(() => vm.SharedFiles.Count == 1);
                vm.OpenChatCommand.Execute(null);
                Layout();
                vm.SetConversationPanelTabCommand.Execute("Files");
                Layout();

                // A refused start, then an open room.
                service.RaiseStatus(ShareSessionStatus.Idle with { Message = "Public network", UntrustedNetworkRefused = true });
                await WaitAsync(() => vm.IsUntrustedNetworkRefused);
                Layout();
                service.RaiseStatus(new ShareSessionStatus(ShareSessionState.RoomOpen, "Private room open",
                    new ShareRoom("123456", "axe0011", "pub", "viewer"), "http://192.0.2.55:8890/axe0011#v=viewer",
                    QualityPreset.Balanced1080, null, ShareTelemetry.Empty));
                await WaitAsync(() => vm.IsRoomOpen);
                Layout();

                // The join dialog.
                vm.OpenJoinModalCommand.Execute(null);
                vm.JoinAddressInput = "   ";
                vm.ConnectToRoomCommand.Execute(null);
                Layout();

                Assert.True(collector.Errors.Count == 0, string.Join(Environment.NewLine, collector.Errors));
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(collector);
                vm?.Dispose();
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });
    }

    /// <summary>Waits while pumping the dispatcher, since the view model marshals work onto it.</summary>
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException("Timed out waiting for the view model");
            await Task.Delay(20);
        }
    }
}
