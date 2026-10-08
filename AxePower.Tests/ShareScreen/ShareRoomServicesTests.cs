using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.ShareScreen;
using Xunit;
using static AxePower.Tests.ShareScreen.ShareScreenViewModelCharacterizationTests;

namespace AxePower.Tests.ShareScreen;

/// <summary>
/// The services the Friends page's room logic moved into (Prompt 18), each tested without WPF:
/// link parsing, attachment chunking and reassembly, the conversation, routing, voice, and the
/// joined-room session against a real viewer host on loopback.
/// </summary>
public sealed class ShareRoomServicesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_ShareRoomSvc_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = new();

    public ShareRoomServicesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _disposables.AsEnumerable().Reverse()) { try { d.Dispose(); } catch { } }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] Protect(byte[] bytes) => bytes.Select(b => (byte)(b ^ 0x5A)).ToArray();
    private ShareAttachmentStore Attachments(string name = "att") => new(Path.Combine(_dir, name), null, Protect, Protect);
    private ShareChatStore Chat(string name = "chat") => new(Path.Combine(_dir, name), null, Protect, Protect);

    private static async Task Until(Func<bool> condition, string what, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException("Timed out waiting for: " + what);
            await Task.Delay(25);
        }
    }

    // -- Room links -------------------------------------------------------------------------

    [Theory]
    [InlineData("192.168.1.5:8890/axe1", "http://192.168.1.5:8890/axe1")]
    [InlineData("  https://pc.tail.ts.net:8890/axe1#v=abc ", "https://pc.tail.ts.net:8890/axe1#v=abc")]
    public void Links_AreNormalised(string input, string expected)
    {
        Assert.Equal(ShareRoomLink.JoinAddressProblem.None, ShareRoomLink.TryNormalize(input, out var address, out var uri));
        Assert.Equal(expected, address);
        Assert.NotNull(uri);
    }

    [Fact]
    public void Links_RejectEmptyAndInvalidInput_AndReadTheSecret()
    {
        Assert.Equal(ShareRoomLink.JoinAddressProblem.Empty, ShareRoomLink.TryNormalize("  ", out _, out _));
        Assert.Equal(ShareRoomLink.JoinAddressProblem.Invalid, ShareRoomLink.TryNormalize("http://[bad", out _, out _));

        Assert.Equal("a b", ShareRoomLink.ParseViewerSecret(new Uri("http://h/axe#x=1&v=a%20b")));
        Assert.Equal(string.Empty, ShareRoomLink.ParseViewerSecret(new Uri("http://h/axe")));
    }

    // -- Attachments ------------------------------------------------------------------------

    private string WriteFile(int size)
    {
        var path = Path.Combine(_dir, "source-" + size + ".bin");
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(i => (byte)(i * 13)).ToArray());
        return path;
    }

    [Fact]
    public void Attachments_RoundTripThroughChunksAndAreVerified()
    {
        var sender = Attachments("sender");
        var outgoing = ShareAttachmentChunker.Prepare(sender.Import(WriteFile(ShareAttachmentStore.WireChunkBytes * 3 + 7)), "Alex");
        Assert.Equal(ShareChatKinds.File, outgoing.Card.Kind);
        Assert.True(outgoing.Card.IsSelf);
        Assert.Equal(4 + 2, outgoing.Wire.Count);                                  // offer, 4 chunks, complete
        Assert.Equal(ShareChatKinds.FileOffer, outgoing.Wire[0].Kind);
        Assert.False(outgoing.Wire[0].IsSelf);
        Assert.Equal(ShareChatKinds.FileComplete, outgoing.Wire[^1].Kind);

        var receiverStore = Attachments("receiver");
        var assembler = new ShareAttachmentAssembler(receiverStore);
        ShareChatMessage? card = null;
        foreach (var wire in outgoing.Wire)
        {
            var outcome = assembler.Accept(wire, out var c, out var error);
            Assert.Null(error);
            if (outcome == ShareAttachmentAssembler.Outcome.Completed) card = c;
        }
        Assert.NotNull(card);
        Assert.Equal(ShareChatKinds.File, card!.Kind);
        Assert.Equal(string.Empty, card.ChunkData);
        Assert.True(receiverStore.IsAvailable(card.AttachmentId));
        Assert.Equal(0, assembler.InFlight);
    }

    [Fact]
    public void Attachments_IncompleteOrTamperedTransfers_AreRejected()
    {
        var outgoing = ShareAttachmentChunker.Prepare(Attachments("s").Import(WriteFile(ShareAttachmentStore.WireChunkBytes * 2)), "Alex");

        var missing = new ShareAttachmentAssembler(Attachments("r1"));
        missing.Accept(outgoing.Wire[0], out _, out _);
        missing.Accept(outgoing.Wire[1], out _, out _);                              // chunk 2 never arrives
        Assert.Equal(ShareAttachmentAssembler.Outcome.Rejected, missing.Accept(outgoing.Wire[^1], out var none, out var why));
        Assert.Null(none);
        Assert.IsType<InvalidDataException>(why);

        var tampered = new ShareAttachmentAssembler(Attachments("r2"));
        var offer = outgoing.Wire[0] with { AttachmentSha256 = new string('0', 64) };
        tampered.Accept(offer, out _, out _);
        foreach (var w in outgoing.Wire.Skip(1).Take(2)) tampered.Accept(w, out _, out _);
        Assert.Equal(ShareAttachmentAssembler.Outcome.Rejected, tampered.Accept(outgoing.Wire[^1], out _, out _));
    }

    [Fact]
    public void Attachments_BadOffersAreIgnored_AndTransfersAreCapped()
    {
        var assembler = new ShareAttachmentAssembler(Attachments(), maxInFlight: 2);
        var good = new ShareChatMessage("m", "S", "", DateTime.Now, false, ShareChatKinds.FileOffer, "a1", "x.bin", 10, new string('a', 64), 0, 1);

        Assert.Equal(ShareAttachmentAssembler.Outcome.Ignored, assembler.Accept(good with { AttachmentSize = 0 }, out _, out _));
        Assert.Equal(ShareAttachmentAssembler.Outcome.Ignored, assembler.Accept(good with { AttachmentSize = ShareAttachmentStore.MaxAttachmentBytes + 1 }, out _, out _));
        Assert.Equal(ShareAttachmentAssembler.Outcome.Ignored, assembler.Accept(good with { AttachmentSha256 = "short" }, out _, out _));
        Assert.Equal(ShareAttachmentAssembler.Outcome.Ignored, assembler.Accept(good with { ChunkCount = 0 }, out _, out _));

        Assert.Equal(ShareAttachmentAssembler.Outcome.Pending, assembler.Accept(good, out _, out _));
        Assert.Equal(ShareAttachmentAssembler.Outcome.Pending, assembler.Accept(good with { AttachmentId = "a2" }, out _, out _));
        Assert.Equal(ShareAttachmentAssembler.Outcome.Ignored, assembler.Accept(good with { AttachmentId = "a3" }, out _, out _)); // cap
        Assert.Equal(ShareAttachmentAssembler.Outcome.Ignored, assembler.Accept(good with { Kind = ShareChatKinds.FileChunk, AttachmentId = "unknown" }, out _, out _));
        Assert.Equal(ShareAttachmentAssembler.Outcome.Ignored, assembler.Accept(good with { Kind = ShareChatKinds.Text }, out _, out _));
    }

    // -- Conversation -----------------------------------------------------------------------

    [Fact]
    public async Task Conversation_SendsAndKeepsText_AndFilesInOrder()
    {
        var sent = new List<ShareChatMessage>();
        var chat = Chat();
        var conversation = new ShareRoomConversation(chat, Attachments(), m => { sent.Add(m); return Task.CompletedTask; }, new LogService());

        ShareChatMessage? shown = null;
        var message = await conversation.SendTextAsync("Alex", "  hello  ", m => shown = m);
        Assert.Same(message, shown);
        Assert.Equal("hello", message!.Text);
        Assert.Same(message, Assert.Single(sent));
        Assert.Null(await conversation.SendTextAsync("Alex", "   "));

        var outgoing = conversation.PrepareFile("Alex", WriteFile(100));
        await conversation.SendFileAsync(outgoing);
        Assert.Equal(new[] { ShareChatKinds.FileOffer, ShareChatKinds.FileChunk, ShareChatKinds.FileComplete }, sent.Skip(1).Select(m => m.Kind));

        var history = conversation.LoadHistory();
        Assert.Contains(history, m => m.Text == "hello");
        Assert.Contains(history, m => m.AttachmentId == outgoing.Card.AttachmentId);
        Assert.True(conversation.IsAvailable(outgoing.Card.AttachmentId));

        var exported = Path.Combine(_dir, "out.bin");
        conversation.Export(outgoing.Card.AttachmentId, exported);
        Assert.Equal(100, new FileInfo(exported).Length);

        conversation.Clear();
        Assert.Empty(conversation.LoadHistory());
    }

    [Fact]
    public void Conversation_ReceivesTextAndFiles_AndReportsRejectedOnes()
    {
        var conversation = new ShareRoomConversation(Chat(), Attachments(), _ => Task.CompletedTask, new LogService());
        var arrived = new List<ShareChatMessage>();
        var rejected = 0;
        conversation.MessageArrived += (_, m) => arrived.Add(m);
        conversation.AttachmentRejected += (_, _) => rejected++;

        conversation.Receive(new ShareChatMessage("t1", "Sam", "hi", DateTime.Now, IsSelf: true));
        conversation.Receive(new ShareChatMessage("t2", "Sam", " ", DateTime.Now));
        Assert.False(Assert.Single(arrived).IsSelf);

        var outgoing = ShareAttachmentChunker.Prepare(Attachments("other").Import(WriteFile(50)), "Sam");
        foreach (var wire in outgoing.Wire) conversation.Receive(wire);
        Assert.Equal(ShareChatKinds.File, arrived.Last().Kind);

        var bad = ShareAttachmentChunker.Prepare(Attachments("other2").Import(WriteFile(60)), "Sam");
        conversation.Receive(bad.Wire[0] with { AttachmentSha256 = new string('f', 64) });
        foreach (var wire in bad.Wire.Skip(1)) conversation.Receive(wire);
        Assert.Equal(1, rejected);
        Assert.Equal(2, arrived.Count);
        Assert.Equal(2, conversation.LoadHistory().Count);
    }

    // -- Routing and voice --------------------------------------------------------------------

    [Fact]
    public async Task Router_SendsThroughTheHostWhileHosting_AndOtherwiseThroughTheSession()
    {
        var host = new RecordingShareService();
        var session = new ShareRoomSession(new LogService());
        _disposables.Add(session);
        var hosting = true;
        var router = new ShareRoomRouter(host, session, () => hosting);

        await router.SendChatAsync(new ShareChatMessage("1", "A", "x", DateTime.Now));
        await router.SendVoiceAsync(new byte[] { 1 });
        Assert.Single(host.SentChat);
        Assert.Single(host.SentVoice);

        hosting = false; // not joined either: nothing is sent, nothing throws
        await router.SendChatAsync(new ShareChatMessage("2", "A", "y", DateTime.Now));
        await router.SendVoiceAsync(new byte[] { 2 });
        Assert.Single(host.SentChat);
        Assert.Single(host.SentVoice);
    }

    [Fact]
    public void Voice_StartsWithTheChosenStates_SendsCaptureAndPlaysIncoming()
    {
        var device = new RecordingVoice();
        var sent = new ConcurrentQueue<byte[]>();
        var voice = new ShareVoiceCoordinator(device, p => { sent.Enqueue(p); return Task.CompletedTask; });

        Assert.True(voice.Start(muted: true, deafened: false));
        Assert.True(device.IsRunning);
        Assert.True(device.IsMuted);
        Assert.False(device.IsDeafened);

        device.Capture(new byte[] { 3 });
        Assert.Equal(new byte[] { 3 }, Assert.Single(sent));
        voice.Receive(new byte[] { 4 });
        Assert.Equal(new byte[] { 4 }, Assert.Single(device.Received));

        voice.Stop();
        Assert.False(device.IsRunning);
        voice.Dispose();
        device.Capture(new byte[] { 5 });               // unsubscribed: nothing more goes out
        Assert.Single(sent);
    }

    // -- Joined-room session against a real host ---------------------------------------------

    private (ShareViewerHost Host, ShareRoom Room) StartHost(int port)
    {
        var log = new LogService();
        var host = new ShareViewerHost(log, new RemoteInputService(log));
        _disposables.Add(host);
        var room = ShareRoomFactory.Create();
        host.Start(room, "127.0.0.1", webrtcPort: 18889, preferredPort: port);
        return (host, room);
    }

    [Fact]
    public async Task Session_JoinsChatAndVoice_ExchangesTraffic_AndLeaves()
    {
        var (host, _) = StartHost(19850);
        var hostChat = new ConcurrentQueue<ShareChatMessage>();
        var hostVoice = new ConcurrentQueue<byte[]>();
        host.ChatMessageReceived += (_, m) => hostChat.Enqueue(m);
        host.VoicePacketReceived += (_, p) => hostVoice.Enqueue(p);

        var session = new ShareRoomSession(new LogService());
        _disposables.Add(session);
        var chats = new ConcurrentQueue<ShareChatMessage>();
        var voices = new ConcurrentQueue<byte[]>();
        var voiceStates = new ConcurrentQueue<bool>();
        session.ChatReceived += (_, m) => chats.Enqueue(m);
        session.VoiceReceived += (_, p) => voices.Enqueue(p);
        session.VoiceConnectionChanged += (_, open) => voiceStates.Enqueue(open);

        var link = new Uri(host.ViewerUrl);
        session.JoinChat(link);
        Assert.True(session.HasSecret);
        Assert.True(session.JoinVoice(link));
        await Until(() => session.IsChatOpen && session.IsVoiceOpen, "both sockets open");
        await Until(() => voiceStates.Contains(true), "voice reported open");

        Assert.True(await session.SendChatAsync(new ShareChatMessage("c1", "Me", "from session", DateTime.Now, IsSelf: true)));
        await Until(() => hostChat.Any(m => m.Text == "from session"), "host got chat");
        host.BroadcastChatMessage(new ShareChatMessage("h1", "Host", "back", DateTime.Now, IsSelf: true));
        await Until(() => chats.Any(m => m.Text == "back"), "session got chat");
        Assert.False(chats.Single(m => m.Text == "back").IsSelf);

        Assert.True(await session.SendVoiceAsync(new byte[] { 1, 2 }));
        await Until(() => hostVoice.Any(p => p.SequenceEqual(new byte[] { 1, 2 })), "host got voice");
        host.BroadcastVoicePacket(new byte[] { 3, 4 });
        await Until(() => voices.Any(p => p.SequenceEqual(new byte[] { 3, 4 })), "session got voice");

        session.Leave();
        Assert.False(session.HasSecret);
        Assert.False(await session.SendChatAsync(new ShareChatMessage("c2", "Me", "after", DateTime.Now)));
        await Until(() => host.GetActiveParticipants().Count == 0, "host saw us leave");
        await Until(() => voiceStates.LastOrDefault() == false, "voice reported closed");
    }

    [Fact]
    public async Task Session_WithoutTheLinksSecret_OpensNothing()
    {
        var (host, room) = StartHost(19870);
        var session = new ShareRoomSession(new LogService());
        _disposables.Add(session);
        var plain = new Uri(host.ViewerUrl.Split('#')[0]);

        session.JoinChat(plain);
        Assert.False(session.HasSecret);
        Assert.False(session.JoinVoice(plain));
        await Task.Delay(300);
        Assert.Empty(host.GetActiveParticipants());
        Assert.False(await session.SendVoiceAsync(new byte[] { 1 }));
        _ = room;
    }

    [Fact]
    public async Task Session_SurvivesAFailedAdmission()
    {
        var states = new ConcurrentQueue<bool>();
        var session = new ShareRoomSession(new LogService(), (_, _, _, _) => throw new IOException("host unreachable"));
        _disposables.Add(session);
        session.VoiceConnectionChanged += (_, open) => states.Enqueue(open);

        var link = new Uri("http://192.0.2.1:8890/axe#v=secret");
        session.JoinChat(link);
        Assert.True(session.JoinVoice(link));
        await Until(() => states.Contains(false), "voice reported closed after the failure");
        Assert.False(session.IsChatOpen);
        Assert.False(session.IsVoiceOpen);
    }
}
