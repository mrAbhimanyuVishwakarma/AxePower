using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.ShareScreen;
using AxePower.Core.Services.Update;
using Xunit;

namespace AxePower.Tests.Nullability;

/// <summary>
/// Replies from the network with fields missing, null or of the wrong type. Each must end in a
/// clear refusal of that one reply, never a NullReferenceException somewhere downstream.
/// </summary>
public class NetworkMissingFieldTests
{
    private const string Secret = "room-secret-for-tests";

    private static ShareChatMessage Chunk() => new(
        "m1", "Ana", string.Empty, new DateTime(2026, 10, 6, 10, 0, 0),
        Kind: ShareChatKinds.FileChunk, AttachmentId: "0123456789abcdef0123456789abcdef",
        AttachmentName: "a.txt", AttachmentSize: 10, AttachmentSha256: new string('a', 64),
        ChunkIndex: 0, ChunkCount: 1, ChunkData: "aGVsbG8=");

    [Fact]
    public void RoomChat_AComplete_Message_StillDecrypts()
    {
        var wire = RoomMessageCrypto.Encrypt(Chunk(), Secret, sequence: 1, sender: "0011223344556677");

        Assert.True(RoomMessageCrypto.TryDecrypt(wire, Secret, new ReplayFilter(), out var message, out _));
        Assert.Equal("aGVsbG8=", message!.ChunkData);
    }

    [Theory]
    [InlineData("ChunkData")]
    [InlineData("AttachmentSha256")]
    [InlineData("Kind")]
    [InlineData("Sender")]
    public void RoomChat_AMessageWithANullField_IsRefused(string field)
    {
        // Authenticated (it carries the room key) but malformed: the chat loops read these fields
        // without checking, so before Prompt 22 a null here threw inside the host's chat loop.
        var broken = field switch
        {
            "ChunkData" => Chunk() with { ChunkData = null! },
            "AttachmentSha256" => Chunk() with { AttachmentSha256 = null! },
            "Kind" => Chunk() with { Kind = null! },
            _ => Chunk() with { Sender = null! },
        };
        var wire = RoomMessageCrypto.Encrypt(broken, Secret, sequence: 1, sender: "0011223344556677");

        Assert.False(RoomMessageCrypto.TryDecrypt(wire, Secret, new ReplayFilter(), out var message, out _));
        Assert.Null(message);
    }

    [Theory]
    [InlineData("""{"v":1}""")]
    [InlineData("""{"n":null}""")]
    [InlineData("""{"n":42}""")]
    [InlineData("""{"n":""}""")]
    [InlineData("""["n"]""")]
    public async Task RoomJoin_AHostReplyWithoutANonce_IsAClearError(string reply)
    {
        using var http = new HttpClient(new Reply(reply));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ShareRoomClient.BuildAdmittedSocketUriAsync(new Uri("https://host.example/room/abc"), Secret, "chat", CancellationToken.None, http));
        Assert.Equal("The host returned no nonce.", ex.Message);
    }

    [Fact]
    public async Task UpdateManifest_WithNullFields_LoadsAsEmpty_AndTheSignatureCheckRefusesIt()
    {
        using var http = new HttpClient(new Reply("""{"version":null,"assetName":null,"byteLength":5,"sha256":null,"signature":null}"""));
        var service = new UpdateService(new LogService(), httpClient: http);

        var manifest = await service.DownloadManifestAsync("https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases/latest/download/update-manifest.json");

        Assert.Equal(string.Empty, manifest.Version);
        Assert.Equal(string.Empty, manifest.AssetName);
        Assert.Equal(string.Empty, manifest.Sha256);
        var verifier = new EcdsaManifestVerifier(UpdateSigningKey.Pinned);
        Assert.False(verifier.VerifyManifest(manifest, manifest.Signature ?? string.Empty, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Theory]
    [InlineData("""{"access_token":"a","refresh_token":"r","user":{}}""")]
    [InlineData("""{"access_token":"a","refresh_token":null,"user":{"id":"u"}}""")]
    [InlineData("""{"access_token":7,"refresh_token":"r","user":{"id":"u"}}""")]
    [InlineData("""{"access_token":"a","refresh_token":"r","user":null}""")]
    public async Task AccountSignIn_AnIncompleteSession_IsRefused(string reply)
    {
        using var api = new SupabaseAccountApi(new SupabaseProject(new Uri("https://example.supabase.co"), "sb_publishable_test"), new Reply(reply));

        var ex = await Assert.ThrowsAsync<AccountException>(() => api.SignInWithPasswordAsync("a@b.c", "pw", CancellationToken.None));
        Assert.Equal(AccountError.Unknown, ex.Error);
    }

    [Fact]
    public void AccountProfile_WithEveryFieldMissingOrNull_ParsesToEmptyValues()
    {
        var profile = SupabaseAccountApi.ParseProfile(System.Text.Json.Nodes.JsonNode.Parse(
            """{"id":null,"username":5,"display_name":null,"avatar_url":null,"created_at":"not a date"}""")!);

        Assert.Equal(string.Empty, profile.UserId);
        Assert.Equal(string.Empty, profile.Username);
        Assert.Equal(string.Empty, profile.DisplayName);
        Assert.Null(profile.AvatarUrl);
        Assert.Equal(DateTime.MinValue, profile.CreatedAtUtc);
        Assert.True(profile.Discoverable);
    }

    private sealed class Reply(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
