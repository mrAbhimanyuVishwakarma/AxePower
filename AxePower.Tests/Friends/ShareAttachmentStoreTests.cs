using System;
using System.IO;
using System.Linq;
using System.Text;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.ShareScreen;
using NAudio.Wave;
using Xunit;

namespace AxePower.Tests.Friends;

public sealed class ShareAttachmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AxePower_Attachments_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ImportAndExport_RoundTripsThroughEncryptedStorage()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "hello.txt");
        var secret = "private attachment contents";
        File.WriteAllText(source, secret);
        var storeDirectory = Path.Combine(_root, "store");
        var store = new ShareAttachmentStore(storeDirectory, new LogService(), ProtectForTest, ProtectForTest);

        var attachment = store.Import(source);
        var encryptedFile = Assert.Single(Directory.GetFiles(storeDirectory, "*.axefile"));
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(encryptedFile)));

        var exported = Path.Combine(_root, "exported.txt");
        store.Export(attachment.Id, exported);
        Assert.Equal(secret, File.ReadAllText(exported));
    }

    [Fact]
    public void Accept_RejectsTamperedBytes()
    {
        var store = new ShareAttachmentStore(Path.Combine(_root, "store"), new LogService(), ProtectForTest, ProtectForTest);
        var bytes = Encoding.UTF8.GetBytes("expected");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.Throws<InvalidDataException>(() => store.Accept(
            Guid.NewGuid().ToString("N"), "safe.txt", bytes.Length, hash,
            Encoding.UTF8.GetBytes("tampered")));
    }

    [Fact]
    public void ChatStore_DoesNotPersistReadableMessageText()
    {
        var store = new ShareChatStore(Path.Combine(_root, "chat"), new LogService(), ProtectForTest, ProtectForTest);
        var message = new ShareChatMessage("1", "Tester", "top secret message", DateTime.Now, true);

        store.SaveMessages(new[] { message });

        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(store.ChatFilePath));
        Assert.DoesNotContain("top secret message", raw);
        Assert.Equal("top secret message", Assert.Single(store.LoadMessages()).Text);
    }

    [Fact]
    public void SanitizeFileName_StripsDirectories()
    {
        var result = ShareAttachmentStore.SanitizeFileName(Path.Combine("folder", "payload.zip"));
        Assert.Equal("payload.zip", result);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, result);
    }

    [Fact]
    public void RoomMessageCrypto_EncryptsAndAuthenticatesPayload()
    {
        const string secret = "session-secret";
        var source = new ShareChatMessage("m1", "Alex", "encrypted hello", DateTime.UtcNow);

        var wire = RoomMessageCrypto.Encrypt(source, secret);

        Assert.DoesNotContain("encrypted hello", Encoding.UTF8.GetString(wire));
        Assert.True(RoomMessageCrypto.TryDecrypt(wire, secret, out var decoded));
        Assert.Equal(source.Text, decoded!.Text);
        Assert.False(RoomMessageCrypto.TryDecrypt(wire, "wrong-secret", out _));
    }

    [Fact]
    public void RoomVoiceCrypto_EncryptsAndAuthenticatesOpusPacket()
    {
        var opus = new byte[] { 1, 4, 9, 16, 25 };
        var wire = RoomVoiceCrypto.Encrypt(opus, "voice-secret");

        Assert.NotEqual(opus, wire);
        Assert.True(RoomVoiceCrypto.TryDecrypt(wire, "voice-secret", out var decoded));
        Assert.Equal(opus, decoded);
        Assert.False(RoomVoiceCrypto.TryDecrypt(wire, "wrong-secret", out _));
    }

    [Fact]
    public void VoiceCaptureConversion_MixesStereoFloatToMono()
    {
        var floats = new[] { 1f, -1f, 0.5f, 0.5f };
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);

        var converted = RoomVoiceService.ConvertToMono48k(
            bytes, bytes.Length, WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2));

        Assert.Equal(2, converted.Length);
        Assert.InRange(converted[0], -1, 1);
        Assert.InRange(converted[1], 16_382, 16_385);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private static byte[] ProtectForTest(byte[] bytes) => bytes.Select(value => (byte)(value ^ 0xA5)).ToArray();
}
