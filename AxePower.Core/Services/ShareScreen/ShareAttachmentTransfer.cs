using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>A file ready to go out: the card shown in chat, and the offer, chunks and completion to send.</summary>
public sealed record OutgoingAttachment(ShareChatMessage Card, IReadOnlyList<ShareChatMessage> Wire);

/// <summary>Splits an imported file into the room's wire messages.</summary>
public static class ShareAttachmentChunker
{
    public static OutgoingAttachment Prepare(PreparedAttachment file, string selfName, string? messageId = null, DateTime? at = null)
    {
        var chunkCount = (int)Math.Ceiling(file.Bytes.Length / (double)ShareAttachmentStore.WireChunkBytes);
        var card = new ShareChatMessage(
            messageId ?? Guid.NewGuid().ToString("N"), selfName, string.Empty, at ?? DateTime.Now, true,
            ShareChatKinds.File, file.Id, file.Name, file.Size, file.Sha256, 0, chunkCount);

        var offer = card with { Kind = ShareChatKinds.FileOffer, IsSelf = false };
        var wire = new List<ShareChatMessage>(chunkCount + 2) { offer };
        for (var i = 0; i < chunkCount; i++)
        {
            var offset = i * ShareAttachmentStore.WireChunkBytes;
            var length = Math.Min(ShareAttachmentStore.WireChunkBytes, file.Bytes.Length - offset);
            wire.Add(offer with { Kind = ShareChatKinds.FileChunk, ChunkIndex = i, ChunkData = Convert.ToBase64String(file.Bytes, offset, length) });
        }
        wire.Add(offer with { Kind = ShareChatKinds.FileComplete, ChunkData = string.Empty });
        return new OutgoingAttachment(card, wire);
    }
}

/// <summary>
/// Reassembles incoming files from their offer, chunks and completion, then hands the bytes to
/// <see cref="ShareAttachmentStore.Accept"/>, which checks size and SHA-256 before keeping them.
/// At most <c>maxInFlight</c> transfers are tracked at once.
/// </summary>
public sealed class ShareAttachmentAssembler
{
    public enum Outcome { Ignored, Pending, Completed, Rejected }

    private readonly ShareAttachmentStore _store;
    private readonly int _maxInFlight;
    private readonly Dictionary<string, Transfer> _transfers = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ShareAttachmentAssembler(ShareAttachmentStore store, int maxInFlight = 8)
    {
        _store = store;
        _maxInFlight = maxInFlight;
    }

    /// <summary>
    /// Feeds one file message. <paramref name="card"/> is the chat card when a transfer completes
    /// and passes the store's checks; <paramref name="error"/> says why one was rejected.
    /// </summary>
    public Outcome Accept(ShareChatMessage message, out ShareChatMessage? card, out Exception? error)
    {
        card = null;
        error = null;
        switch (message.Kind)
        {
            case ShareChatKinds.FileOffer:
                if (message.AttachmentSize <= 0 || message.AttachmentSize > ShareAttachmentStore.MaxAttachmentBytes ||
                    message.ChunkCount <= 0 || message.ChunkCount > ShareAttachmentStore.MaxWireChunks ||
                    message.AttachmentSha256.Length != 64 || string.IsNullOrWhiteSpace(message.AttachmentId)) return Outcome.Ignored;
                lock (_lock)
                {
                    if (_transfers.Count >= _maxInFlight && !_transfers.ContainsKey(message.AttachmentId)) return Outcome.Ignored;
                    _transfers[message.AttachmentId] = new Transfer(message);
                }
                return Outcome.Pending;

            case ShareChatKinds.FileChunk:
                lock (_lock)
                {
                    if (!_transfers.TryGetValue(message.AttachmentId, out var transfer)) return Outcome.Ignored;
                    transfer.AddChunk(message.ChunkIndex, message.ChunkData);
                }
                return Outcome.Pending;

            case ShareChatKinds.FileComplete:
                Transfer? done;
                lock (_lock)
                {
                    if (!_transfers.Remove(message.AttachmentId, out done)) return Outcome.Ignored;
                }
                try
                {
                    var bytes = done.Combine();
                    _store.Accept(done.Offer.AttachmentId, done.Offer.AttachmentName, done.Offer.AttachmentSize, done.Offer.AttachmentSha256, bytes);
                    card = done.Offer with { Kind = ShareChatKinds.File, Text = string.Empty, ChunkData = string.Empty, ChunkIndex = 0 };
                    return Outcome.Completed;
                }
                catch (Exception ex)
                {
                    error = ex;
                    return Outcome.Rejected;
                }

            default:
                return Outcome.Ignored;
        }
    }

    public int InFlight
    {
        get { lock (_lock) return _transfers.Count; }
    }

    private sealed class Transfer
    {
        private readonly byte[][] _chunks;

        public ShareChatMessage Offer { get; }

        public Transfer(ShareChatMessage offer)
        {
            Offer = offer;
            _chunks = new byte[offer.ChunkCount][];
        }

        public void AddChunk(int index, string base64)
        {
            if (index < 0 || index >= _chunks.Length || string.IsNullOrWhiteSpace(base64)) return;
            try
            {
                var bytes = Convert.FromBase64String(base64);
                if (bytes.Length <= ShareAttachmentStore.WireChunkBytes) _chunks[index] = bytes;
            }
            catch (FormatException) { /* A corrupt chunk is left empty; the hash check then rejects the file. */ }
        }

        public byte[] Combine()
        {
            if (_chunks.Any(chunk => chunk == null))
                throw new InvalidDataException("The attachment transfer was incomplete.");

            using var output = new MemoryStream((int)Offer.AttachmentSize);
            foreach (var chunk in _chunks) output.Write(chunk!);
            return output.ToArray();
        }
    }
}
