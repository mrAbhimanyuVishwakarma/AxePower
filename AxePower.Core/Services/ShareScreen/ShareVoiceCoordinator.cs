using System;
using System.Threading.Tasks;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Room voice without the UI: starts and stops the audio devices, applies mute and deafen, sends
/// what the microphone encodes, and plays what arrives.
/// </summary>
public sealed class ShareVoiceCoordinator : IDisposable
{
    private readonly IRoomVoiceService _voice;
    private readonly Func<byte[], Task> _send;

    public ShareVoiceCoordinator(IRoomVoiceService voice, Func<byte[], Task> send)
    {
        _voice = voice;
        _send = send;
        _voice.EncodedPacketReady += OnEncodedPacketReady;
    }

    /// <summary>Starts the devices with the current mute and deafen choices. False when no device could start.</summary>
    public bool Start(bool muted, bool deafened)
    {
        if (!_voice.Start()) return false;
        _voice.SetMuted(muted);
        _voice.SetDeafened(deafened);
        return true;
    }

    public void Stop() => _voice.Stop();

    public void SetMuted(bool muted) => _voice.SetMuted(muted);

    public void SetDeafened(bool deafened) => _voice.SetDeafened(deafened);

    /// <summary>Plays one Opus packet from the room.</summary>
    public void Receive(byte[] opusPacket) => _voice.ReceivePacket(opusPacket);

    private void OnEncodedPacketReady(object? sender, byte[] opusPacket) => _ = _send(opusPacket);

    public void Dispose()
    {
        _voice.EncodedPacketReady -= OnEncodedPacketReady;
        _voice.Dispose();
    }
}
