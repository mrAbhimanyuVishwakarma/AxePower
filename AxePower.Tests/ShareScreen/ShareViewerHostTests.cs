using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.ShareScreen;

namespace AxePower.Tests.ShareScreen;

/// <summary>
/// The viewer host, exercised over a real socket on a loopback port.
///
/// These deliberately never send an input frame: the point is to prove the door is locked, and
/// injecting input to prove otherwise would move the developer's actual mouse.
/// </summary>
public class ShareViewerHostTests : IDisposable
{
    private readonly RemoteInputService _input = new(new LogService());
    private readonly ShareViewerHost _host;
    private readonly ShareRoom _room = ShareRoomFactory.Create();

    private static readonly CaptureDisplay Screen =
        new(0, @"\\.\DISPLAY1", "Display 1", 1920, 1080, 60, true);

    public ShareViewerHostTests()
    {
        _host = new ShareViewerHost(new LogService(), _input);
        // A high port well away from the defaults, so a test never collides with a real session.
        _host.Start(_room, "127.0.0.1", webrtcPort: 18889, preferredPort: 18990);
    }

    private string Base => $"http://127.0.0.1:{_host.Port}";

    [Fact]
    public async Task ServesTheViewerPageOnTheSessionPath()
    {
        using var client = new HttpClient();
        var response = await client.GetAsync($"{Base}/{_room.PathName}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<video", html);
        Assert.Contains(_room.PathName, html);          // the WHEP and socket URLs were substituted
        Assert.DoesNotContain("__WHEP__", html);        // no placeholder survived
        Assert.DoesNotContain("__WS__", html);
    }

    [Fact]
    public async Task ServesNothingOnAnyOtherPath()
    {
        using var client = new HttpClient();

        // The path is the session secret, so a guess must not reveal that a session exists.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/axewrongpath")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/{_room.PathName}x")).StatusCode);
    }

    [Fact]
    public async Task ControlSocketIsRefusedWhenControlIsOff()
    {
        // Control has never been enabled, so there is no code that could work.
        var status = await TryUpgradeAsync($"/{_room.PathName}/ws?k=ANYTHING");

        Assert.Equal(403, status);
        Assert.False(_input.IsAccepting);
    }

    [Fact]
    public async Task ControlSocketIsRefusedWithoutACode()
    {
        _host.EnableControl(Screen);
        try
        {
            Assert.Equal(403, await TryUpgradeAsync($"/{_room.PathName}/ws"));
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public async Task ControlSocketIsRefusedWithTheWrongCode()
    {
        var code = _host.EnableControl(Screen);
        try
        {
            Assert.Equal(403, await TryUpgradeAsync($"/{_room.PathName}/ws?k={code}X"));
            Assert.Equal(403, await TryUpgradeAsync($"/{_room.PathName}/ws?k=WRONGCDE"));
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public async Task ControlSocketUpgradesWithTheRightCode()
    {
        var code = _host.EnableControl(Screen);
        try
        {
            Assert.Equal(101, await TryUpgradeAsync($"/{_room.PathName}/ws?k={code}"));
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public void EnablingControlMintsAFreshCodeEachTime()
    {
        var first = _host.EnableControl(Screen);
        _host.DisableControl();
        var second = _host.EnableControl(Screen);
        _host.DisableControl();

        Assert.Equal(8, first.Length);
        Assert.NotEqual(first, second);

        // Characters that are easy to confuse when read aloud are left out on purpose.
        Assert.DoesNotContain(first, c => c is 'I' or 'L' or 'O' or '0' or '1');
    }

    [Fact]
    public void TheViewingLinkNeverCarriesTheControlCode()
    {
        var code = _host.EnableControl(Screen);
        try
        {
            // This is the whole security model: handing someone the link is not handing them
            // the keyboard.
            Assert.DoesNotContain(code, _host.ViewerUrl);
            Assert.DoesNotContain("k=", _host.ViewerUrl);
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public void DisablingControlClosesTheInputGate()
    {
        _host.EnableControl(Screen);
        Assert.True(_input.IsAccepting);

        _host.DisableControl();

        Assert.False(_input.IsAccepting);
    }

    [Fact]
    public void StoppingTheHostClosesTheInputGate()
    {
        _host.EnableControl(Screen);
        Assert.True(_input.IsAccepting);

        // Ending the share must never leave control alive behind it.
        _host.Stop();

        Assert.False(_input.IsAccepting);
    }

    /// <summary>
    /// Performs a WebSocket handshake by hand and returns the HTTP status the server replied with.
    /// No frames are ever sent, so nothing can reach the desktop.
    /// </summary>
    private async Task<int> TryUpgradeAsync(string target)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _host.Port);

        var request =
            $"GET {target} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{_host.Port}\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n";

        var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);

        var buffer = new byte[512];
        var read = await stream.ReadAsync(buffer);
        var statusLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0];

        return int.Parse(statusLine.Split(' ')[1]);
    }

    public void Dispose()
    {
        _host.Dispose();
        _input.RevokeAll();
    }
}
