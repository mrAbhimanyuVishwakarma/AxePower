using System;
using System.Text;
using System.Threading;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.LanDrop;
using Xunit;

namespace AxePower.Tests.Fuzzing;

/// <summary>
/// Deterministic fuzz and property tests for custom query-string parsers across
/// LAN Drop and OAuthLoopback.
/// </summary>
public class QueryStringParserFuzzTests
{
    [Fact]
    public void MinimizedQueryStringRegressions_AllParsersHandleSafely()
    {
        foreach (var query in FuzzTestCorpus.MinimizedQueryStringRegressions)
        {
            // LanDropHttp.ParseQueryString
            var lanDropDict = LanDropHttp.ParseQueryString(query);
            Assert.NotNull(lanDropDict);
            Assert.True(lanDropDict.Count <= LanDropHttp.MaxQueryParameters);

            // OAuthLoopback.ParseQuery
            var target = $"/callback?{query}";
            var oauthDict = OAuthLoopback.ParseQuery(target);
            Assert.NotNull(oauthDict);
            Assert.True(oauthDict.Count <= 200);
        }
    }

    [Fact]
    public void MalformedPercentEncoding_DoesNotThrowUriFormatException()
    {
        var malformedEncodings = new[]
        {
            "code=%",
            "code=%1",
            "code=%Z",
            "code=%ZZ",
            "code=%2",
            "code=%%%20",
            "code=%u0000",
            "code=%uFFFF",
            "key=%invalid&other=value"
        };

        foreach (var malformed in malformedEncodings)
        {
            var target = $"/callback?{malformed}";

            // OAuthLoopback previously threw UriFormatException on malformed percent encodings
            var oauthResult = OAuthLoopback.ParseQuery(target);
            Assert.NotNull(oauthResult);

            var lanDropResult = LanDropHttp.ParseQueryString(malformed);
            Assert.NotNull(lanDropResult);
        }
    }

    [Fact]
    public void ExcessiveParameters_EnforcesBoundedAllocations()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 1000; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append($"param{i}=val{i}");
        }
        var massiveQuery = sb.ToString();

        var lanDrop = LanDropHttp.ParseQueryString(massiveQuery);
        Assert.Equal(LanDropHttp.MaxQueryParameters, lanDrop.Count);

        var oauth = OAuthLoopback.ParseQuery($"/callback?{massiveQuery}");
        Assert.Equal(200, oauth.Count);
    }

    [Fact]
    public void RandomByteFuzz_QueryParsersNeverCrash()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        foreach (var randomBytes in FuzzTestCorpus.GenerateMutatedByteSequences(count: 250, maxLength: 2048))
        {
            cts.Token.ThrowIfCancellationRequested();

            var query = Encoding.ASCII.GetString(randomBytes);

            var ld = LanDropHttp.ParseQueryString(query);
            Assert.NotNull(ld);

            var oa = OAuthLoopback.ParseQuery($"/auth?{query}");
            Assert.NotNull(oa);
        }
    }
}
