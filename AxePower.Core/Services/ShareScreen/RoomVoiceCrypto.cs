using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Authenticated encryption for Opus room-audio packets (packet v3).
///
/// Layout: <c>0x03 | sender (8) | sequence (8, big-endian) | nonce (12) | tag (16) | ciphertext</c>.
/// The first 17 bytes are AES-GCM associated data, so the sender and sequence cannot be altered,
/// and the replay check runs only after a packet has decrypted. Versions 1 and 2, which either had
/// no sequence or did not authenticate it, are rejected.
/// </summary>
public static class RoomVoiceCrypto
{
    public const byte CurrentVersion = 3;
    private const int SenderBytes = 8;
    private const int SequenceBytes = 8;
    private const int HeaderBytes = 1 + SenderBytes + SequenceBytes;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly byte[] LocalSender = Convert.FromHexString(RoomMessageCrypto.LocalSender);
    private static long _sequence;

    public static byte[] Encrypt(ReadOnlySpan<byte> opusPacket, string viewerToken, long? sequence = null, byte[]? sender = null)
    {
        var seq = sequence ?? Interlocked.Increment(ref _sequence);
        var from = sender ?? LocalSender;
        if (from.Length != SenderBytes) throw new ArgumentException("Sender id must be 8 bytes.", nameof(sender));

        var result = new byte[HeaderBytes + NonceBytes + TagBytes + opusPacket.Length];
        result[0] = CurrentVersion;
        from.CopyTo(result.AsSpan(1, SenderBytes));
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(1 + SenderBytes, SequenceBytes), seq);

        var nonce = result.AsSpan(HeaderBytes, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        var tag = result.AsSpan(HeaderBytes + NonceBytes, TagBytes);
        var cipher = result.AsSpan(HeaderBytes + NonceBytes + TagBytes);

        using var aes = new AesGcm(ShareScreenCrypto.DeriveVoiceKey(viewerToken), TagBytes);
        aes.Encrypt(nonce, opusPacket, cipher, tag, result.AsSpan(0, HeaderBytes));
        return result;
    }

    /// <summary>
    /// Decrypts and authenticates a packet, then rejects it if <paramref name="replayFilter"/> has
    /// already seen its sender and sequence.
    /// </summary>
    public static bool TryDecrypt(
        ReadOnlySpan<byte> payload,
        string viewerToken,
        ReplayFilter replayFilter,
        out byte[] opusPacket,
        out long sequence)
    {
        opusPacket = Array.Empty<byte>();
        sequence = 0;

        if (payload.Length <= HeaderBytes + NonceBytes + TagBytes || payload[0] != CurrentVersion) return false;

        var header = payload[..HeaderBytes];
        var seq = BinaryPrimitives.ReadInt64BigEndian(header.Slice(1 + SenderBytes, SequenceBytes));
        if (seq < 1) return false;

        try
        {
            var nonce = payload.Slice(HeaderBytes, NonceBytes);
            var tag = payload.Slice(HeaderBytes + NonceBytes, TagBytes);
            var cipher = payload[(HeaderBytes + NonceBytes + TagBytes)..];
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(ShareScreenCrypto.DeriveVoiceKey(viewerToken), TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain, header);

            var sender = Convert.ToHexString(header.Slice(1, SenderBytes)).ToLowerInvariant();
            if (!replayFilter.TryAccept(sender, seq)) return false;

            opusPacket = plain;
            sequence = seq;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
