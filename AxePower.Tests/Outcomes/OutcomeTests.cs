using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Outcomes;
using AxePower.Core.Services.SoftwareHub;
using Xunit;

namespace AxePower.Tests.Outcomes;

/// <summary>
/// Error, offline, cancellation and partial-success outcomes (Prompt 30): every expected failure
/// gets an actionable message, a cancel is never an error, a batch names exactly what did and did
/// not complete, and no raw exception text, stack trace or secret reaches the user.
/// </summary>
public class OutcomeTests
{
    public static TheoryData<Exception, FailureKind> Failures => new()
    {
        { new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)), FailureKind.Offline },
        { new SocketException((int)SocketError.NetworkUnreachable), FailureKind.Offline },
        { new HttpRequestException("x", null, HttpStatusCode.Unauthorized), FailureKind.SessionExpired },
        { new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable), FailureKind.ServiceUnavailable },
        { new HttpRequestException("x", null, HttpStatusCode.TooManyRequests), FailureKind.ServiceUnavailable },
        { new HttpRequestException("x", null, HttpStatusCode.NotFound), FailureKind.NotFound },
        { new TaskCanceledException("x", new TimeoutException()), FailureKind.TimedOut },
        { new Win32Exception(1223), FailureKind.ElevationDenied },
        { new Win32Exception(740), FailureKind.ElevationDenied },
        { new Win32Exception(225), FailureKind.Blocked },
        { new UnauthorizedAccessException("x"), FailureKind.Blocked },
        { new IOException("in use", unchecked((int)0x80070020)), FailureKind.Blocked },
        { new FileNotFoundException("x"), FailureKind.NotFound },
        { new PlatformNotSupportedException(), FailureKind.HardwareUnavailable },
        { new DllNotFoundException(), FailureKind.HardwareUnavailable },
        { new AccountException(AccountError.SessionExpired, "Sign in again."), FailureKind.SessionExpired },
        { new AccountException(AccountError.Offline, "No connection."), FailureKind.Offline },
        { new AggregateException(new UnauthorizedAccessException("x")), FailureKind.Blocked },
        { new InvalidOperationException("x"), FailureKind.Unexpected },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Exceptions_MapToActionableKinds(Exception error, FailureKind expected) =>
        Assert.Equal(expected, OutcomeClassifier.Classify(error));

    [Fact]
    public void ACancelTheUserAskedFor_IsCancelled_NotAnError()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var outcome = Outcome.From("Install apps", new OperationCanceledException(cts.Token), cts.Token);
        var message = UserMessages.For(outcome);

        Assert.Equal(OutcomeKind.Cancelled, outcome.Kind);
        Assert.False(message.IsFailure);
        Assert.Contains("cancelled", message.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not", message.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(FailureKind.Offline, "internet connection")]
    [InlineData(FailureKind.SessionExpired, "Sign in again")]
    [InlineData(FailureKind.ElevationDenied, "administrator")]
    [InlineData(FailureKind.Blocked, "Close other programs")]
    [InlineData(FailureKind.ServiceUnavailable, "try again later")]
    [InlineData(FailureKind.HardwareUnavailable, "does not support")]
    [InlineData(FailureKind.PackageManagerFailed, "WinGet")]
    [InlineData(FailureKind.Unexpected, "Diagnostics")]
    public void EveryKind_SaysWhatToDo(FailureKind kind, string advice)
    {
        Assert.Contains(advice, UserMessages.Advice(kind), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheMessage_NeverCarriesTheRawExceptionText_AndTheDetailsAreRedacted()
    {
        var error = new HttpRequestException(
            "GET https://landrop.local/?token=SECRETTOKEN1234567890 failed for someone@example.com at   at Foo.Bar() in C:\\src\\x.cs:line 3",
            null, HttpStatusCode.BadGateway);
        var message = UserMessages.Failed("Check for updates", error);

        Assert.True(message.IsFailure);
        Assert.StartsWith("Could not check for updates.", message.Text);
        Assert.DoesNotContain("SECRETTOKEN", message.Text);
        Assert.DoesNotContain("example.com", message.Text);
        Assert.DoesNotContain("0x", message.Text);
        Assert.NotNull(message.Details);
        Assert.Contains("HTTP 502", message.Details);
        Assert.DoesNotContain("SECRETTOKEN1234567890", message.Details);
        Assert.DoesNotContain("someone@example.com", message.Details);
    }

    [Fact]
    public void APartialBatch_NamesExactlyWhatWasAndWasNotDone()
    {
        var outcome = Outcome.Batch("Install apps", new[]
        {
            new OutcomeItem("7-Zip", OutcomeKind.Succeeded),
            new OutcomeItem("VLC", OutcomeKind.Succeeded),
            new OutcomeItem("Steam", OutcomeKind.Failed, FailureKind.Offline),
            new OutcomeItem("Discord", OutcomeKind.Cancelled),
        });
        var text = UserMessages.For(outcome).Text;

        Assert.Equal(OutcomeKind.PartiallySucceeded, outcome.Kind);
        Assert.Contains("2 of 4 done (7-Zip, VLC)", text);
        Assert.Contains("Not done: Steam (offline)", text);
        Assert.Contains("Cancelled before it started: Discord", text);
        Assert.Contains("internet connection", text);
    }

    [Fact]
    public void ABatchStoppedByCancelAlone_IsCancelled_NotFailed()
    {
        var outcome = Outcome.Batch("Install apps", new[] { new OutcomeItem("VLC", OutcomeKind.Cancelled) });
        Assert.Equal(OutcomeKind.Cancelled, outcome.Kind);
        Assert.False(UserMessages.For(outcome).IsFailure);
    }

    [Fact]
    public void PackageBatch_BecomesAnOutcome_WithReasons()
    {
        var batch = new PackageBatchResult(new[]
        {
            new PackageBatchItem("a", "7-Zip", new PackageOperationResult(PackageOutcome.Succeeded)),
            new PackageBatchItem("b", "Steam", new PackageOperationResult(PackageOutcome.Failed, Failure: FailureKind.Blocked)),
            new PackageBatchItem("c", "VLC", new PackageOperationResult(PackageOutcome.Failed)),
            new PackageBatchItem("d", "Discord", new PackageOperationResult(PackageOutcome.Skipped)),
        });
        var text = UserMessages.For(batch.ToOutcome("Install apps")).Text;

        Assert.Contains("1 of 4 done (7-Zip)", text);
        Assert.Contains("Steam (blocked)", text);
        Assert.Contains("VLC (WinGet error)", text);
        Assert.Contains("Cancelled before it started: Discord", text);
    }

    [Theory]
    [InlineData(0, FailureKind.None, null)]
    [InlineData(unchecked((int)0x8A150014), FailureKind.NotFound, "could not find")]
    [InlineData(unchecked((int)0x8A15002B), FailureKind.None, "up to date")]
    [InlineData(unchecked((int)0x8A150061), FailureKind.None, "already installed")]
    [InlineData(unchecked((int)0x8A150011), FailureKind.Blocked, "checksum")]
    [InlineData(unchecked((int)0x8A150101), FailureKind.Blocked, "in use")]
    [InlineData(unchecked((int)0x8A150109), FailureKind.Offline, "internet")]
    [InlineData(unchecked((int)0x8A15010B), FailureKind.ElevationDenied, "administrator")]
    [InlineData(12345, FailureKind.PackageManagerFailed, null)]
    public void WinGetExitCodes_HavePlainMeanings(int code, FailureKind kind, string? reason)
    {
        var (actual, text) = OutcomeClassifier.ClassifyWinGet(code);
        Assert.Equal(kind, actual);
        if (reason is null) Assert.Null(text);
        else Assert.Contains(reason, text!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Failures_AreShown_EvenWithNotificationsTurnedOff_ButNewsIsNot()
    {
        var folder = Path.Combine(Path.GetTempPath(), "axepower-outcome-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(new LogService(), folder);
            var current = settings.GetSettings();
            current.ShowNotifications = false;
            settings.SaveSettings(current);
            var notifications = new NotificationService(settings);
            var shown = new System.Collections.Generic.List<UserMessage>();
            notifications.OnShowMessage += (_, m) => shown.Add(m);

            notifications.Show(new UserMessage("Mode switched.", null, false));
            notifications.ShowFailure("Open the battery report", new FileNotFoundException("x"));

            var only = Assert.Single(shown);
            Assert.True(only.IsFailure);
            Assert.StartsWith("Could not open the battery report.", only.Text);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    /// <summary>
    /// No view, view model or window puts an exception's raw Message or StackTrace in front of the
    /// user; they go through <see cref="UserMessages"/>, and raw text only to the log.
    /// </summary>
    [Fact]
    public void TheApp_ShowsNoRawExceptionText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        var app = Path.Combine(dir!.FullName, "AxePower.App");
        var raw = new Regex(@"\b(ex|e|err|error|exception|caught)\.(Message|StackTrace)\b");
        var logging = new Regex(@"\bLog(Error|Warning|Info|Debug)?\(|_log\??\.|[Ll]ogService\??\.|Trace\.|Debug\.Write|AppShutdown");

        var offenders = Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, i)))
            .Where(x => raw.IsMatch(x.line) && !logging.IsMatch(x.line) && !x.line.TrimStart().StartsWith("//")
                        // Shutdown records errors for the log only
                        && Path.GetFileName(x.f) != "AppShutdown.cs"
                        // AccountException messages are written for users (AccountService)
                        && !IsAccountCatch(x.f, x.i))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Raw exception text shown to the user:\n" + string.Join("\n", offenders));
    }

    /// <summary>True when the line sits in a catch of AccountException, whose messages are user-facing.</summary>
    private static bool IsAccountCatch(string file, int lineIndex)
    {
        var lines = File.ReadAllLines(file);
        for (int i = lineIndex; i >= Math.Max(0, lineIndex - 4); i--)
            if (lines[i].Contains("catch (")) return lines[i].Contains("AccountException");
        return false;
    }
}
