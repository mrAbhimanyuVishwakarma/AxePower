using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Key derivation and admission proofs for a Share Screen room.
///
/// Every key is HKDF-SHA256 over the room's viewer token with its own <c>info</c> string, so a key
/// for one purpose is never usable for another. The viewer token itself never crosses the wire:
/// clients prove they hold it by answering a single-use nonce with an HMAC (see
/// docs/security/SHARE_SCREEN_THREAT_MODEL.md §4.2 and §4.5).
/// </summary>
public static class ShareScreenCrypto
{
    private static readonly byte[] ChatDomainInfo = "AxePower-ShareScreen-Chat-v1"u8.ToArray();
    private static readonly byte[] VoiceDomainInfo = "AxePower-ShareScreen-Voice-v1"u8.ToArray();
    private static readonly byte[] AuthDomainInfo = "AxePower-ShareScreen-Auth-v1"u8.ToArray();
    private static readonly byte[] ControlDomainInfo = "AxePower-ShareScreen-Control-v1"u8.ToArray();
    private static readonly byte[] ControlFramesDomainInfo = "AxePower-ShareScreen-ControlFrames-v2"u8.ToArray();

    public const string ChatChannel = "chat";
    public const string VoiceChannel = "voice";
    public const string ControlChannel = "control";
    public const string WhepChannel = "whep";

    /// <summary>256-bit AES-GCM key for chat and file messages.</summary>
    public static byte[] DeriveChatKey(string viewerToken) => Derive(viewerToken, null, ChatDomainInfo);

    /// <summary>256-bit AES-GCM key for Opus voice packets.</summary>
    public static byte[] DeriveVoiceKey(string viewerToken) => Derive(viewerToken, null, VoiceDomainInfo);

    /// <summary>Key that proves knowledge of the viewer token when opening the chat or voice socket.</summary>
    public static byte[] DeriveAuthKey(string viewerToken) => Derive(viewerToken, null, AuthDomainInfo);

    /// <summary>
    /// Key that proves knowledge of both the viewer token and the control code. Taking control
    /// needs both: the link alone (viewer token) is not enough, and neither is a code alone.
    /// </summary>
    public static byte[] DeriveControlKey(string viewerToken, string controlCode) =>
        Derive(viewerToken, Encoding.UTF8.GetBytes(controlCode), ControlDomainInfo);

    /// <summary>Per-connection AES-256-GCM key for control frames, bound to the nonce that admitted it.</summary>
    public static byte[] DeriveControlFrameKey(byte[] controlKey, string nonce) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, controlKey, 32, Encoding.UTF8.GetBytes(nonce), ControlFramesDomainInfo);

    private static byte[] Derive(string viewerToken, byte[]? salt, byte[] info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(viewerToken), 32, salt, info);

    /// <summary>The admission proof for <paramref name="channel"/>: lowercase hex HMAC-SHA256.</summary>
    public static string ComputeProof(byte[] key, string channel, string nonce) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"axe-auth|{channel}|{nonce}")))
            .ToLowerInvariant();

    /// <summary>Checks an admission proof in constant time.</summary>
    public static bool VerifyProof(byte[] key, string channel, string nonce, string? suppliedProof)
    {
        if (string.IsNullOrEmpty(suppliedProof) || suppliedProof.Length != 64) return false;
        var expected = Encoding.ASCII.GetBytes(ComputeProof(key, channel, nonce));
        var supplied = Encoding.ASCII.GetBytes(suppliedProof.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    /// <summary>
    /// Seals one control event as <c>&lt;seq&gt;|&lt;base64(ciphertext || tag)&gt;</c> with AES-256-GCM.
    /// The nonce is the sequence number, which is strictly increasing and so never repeats under
    /// one connection's key; the sequence is also the associated data, so it cannot be altered.
    /// </summary>
    public static string SealControlFrame(byte[] frameKey, long sequence, string eventJson)
    {
        var plain = Encoding.UTF8.GetBytes(eventJson);
        var sealedBytes = new byte[plain.Length + 16];
        using var aes = new AesGcm(frameKey, 16);
        aes.Encrypt(ControlNonce(sequence), plain, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), ControlAad(sequence));
        return $"{sequence}|{Convert.ToBase64String(sealedBytes)}";
    }

    /// <summary>Opens a frame sealed by <see cref="SealControlFrame"/>. False if it was altered or forged.</summary>
    public static bool TryOpenControlFrame(byte[] frameKey, long sequence, string sealedBase64, out string eventJson)
    {
        eventJson = string.Empty;
        if (sequence < 1) return false;
        byte[] sealedBytes;
        try { sealedBytes = Convert.FromBase64String(sealedBase64); }
        catch (FormatException) { return false; }
        if (sealedBytes.Length < 16) return false;

        var plain = new byte[sealedBytes.Length - 16];
        try
        {
            using var aes = new AesGcm(frameKey, 16);
            aes.Decrypt(ControlNonce(sequence), sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), plain, ControlAad(sequence));
        }
        catch (CryptographicException)
        {
            return false;
        }

        try
        {
            eventJson = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(plain);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static byte[] ControlNonce(long sequence)
    {
        var nonce = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), sequence);
        return nonce;
    }

    private static byte[] ControlAad(long sequence) => Encoding.ASCII.GetBytes($"axe-ctl|{sequence}");

    /// <summary>A random 64-bit sender id, as 16 hex characters, for this process.</summary>
    public static string NewSenderId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}

