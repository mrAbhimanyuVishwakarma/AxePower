// Portions adapted from Bulk Crap Uninstaller (https://github.com/BCUninstaller/Bulk-Crap-Uninstaller)
// Copyright (c) 2017 Marcin Szeniak. Licensed under the Apache License, Version 2.0. Modified for AxePower.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace AxePower.Core.Services.Uninstaller;

public sealed record ProcessWatchOptions(
    bool IncludeMsiexecChildren,
    string? InstallLocation,
    TimeSpan PollInterval,
    TimeSpan GracePeriod)
{
    /// <summary>Ask <see cref="OnStalled"/> once the tree has run this long (then again after the same interval).</summary>
    public TimeSpan? StallAfter { get; init; }

    /// <summary>Called when the tree stalls; return true to stop the uninstaller.</summary>
    public Func<TimeSpan, CancellationToken, Task<bool>>? OnStalled { get; init; }

    /// <summary>Cancel to terminate every process in the watched tree (the user chose "Stop uninstaller").</summary>
    public CancellationToken KillToken { get; init; }

    public static ProcessWatchOptions Default { get; } =
        new(false, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
}

public enum ProcessTreeStatus
{
    /// <summary>Every process in the tree exited on its own.</summary>
    Exited,

    /// <summary>We stopped waiting (cancellation). Nothing was killed.</summary>
    WaitAbandoned,

    /// <summary>The user chose to stop the uninstaller and the tree was terminated.</summary>
    Killed,
}

/// <param name="RootExitCode">Exit code of the process we started, when it could be read.</param>
/// <param name="LastExitCode">Exit code of the last watched process to exit (the NSIS temp copy, for example).</param>
/// <param name="ProcessCount">How many processes the tree contained over its life.</param>
/// <param name="KillFailed">A stop was requested but some processes couldn't be terminated (they run elevated).</param>
public sealed record ProcessTreeResult(
    ProcessTreeStatus Status,
    int? RootExitCode,
    int? LastExitCode,
    int ProcessCount,
    TimeSpan Elapsed,
    bool KillFailed = false);

public interface IProcessTreeWatcher
{
    /// <summary>
    /// Waits until the process <paramref name="rootPid"/> and everything it started have exited.
    /// <paramref name="launchedUtc"/> is taken just before the root was started.
    /// </summary>
    Task<ProcessTreeResult> WaitAsync(int rootPid, DateTime launchedUtc, ProcessWatchOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// Waits for an uninstaller's whole process tree, the way BCU's <c>BulkUninstallEntry</c> does.
/// NSIS uninstallers copy themselves to %TEMP% (Au_.exe), start the copy and exit at once;
/// waiting only for the first process reports success while the wizard is still open.
///
/// Every poll takes a Toolhelp snapshot and adds any process whose parent has ever been watched
/// and which was created after that parent (the parent-PID field survives the parent's exit, and
/// the creation-time check guards against PID reuse). When nothing is left it waits a grace
/// period and looks once for stragglers in the install folder or %TEMP%.
/// It never kills anything unless <see cref="ProcessWatchOptions.KillToken"/> is cancelled.
/// </summary>
public sealed partial class ProcessTreeWatcher : IProcessTreeWatcher
{
    private sealed class Tracked
    {
        public required int Pid { get; init; }
        public long? CreationTime { get; set; }
        public SafeProcessHandle? Handle { get; set; }
        public bool Exited { get; set; }
        public int? ExitCode { get; set; }
    }

    public async Task<ProcessTreeResult> WaitAsync(int rootPid, DateTime launchedUtc, ProcessWatchOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var launchTime = launchedUtc.ToFileTimeUtc();
        var tracked = new Dictionary<int, Tracked>();
        var ownPid = Environment.ProcessId;
        int? lastExitCode = null;
        var killRequested = false;
        var killFailed = false;
        var nextStall = options.StallAfter;

        Track(rootPid, launchTime);

        try
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Result(ProcessTreeStatus.WaitAbandoned);

                if (options.KillToken.IsCancellationRequested && !killRequested)
                {
                    killRequested = true;
                    killFailed = !KillAlive();
                }

                var snapshot = NativeProcesses.Snapshot();
                AddDescendants(snapshot);
                UpdateAlive(snapshot);

                if (!tracked.Values.Any(t => !t.Exited))
                {
                    // Nothing left: give a handed-off uninstaller a moment to appear, then look for
                    // stragglers once. Anything found is watched like the rest of the tree.
                    if (!await DelayAsync(options.GracePeriod)) return Result(ProcessTreeStatus.WaitAbandoned);

                    snapshot = NativeProcesses.Snapshot();
                    AddDescendants(snapshot);
                    AddStragglers(snapshot);
                    UpdateAlive(snapshot);

                    if (!tracked.Values.Any(t => !t.Exited))
                        return Result(killRequested ? ProcessTreeStatus.Killed : ProcessTreeStatus.Exited);
                    continue;
                }

                if (nextStall is { } stallAt && stopwatch.Elapsed >= stallAt && options.OnStalled is not null && !killRequested)
                {
                    var stop = await options.OnStalled(stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
                    if (stop)
                    {
                        killRequested = true;
                        killFailed = !KillAlive();
                    }
                    nextStall = stopwatch.Elapsed + options.StallAfter!.Value;
                }

                if (!await DelayAsync(options.PollInterval)) return Result(ProcessTreeStatus.WaitAbandoned);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(ProcessTreeStatus.WaitAbandoned);
        }
        finally
        {
            foreach (var t in tracked.Values)
                t.Handle?.Dispose();
        }

        // ── local functions ─────────────────────────────────────────────────

        ProcessTreeResult Result(ProcessTreeStatus status)
        {
            tracked.TryGetValue(rootPid, out var root);
            return new ProcessTreeResult(status, root?.ExitCode, lastExitCode ?? root?.ExitCode,
                tracked.Count, stopwatch.Elapsed, killFailed);
        }

        void Track(int pid, long? fallbackCreation)
        {
            var handle = NativeProcesses.OpenForQuery(pid);
            var creation = handle is null ? null : NativeProcesses.GetCreationTime(handle);
            tracked[pid] = new Tracked { Pid = pid, Handle = handle, CreationTime = creation ?? fallbackCreation };
        }

        void AddDescendants(List<ProcessSnapshotEntry> snapshot)
        {
            var byPid = snapshot.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.First());
            bool changed;
            do
            {
                changed = false;
                foreach (var entry in snapshot)
                {
                    if (entry.Pid == ownPid || entry.Pid == 0 || tracked.ContainsKey(entry.Pid)) continue;

                    bool isChild = false;
                    if (tracked.TryGetValue(entry.ParentPid, out var parent))
                    {
                        var created = NativeProcesses.GetCreationTime(entry.Pid);
                        isChild = created is null || parent.CreationTime is null || created >= parent.CreationTime;
                    }
                    else if (options.IncludeMsiexecChildren &&
                             byPid.TryGetValue(entry.ParentPid, out var parentEntry) &&
                             UninstallerKindDetector.IsMsiExec(parentEntry.ExeName))
                    {
                        // Custom actions run as children of the Windows Installer service's msiexec
                        var created = NativeProcesses.GetCreationTime(entry.Pid);
                        isChild = created is not null && created >= launchTime;
                    }

                    if (!isChild) continue;
                    Track(entry.Pid, null);
                    changed = true;
                }
            }
            while (changed);
        }

        void AddStragglers(List<ProcessSnapshotEntry> snapshot)
        {
            var temp = ProtectedPaths.Normalize(Path.GetTempPath());
            var install = ProtectedPaths.Normalize(options.InstallLocation);

            foreach (var entry in snapshot)
            {
                if (entry.Pid == ownPid || entry.Pid == 0 || tracked.ContainsKey(entry.Pid)) continue;

                var byName = TempUninstallerNameRegex().IsMatch(entry.ExeName);
                using var handle = NativeProcesses.OpenForQuery(entry.Pid);
                if (handle is null) continue;
                var created = NativeProcesses.GetCreationTime(handle);
                if (created is null || created < launchTime) continue;

                var matches = byName;
                if (!matches)
                {
                    var image = NativeProcesses.GetImagePath(handle);
                    // Anything in the app's own folder, or something in %TEMP% that names itself
                    // an uninstaller. Adopting every new %TEMP% process would make us wait for
                    // unrelated installers the user happens to start.
                    matches = image is not null &&
                              (install is not null && ProtectedPaths.IsSameOrUnder(image, install) ||
                               temp is not null && ProtectedPaths.IsSameOrUnder(image, temp) &&
                               UninstallerLikeNameRegex().IsMatch(Path.GetFileName(image)));
                }
                if (matches) Track(entry.Pid, created);
            }
        }

        void UpdateAlive(List<ProcessSnapshotEntry> snapshot)
        {
            var present = snapshot.Select(e => e.Pid).ToHashSet();
            foreach (var t in tracked.Values.Where(t => !t.Exited))
            {
                bool exited;
                if (t.Handle is not null)
                    exited = NativeProcesses.HasExited(t.Handle);
                else
                    exited = !present.Contains(t.Pid) ||
                             t.CreationTime is { } c && NativeProcesses.GetCreationTime(t.Pid) is { } now && now != c;

                if (!exited) continue;
                t.Exited = true;
                if (t.Handle is not null)
                {
                    t.ExitCode = NativeProcesses.GetExitCode(t.Handle);
                    if (t.ExitCode is not null) lastExitCode = t.ExitCode;
                }
            }
        }

        bool KillAlive()
        {
            var allKilled = true;
            foreach (var t in tracked.Values.Where(t => !t.Exited))
                if (!NativeProcesses.Terminate(t.Pid)) allKilled = false;
            return allKilled;
        }

        async Task<bool> DelayAsync(TimeSpan delay)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, options.KillToken);
            try
            {
                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) return false;
                // Kill requested: loop round immediately to act on it
            }
            return true;
        }
    }

    /// <summary>NSIS (Au_.exe, Un_A.exe) and Inno/InstallShield (_iu*.tmp) temp copies.</summary>
    [GeneratedRegex(@"^(Au_\.exe|Un_A\.exe|Un_[A-Z]\.exe|_iu.*\.tmp)$", RegexOptions.IgnoreCase)]
    private static partial Regex TempUninstallerNameRegex();

    /// <summary>A program in %TEMP% is only adopted when its name says it is an (un)installer.</summary>
    [GeneratedRegex(@"(unins|uninstall|setup|install)", RegexOptions.IgnoreCase)]
    private static partial Regex UninstallerLikeNameRegex();
}
