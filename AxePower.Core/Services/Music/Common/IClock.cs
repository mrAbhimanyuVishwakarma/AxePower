using System;

namespace AxePower.Core.Services.Music;

/// <summary>Current time, injectable so timers and play-history rules can be tested with a fake clock.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
