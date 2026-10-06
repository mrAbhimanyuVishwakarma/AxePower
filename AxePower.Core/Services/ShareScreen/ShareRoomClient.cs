using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// The joining side of room admission: fetch a single-use nonce from the host and answer it, so
/// the viewer token never travels in the socket URL (threat model §4.2).
/// </summary>
public static class ShareRoomClient
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// The WebSocket address for <paramref name="channel"/> (<c>chat</c> or <c>voice</c>) in the
    /// room at <paramref name="roomUri"/>, carrying a fresh nonce and its proof.
    /// </summary>
    public static async Task<Uri> BuildAdmittedSocketUriAsync(
        Uri roomUri, string viewerToken, string channel, CancellationToken ct, HttpClient? http = null)
    {
        var roomPath = roomUri.AbsolutePath.TrimEnd('/');
        var nonceUri = new UriBuilder(roomUri) { Path = roomPath + "/nonce", Query = string.Empty, Fragment = string.Empty }.Uri;

        using var response = await (http ?? SharedHttp).GetAsync(nonceUri, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        var nonce = json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()
            : null;
        if (string.IsNullOrEmpty(nonce)) throw new InvalidOperationException("The host returned no nonce.");

        var proof = ShareScreenCrypto.ComputeProof(ShareScreenCrypto.DeriveAuthKey(viewerToken), channel, nonce);
        return new UriBuilder(roomUri)
        {
            Scheme = roomUri.Scheme == "https" ? "wss" : "ws",
            Path = roomPath + "/" + channel,
            Query = "n=" + Uri.EscapeDataString(nonce) + "&p=" + proof,
            Fragment = string.Empty
        }.Uri;
    }
}
