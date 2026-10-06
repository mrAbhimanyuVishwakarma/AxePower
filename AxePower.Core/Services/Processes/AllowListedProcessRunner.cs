using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.Processes;

public interface IProcessRunner
{
    /// <summary>
    /// Runs the command with no shell and no window, streams its output, and ends its whole process
    /// tree on timeout or cancellation. Never throws for a failed run; the result says what happened.
    /// </summary>
    Task<ProcessRunResult> RunAsync(ProcessCommand command, Action<ProcessOutputLine>? output = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// The one place non-interactive programs are started: allow-listed path, <c>ArgumentList</c>,
/// stdin closed (a prompt reads end-of-file instead of waiting), the system folder as working
/// directory, and a per-run job object so cancelling ends exactly this run's tree.
/// </summary>
public sealed class AllowListedProcessRunner : IProcessRunner
{
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private readonly ExecutableAllowList _allowList;
    private readonly LogService? _log;

    public AllowListedProcessRunner(ExecutableAllowList allowList, LogService? log = null)
    {
        _allowList = allowList;
        _log = log;
    }

    public async Task<ProcessRunResult> RunAsync(ProcessCommand command, Action<ProcessOutputLine>? output = null, CancellationToken cancellationToken = default)
    {
        var path = _allowList.Resolve(command.Tool);
        if (path is null)
        {
            _log?.LogWarning($"[Process] {command.Tool} is not available at its allowed location; not run.");
            return new ProcessRunResult(ProcessRunOutcome.NotAllowed, Error: $"{command.Tool} is not installed in its expected location.");
        }
        if (cancellationToken.IsCancellationRequested) return new ProcessRunResult(ProcessRunOutcome.Cancelled);

        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        foreach (var argument in command.Arguments) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output?.Invoke(new ProcessOutputLine(e.Data, false)); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) output?.Invoke(new ProcessOutputLine(e.Data, true)); };

        using var job = new ProcessTreeJob();
        try
        {
            if (!process.Start()) return new ProcessRunResult(ProcessRunOutcome.FailedToStart, Error: "The process did not start.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log?.LogWarning($"[Process] {command.Display} failed to start: {ex.Message}");
            return new ProcessRunResult(ProcessRunOutcome.FailedToStart, Error: ex.Message);
        }

        job.Assign(process);
        try { process.StandardInput.Close(); } catch (Exception) { /* Best-effort: the process may already have exited; stdin is closed either way when it goes. */ }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(command.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EndTree(process, job);
            var outcome = cancellationToken.IsCancellationRequested ? ProcessRunOutcome.Cancelled : ProcessRunOutcome.TimedOut;
            _log?.LogWarning($"[Process] {command.Tool} {outcome.ToString().ToLowerInvariant()}; its process tree was ended.");
            return new ProcessRunResult(outcome);
        }

        job.Release();
        return new ProcessRunResult(ProcessRunOutcome.Exited, process.ExitCode);
    }

    /// <summary>The job ends everything that joined it; the tree kill catches anything that started before the job took hold.</summary>
    private static void EndTree(Process process, ProcessTreeJob job)
    {
        job.Terminate();
        try { process.Kill(entireProcessTree: true); } catch (Exception) { /* Best-effort after the job object already ended the tree; anything left is already gone. */ }
        try { process.WaitForExit((int)KillWait.TotalMilliseconds); } catch (Exception) { /* Best-effort wait; the outcome is already decided as cancelled or timed out. */ }
    }
}
