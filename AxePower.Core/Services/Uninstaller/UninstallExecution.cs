using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// What an executor observed while running an uninstaller. It is provisional: the batch
/// verifies the result before it becomes an <see cref="UninstallOutcome"/>.
/// </summary>
public sealed class UninstallExecution
{
    public ExecutionVerdict Verdict { get; init; }
    public int? ExitCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public string CommandExecuted { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public bool WasQuiet { get; init; }
    public string LogPath { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// The uninstaller exited within seconds without starting anything we could follow: it
    /// probably handed the job to another program (Steam, a running updater).
    /// </summary>
    public bool HandedOff { get; init; }

    /// <summary>The user asked to stop the uninstaller but some of its processes run elevated.</summary>
    public bool StopFailed { get; init; }
}

/// <summary>Per-app context the batch hands to an executor.</summary>
public sealed class UninstallRunContext
{
    public string SessionId { get; init; } = string.Empty;
    public bool IsElevated { get; init; }

    /// <summary>Cancelled when the user chooses "Stop uninstaller".</summary>
    public CancellationToken KillToken { get; init; }

    /// <summary>A silent uninstall has run past the stall limit; return true to stop it.</summary>
    public Func<TimeSpan, CancellationToken, Task<bool>>? OnQuietStalled { get; init; }

    /// <summary>Called once the uninstaller has started, with the plan that runs.</summary>
    public Action<UninstallPlan>? OnLaunched { get; init; }
}

/// <summary>What to do about an app that is running when its uninstall is due.</summary>
public enum RunningAppDecision
{
    /// <summary>Ask it to close (WM_CLOSE) and wait up to 10 seconds.</summary>
    Close,

    /// <summary>Terminate its processes.</summary>
    ForceClose,

    /// <summary>Don't uninstall it in this batch.</summary>
    Skip,

    /// <summary>Uninstall anyway.</summary>
    Continue,
}

/// <summary>One running process that belongs to an application.</summary>
public sealed record RunningProcessInfo(int Pid, string Name, string ImagePath);

/// <summary>Questions the batch asks the user while it runs. The app answers with dialogs; tests use a fake.</summary>
public interface IUninstallInteraction
{
    /// <param name="closeAttemptFailed">True when a polite close was already tried and the app is still running.</param>
    Task<RunningAppDecision> ResolveRunningApplicationAsync(InstalledApplication app,
        IReadOnlyList<RunningProcessInfo> processes, bool closeAttemptFailed, CancellationToken cancellationToken);

    /// <summary>A silent uninstall is still running after the stall limit. True = stop it.</summary>
    Task<bool> ShouldStopStalledUninstallerAsync(InstalledApplication app, TimeSpan elapsed,
        CancellationToken cancellationToken);
}

/// <summary>
/// Lets the UI steer a running batch: stop the queue after the current app, stop waiting for the
/// current uninstaller, or (silent uninstalls only) stop the uninstaller itself. Nothing is ever
/// killed without an explicit call to <see cref="StopCurrentUninstaller"/>.
/// </summary>
public sealed class UninstallBatchControl
{
    private readonly object _gate = new();
    private CancellationTokenSource? _abandon;
    private CancellationTokenSource? _kill;
    private volatile bool _stopRequested;

    public bool StopRequested => _stopRequested;

    /// <summary>An uninstaller is running right now.</summary>
    public bool IsUninstallerRunning { get; private set; }

    /// <summary>The running uninstaller has no UI of its own (so stopping it may be offered).</summary>
    public bool IsCurrentQuiet { get; private set; }

    public string CurrentApplicationName { get; private set; } = string.Empty;

    /// <summary>Don't start any more apps; the current one finishes normally.</summary>
    public void RequestStop() => _stopRequested = true;

    /// <summary>Stop waiting for the current uninstaller. It keeps running; nothing is killed.</summary>
    public void AbandonCurrentWait()
    {
        lock (_gate) _abandon?.Cancel();
    }

    /// <summary>Terminate the current uninstaller's process tree (offered for silent uninstalls only).</summary>
    public void StopCurrentUninstaller()
    {
        lock (_gate) _kill?.Cancel();
    }

    internal AppScope BeginApp(string applicationName, CancellationToken batchToken)
    {
        lock (_gate)
        {
            _abandon = CancellationTokenSource.CreateLinkedTokenSource(batchToken);
            _kill = new CancellationTokenSource();
            CurrentApplicationName = applicationName;
            return new AppScope(this, _abandon.Token, _kill.Token);
        }
    }

    internal void SetRunning(bool running, bool quiet)
    {
        IsUninstallerRunning = running;
        IsCurrentQuiet = quiet;
    }

    private void EndApp()
    {
        lock (_gate)
        {
            _abandon?.Dispose();
            _kill?.Dispose();
            _abandon = _kill = null;
            IsUninstallerRunning = false;
            IsCurrentQuiet = false;
            CurrentApplicationName = string.Empty;
        }
    }

    internal sealed class AppScope : IDisposable
    {
        private readonly UninstallBatchControl _owner;

        public AppScope(UninstallBatchControl owner, CancellationToken abandonToken, CancellationToken killToken)
        {
            _owner = owner;
            AbandonToken = abandonToken;
            KillToken = killToken;
        }

        public CancellationToken AbandonToken { get; }
        public CancellationToken KillToken { get; }

        public void Dispose() => _owner.EndApp();
    }
}
