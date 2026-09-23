using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>What the router knows about the published path right now.</summary>
public sealed record SharePathStats(int Readers, long BytesReceived, bool IsReady)
{
    public static SharePathStats Empty { get; } = new(0, 0, false);
}

/// <summary>
/// Runs the bundled MediaMTX router for one session.
///
/// The host encodes once and publishes one stream here; MediaMTX fans it out to every viewer, so
/// four friends watching costs the host the same uplink as one. It is configured to route only --
/// no transcoding -- because re-encoding on the way through would add exactly the latency this
/// feature exists to avoid.
/// </summary>
public sealed class MediaMtxServer : IDisposable
{
    private readonly LogService _logService;
    private readonly HttpClient _httpClient;
    private readonly ChildProcessJob _job = new();

    private Process? _process;
    private string? _configPath;
    private int _apiPort;

    public bool IsRunning => _process is { HasExited: false };

    public MediaMtxServer(LogService logService)
    {
        _logService = logService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    }

    /// <summary>
    /// Writes a session-scoped config and starts the router. Throws when it cannot come up, since
    /// there is no point starting the encoder if there is nowhere to publish.
    /// </summary>
    public async Task StartAsync(string mediaMtxExePath, ShareRoom room, ShareScreenSettings settings, CancellationToken ct)
    {
        Stop();

        _apiPort = FindFreePort(9997);
        var sessionDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AxePower", "share");
        Directory.CreateDirectory(sessionDir);

        _configPath = Path.Combine(sessionDir, "mediamtx.yml");
        File.WriteAllText(_configPath, BuildConfig(room, settings, _apiPort), new UTF8Encoding(false));

        var startInfo = new ProcessStartInfo
        {
            FileName = mediaMtxExePath,
            Arguments = $"\"{_configPath}\"",
            WorkingDirectory = Path.GetDirectoryName(mediaMtxExePath) ?? sessionDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => LogServerLine(e.Data);
        _process.ErrorDataReceived += (_, e) => LogServerLine(e.Data);

        _process.Start();
        // Assign before reading output so a crash between the two cannot leave it orphaned.
        _job.Assign(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        if (!await WaitUntilReadyAsync(ct))
        {
            Stop();
            throw new InvalidOperationException(
                "The media server did not start. Another program may be using the streaming ports.");
        }

        _logService.LogInfo($"[ShareScreen] MediaMTX is up: RTSP {settings.RtspPort}, WebRTC {settings.WebrtcPort}.");
    }

    private void LogServerLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        if (line.Contains("ERR", StringComparison.OrdinalIgnoreCase))
        {
            _logService.LogError($"[MediaMTX] {line}");
        }
    }

    /// <summary>Polls the control API until it answers, so we never publish into a dead server.</summary>
    private async Task<bool> WaitUntilReadyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process is { HasExited: true })
            {
                _logService.LogError($"[ShareScreen] MediaMTX exited immediately with code {_process.ExitCode}.");
                return false;
            }

            try
            {
                using var response = await _httpClient.GetAsync($"http://127.0.0.1:{_apiPort}/v3/config/global/get", ct);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // Request timeout; try again.
            }

