using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>Encrypts room chat and attachment messages with the per-session viewer secret.</summary>
public static class RoomMessageCrypto
{
    private const int TagBytes = 16;

    public static byte[] Encrypt(ShareChatMessage message, string sessionSecret)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(message);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(sessionSecret));
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, cipher, tag);

        return JsonSerializer.SerializeToUtf8Bytes(new EncryptedRoomMessage(
            1,
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(cipher),
            Convert.ToBase64String(tag)));
    }

    public static bool TryDecrypt(ReadOnlySpan<byte> payload, string sessionSecret, out ShareChatMessage? message)
    {
        message = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<EncryptedRoomMessage>(payload);
            if (envelope is not { Version: 1 }) return false;

            var nonce = Convert.FromBase64String(envelope.Nonce);
            var cipher = Convert.FromBase64String(envelope.Ciphertext);
            var tag = Convert.FromBase64String(envelope.Tag);
            if (nonce.Length != 12 || tag.Length != TagBytes || cipher.Length > 64 * 1024) return false;

            var plain = new byte[cipher.Length];
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(sessionSecret));
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain);
            message = JsonSerializer.Deserialize<ShareChatMessage>(plain);
            return message != null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            return false;
        }
    }

    private sealed record EncryptedRoomMessage(int Version, string Nonce, string Ciphertext, string Tag);
}
