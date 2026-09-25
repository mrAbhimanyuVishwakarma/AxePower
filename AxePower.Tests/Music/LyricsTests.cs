using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

public sealed class LyricsTests
{
    [Fact]
    public void Parser_PreservesMilliseconds_MultipleTimestamps_AndOffset()
    {
        const string lrc = "[offset:25]\n[00:01.2][00:03.045] First <00:01.250>line\n[01:02.34]Second";

        var lines = LrcParser.Parse(lrc);

        Assert.Equal(3, lines.Count);
        Assert.Equal(1225, lines[0].Timestamp.TotalMilliseconds);
        Assert.Equal("First line", lines[0].Text);
        Assert.Equal(3070, lines[1].Timestamp.TotalMilliseconds);
        Assert.Equal(62365, lines[2].Timestamp.TotalMilliseconds);
    }

    [Fact]
    public async Task Service_ReturnsSynchronizedLrcLibLyricsWithoutCallingFallback()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("lrclib.net", request.RequestUri!.Host);
            return Json("{\"plainLyrics\":\"Plain\",\"syncedLyrics\":\"[00:00.125]Hello\"}");
        });
        using var client = new HttpClient(handler);
        using var service = new LrcLibLyricsService(client, new LogService());

        var result = await service.GetLyricsAsync(TrackFor("Song", "Artist", "Album"));

        Assert.NotNull(result);
        Assert.Equal("LRCLIB", result!.Provider);
        Assert.Equal(125, result.Lines.Single().Timestamp.TotalMilliseconds);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Service_UsesKugouWhenLrcLibHasNoMatch()
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:02.500]Fallback"));
        var handler = new StubHandler(request => request.RequestUri!.Host switch
        {
            "lrclib.net" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ when request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal) =>
                Json("{\"candidates\":[{\"id\":\"42\",\"accesskey\":\"key\"}]}"),
            _ => Json("{\"content\":\"" + encoded + "\"}")
        });
        using var client = new HttpClient(handler);
        using var service = new LrcLibLyricsService(client, new LogService());

        var result = await service.GetLyricsAsync(TrackFor("Song", "Artist"));

        Assert.NotNull(result);
        Assert.Equal("Kugou", result!.Provider);
        Assert.Equal("Fallback", result.Lines.Single().Text);
        Assert.Contains(handler.Requests, request => request.RequestUri!.Host == "lyrics.kugou.com");
    }

    [Fact]
    public async Task Service_CleansNoisyYouTubeTitleAndVerifiesLrcLibSearchResult()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/get", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal))
            {
                return Json("[{\"id\":21,\"trackName\":\"21 Saal\",\"artistName\":\"Jassie Gill\","
                    + "\"duration\":249,\"syncedLyrics\":\"[00:01.250]Matched\"}]");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);
        using var service = new LrcLibLyricsService(client, new LogService());
        var track = TrackFor(
            "21 Saal | Jassie Gill | Karan Aujla | Latest Punjabi Song (Official Music Video)",
            "Speed Records") with { Duration = TimeSpan.FromSeconds(249) };

        var result = await service.GetLyricsAsync(track);

        Assert.NotNull(result);
        Assert.Equal("LRCLIB", result!.Provider);
        Assert.Equal("Matched", result.Lines.Single().Text);
        Assert.Contains(handler.Requests, request => request.RequestUri!.AbsolutePath.EndsWith("/search"));
    }

    [Fact]
    public async Task Service_UsesYouTubeCaptionsAfterCatalogueProvidersHaveNoMatch()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host switch
        {
            "lrclib.net" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ when request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal) =>
                Json("{\"candidates\":[]}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var captions = new StubYouTubeCaptions(new LyricsResult(
            "YouTube captions",
            new[] { new LyricLine(TimeSpan.FromMilliseconds(275), "Caption line") }));
        using var client = new HttpClient(handler);
        using var service = new LrcLibLyricsService(client, new LogService(), captions);

        var track = TrackFor("Song", "Artist") with
        {
            Source = new ProviderRef("youtube", "video-123")
        };
        var result = await service.GetLyricsAsync(track);

        Assert.NotNull(result);
        Assert.Equal("YouTube captions", result!.Provider);
        Assert.Equal("video-123", captions.RequestedVideoId);
        Assert.Equal(275, result.Lines.Single().Timestamp.TotalMilliseconds);
    }

    private static Track TrackFor(string title, string artist, string? album = null) => new(
        Id: 0,
        Path: string.Empty,
        Title: title,
        Artists: new[] { artist },
        Album: album,
        AlbumArtist: null,
        TrackNumber: null,
        DiscNumber: null,
        Year: null,
        Genres: Array.Empty<string>(),
        Duration: TimeSpan.FromMinutes(3),
        Format: new AudioFormatInfo("Test", 0, 0, 2, 0, false),
        ReplayGain: null,
        CoverKey: null,
        Liked: false);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public System.Collections.Generic.List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubYouTubeCaptions(LyricsResult? result) : IYouTubeCaptionLyricsProvider
    {
        public string? RequestedVideoId { get; private set; }

        public Task<LyricsResult?> GetLyricsAsync(string videoId, CancellationToken ct = default)
        {
            RequestedVideoId = videoId;
            return Task.FromResult(result);
        }
    }
}
