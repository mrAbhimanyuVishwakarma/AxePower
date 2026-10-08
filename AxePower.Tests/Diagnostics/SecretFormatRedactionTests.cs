using System;
using AxePower.Core.Services;
using Xunit;

namespace AxePower.Tests.Diagnostics;

/// <summary>
/// Known secret formats, one per case, each embedded in the kind of text that ends up in a log
/// (an exception message, a header dump, a URL). The secret part must be gone afterwards.
/// </summary>
public class SecretFormatRedactionTests
{
    [Theory]
    // URLs: LAN Drop token, room link secret, OAuth code and implicit token, signed query
    [InlineData("GET http://192.168.1.23:5050/?t=Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MEFCQ0RFRkdISUpLTE0 failed", "Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MEFCQ0RFRkdISUpLTE0")]
    [InlineData("Joining https://axepower.example/r/abc#v=0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef")]
    [InlineData("Callback http://127.0.0.1:53682/callback?code=4/0AY0e-g7secretcode&state=xyz", "4/0AY0e-g7secretcode")]
    [InlineData("Redirect https://app.example/#access_token=ya29.a0AfH6SMBsecret&expires_in=3599", "a0AfH6SMBsecret")]
    [InlineData("request line: POST /api/text?t=hunter2hunter2 HTTP/1.1", "hunter2hunter2")]
    // Credentials inside a URL
    [InlineData("Proxy http://alice:S3cretPassw0rd@proxy.local:8080 refused", "S3cretPassw0rd")]
    [InlineData("git remote https://bob:ghp_notarealtokenbutlooksreal1234@github.com/x/y.git", "ghp_notarealtokenbutlooksreal1234")]
    // Headers
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.c2lnbmF0dXJlc2ln", "eyJzdWIiOiIxMjM0In0")]
    [InlineData("Authorization: Basic YWxhZGRpbjpvcGVuc2VzYW1l", "YWxhZGRpbjpvcGVuc2VzYW1l")]
    [InlineData("Cookie: axe_session=abc123def456; theme=dark; sid=s3ss10n", "abc123def456")]
    [InlineData("Cookie: theme=dark; sid=s3ss10nCookieValue", "s3ss10nCookieValue")]
    [InlineData("Set-Cookie: axe_session=Zm9vYmFyQmF6UXV4MTIzNDU2; HttpOnly; SameSite=Strict", "Zm9vYmFyQmF6UXV4MTIzNDU2")]
    [InlineData("X-AxePower-Token: token=short1", "short1")]
    // JSON bodies and key=value pairs
    [InlineData("{\"access_token\":\"acc-token-value-1\",\"refresh_token\":\"ref-token-value-2\"}", "acc-token-value-1")]
    [InlineData("{\"access_token\":\"acc-token-value-1\",\"refresh_token\":\"ref-token-value-2\"}", "ref-token-value-2")]
    [InlineData("password=correct-horse-battery", "correct-horse-battery")]
    [InlineData("client_secret: 9f86d081884c7d659a2feaa0c55ad015", "9f86d081884c7d659a2feaa0c55ad015")]
    // Recognisable key formats
    [InlineData("token ghp_1234567890abcdefghijABCDEFGHIJ0123 rejected", "ghp_1234567890abcdefghijABCDEFGHIJ0123")]
    [InlineData("using github_pat_11ABCDEFG0123456789_abcdefghijklmnop", "github_pat_11ABCDEFG0123456789_abcdefghijklmnop")]
    [InlineData("key sb_secret_abcdefghijklmnopqrstuv was refused", "sb_secret_abcdefghijklmnopqrstuv")]
    [InlineData("apikey sb_publishable_ABCdefGHIjklMNO", "sb_publishable_ABCdefGHIjklMNO")]
    [InlineData("OPENAI_KEY sk-proj-abcdefghijklmnopqrstuvwx", "sk-proj-abcdefghijklmnopqrstuvwx")]
    [InlineData("anthropic sk-ant-api03-abcdefghijklmnop", "sk-ant-api03-abcdefghijklmnop")]
    [InlineData("stripe sk_live_abcdefghijklmnop", "sk_live_abcdefghijklmnop")]
    [InlineData("slack xoxb-123456789012-abcdefghij", "xoxb-123456789012-abcdefghij")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE used", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("google AIzaSyA-abcdefghijklmnopqrstuvwxyz12345", "AIzaSyA-abcdefghijklmnopqrstuvwxyz12345")]
    [InlineData("oauth ya29.a0ARrdaM-abcdefghijklmnop", "ya29.a0ARrdaM-abcdefghijklmnop")]
    [InlineData("discord MTA1234567890123456789012.GaBcDe.abcdefghijklmnopqrstuvwxyz12345", "MTA1234567890123456789012.GaBcDe.abcdefghijklmnopqrstuvwxyz12345")]
    [InlineData("JWT eyJhbGciOiJFUzI1NiJ9.eyJyb2xlIjoiYW5vbiJ9.MEUCIQDsignature", "eyJyb2xlIjoiYW5vbiJ9")]
    // Opaque tokens: base64url session tokens and hex room ids
    [InlineData("session Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MEFCQ0RFRkdI expired", "Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MEFCQ0RFRkdI")]
    // Email addresses
    [InlineData("Signed in as me.myself+tag@example.co.uk", "me.myself+tag@example.co.uk")]
    public void KnownSecretFormats_AreRedacted(string text, string secret)
    {
        var redacted = LogRedactor.Redact(text);

        Assert.DoesNotContain(secret, redacted);
    }

    [Fact]
    public void APrivateKeyBlock_IsRemovedWhole()
    {
        var text = "loaded:\n-----BEGIN EC PRIVATE KEY-----\nMHcCAQEEIBVsZWNyZXRrZXltYXRlcmlhbA==\nAwEHoUQDQgAE\n-----END EC PRIVATE KEY-----\ndone";

        var redacted = LogRedactor.Redact(text);

        Assert.DoesNotContain("MHcCAQEEIBVsZWNyZXRrZXltYXRlcmlhbA", redacted);
        Assert.DoesNotContain("AwEHoUQDQgAE", redacted);
        Assert.StartsWith("loaded:", redacted);
        Assert.EndsWith("done", redacted);
    }

    // -- The user and the computer ---------------------------------------------

    private const string Profile = @"C:\Users\priyanka.s";
    private static string Redact(string text) => LogRedactor.RedactFor(text, Profile, "priyanka.s", "PRIYA-LAPTOP");

    [Theory]
    [InlineData(@"Could not read C:\Users\priyanka.s\Music\Lofi\track.mp3", @"%USERPROFILE%\Music\Lofi\track.mp3")]
    [InlineData(@"Path c:\users\PRIYANKA.S\AppData\Local\AxePower", @"%USERPROFILE%\AppData\Local\AxePower")]
    [InlineData("Saved to C:/Users/priyanka.s/Downloads/x.zip", "%USERPROFILE%/Downloads/x.zip")]
    [InlineData("Running as priyanka.s on PRIYA-LAPTOP", "Running as [user] on [computer]")]
    [InlineData(@"Shared folder \\PRIYA-LAPTOP\share", @"\\[computer]\share")]
    public void TheUserAndComputer_AreReplaced(string text, string expectedTail)
    {
        var redacted = Redact(text);

        Assert.EndsWith(expectedTail, redacted);
        Assert.DoesNotContain("priyanka", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIYA-LAPTOP", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnotherUsersProfile_IsHiddenToo_ButSharedFoldersAreKept()
    {
        Assert.Equal(@"Found C:\Users\[user]\Desktop\app.lnk", Redact(@"Found C:\Users\rahul\Desktop\app.lnk"));
        Assert.Equal(@"Found C:\Users\Public\Desktop\app.lnk", Redact(@"Found C:\Users\Public\Desktop\app.lnk"));
    }

    [Fact]
    public void ALongerNameThatStartsWithTheUsersName_IsNotTheUser()
    {
        Assert.Equal("met priyanka.sharma today", Redact("met priyanka.sharma today"));
        Assert.Equal("follow @priyanka.s_music", Redact("follow @priyanka.s_music"));
    }

    [Fact]
    public void ACommonAccountName_IsReplacedOnlyInsidePaths()
    {
        var redacted = LogRedactor.RedactFor(@"The user clicked Save in C:\Users\user\Documents", @"C:\Users\user", "user", "DESKTOP-ABC1234");

        Assert.Equal(@"The user clicked Save in %USERPROFILE%\Documents", redacted);
    }

    // -- What must survive -------------------------------------------------------

    [Theory]
    [InlineData("MSI {0E3BB569-69D6-4C34-BFF9-C2F81DB5E5F0} returned 1603")]
    [InlineData("Local HTTP server started on http://192.168.1.23:5050 (Private network)")]
    [InlineData("SystemTelemetryServiceFailure after 3 attempts")]
    [InlineData(@"Deleted C:\Program Files\App\cache")]
    public void UsefulDetails_AreKept(string text)
    {
        Assert.Equal(text, Redact(text));
    }
}
