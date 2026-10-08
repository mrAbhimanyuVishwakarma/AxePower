using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services.ShareScreen;

namespace AxePower.Tests.ShareScreen;

/// <summary>
/// Runs the real MediaMTX with an FFmpeg test-pattern encoder for the live tests, and survives a
/// loaded test run: a port picked as free can be taken by another test before MediaMTX binds it,
/// so a server that exits or never answers is restarted on fresh ports, and request timeouts
/// count as "not up yet". Failure messages carry the tools' own output.
/// </summary>
internal sealed class LiveMediaMtx : IDisposable
{
    private const int StartAttempts = 3;
    private static readonly TimeSpan ApiWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PublishWait = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<string> _output = new();
    private readonly HttpClient _api = new() { Timeout = TimeSpan.FromSeconds(5) };
    private Process? _server;
    private Process? _encoder;

    public ShareScreenSettings Settings { get; private set; } = new();
    public int ApiPort { get; private set; }

    public static string MediaMtxPath => Path.Combine(ShareToolProvider.ToolsRoot, "mediamtx", "mediamtx.exe");

    public static string? FindFfmpeg() =>
        new[] { FindOnPath("ffmpeg.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe") }
        .FirstOrDefault(p => p != null && File.Exists(p));

    /// <summary>True when both tools are on this PC; the live tests skip themselves otherwise.</summary>
    public static bool Available => File.Exists(MediaMtxPath) && FindFfmpeg() != null;

    /// <summary>
    /// Starts MediaMTX in <paramref name="dir"/> with the config <paramref name="buildConfig"/>
    /// makes for the chosen ports, then the encoder, and waits until the room's path is ready.
    /// </summary>
    public async Task StartAsync(string dir, ShareRoom room, Func<ShareScreenSettings, int, string> buildConfig)
    {
        var configPath = Path.Combine(dir, "mediamtx.yml");
        for (var attempt = 1; ; attempt++)
        {
            // MediaMTX's WebRTC UDP listener is WebrtcPort + 300 (MediaMtxServer.BuildConfig), so the
            // WebRTC port must leave that much room below 65535; high ephemeral ports do not.
            Settings = new ShareScreenSettings { RtspPort = FreePort(), WebrtcPort = FreePort(max: 65535 - 300) };
            ApiPort = FreePort();
            File.WriteAllText(configPath, buildConfig(Settings, ApiPort));

            _server = Start(MediaMtxPath, $"\"{configPath}\"", dir, "mediamtx");
            if (await WaitUntilAsync(ApiAnswersAsync, ApiWait, _server)) break;

            Kill(ref _server);
            if (attempt == StartAttempts) throw Failure($"MediaMTX did not start after {StartAttempts} attempts");
            _output.Enqueue($"-- attempt {attempt} failed, retrying on fresh ports");
        }

        _encoder = Start(FindFfmpeg()!,
            $"-hide_banner -loglevel error -re -f lavfi -i testsrc=size=320x240:rate=15 -c:v libx264 -preset ultrafast -tune zerolatency " +
            $"-f rtsp -rtsp_transport tcp {room.PublishUrl(Settings.RtspPort)}", dir, "ffmpeg");

        if (!await WaitUntilAsync(() => PathReadyAsync(room.PathName), PublishWait, _server))
            throw Failure("the encoder could not publish through the host's auth check");
    }

    private async Task<bool> ApiAnswersAsync()
    {
        try { return (await _api.GetAsync($"http://127.0.0.1:{ApiPort}/v3/config/global/get")).IsSuccessStatusCode; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    private async Task<bool> PathReadyAsync(string pathName)
    {
        try
        {
            var json = await _api.GetStringAsync($"http://127.0.0.1:{ApiPort}/v3/paths/get/{pathName}");
            return JsonDocument.Parse(json).RootElement.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return false; }
    }

    private Process Start(string exe, string args, string dir, string tag)
    {
        var process = Process.Start(new ProcessStartInfo(exe, args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir })!;
        process.OutputDataReceived += (_, e) => { if (e.Data != null) _output.Enqueue($"[{tag}] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) _output.Enqueue($"[{tag}] {e.Data}"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Polls until the condition holds, giving up early if the server process has exited.</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan within, Process? server)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            if (server is { HasExited: true }) return false;
            await Task.Delay(250);
        }
        return false;
    }

    private Exception Failure(string message) =>
        new Xunit.Sdk.XunitException(message + Environment.NewLine + string.Join(Environment.NewLine, _output.TakeLast(40)));

    /// <summary>A port that is free now and no higher than <paramref name="max"/>.</summary>
    private static int FreePort(int max = 65535)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            // First let Windows choose; if its pick is too high, try random ports in a lower range.
            int candidate = attempt == 0 ? 0 : Random.Shared.Next(20000, Math.Min(max, 60000));
            try
            {
                using var l = new TcpListener(IPAddress.Loopback, candidate);
                l.Start();
                var port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                if (port <= max) return port;
            }
            catch (SocketException) { /* taken: try another */ }
        }
        throw new InvalidOperationException($"No free port at or below {max}.");
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
        .Select(d => { try { return Path.Combine(d.Trim(), exe); } catch (ArgumentException) { return null; } })
        .FirstOrDefault(p => p != null && File.Exists(p));

    private static void Kill(ref Process? process)
    {
        try { process?.Kill(entireProcessTree: true); } catch { }
        try { process?.WaitForExit(2000); } catch { }
        process?.Dispose();
        process = null;
    }

    public void Dispose()
    {
        Kill(ref _encoder);
        Kill(ref _server);
        _api.Dispose();
    }
}