            await Task.Delay(250, ct);
        }

        return false;
    }

    /// <summary>
    /// Viewer count and the running byte total for the published stream.
    ///
    /// The byte counter is the only trustworthy source of bitrate: FFmpeg's RTSP muxer reports
    /// <c>bitrate=N/A</c>, so the number shown to the user is derived from what the router has
    /// actually received rather than from what was requested.
    /// </summary>
    public async Task<SharePathStats> GetPathStatsAsync(string pathName, CancellationToken ct)
    {
        if (!IsRunning) return SharePathStats.Empty;

        try
        {
            var path = await _httpClient.GetFromJsonAsync<PathInfo>(
                $"http://127.0.0.1:{_apiPort}/v3/paths/get/{pathName}", ct);

            return path == null
                ? SharePathStats.Empty
                : new SharePathStats(path.Readers?.Count ?? 0, path.BytesReceived, path.Ready);
        }
        catch
        {
            // A missing path just means the encoder has not connected yet.
            return SharePathStats.Empty;
        }
    }

    /// <summary>
    /// The address a viewer on another machine should use. An explicit override wins; otherwise
    /// the first private IPv4 on an up, non-loopback adapter, which is right for LAN and Tailscale.
    /// </summary>
    public static string ResolveViewerHost(string? overrideHost)
    {
        if (!string.IsNullOrWhiteSpace(overrideHost)) return overrideHost.Trim();

        foreach (var address in LocalAddresses())
        {
            return address;
        }

        return "127.0.0.1";
    }

    public static IReadOnlyList<string> LocalAddresses()
    {
        var addresses = new List<string>();
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                // Wired first: the blueprint's advice is to prefer Ethernet for the host.
                .OrderByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet);

            foreach (var ni in interfaces)
            {
                foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;

                    var text = unicast.Address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;   // self-assigned
                    if (!addresses.Contains(text)) addresses.Add(text);
                }
            }
        }
        catch
        {
            // Fall through to whatever was collected.
        }

        return addresses;
    }

    private static int FindFreePort(int preferred)
    {
        for (int port = preferred; port < preferred + 50; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // In use; try the next one.
            }
        }

        return preferred;
    }

    /// <summary>
    /// Builds the router config. Only the one session path is defined and only the host may
    /// publish to it; viewers read anonymously, because the unguessable path in their link is what
    /// grants access and asking a friend to type a password would defeat the point.
    /// </summary>
    private static string BuildConfig(ShareRoom room, ShareScreenSettings settings, int apiPort)
    {
        var additionalHosts = string.Join(", ", LocalAddresses().Select(a => $"'{a}'"));
        if (!string.IsNullOrWhiteSpace(settings.ViewerHostOverride))
        {
            additionalHosts = $"'{settings.ViewerHostOverride.Trim()}'" +
                              (additionalHosts.Length > 0 ? ", " + additionalHosts : string.Empty);
        }

        return $"""
                # Generated by AxePower Share Screen. Rewritten on every session; do not edit.
                logLevel: info
                logDestinations: [stdout]
                readTimeout: 10s
                writeTimeout: 10s

                api: yes
                apiAddress: 127.0.0.1:{apiPort}
                metrics: no
                pprof: no
                playback: no

                # RTSP is the host's own ingest only, bound to loopback.
                rtsp: yes
                rtspTransports: [tcp]
                rtspEncryption: 'no'
                rtspAddress: 127.0.0.1:{settings.RtspPort}

                # Everything the host does not use stays off, so no unexpected port is opened.
                rtmp: no
                hls: no
                srt: no
                moq: no

                # WebRTC is what viewers connect with: hardware decoding in the browser, no plugin.
                webrtc: yes
                webrtcAddress: :{settings.WebrtcPort}
                webrtcEncryption: no
                webrtcAllowOrigins: ['*']
                webrtcLocalUDPAddress: :{settings.WebrtcPort + 300}
                webrtcIPsFromInterfaces: yes
                webrtcAdditionalHosts: [{additionalHosts}]

                authMethod: internal
                authInternalUsers:
                  - user: publisher
                    pass: {room.PublisherToken}
                    ips: ['127.0.0.1', '::1']
                    permissions:
                      - action: publish
                        path: {room.PathName}
                  - user: any
                    pass:
                    ips: []
                    permissions:
                      - action: read
                        path: {room.PathName}
                  - user: any
                    pass:
                    ips: ['127.0.0.1', '::1']
                    permissions:
                      - action: api

                paths:
                  {room.PathName}:
                    source: publisher

                """;
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process == null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(4000);
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not stop MediaMTX cleanly", ex);
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
        _job.Dispose();
    }

    private sealed class PathInfo
    {
        [JsonPropertyName("readers")] public List<object>? Readers { get; set; }
        [JsonPropertyName("bytesReceived")] public long BytesReceived { get; set; }
        [JsonPropertyName("ready")] public bool Ready { get; set; }
    }
}
