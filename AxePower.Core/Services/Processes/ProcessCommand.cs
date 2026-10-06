using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AxePower.Core.Services.Processes;

/// <summary>
/// One program run: an allow-listed tool and its arguments as separate strings. The runner hands
/// them to <c>ProcessStartInfo.ArgumentList</c>, which quotes each one, so no value can split into
/// extra arguments and nothing passes through a shell.
/// </summary>
public sealed class ProcessCommand
{
    public AllowedTool Tool { get; }
    public IReadOnlyList<string> Arguments { get; }
    public TimeSpan Timeout { get; }

    public ProcessCommand(AllowedTool tool, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var list = arguments?.ToList() ?? throw new ArgumentNullException(nameof(arguments));
        foreach (var argument in list)
        {
            if (argument is null) throw new ArgumentException("An argument is null.", nameof(arguments));
            if (argument.IndexOf('\0') >= 0) throw new ArgumentException("An argument contains a NUL character.", nameof(arguments));
        }
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        Tool = tool;
        Arguments = list.AsReadOnly();
        Timeout = timeout;
    }

    /// <summary>For logs only. It is never parsed or executed.</summary>
    public string Display
    {
        get
        {
            var sb = new StringBuilder(Tool.ToString().ToLowerInvariant());
            foreach (var argument in Arguments)
            {
                sb.Append(' ');
                sb.Append(argument.Length == 0 || argument.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + argument.Replace("\"", "\\\"") + "\"" : argument);
            }
            return sb.ToString();
        }
    }

    public override string ToString() => Display;
}

public enum ProcessRunOutcome
{
    /// <summary>The program ran and exited; see the exit code.</summary>
    Exited,
    TimedOut,
    Cancelled,
    /// <summary>The tool is not on the allow-list or not on this PC.</summary>
    NotAllowed,
    FailedToStart
}

public sealed record ProcessRunResult(ProcessRunOutcome Outcome, int? ExitCode = null, string? Error = null)
{
    public bool ExitedWithAny(params int[] codes) => Outcome == ProcessRunOutcome.Exited && ExitCode is int code && codes.Contains(code);
}

public readonly record struct ProcessOutputLine(string Text, bool IsError);