/// <summary>
/// Issues the single-use challenges a client must answer to open a room socket. A nonce lives
/// 30 seconds and is consumed by its first use, so a captured upgrade request cannot be replayed.
/// At most 64 are outstanding, and at most 8 per client address, so one address cannot use them
/// all up and keep everyone else out.
/// </summary>
public sealed class AdmissionNonces
{
    private readonly ConcurrentDictionary<string, (DateTime Expires, string Client)> _issued = new();
    private readonly int _maxOutstanding;
    private readonly int _maxPerClient;
    private readonly TimeSpan _lifetime;
    private readonly Func<DateTime> _clock;

    public AdmissionNonces(int maxOutstanding = 64, TimeSpan? lifetime = null, Func<DateTime>? clock = null, int maxPerClient = 8)
    {
        _maxOutstanding = maxOutstanding;
        _maxPerClient = maxPerClient;
        _lifetime = lifetime ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>A fresh nonce, or null when too many are outstanding overall or for <paramref name="client"/>.</summary>
    public string? Issue(string client = "")
    {
        Prune();
        if (_issued.Count >= _maxOutstanding) return null;
        if (_issued.Values.Count(v => v.Client == client) >= _maxPerClient) return null;
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _issued[nonce] = (_clock() + _lifetime, client);
        return nonce;
    }

    /// <summary>Consumes the nonce. True only for an issued, unexpired, not yet used nonce.</summary>
    public bool TryConsume(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce)) return false;
        return _issued.TryRemove(nonce, out var entry) && entry.Expires > _clock();
    }

    public void Reset() => _issued.Clear();

    private void Prune()
    {
        var now = _clock();
        foreach (var (nonce, entry) in _issued)
        {
            if (entry.Expires <= now) _issued.TryRemove(nonce, out _);
        }
    }
}

/// <summary>
/// Per-sender sliding-window replay filter. A sequence number is accepted once: above the highest
/// seen, or within the last <see cref="WindowSize"/> and not yet seen. Callers must check it only
/// after the packet has authenticated, so a forger cannot move the window.
/// </summary>
public sealed class ReplayFilter
{
    public const int WindowSize = 1024;

    private sealed class SenderWindow
    {
        public long Highest = -1;
        public readonly ulong[] Bits = new ulong[WindowSize / 64];
        public DateTime LastSeen;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, SenderWindow> _senders = new(StringComparer.Ordinal);
    private readonly int _maxSenders;

    public ReplayFilter(int maxSenders = 64) => _maxSenders = maxSenders;

    /// <summary>True when (<paramref name="sender"/>, <paramref name="sequence"/>) is new.</summary>
    public bool TryAccept(string sender, long sequence)
    {
        if (sequence < 0) return false;
        lock (_lock)
        {
            if (!_senders.TryGetValue(sender, out var window))
            {
                if (_senders.Count >= _maxSenders) EvictOldest();
                window = new SenderWindow();
                _senders[sender] = window;
            }
            window.LastSeen = DateTime.UtcNow;

            if (sequence > window.Highest)
            {
                // Slots between the old and new highest still hold sequences that have now fallen
                // out of the window; clear them before they are reused.
                if (window.Highest < 0 || sequence - window.Highest >= WindowSize)
                {
                    Array.Clear(window.Bits);
                }
                else
                {
                    for (long s = window.Highest + 1; s < sequence; s++) Clear(window, s);
                }
                window.Highest = sequence;
                Set(window, sequence);
                return true;
            }

            if (window.Highest - sequence >= WindowSize) return false; // too old to judge
            if (IsSet(window, sequence)) return false;                 // already seen
            Set(window, sequence);
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock) { _senders.Clear(); }
    }

    private void EvictOldest()
    {
        string? oldest = null;
        DateTime oldestSeen = DateTime.MaxValue;
        foreach (var (sender, window) in _senders)
        {
            if (window.LastSeen < oldestSeen) { oldestSeen = window.LastSeen; oldest = sender; }
        }
        if (oldest != null) _senders.Remove(oldest);
    }

    private static int Slot(long sequence) => (int)(sequence % WindowSize);
    private static bool IsSet(SenderWindow w, long s) => (w.Bits[Slot(s) / 64] & (1UL << (Slot(s) % 64))) != 0;
    private static void Set(SenderWindow w, long s) => w.Bits[Slot(s) / 64] |= 1UL << (Slot(s) % 64);
    private static void Clear(SenderWindow w, long s) => w.Bits[Slot(s) / 64] &= ~(1UL << (Slot(s) % 64));
}
