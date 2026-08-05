using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Helpers;

public static class CommandRunner
{
    private static readonly HashSet<string> AllowedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "powercfg",
        "powercfg.exe"
    };

    public static async Task<CommandResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var result = new CommandResult();
        var stopwatch = Stopwatch.StartNew();

        if (!AllowedExecutables.Contains(fileName))
        {
            result.Exception = new UnauthorizedAccessException($"Executable '{fileName}' is not in the allow-list.");
            return result;
        }

        try
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            foreach (var arg in arguments)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            result.Started = true;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout.HasValue)
            {
                cts.CancelAfter(timeout.Value);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
                result.Completed = true;
                result.ExitCode = process.ExitCode;
                result.Output = await outputTask;
                result.Error = await errorTask;
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.Cancelled = true;
                }
                else
                {
                    result.TimedOut = true;
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore kill errors
                }
            }
        }
        catch (Exception ex)
        {
            result.Exception = ex;
        }
        finally
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
        }

        return result;
    }

    // For backwards compatibility during refactor
    public static Task<CommandResult> RunAsync(string fileName, string rawArguments, TimeSpan? timeout = null)
    {
        // Only split by space for simple args, but prefer IEnumerable overload
        var args = rawArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return RunAsync(fileName, args, timeout);
    }
}
