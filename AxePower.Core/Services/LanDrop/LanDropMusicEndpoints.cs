using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Music;

namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// The phone remote for AxePower Music: current state, commands, and a Server-Sent Events stream
/// that keeps every phone in step with the desktop. Without a player every route answers 404.
/// </summary>
internal sealed class LanDropMusicEndpoints
{
    private const string Lf = "\n";
    private readonly MusicRemote? _music;

    public LanDropMusicEndpoints(MusicRemote? music) => _music = music;

    /// <summary>GET /api/music/state.</summary>
    public Task StateAsync(Stream stream) => _music is null
        ? NoPlayerAsync(stream)
        : LanDropHttp.SendJsonAsync(stream, 200, "OK", _music.StateJson());

    /// <summary>POST /api/music/command?action=&amp;value=.</summary>
    public async Task CommandAsync(Stream stream, LanDropRequest request, CancellationToken ct)
    {
        if (_music is null)
        {
            await NoPlayerAsync(stream);
            return;
        }

        request.Query.TryGetValue("action", out var action);
        request.Query.TryGetValue("value", out var value);

        if (!await _music.CommandAsync(action ?? string.Empty, value, ct))
        {
            // An unknown action or an out-of-range value is the caller's mistake, and saying so
            // is more useful than a success the player quietly ignored.
            await LanDropHttp.SendJsonAsync(stream, 400, "Bad Request", "{\"success\":false}");
            return;
        }

        await LanDropHttp.SendJsonAsync(stream, 200, "OK", _music.StateJson());
    }

    /// <summary>
    /// GET /api/music/events — pushes the player's state to the phone as Server-Sent Events, so
    /// several devices stay in step with the desktop and with each other without any of them polling.
    /// </summary>
    public async Task EventsAsync(Stream stream, CancellationToken ct)
    {
        if (_music is null)
        {
            await NoPlayerAsync(stream);
            return;
        }

        await LanDropHttp.WriteEventStreamHeadAsync(stream, ct);

        // A signal rather than a queue of states: the phone only ever wants the latest one, so
        // a burst of changes collapses into a single send. It starts released on purpose, so a
        // client that has just connected is sent the current state instead of waiting for a
        // change that may be minutes away.
        using var pending = new SemaphoreSlim(1, 1);
        void OnChanged(object? sender, EventArgs e)
        {
            try { if (pending.CurrentCount == 0) pending.Release(); } catch (SemaphoreFullException) { /* A wake-up is already pending; one is enough. */ }
        }

        _music.Changed += OnChanged;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // The wait doubles as a heartbeat: with nothing happening, a comment line every
                // 20 seconds keeps the connection open through anything that drops idle ones.
                bool changed = await pending.WaitAsync(TimeSpan.FromSeconds(20), ct);

                string payload = changed
                    ? "event: state" + Lf + "data: " + _music.StateJson() + Lf + Lf
                    : ": ping" + Lf + Lf;

                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                await stream.WriteAsync(bytes, 0, bytes.Length, ct);
                await stream.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The request ended or the portal stopped.
        }
        catch (Exception)
        {
            // The phone closed the tab or left the network; nothing to report.
        }
        finally
        {
            _music.Changed -= OnChanged;
        }
    }

    private static Task NoPlayerAsync(Stream stream) => LanDropHttp.SendTextAsync(stream, 404, "Not Found", "No player");
}
