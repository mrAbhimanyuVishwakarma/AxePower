using System;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>Game duck/pause, power-mode playback profiles and the low-battery rule.</summary>
public interface IMusicAutomationService : IDisposable
{
    void Start();
    void Stop();
}

public interface ISleepTimerService
{
    DateTime? EndsAtUtc { get; }
    SleepTimerAfterAction AfterAction { get; }

    event EventHandler? Changed;

    void StartMinutes(int minutes, SleepTimerAfterAction after);
    void StartEndOfTrack(SleepTimerAfterAction after);
    void StartEndOfQueue(SleepTimerAfterAction after);
    void Cancel();
}

/// <summary>True while a fullscreen game or presentation is in front (SHQueryUserNotificationState).</summary>
public interface IFullscreenDetector
{
    bool IsFullscreenAppActive();
}

/// <summary>Sleep and hibernate. Tests always use a fake; the real one is never called from tests.</summary>
public interface ISystemPowerActions
{
    bool CanHibernate { get; }
    bool Sleep();
    bool Hibernate();
}
