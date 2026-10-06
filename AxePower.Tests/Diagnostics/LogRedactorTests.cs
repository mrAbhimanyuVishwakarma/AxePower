using System;
using System.Collections.Generic;
using AxePower.Core.Models;
using AxePower.Core.Services;
using Xunit;

namespace AxePower.Tests.Diagnostics;

/// <summary>
/// Secrets never reach the log: LogService redacts every message and exception, so a failure that
/// echoes a link, a header or a response cannot leak a token, a room secret, a code or an email.
/// </summary>
public class LogRedactorTests
{
    [Theory]
    // Share Screen room link: the viewer secret travels in the fragment.
    [InlineData("Joining http://192.168.1.5:8890/axe0011#v=0123456789abcdef0123456789abcdef now", "0123456789abcdef0123456789abcdef")]
    // LAN Drop share link: the access token travels in the query.
    [InlineData("Open http://192.168.1.5:5050/?t=Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MEFCQ0RFRkdISUpL", "Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MEFCQ0RFRkdISUpL")]
    // WHEP admission proof and nonce.
    [InlineData("POST https://host.tail1234.ts.net:8889/axe0011/whep?n=abcdef0123456789&p=9f86d081884c7d659a2feaa0c55ad015", "9f86d081884c7d659a2feaa0c55ad015")]
    // A request line without a scheme.
    [InlineData("GET /api/text?t=SECRETVALUE42abc HTTP/1.1", "SECRETVALUE42abc")]
    // OAuth loopback code.
    [InlineData("callback http://127.0.0.1:53682/callback?code=4/0AeanS0abc123", "4/0AeanS0abc123")]
    [InlineData("Authorization: Bearer abcDEF123.ghi-JKL_456", "abcDEF123.ghi-JKL_456")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.c2lnbmF0dXJl expired", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("{\"refresh_token\":\"v1.MjAyNi0xMC0wNVQxMjozNDo1NlpfYWJjZGVm\"}", "v1.MjAyNi0xMC0wNVQxMjozNDo1NlpfYWJjZGVm")]
    [InlineData("password=hunter2 rejected", "hunter2")]
    [InlineData("Set-Cookie: axe_session=a1b2c3d4e5f6; HttpOnly", "a1b2c3d4e5f6")]
    [InlineData("Signed in as someone.name+tag@example.co.uk", "someone.name+tag@example.co.uk")]
    [InlineData("room token 3f9a1c7e5b2d4f608a1c3e5f7b9d1e2f in use", "3f9a1c7e5b2d4f608a1c3e5f7b9d1e2f")]
    public void Secrets_AreRemoved(string message, string secret)
    {
        var redacted = LogRedactor.Redact(message);
        Assert.DoesNotContain(secret, redacted);
        Assert.NotEqual(message, redacted);
    }

    [Theory]
    [InlineData("[Uninstaller] Product {23170F69-40C1-2702-2201-000001000000} removed")]
    [InlineData("Could not remove C:\\Program Files\\Some App\\bin\\tool.exe")]
    [InlineData("winget 1.29.380 at C:\\Program Files\\WindowsApps\\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\\winget.exe")]
    [InlineData("[LanDrop] Local HTTP server started on http://192.168.1.5:5050 (Private network)")]
    [InlineData("System.IO.IOException: The process cannot access the file because it is being used by another process.")]
    [InlineData("SystemTelemetryServiceFailureWithAVeryLongName happened")]
    public void UsefulDetail_IsKept(string message) => Assert.Equal(message, LogRedactor.Redact(message));

    [Fact]
    public void Describe_GivesTypeAndRedactedMessage_WithoutAStack()
    {
        var ex = new InvalidOperationException("Request to https://api.example.com/x?access_token=abc123def456ghi failed");
        var text = LogRedactor.Describe(ex);
        Assert.StartsWith("InvalidOperationException: ", text);
        Assert.DoesNotContain("abc123def456ghi", text);
        Assert.DoesNotContain(" at ", text);
    }

    [Fact]
    public void LogService_RedactsEveryLevel_BeforeAnyoneSeesTheEntry()
    {
        var log = new LogService();
        var seen = new List<LogEntry>();
        log.OnLogAdded += (_, e) => seen.Add(e);
        const string link = "http://192.168.1.5:8890/axe0011#v=0123456789abcdef0123456789abcdef";

        log.LogInfo($"info {link}");
        log.LogWarning($"warn {link}");
        log.LogWarning("warn with exception", new InvalidOperationException($"failed {link}"));
        log.LogError("error", new InvalidOperationException($"failed {link}"));

        Assert.Equal(4, seen.Count);
        Assert.All(seen, e => Assert.DoesNotContain("0123456789abcdef0123456789abcdef", e.Message));
        Assert.Contains("InvalidOperationException", seen[2].Message);
    }

    [Fact]
    public void AHugeMessage_IsWithheldRatherThanLoggedUnredacted()
    {
        var huge = new string('a', 2_000_000) + "?t=" + new string('b', 2_000_000);
        var redacted = LogRedactor.Redact(huge);
        Assert.True(redacted.Length < huge.Length || !redacted.Contains("?t=b"));
    }
}
