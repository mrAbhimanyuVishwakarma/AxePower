using System.Diagnostics;
using System.Text;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Runs a fixed PowerShell script from AxePower's own code (never user input) with the arguments
/// passed through ArgumentList. Output is UTF-8 so non-ASCII package names survive.
/// </summary>
internal static class PowerShellRunner
{
    public sealed record Result(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

    public static async Task<Result> RunAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = UninstallCommandBuilder.PowerShellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " + script);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("PowerShell could not be started.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new Result(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new Result(-1, string.Empty, "PowerShell timed out.", true);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }
}
