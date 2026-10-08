using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.Outcomes;

/// <summary>How an action ended (Prompt 30). Cancelled is its own result, never an error.</summary>
public enum OutcomeKind
{
    Succeeded,
    PartiallySucceeded,
    Cancelled,
    Failed,
}

/// <summary>Why something failed, in terms a user can act on.</summary>
public enum FailureKind
{
    None,
    /// <summary>No internet connection (or the host could not be found).</summary>
    Offline,
    /// <summary>The account session expired or was rejected: sign in again.</summary>
    SessionExpired,
    /// <summary>A third-party service (GitHub, YouTube, Supabase, a lyrics site) is down or refused.</summary>
    ServiceUnavailable,
    /// <summary>This PC lacks the hardware, driver or Windows feature the action needs.</summary>
    HardwareUnavailable,
    /// <summary>The administrator prompt was declined, or the action needs AxePower to run as administrator.</summary>
    ElevationDenied,
    /// <summary>Windows, antivirus or another program blocked the file (in use, read-only, access denied).</summary>
    Blocked,
    /// <summary>WinGet or the Microsoft Store reported a failure.</summary>
    PackageManagerFailed,
    /// <summary>A file, app or setting it needed is not there.</summary>
    NotFound,
    /// <summary>It took too long; the other side did not answer.</summary>
    TimedOut,
    /// <summary>Anything else: details are in the diagnostics log.</summary>
    Unexpected,
}

/// <summary>One item of a batch (an app to install, a file to delete) and how it ended.</summary>
public sealed record OutcomeItem(string Name, OutcomeKind Kind, FailureKind Failure = FailureKind.None);

/// <summary>
/// The result of a user action: what happened, why it failed if it did, and for a batch exactly
/// which items completed. <see cref="TechnicalDetail"/> is already redacted.
/// </summary>
public sealed record Outcome(string Action, OutcomeKind Kind, FailureKind Failure, IReadOnlyList<OutcomeItem> Items, string? TechnicalDetail)
{
    public static Outcome Success(string action) => new(action, OutcomeKind.Succeeded, FailureKind.None, Array.Empty<OutcomeItem>(), null);

    public static Outcome Cancelled(string action) => new(action, OutcomeKind.Cancelled, FailureKind.None, Array.Empty<OutcomeItem>(), null);

    public static Outcome Failed(string action, FailureKind failure, string? technicalDetail = null) =>
        new(action, OutcomeKind.Failed, failure, Array.Empty<OutcomeItem>(), technicalDetail is null ? null : LogRedactor.Redact(technicalDetail));

    /// <summary>The outcome of <paramref name="action"/> ending with <paramref name="error"/>.</summary>
    public static Outcome From(string action, Exception error, CancellationToken token = default)
    {
        var failure = OutcomeClassifier.Classify(error, token);
        return failure is null
            ? Cancelled(action)
            : new(action, OutcomeKind.Failed, failure.Value, Array.Empty<OutcomeItem>(), OutcomeClassifier.Detail(error));
    }

    /// <summary>
    /// A batch: succeeded if every item did, failed if none did, partial otherwise. Cancelled items
    /// count as not done, not as failures.
    /// </summary>
    public static Outcome Batch(string action, IReadOnlyList<OutcomeItem> items, string? technicalDetail = null)
    {
        int done = items.Count(i => i.Kind == OutcomeKind.Succeeded);
        var kind = items.Count == 0 || done == items.Count ? OutcomeKind.Succeeded
            : done == 0 ? (items.All(i => i.Kind == OutcomeKind.Cancelled) ? OutcomeKind.Cancelled : OutcomeKind.Failed)
            : OutcomeKind.PartiallySucceeded;
        var failure = items.FirstOrDefault(i => i.Kind == OutcomeKind.Failed)?.Failure ?? FailureKind.None;
        return new(action, kind, failure, items, technicalDetail is null ? null : LogRedactor.Redact(technicalDetail));
    }
}

/// <summary>Maps exceptions and exit codes to a <see cref="FailureKind"/>.</summary>
public static class OutcomeClassifier
{
    private const int ErrorCancelled = 1223;          // ERROR_CANCELLED: the UAC prompt was declined
    private const int ErrorElevationRequired = 740;   // ERROR_ELEVATION_REQUIRED
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorNotSupported = 50;
    private const int ErrorVirusInfected = 225;       // blocked by antivirus
    private const int ErrorVirusDeleted = 226;

