using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using AxePower.Core.Services.Friends;
using AxePower.Core.Services.Music;
using AxePower.Core.Services.Routines;
using AxePower.Core.Services.ShareScreen;
using Microsoft.Extensions.DependencyInjection;

namespace AxePower.App.Services;

/// <summary>
/// Everything AxePower does on exit, in order (Prompt 29). First what the user would notice if
/// skipped (the music queue is saved, routines are undone, friends see you leave), each with its
/// own wait. Then every resource is released: periodic work stops before the services it reads
/// are disposed, then listeners, child-process hosts, hotkeys, sensors and the driver, and finally
/// everything else the container created. That second stage has a hard limit, so exit can
/// never hang: past it, the process ends anyway.
/// </summary>
public static class AppShutdown
{
    /// <summary>The longest the release stage may take before the process is ended.</summary>
    public static readonly TimeSpan ReleaseLimit = TimeSpan.FromSeconds(5);

    /// <summary>One step: its name, how long it took, and the error if it failed.</summary>
    public sealed record Step(string Name, TimeSpan Took, string? Error);

    /// <param name="services">The app's container.</param>
    /// <param name="shell">The main window's view model, whose periodic work stops first (not resolved: it is transient).</param>
    /// <param name="hardExit">Called if the release stage passes <paramref name="limit"/>; the app ends the process.</param>
    public static IReadOnlyList<Step> Run(IServiceProvider services, MainViewModel? shell, Action? hardExit, TimeSpan? limit = null)
    {
        var steps = new List<Step>();
        var log = services.GetService<LogService>();

        void Do(string name, Action action)
        {
            var sw = Stopwatch.StartNew();
            string? error = null;
            try { action(); }
            catch (Exception ex)
            {
                // Broad on purpose: one service failing to stop must not skip the rest of shutdown.
                // Recorded in the step and logged below.
                error = ex.GetType().Name + ": " + ex.Message;
            }
            steps.Add(new Step(name, sw.Elapsed, error));
            if (error != null) log?.LogWarning($"[Shutdown] {name} failed: {error}");
        }

        void Wait(string name, Func<Task> work, TimeSpan timeout) =>
            Do(name, () => { if (!Task.Run(work).Wait(timeout)) throw new TimeoutException($"still running after {timeout.TotalSeconds:0} s"); });

        // ── 1. What the user would notice ─────────────────────────────────────────
        // Saves the queue and position, then releases the audio device. Run off the UI thread so
        // its awaits cannot wait on this (blocked) dispatcher.
        if (services.GetService<IMusicPlayerService>() is { } player) Wait("Music player", player.ShutdownAsync, TimeSpan.FromSeconds(3));
        Do("Player integrations", () => services.GetService<PlayerIntegrationHost>()?.Dispose());
        Do("Ambient window", () => services.GetService<AmbientWindowService>()?.Dispose());
        // Running routines are undone (previous mode, reopened apps) before the timer goes
        if (services.GetService<RoutineEngine>() is { } routines) Wait("Routines", routines.StopAsync, TimeSpan.FromSeconds(5));
        Do("Power timer", () => services.GetService<PowerTimerService>()?.Dispose()); // closing AxePower cancels the timer
        // Friends see you leave straight away instead of when the heartbeat runs out
        if (services.GetService<FriendsService>() is { IsActive: true } friends)
            Wait("Friends offline", () => friends.GoOfflineAsync(TimeSpan.FromSeconds(2)), TimeSpan.FromSeconds(3));
        Do("Friends", () => services.GetService<FriendsService>()?.Dispose());
        Do("Auto-switch", () => services.GetService<AutoSwitchService>()?.Stop());
        Do("App monitor", () => services.GetService<AppMonitorService>()?.Stop());

        // ── 2. Release everything, within the limit ───────────────────────────────
        var cap = limit ?? ReleaseLimit;
        using var watchdog = new Timer(_ =>
        {
            log?.LogWarning($"[Shutdown] Releasing resources took over {cap.TotalSeconds:0} s; ending the process.");
            hardExit?.Invoke();
        }, null, cap, Timeout.InfiniteTimeSpan);

        // Periodic work first: a refresh tick must not reach a service that is already disposed
        Do("Periodic refreshes", () => { if (shell != null) shell.IsOnScreen = false; });
        if (services.GetService<ControllerNavigationService>() is { } controller)
            Wait("Controller navigation", controller.StopAsync, TimeSpan.FromSeconds(1));
        Do("LAN Drop listener", () => services.GetService<LanDropService>()?.Dispose());
        Do("Share Screen", () => services.GetService<ShareScreenService>()?.Dispose());
        Do("Terminal shell", () => services.GetService<TerminalService>()?.StopSession());
        Do("Hotkeys", () => services.GetService<HotkeyService>()?.Dispose());
        Do("Telemetry", () => services.GetService<SystemTelemetryService>()?.Dispose());
        Do("Hardware sensors", () => services.GetService<HardwareMonitorService>()?.Dispose());
        Do("Battery history", () => services.GetService<BatteryService>()?.Dispose());
        // Everything else the container created, newest first (each Dispose above is safe to repeat)
        Do("Remaining services", () => (services as IDisposable)?.Dispose());

        return steps;
    }
}
