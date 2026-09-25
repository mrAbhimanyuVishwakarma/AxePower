using System;
using System.Security.Cryptography;
using System.Text;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>Authenticated encryption for one Opus room-audio packet.</summary>
public static class RoomVoiceCrypto
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static byte[] Encrypt(ReadOnlySpan<byte> opusPacket, string sessionSecret)
    {
        var result = new byte[1 + NonceBytes + TagBytes + opusPacket.Length];
        result[0] = 1;
        var nonce = result.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        var tag = result.AsSpan(1 + NonceBytes, TagBytes);
        var cipher = result.AsSpan(1 + NonceBytes + TagBytes);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(sessionSecret));
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, opusPacket, cipher, tag);
        return result;
    }

    public static bool TryDecrypt(ReadOnlySpan<byte> payload, string sessionSecret, out byte[] opusPacket)
    {
        opusPacket = Array.Empty<byte>();
        if (payload.Length <= 1 + NonceBytes + TagBytes || payload[0] != 1) return false;
        try
        {
            var nonce = payload.Slice(1, NonceBytes);
            var tag = payload.Slice(1 + NonceBytes, TagBytes);
            var cipher = payload[(1 + NonceBytes + TagBytes)..];
            opusPacket = new byte[cipher.Length];
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(sessionSecret));
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, opusPacket);
            return true;
        }
        catch (CryptographicException)
        {
            opusPacket = Array.Empty<byte>();
            return false;
        }
    }
}
