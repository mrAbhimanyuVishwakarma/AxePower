using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Where outgoing room traffic goes: through this PC's own room when it is hosting, otherwise
/// through the friend's room it has joined. Nothing is sent when neither is open.
/// </summary>
public sealed class ShareRoomRouter
{
    private readonly IShareScreenService _host;
    private readonly ShareRoomSession _session;
    private readonly Func<bool> _isHosting;

    public ShareRoomRouter(IShareScreenService host, ShareRoomSession session, Func<bool> isHosting)
    {
        _host = host;
        _session = session;
        _isHosting = isHosting;
    }

    public Task SendChatAsync(ShareChatMessage message)
    {
        if (_isHosting())
        {
            _host.SendChatMessage(message);
            return Task.CompletedTask;
        }
        return _session.SendChatAsync(message);
    }

    public Task SendVoiceAsync(byte[] opusPacket)
    {
        if (_isHosting())
        {
            _host.SendVoicePacket(opusPacket);
            return Task.CompletedTask;
        }
        return _session.SendVoiceAsync(opusPacket);
    }
}

/// <summary>
/// Room chat without the UI: history on disk, sending text and files, and turning incoming wire
/// messages (from the host or a joined room) into messages to show. Events fire on whatever
/// thread delivered the message.
/// </summary>
public sealed class ShareRoomConversation
{
    private readonly ShareChatStore _store;
    private readonly ShareAttachmentStore _attachments;
    private readonly ShareAttachmentAssembler _assembler;
    private readonly Func<ShareChatMessage, Task> _send;
    private readonly LogService _log;

    public ShareRoomConversation(ShareChatStore store, ShareAttachmentStore attachments, Func<ShareChatMessage, Task> send, LogService log)
    {
        _store = store;
        _attachments = attachments;
        _assembler = new ShareAttachmentAssembler(attachments);
        _send = send;
        _log = log;
    }

    /// <summary>A message to show: incoming text, or a received file that passed its checks. Already saved.</summary>
    public event EventHandler<ShareChatMessage>? MessageArrived;

    /// <summary>A received file failed its size or hash check and was discarded.</summary>
    public event EventHandler<Exception>? AttachmentRejected;

    public IReadOnlyList<ShareChatMessage> LoadHistory() => _store.LoadMessages();

    /// <summary>
    /// Saves and sends a text message. Returns it so the caller can show it, or null when there
    /// was nothing to send. <paramref name="beforeSend"/> runs once it is saved.
    /// </summary>
    public async Task<ShareChatMessage?> SendTextAsync(string selfName, string? text, Action<ShareChatMessage>? beforeSend = null)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        var message = new ShareChatMessage(Guid.NewGuid().ToString("N"), selfName, trimmed, DateTime.Now, IsSelf: true);
        beforeSend?.Invoke(message);
        _store.AppendMessage(message);
        await _send(message);
        return message;
    }

    /// <summary>Imports a file into the encrypted attachment store and saves its card. Throws if the file is unusable.</summary>
    public OutgoingAttachment PrepareFile(string selfName, string path)
    {
        var outgoing = ShareAttachmentChunker.Prepare(_attachments.Import(path), selfName);
        _store.AppendMessage(outgoing.Card);
        return outgoing;
    }

    /// <summary>Sends a prepared file's offer, chunks and completion, in order.</summary>
    public async Task SendFileAsync(OutgoingAttachment outgoing)
    {
        foreach (var wire in outgoing.Wire) await _send(wire);
    }

    /// <summary>Handles one wire message from the room. Anything not worth showing is dropped.</summary>
    public void Receive(ShareChatMessage wire)
    {
        var message = wire with { IsSelf = false };
        if (message.Kind == ShareChatKinds.Text)
        {
            if (string.IsNullOrWhiteSpace(message.Text)) return;
            Arrive(message);
            return;
        }

        switch (_assembler.Accept(message, out var card, out var error))
        {
            case ShareAttachmentAssembler.Outcome.Completed:
                Arrive(card!);
                break;
            case ShareAttachmentAssembler.Outcome.Rejected:
                _log.LogError("[Friends] Rejected an invalid attachment", error);
                AttachmentRejected?.Invoke(this, error!);
                break;
        }
    }

    private void Arrive(ShareChatMessage message)
    {
        _store.AppendMessage(message);
        MessageArrived?.Invoke(this, message);
    }

    public void Clear() => _store.Clear();

    public bool IsAvailable(string attachmentId) => _attachments.IsAvailable(attachmentId);

    public void Export(string attachmentId, string destinationPath) => _attachments.Export(attachmentId, destinationPath);
}
