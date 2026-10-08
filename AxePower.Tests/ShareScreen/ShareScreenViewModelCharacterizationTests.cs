using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AxePower.App.ViewModels.ShareScreen;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.ShareScreen;
using Xunit;

namespace AxePower.Tests.ShareScreen;

/// <summary>
/// What the Friends page view model does with chat, attachments and voice, pinned before its
/// socket, crypto and file work moved into Core services (Prompt 18). Hosting is driven through a
/// recording fake service; joining is driven against a real viewer host on loopback.
///
/// These run in the WPF collection, so a live dispatcher exists just as in the app: the view model
/// marshals incoming work onto it, and the assertions poll for the result. (Outside the
/// collection, Application.Current can outlive the fixture's dispatcher and that work would
/// never run.)
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ShareScreenViewModelCharacterizationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_ShareVmChar_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = new();

    private readonly WpfApplicationFixture _wpf;

    public ShareScreenViewModelCharacterizationTests(WpfApplicationFixture wpf)
    {
        // Every test body runs on the Application's dispatcher thread, as the page does: the
        // view model's collections are only ever touched there, and work it queues from
        // background sockets runs between the test's awaits.
        _wpf = wpf;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var d in _disposables.AsEnumerable().Reverse()) { try { d.Dispose(); } catch { } }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] Protect(byte[] bytes) => bytes.Select(b => (byte)(b ^ 0x5A)).ToArray();

    internal sealed class RecordingShareService : IShareScreenService
    {
        public ShareSessionStatus Status { get; set; } = ShareSessionStatus.Idle;
        public ConcurrentQueue<ShareChatMessage> SentChat { get; } = new();
        public ConcurrentQueue<byte[]> SentVoice { get; } = new();
        public event EventHandler<ShareSessionStatus>? StatusChanged;
        public event EventHandler<ShareChatMessage>? ChatMessageReceived;
        public event EventHandler<byte[]>? VoicePacketReceived;
        public event EventHandler? ParticipantsChanged { add { } remove { } }
        public event EventHandler<PendingControlApproval>? ControlApprovalRequested { add { } remove { } }
        public Task<ShareSessionStatus> StartAsync(ShareScreenSettings settings, IProgress<string>? step, CancellationToken ct) => Task.FromResult(Status);
        public Task<ShareSessionStatus> StartRoomAsync(ShareScreenSettings settings, CancellationToken ct) => Task.FromResult(Status);
        public Task StopScreenShareAsync() => Task.CompletedTask;
        public Task StopAsync() { Status = ShareSessionStatus.Idle; StatusChanged?.Invoke(this, Status); return Task.CompletedTask; }
        public string? SetRemoteControl(bool enabled) => null;
        public void SendChatMessage(ShareChatMessage message) => SentChat.Enqueue(message);
        public void SendVoicePacket(byte[] opusPacket) => SentVoice.Enqueue(opusPacket);
        public IReadOnlyList<ShareParticipant> GetParticipants() => Array.Empty<ShareParticipant>();
        public void RaiseStatus(ShareSessionStatus status) { Status = status; StatusChanged?.Invoke(this, status); }
        public void RaiseChat(ShareChatMessage message) => ChatMessageReceived?.Invoke(this, message);
        public void RaiseVoice(byte[] packet) => VoicePacketReceived?.Invoke(this, packet);
    }

    internal sealed class RecordingVoice : IRoomVoiceService
    {
        public bool IsRunning { get; private set; }
        public bool IsMuted { get; private set; }
        public bool IsDeafened { get; private set; }
        public ConcurrentQueue<byte[]> Received { get; } = new();
        public event EventHandler<byte[]>? EncodedPacketReady;
        public bool Start() { IsRunning = true; return true; }
        public void Stop() => IsRunning = false;
        public void SetMuted(bool muted) => IsMuted = muted;
        public void SetDeafened(bool deafened) => IsDeafened = deafened;
        public void ReceivePacket(byte[] opusPacket) => Received.Enqueue(opusPacket);
        public void Capture(byte[] packet) => EncodedPacketReady?.Invoke(this, packet);
        public void Dispose() => Stop();
    }

    private (ShareScreenViewModel Vm, RecordingShareService Service, RecordingVoice Voice, ShareAttachmentStore Attachments, string ChatDir) Create()
    {
        var log = new LogService();
        var settingsService = new SettingsService(log, Path.Combine(_dir, "settings"));
        var settingsVm = new ShareScreenSettingsViewModel(new Friends.FriendsHubTests.FakeDeviceProvider(), new Friends.FriendsHubTests.FakeToolProvider(), settingsService, log);
        var chatDir = Path.Combine(_dir, "chat");
        var attachments = new ShareAttachmentStore(Path.Combine(_dir, "attachments"), log, Protect, Protect);
        var service = new RecordingShareService();
        var voice = new RecordingVoice();
        var vm = new ShareScreenViewModel(service, settingsVm, new NotificationService(), log,
            new ShareChatStore(chatDir, log, Protect, Protect), attachments, voice);
        _disposables.Add(vm);
        return (vm, service, voice, attachments, chatDir);
    }

    private static async Task Until(Func<bool> condition, string what, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException("Timed out waiting for: " + what);
            await Task.Delay(25);
        }
    }

    private static ShareSessionStatus RoomOpen() => new(
        ShareSessionState.RoomOpen, "Private room open", new ShareRoom("123456", "axe0011", "pub", "viewer"),
        "http://192.0.2.55:8890/axe0011#v=viewer", QualityPreset.Balanced1080, null, ShareTelemetry.Empty);

    // -- Hosting -------------------------------------------------------------------------

    [Fact]
    public Task Hosting_SendingText_GoesThroughTheHostAndIsKept() => _wpf.RunAsync(async () =>
    {
        var (vm, service, _, _, chatDir) = Create();
        service.RaiseStatus(RoomOpen());
        await Until(() => vm.State == ShareSessionState.RoomOpen, "room open");

        vm.ChatInputText = "  hello room  ";
        await vm.SendMessageCommand.ExecuteAsync(null);

        var sent = Assert.Single(service.SentChat);
        Assert.Equal("hello room", sent.Text);
        Assert.True(sent.IsSelf);
        Assert.Equal(string.Empty, vm.ChatInputText);
        Assert.Contains(vm.Messages, m => m.Text == "hello room");
        Assert.Contains(new ShareChatStore(chatDir, null, Protect, Protect).LoadMessages(), m => m.Text == "hello room");

        vm.ChatInputText = "   ";
        await vm.SendMessageCommand.ExecuteAsync(null);
        Assert.Single(service.SentChat);
    });

    [Fact]
    public Task Hosting_IncomingText_IsShownAndCountedUnreadUntilChatOpens() => _wpf.RunAsync(async () =>
    {
        var (vm, service, _, _, _) = Create();
        service.RaiseChat(new ShareChatMessage("m1", "Sam", "hi there", DateTime.Now, IsSelf: true));

        await Until(() => vm.Messages.Any(m => m.Text == "hi there"), "incoming message");
        Assert.False(vm.Messages.Single(m => m.Text == "hi there").IsSelf); // never trusted as ours
        Assert.Equal(1, vm.UnreadChatCount);
        Assert.True(vm.HasUnreadChat);

        vm.OpenChatCommand.Execute(null);
        Assert.Equal(0, vm.UnreadChatCount);
        service.RaiseChat(new ShareChatMessage("m2", "Sam", "again", DateTime.Now));
        await Until(() => vm.Messages.Any(m => m.Text == "again"), "second message");
        Assert.Equal(0, vm.UnreadChatCount);

        service.RaiseChat(new ShareChatMessage("m3", "Sam", "   ", DateTime.Now)); // blank text is ignored
        await Task.Delay(200);
        Assert.DoesNotContain(vm.Messages, m => m.Id == "m3");
    });

    private static IEnumerable<ShareChatMessage> AttachmentWire(string id, string name, byte[] bytes, string? hashOverride = null)
    {
        var chunks = (int)Math.Ceiling(bytes.Length / (double)ShareAttachmentStore.WireChunkBytes);
        var offer = new ShareChatMessage("f1", "Sam", string.Empty, DateTime.Now, false, ShareChatKinds.FileOffer,
            id, name, bytes.Length, hashOverride ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 0, chunks);
        yield return offer;
        for (var i = 0; i < chunks; i++)
        {
            var offset = i * ShareAttachmentStore.WireChunkBytes;
            var length = Math.Min(ShareAttachmentStore.WireChunkBytes, bytes.Length - offset);
            yield return offer with { Kind = ShareChatKinds.FileChunk, ChunkIndex = i, ChunkData = Convert.ToBase64String(bytes, offset, length) };
        }
        yield return offer with { Kind = ShareChatKinds.FileComplete };
    }

    [Fact]
    public Task Hosting_IncomingAttachment_IsReassembledVerifiedAndListed() => _wpf.RunAsync(async () =>
    {
        var (vm, service, _, attachments, _) = Create();
        var bytes = Enumerable.Range(0, ShareAttachmentStore.WireChunkBytes * 2 + 100).Select(i => (byte)i).ToArray();
        var id = Guid.NewGuid().ToString("N");
        foreach (var m in AttachmentWire(id, "photo.png", bytes)) service.RaiseChat(m);

        await Until(() => vm.SharedFiles.Any(f => f.AttachmentId == id), "attachment card");
        var card = vm.Messages.Single(m => m.AttachmentId == id);
        Assert.Equal(ShareChatKinds.File, card.Kind);
        Assert.Equal("photo.png", card.AttachmentName);
        Assert.Equal(string.Empty, card.ChunkData);
        Assert.True(attachments.IsAvailable(id));
    });

    [Fact]
    public Task Hosting_AttachmentWithAWrongHash_IsDiscarded() => _wpf.RunAsync(async () =>
    {
        var (vm, service, _, attachments, _) = Create();
        var id = Guid.NewGuid().ToString("N");
        foreach (var m in AttachmentWire(id, "evil.exe", new byte[500], hashOverride: new string('a', 64))) service.RaiseChat(m);
        await Task.Delay(300);
        Assert.DoesNotContain(vm.Messages, m => m.AttachmentId == id);
        Assert.False(attachments.IsAvailable(id));
    });

    [Fact]
    public Task Hosting_VoiceFlowsBothWaysThroughTheHost() => _wpf.RunAsync(async () =>
    {
        var (vm, service, voice, _, _) = Create();
        service.RaiseStatus(RoomOpen());
        await Until(() => vm.State == ShareSessionState.RoomOpen, "room open");

        service.RaiseVoice(new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(voice.Received));

        voice.Capture(new byte[] { 9, 9 });
        await Until(() => !service.SentVoice.IsEmpty, "voice sent to host");
        Assert.Equal(new byte[] { 9, 9 }, service.SentVoice.Single());

        vm.ToggleMicCommand.Execute(null);
        Assert.True(voice.IsMuted);
        vm.ToggleAudioCommand.Execute(null);
        Assert.True(voice.IsDeafened);
    });

    [Fact]
    public Task ChatHistory_IsRestoredAndCanBeCleared() => _wpf.RunAsync(async () =>
    {
        var chatDir = Path.Combine(_dir, "chat");
        new ShareChatStore(chatDir, null, Protect, Protect).AppendMessage(new ShareChatMessage("old", "Me", "from yesterday", DateTime.Now.AddDays(-1), true));

        var (vm, _, _, _, _) = Create();
        Assert.Contains(vm.Messages, m => m.Text == "from yesterday");

        vm.ClearChatCommand.Execute(null);
        Assert.Empty(vm.Messages);
        Assert.Empty(new ShareChatStore(chatDir, null, Protect, Protect).LoadMessages());
        await Task.CompletedTask;
    });

    // -- Joining a real room ----------------------------------------------------------------

    [Fact]
    public Task Joining_ConnectsChatAndVoice_AndExchangesWithTheHost() => _wpf.RunAsync(async () =>
    {
        var log = new LogService();
        var input = new RemoteInputService(log);
        var host = new ShareViewerHost(log, input);
        _disposables.Add(host);
        var room = ShareRoomFactory.Create();
        host.Start(room, "127.0.0.1", webrtcPort: 18889, preferredPort: 19800);
        var hostChat = new ConcurrentQueue<ShareChatMessage>();
        var hostVoice = new ConcurrentQueue<byte[]>();
        host.ChatMessageReceived += (_, m) => hostChat.Enqueue(m);
        host.VoicePacketReceived += (_, p) => hostVoice.Enqueue(p);

        var (vm, _, voice, _, _) = Create();
        vm.RememberFriend = false;
        vm.FriendNameInput = "Sam";
        vm.JoinAddressInput = host.ViewerUrl;
        vm.ConnectToRoomCommand.Execute(null);

        Assert.True(vm.IsFriendJoined);
        Assert.Equal("Sam", vm.FriendName);
        Assert.True(vm.IsSecureTransport);
        Assert.Equal("Private - encrypted chat and files", vm.PrivacyStatusText);
        await Until(() => host.GetActiveParticipants().Any(p => p.Role == "Chat/Viewer"), "chat socket admitted");
        await Until(() => host.GetActiveParticipants().Any(p => p.Role == "Voice"), "voice socket admitted");

        // Our chat reaches the host, encrypted with the room's key.
        vm.ChatInputText = "joined!";
        await vm.SendMessageCommand.ExecuteAsync(null);
        await Until(() => hostChat.Any(m => m.Text == "joined!"), "host received our chat");

        // The host's chat reaches us.
        host.BroadcastChatMessage(new ShareChatMessage("h1", "Host", "welcome", DateTime.Now));
        await Until(() => vm.Messages.Any(m => m.Text == "welcome" && !m.IsSelf), "we received host chat");

        // Voice both ways.
        await Until(() => { voice.Capture(new byte[] { 4, 5, 6 }); return hostVoice.Any(p => p.SequenceEqual(new byte[] { 4, 5, 6 })); }, "host received our voice");
        host.BroadcastVoicePacket(new byte[] { 7, 8 });
        await Until(() => voice.Received.Any(p => p.SequenceEqual(new byte[] { 7, 8 })), "we received host voice");

        // Leaving closes both sockets.
        await vm.DisconnectAllCommand.ExecuteAsync(null);
        Assert.False(vm.IsFriendJoined);
        Assert.False(vm.IsSecureTransport);
        await Until(() => host.GetActiveParticipants().Count == 0, "sockets closed on leave");
    });

    [Fact]
    public Task Joining_RejectsBadAddresses() => _wpf.RunAsync(() =>
    {
        var (vm, _, _, _, _) = Create();
        vm.JoinAddressInput = "  ";
        vm.ConnectToRoomCommand.Execute(null);
        Assert.Equal("Please enter your friend's room link or IP address.", vm.JoinStatusMessage);
        Assert.False(vm.IsFriendJoined);

        vm.JoinAddressInput = "http://[bad";
        vm.ConnectToRoomCommand.Execute(null);
        Assert.Equal("Invalid address. Format: http://<ip>:<port>/<room>", vm.JoinStatusMessage);
        Assert.False(vm.IsFriendJoined);

        // A link without its #v= part joins, but cannot be secure.
        vm.RememberFriend = false;
        vm.JoinAddressInput = "192.0.2.10:8890/axe00";
        vm.ConnectToRoomCommand.Execute(null);
        Assert.True(vm.IsFriendJoined);
        Assert.Equal("http://192.0.2.10:8890/axe00", vm.ActiveFriendWatchUrl);
        Assert.False(vm.IsSecureTransport);
        Assert.Equal("Private room - use a trusted LAN or VPN", vm.PrivacyStatusText);
        return Task.CompletedTask;
    });
}
