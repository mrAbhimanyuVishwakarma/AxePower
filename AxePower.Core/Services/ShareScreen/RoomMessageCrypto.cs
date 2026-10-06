using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Helpers;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Authenticated encryption for room chat and attachment messages (envelope v2).
///
/// The sender id and sequence number are AES-GCM associated data, so they cannot be altered
/// without failing authentication, and the replay check runs only after a message has decrypted.
/// Version 1 envelopes, whose sequence was not authenticated, are rejected.
/// </summary>
public static class RoomMessageCrypto
{
    public const int CurrentVersion = 2;
    private const int TagBytes = 16;
    private const int MaxCiphertextBytes = 64 * 1024;

    /// <summary>This process's sender id. Random per run, so sequence numbers never need persisting.</summary>
    public static readonly string LocalSender = ShareScreenCrypto.NewSenderId();
    private static long _sequence;

    public static byte[] Encrypt(ShareChatMessage message, string viewerToken, long? sequence = null, string? sender = null)
    {
        var seq = sequence ?? Interlocked.Increment(ref _sequence);
        var from = sender ?? LocalSender;
        var plain = JsonSerializer.SerializeToUtf8Bytes(message);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(ShareScreenCrypto.DeriveChatKey(viewerToken), TagBytes);
        aes.Encrypt(nonce, plain, cipher, tag, AssociatedData(from, seq));

        return JsonSerializer.SerializeToUtf8Bytes(new EncryptedRoomMessage(
            CurrentVersion,
            from,
            seq,
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(cipher),
            Convert.ToBase64String(tag)));
    }

    /// <summary>
    /// Decrypts and authenticates a message, then rejects it if <paramref name="replayFilter"/> has
    /// already seen its sender and sequence.
    /// </summary>
    public static bool TryDecrypt(
        ReadOnlySpan<byte> payload,
        string viewerToken,
        ReplayFilter replayFilter,
        out ShareChatMessage? message,
        out long sequence)
    {
        message = null;
        sequence = 0;
        try
        {
            var envelope = JsonSerializer.Deserialize<EncryptedRoomMessage>(payload);
            if (envelope is not { Version: CurrentVersion } || !IsSenderId(envelope.Sender) || envelope.Sequence < 1)
                return false;

            var nonce = Convert.FromBase64String(envelope.Nonce);
            var cipher = Convert.FromBase64String(envelope.Ciphertext);
            var tag = Convert.FromBase64String(envelope.Tag);
            if (nonce.Length != 12 || tag.Length != TagBytes || cipher.Length > MaxCiphertextBytes) return false;

            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(ShareScreenCrypto.DeriveChatKey(viewerToken), TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain, AssociatedData(envelope.Sender, envelope.Sequence));

            // Only now, with the header authenticated, may the replay window move.
            if (!replayFilter.TryAccept(envelope.Sender, envelope.Sequence)) return false;

            message = JsonSerializer.Deserialize<ShareChatMessage>(plain);
            sequence = envelope.Sequence;
            // Every field of a chat message is required. A null one ("chunkData": null) would
            // otherwise reach the chat loops, which read the fields without checking them.
            if (message is null || !JsonNullRepair.Repair(message).IsComplete)
            {
                message = null;
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            message = null;
            return false;
        }
    }

    private static byte[] AssociatedData(string sender, long sequence) =>
        Encoding.ASCII.GetBytes($"axe-chat|{CurrentVersion}|{sender}|{sequence}");

    private static bool IsSenderId(string? value) =>
        value is { Length: 16 } && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private sealed record EncryptedRoomMessage(
        int Version,
        string Sender,
        long Sequence,
        string Nonce,
        string Ciphertext,
        string Tag);
}