    /// <summary>
    /// Why <paramref name="error"/> happened, or null when it is a cancellation (the user's, or
    /// <paramref name="token"/>'s). A timeout that surfaces as a cancellation is TimedOut.
    /// </summary>
    public static FailureKind? Classify(Exception error, CancellationToken token = default)
    {
        error = Unwrap(error);
        switch (error)
        {
            case OperationCanceledException when token.IsCancellationRequested:
                return null;
            case TaskCanceledException { InnerException: TimeoutException }:
            case TimeoutException:
                return FailureKind.TimedOut;
            case OperationCanceledException:
                // HttpClient reports its own timeout as a cancellation nobody asked for
                return token == default ? null : FailureKind.TimedOut;
            case HttpRequestException http:
                return ClassifyHttp(http);
            case WebException web:
                return web.Status is WebExceptionStatus.NameResolutionFailure or WebExceptionStatus.ConnectFailure
                    ? FailureKind.Offline
                    : web.Status == WebExceptionStatus.Timeout ? FailureKind.TimedOut : FailureKind.ServiceUnavailable;
            case SocketException socket:
                return socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NetworkUnreachable or SocketError.HostUnreachable
                        or SocketError.NetworkDown or SocketError.TryAgain or SocketError.NoData
                    ? FailureKind.Offline
                    : socket.SocketErrorCode == SocketError.TimedOut ? FailureKind.TimedOut : FailureKind.ServiceUnavailable;
            case PingException:
                return FailureKind.Offline;
            case Win32Exception win32:
                return win32.NativeErrorCode switch
                {
                    ErrorCancelled or ErrorElevationRequired => FailureKind.ElevationDenied,
                    ErrorAccessDenied => FailureKind.Blocked,
                    ErrorFileNotFound or ErrorPathNotFound => FailureKind.NotFound,
                    ErrorVirusInfected or ErrorVirusDeleted => FailureKind.Blocked,
                    ErrorNotSupported => FailureKind.HardwareUnavailable,
                    _ => FailureKind.Unexpected,
                };
            case UnauthorizedAccessException or SecurityException:
                return FailureKind.Blocked;
            case FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException:
                return FailureKind.NotFound;
            case IOException io when (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation or ErrorAccessDenied:
                return FailureKind.Blocked;
            case ManagementException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException:
                return FailureKind.HardwareUnavailable;
            case COMException com when (com.HResult & 0xFFFF) == ErrorCancelled:
                return FailureKind.ElevationDenied;
            case COMException com when (com.HResult & 0xFFFF) == ErrorAccessDenied:
                return FailureKind.Blocked;
            case Accounts.AccountException account:
                return account.Error switch
                {
                    Accounts.AccountError.Offline => FailureKind.Offline,
                    Accounts.AccountError.ServiceUnavailable or Accounts.AccountError.RateLimited => FailureKind.ServiceUnavailable,
                    Accounts.AccountError.SessionExpired or Accounts.AccountError.NotSignedIn => FailureKind.SessionExpired,
                    _ => FailureKind.Unexpected,
                };
            default:
                return FailureKind.Unexpected;
        }
    }

    private static FailureKind ClassifyHttp(HttpRequestException http)
    {
        if (http.StatusCode is HttpStatusCode.Unauthorized) return FailureKind.SessionExpired;
        if (http.StatusCode is HttpStatusCode.NotFound) return FailureKind.NotFound;
        if (http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout) return FailureKind.TimedOut;
        if (http.StatusCode is not null) return FailureKind.ServiceUnavailable;
        if (http.InnerException is SocketException socket) return Classify(socket) ?? FailureKind.Offline;
        // No status and no socket detail: the request never reached a server
        return NetworkInterface.GetIsNetworkAvailable() ? FailureKind.ServiceUnavailable : FailureKind.Offline;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is AggregateException { InnerExceptions.Count: 1 } aggregate) error = aggregate.InnerExceptions[0];
        if (error is System.Reflection.TargetInvocationException { InnerException: { } inner }) return Unwrap(inner);
        return error;
    }

    /// <summary>
    /// Technical detail for the Details link and support: exception type, Windows error or HTTP
    /// status, and the message, redacted (no tokens, links, emails, user or computer name).
    /// No stack trace: that stays in the diagnostics log.
    /// </summary>
    public static string Detail(Exception error)
    {
        error = Unwrap(error);
        string code = error switch
        {
            Win32Exception w => $" (Windows error {w.NativeErrorCode})",
            HttpRequestException { StatusCode: { } s } => $" (HTTP {(int)s})",
            ExternalException e => $" (0x{e.HResult:X8})",
            IOException io => $" (0x{io.HResult:X8})",
            _ => "",
        };
        return LogRedactor.Redact($"{error.GetType().Name}{code}: {error.Message}");
    }

