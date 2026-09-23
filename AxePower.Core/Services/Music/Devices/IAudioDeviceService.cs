using System;
using System.Collections.Generic;

namespace AxePower.Core.Services.Music;

public sealed record AudioOutputDevice(string Id, string Name, bool IsDefault);

/// <summary>Active render devices and their plug/unplug/default-change notifications.</summary>
public interface IAudioDeviceService : IDisposable
{
    IReadOnlyList<AudioOutputDevice> GetRenderDevices();

    event EventHandler? DefaultDeviceChanged;

    /// <summary>Carries the id of the device that disappeared.</summary>
    event EventHandler<string>? DeviceRemoved;

    event EventHandler? DevicesChanged;
}
