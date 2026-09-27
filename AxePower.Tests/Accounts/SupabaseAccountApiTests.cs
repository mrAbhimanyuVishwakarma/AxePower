using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Accounts;

namespace AxePower.Tests.Accounts;

/// <summary>
/// The HTTP side of accounts, against a stub network. These pin the wire format Supabase expects
/// and the error mapping the UI relies on; they do not prove the real project answers the same
/// way, which is checked live.
/// </summary>
public class SupabaseAccountApiTests
{
    private static readonly SupabaseProject Project = new(new Uri("https://example.supabase.co"), "sb_publishable_test");

    private const string SessionJson = """
        {"access_token":"acc-1","token_type":"bearer","expires_in":3600,"expires_at":1790000000,
         "refresh_token":"ref-1",
         "user":{"id":"u-1","email":"me@example.com",
                 "app_metadata":{"provider":"google"},
                 "user_metadata":{"username":"mrabhimanyu","picture":"https://img/x.png","email_verified":true}}}
        """;

    [Fact]
    public async Task SignIn_PostsPasswordGrant_WithPublishableKeyOnly_AndParsesTheSession()
    {
        var handler = new StubHandler(_ => Json(SessionJson));
        using var api = new SupabaseAccountApi(Project, handler);

        var session = await api.SignInWithPasswordAsync("me@example.com", "pw", CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.supabase.co/auth/v1/token?grant_type=password", request.Uri);
        Assert.Equal("sb_publishable_test", request.Headers["apikey"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.Contains("\"email\":\"me@example.com\"", request.Body);

        Assert.Equal("acc-1", session.AccessToken);
        Assert.Equal("ref-1", session.RefreshToken);
        Assert.Equal("u-1", session.UserId);
        Assert.Equal("google", session.Provider);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000).UtcDateTime, session.ExpiresAtUtc);
        Assert.Equal("mrabhimanyu", session.UserMetadata["username"]);
        Assert.False(session.UserMetadata.ContainsKey("email_verified")); // non-string values are skipped
    }

    [Fact]
    public async Task SignUp_WithConfirmationOn_ReturnsNull_AndSendsTheProfileMetadata()
    {
        var handler = new StubHandler(_ => Json("""{"id":"u-1","email":"me@example.com","confirmation_sent_at":"2026-09-26T00:00:00Z"}"""));
        using var api = new SupabaseAccountApi(Project, handler);

        var session = await api.SignUpAsync(
            "me@example.com", "pw", new Dictionary<string, string> { ["username"] = "mrabhimanyu", ["display_name"] = "Abhimanyu" },
            CancellationToken.None);

        Assert.Null(session);
        var request = handler.Requests.Single();
        Assert.EndsWith("/auth/v1/signup", request.Uri);
        Assert.Contains("\"data\":{\"username\":\"mrabhimanyu\",\"display_name\":\"Abhimanyu\"}", request.Body);
    }

    [Fact]
    public async Task SignedInCalls_SendTheUserTokenAsBearer_NeverThePublishableKey()
    {
        var handler = new StubHandler(_ => Json("[]"));
        using var api = new SupabaseAccountApi(Project, handler);

        var profile = await api.GetProfileAsync("user-token", "u-1", CancellationToken.None);

        Assert.Null(profile);
        var request = handler.Requests.Single();
        Assert.Equal("Bearer user-token", request.Headers["Authorization"]);
        Assert.Equal("sb_publishable_test", request.Headers["apikey"]);
        Assert.StartsWith("https://example.supabase.co/rest/v1/profiles?id=eq.u-1&select=", request.Uri);
    }

    [Fact]
    public async Task CreateProfile_AsksForTheRowBack_AndParsesIt()
    {
        var handler = new StubHandler(_ => Json("""
            [{"id":"u-1","username":"mrabhimanyu","display_name":"Abhimanyu","avatar_url":null,
              "created_at":"2026-09-26T05:00:00.123456+00:00","username_changed_at":null}]
            """));
        using var api = new SupabaseAccountApi(Project, handler);

        var profile = await api.CreateProfileAsync("tok", "u-1", "mrabhimanyu", "Abhimanyu", null, CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("return=representation", request.Headers["Prefer"]);
        Assert.Contains("\"username\":\"mrabhimanyu\"", request.Body);
        Assert.Equal("mrabhimanyu", profile.Username);
        Assert.Equal("Abhimanyu", profile.DisplayName);
        Assert.Equal(new DateTime(2026, 9, 26, 5, 0, 0, DateTimeKind.Utc), profile.CreatedAtUtc, TimeSpan.FromSeconds(1));
        Assert.Null(profile.UsernameChangedAtUtc);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task UsernameAvailable_ReadsTheBooleanReply(string reply, bool expected)
    {
        var handler = new StubHandler(_ => Json(reply));
        using var api = new SupabaseAccountApi(Project, handler);

        Assert.Equal(expected, await api.IsUsernameAvailableAsync("mrabhimanyu", CancellationToken.None));
        Assert.EndsWith("/rest/v1/rpc/username_available", handler.Requests.Single().Uri);
    }

    [Fact]
    public async Task DeleteAccount_CallsTheEdgeFunctionAsTheUser()
    {
        var handler = new StubHandler(_ => Json("""{"deleted":true}"""));
        using var api = new SupabaseAccountApi(Project, handler);

        await api.DeleteAccountAsync("user-token", CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("https://example.supabase.co/functions/v1/delete-account", request.Uri);
        Assert.Equal("Bearer user-token", request.Headers["Authorization"]);
    }

    [Fact]
    public async Task NoNetwork_IsReportedAsOffline()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no route"));
        using var api = new SupabaseAccountApi(Project, handler);

        var ex = await Assert.ThrowsAsync<AccountException>(() => api.RefreshAsync("r", CancellationToken.None));
        Assert.Equal(AccountError.Offline, ex.Error);
    }

    [Theory]
    [InlineData(400, """{"code":400,"error_code":"invalid_credentials","msg":"Invalid login credentials"}""", AccountError.InvalidCredentials)]
    [InlineData(400, """{"error":"invalid_grant","error_description":"Invalid login credentials"}""", AccountError.InvalidCredentials)]
    [InlineData(400, """{"code":400,"error_code":"email_not_confirmed","msg":"Email not confirmed"}""", AccountError.EmailNotConfirmed)]
    [InlineData(422, """{"code":422,"error_code":"weak_password","msg":"Password should be at least 8 characters."}""", AccountError.WeakPassword)]
    [InlineData(422, """{"code":422,"error_code":"user_already_exists","msg":"User already registered"}""", AccountError.EmailTaken)]
    [InlineData(400, """{"code":400,"error_code":"refresh_token_not_found","msg":"Invalid Refresh Token"}""", AccountError.SessionExpired)]
    [InlineData(429, """{"code":429,"error_code":"over_email_send_rate_limit","msg":"rate limit"}""", AccountError.RateLimited)]
    [InlineData(409, """{"code":"23505","message":"duplicate key value violates unique constraint \"profiles_username_key\""}""", AccountError.UsernameTaken)]
    [InlineData(400, """{"code":"P0001","message":"USERNAME_RESERVED","hint":"That username is reserved."}""", AccountError.UsernameReserved)]
    [InlineData(400, """{"code":"P0001","message":"USERNAME_CHANGE_TOO_SOON"}""", AccountError.UsernameChangeTooSoon)]
    [InlineData(400, """{"code":"23514","message":"new row violates check constraint \"profiles_username_format\""}""", AccountError.UsernameInvalid)]
    [InlineData(400, """{"code":"23514","message":"new row violates check constraint \"profiles_display_name_length\""}""", AccountError.DisplayNameInvalid)]
    [InlineData(401, """{"code":"PGRST301","message":"JWT expired"}""", AccountError.SessionExpired)]
    [InlineData(401, """{"error":"session_expired"}""", AccountError.SessionExpired)]
    [InlineData(503, "upstream down", AccountError.ServiceUnavailable)]
    [InlineData(418, """{"message":"something odd"}""", AccountError.Unknown)]
    public void Errors_MapToWhatTheUiNeeds(int status, string body, AccountError expected)
    {
        Assert.Equal(expected, SupabaseAccountApi.MapError((HttpStatusCode)status, body).Error);
    }

    [Theory]
    [InlineData("""{"code":400,"error_code":"validation_failed","msg":"Refresh token is not valid"}""")] // seen live for a damaged token
    [InlineData("""{"code":400,"error_code":"refresh_token_not_found","msg":"Invalid Refresh Token: Refresh Token Not Found"}""")]
    public async Task Refresh_AnyRefusal_MeansTheSessionHasEnded(string reply)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(reply) });
        using var api = new SupabaseAccountApi(Project, handler);

        var ex = await Assert.ThrowsAsync<AccountException>(() => api.RefreshAsync("r", CancellationToken.None));
        Assert.Equal(AccountError.SessionExpired, ex.Error);
    }

    [Fact]
    public async Task Refresh_WhenTheServerIsDown_IsNotASignOut()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("bad gateway") });
        using var api = new SupabaseAccountApi(Project, handler);

        var ex = await Assert.ThrowsAsync<AccountException>(() => api.RefreshAsync("r", CancellationToken.None));
        Assert.Equal(AccountError.ServiceUnavailable, ex.Error);
    }

    [Fact]
    public async Task AnErrorReply_ThrowsTheMappedError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"code":400,"error_code":"invalid_credentials","msg":"Invalid login credentials"}""")
        });
        using var api = new SupabaseAccountApi(Project, handler);

        var ex = await Assert.ThrowsAsync<AccountException>(() => api.SignInWithPasswordAsync("a@b.c", "x", CancellationToken.None));
        Assert.Equal(AccountError.InvalidCredentials, ex.Error);
        Assert.Equal("Wrong email or password.", ex.Message);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    /// <summary>A request as it went out; bodies are read before the client disposes the message.</summary>
    private sealed record SentRequest(HttpMethod Method, string Uri, Dictionary<string, string> Headers, string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new SentRequest(request.Method, request.RequestUri!.ToString(), headers, body));
            return respond(request);
        }
    }
}