    /// <summary>
    /// WinGet's exit codes (HRESULTs in the 0x8A15xxxx range, as signed integers) that have a plain
    /// meaning; the rest are a generic package-manager failure.
    /// </summary>
    public static (FailureKind Kind, string? Reason) ClassifyWinGet(int exitCode) => unchecked((uint)exitCode) switch
    {
        0 => (FailureKind.None, null),
        0x8A150014 => (FailureKind.NotFound, "WinGet could not find that package"),
        0x8A15002B => (FailureKind.None, "it is already up to date"),
        0x8A150061 => (FailureKind.None, "it is already installed"),
        0x8A150010 or 0x8A15000F => (FailureKind.ServiceUnavailable, "WinGet could not reach its package source"),
        0x8A150011 => (FailureKind.Blocked, "the installer's checksum did not match, so WinGet refused it"),
        0x8A150101 or 0x8A150102 => (FailureKind.Blocked, "the app is running or a file is in use: close it and try again"),
        0x8A150103 or 0x8A150104 => (FailureKind.Blocked, "a reboot is needed before it can be installed"),
        0x8A150105 => (FailureKind.Blocked, "there is not enough disk space"),
        0x8A15010B or 0x8A150056 => (FailureKind.ElevationDenied, "it needs administrator rights, and the prompt was declined"),
        0x8A150107 => (FailureKind.Blocked, "your organisation's policy blocks it"),
        0x8A150109 or 0x8A15010A => (FailureKind.Offline, "the download failed: check your internet connection"),
        0x8A15002E => (FailureKind.Unexpected, "the installer failed"),
        0x800704C7 => (FailureKind.None, "cancelled"),
        _ => (FailureKind.PackageManagerFailed, null),
    };
}

/// <summary>A message for the user: one plain line, and optional redacted details.</summary>
public sealed record UserMessage(string Text, string? Details, bool IsFailure);

/// <summary>Turns an <see cref="Outcome"/> into concise, actionable text (Prompt 30).</summary>
public static class UserMessages
{
    /// <summary>What to do about each kind of failure, said once.</summary>
    public static string Advice(FailureKind failure) => failure switch
    {
        FailureKind.Offline => "You appear to be offline. Check your internet connection and try again.",
        FailureKind.SessionExpired => "Your sign-in has expired. Sign in again from Profile.",
        FailureKind.ServiceUnavailable => "The online service did not respond properly. It may be down for a while; try again later.",
        FailureKind.HardwareUnavailable => "This PC does not support it, or the driver it needs is missing.",
        FailureKind.ElevationDenied => "It needs administrator rights. Accept the Windows prompt, or turn on Run as administrator in Settings.",
        FailureKind.Blocked => "Windows or another program blocked it: the file may be in use, read-only, or held by antivirus. Close other programs and try again.",
        FailureKind.PackageManagerFailed => "WinGet reported a problem. Open Software Hub's log for details, or try again later.",
        FailureKind.NotFound => "Something it needs is missing. It may have been moved or removed.",
        FailureKind.TimedOut => "It took too long to answer. Try again in a moment.",
        _ => "Something unexpected went wrong. Details are in Settings › Diagnostics.",
    };

    public static UserMessage For(Outcome outcome) => outcome.Kind switch
    {
        OutcomeKind.Succeeded => new($"{outcome.Action}: done.", null, false),
        OutcomeKind.Cancelled => new($"{outcome.Action}: cancelled. Nothing was changed.", null, false),
        OutcomeKind.Failed when outcome.Items.Count == 0 => new($"Could not {Lower(outcome.Action)}. {Advice(outcome.Failure)}", outcome.TechnicalDetail, true),
        _ => ForBatch(outcome),
    };

    /// <summary>The usual case: <paramref name="action"/> (e.g. "Open the battery report") failed with <paramref name="error"/>.</summary>
    public static UserMessage Failed(string action, Exception error, CancellationToken token = default) => For(Outcome.From(action, error, token));

    private static UserMessage ForBatch(Outcome outcome)
    {
        var done = outcome.Items.Where(i => i.Kind == OutcomeKind.Succeeded).Select(i => i.Name).ToList();
        var failed = outcome.Items.Where(i => i.Kind == OutcomeKind.Failed).ToList();
        var skipped = outcome.Items.Where(i => i.Kind == OutcomeKind.Cancelled).Select(i => i.Name).ToList();
        var parts = new List<string>
        {
            $"{outcome.Action}: {done.Count} of {outcome.Items.Count} done" + (done.Count > 0 ? $" ({string.Join(", ", done)})." : "."),
        };
        if (failed.Count > 0)
            parts.Add("Not done: " + string.Join(", ", failed.Select(f => $"{f.Name} ({Short(f.Failure)})")) + ".");
        if (skipped.Count > 0)
            parts.Add("Cancelled before it started: " + string.Join(", ", skipped) + ".");
        if (failed.Count > 0) parts.Add(Advice(failed[0].Failure));
        return new(string.Join(" ", parts), outcome.TechnicalDetail, failed.Count > 0);
    }

    /// <summary>A few words for one item of a batch.</summary>
    public static string Short(FailureKind failure) => failure switch
    {
        FailureKind.Offline => "offline",
        FailureKind.SessionExpired => "signed out",
        FailureKind.ServiceUnavailable => "service unavailable",
        FailureKind.HardwareUnavailable => "not supported here",
        FailureKind.ElevationDenied => "needs administrator",
        FailureKind.Blocked => "blocked",
        FailureKind.PackageManagerFailed => "WinGet error",
        FailureKind.NotFound => "not found",
        FailureKind.TimedOut => "timed out",
        _ => "failed",
    };

    private static string Lower(string action) => action.Length == 0 ? action : char.ToLowerInvariant(action[0]) + action[1..];
}
