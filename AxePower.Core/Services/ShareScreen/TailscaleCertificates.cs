using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>A certificate the room can serve HTTPS with, and the host name it is valid for.</summary>
public sealed record ShareTlsCertificate(X509Certificate2 Certificate, string HostName, string CertPemPath, string KeyPemPath);

/// <summary>
/// Gets a browser-trusted certificate for this PC's Tailscale name (<c>machine.tailnet.ts.net</c>)
/// with <c>tailscale cert</c>, which obtains it from Let's Encrypt through Tailscale. Needs
/// MagicDNS and "HTTPS Certificates" switched on in the tailnet's admin console; when it cannot,
/// the room is served over plain HTTP inside the WireGuard tunnel, exactly as before.
/// See https://tailscale.com/kb/1153/tailscale-cert.
/// </summary>
public class TailscaleCertificateProvider
{
    public delegate Task<(int ExitCode, string StdOut, string StdErr)> CommandRunner(string exe, string arguments, CancellationToken ct);

    private readonly LogService _log;
    private readonly string? _tailscaleExe;
    private readonly string _storeDirectory;
    private readonly CommandRunner _run;

    public TailscaleCertificateProvider(LogService log, string? tailscaleExe = null, string? storeDirectory = null, CommandRunner? run = null)
    {
        _log = log;
        _tailscaleExe = tailscaleExe;
        _storeDirectory = storeDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "share", "tls");
        _run = run ?? RunAsync;
    }

    /// <summary>
    /// A certificate for <paramref name="viewerHost"/> when it is this PC's Tailscale address or
    /// name; null otherwise, or when Tailscale cannot issue one (the reason is logged).
    /// </summary>
    public virtual async Task<ShareTlsCertificate?> TryGetAsync(string viewerHost, CancellationToken ct)
    {
        if (!IsTailscaleHost(viewerHost)) return null;

        var exe = _tailscaleExe ?? FindTailscale();
        if (exe == null)
        {
            _log.LogInfo("[ShareScreen] The viewer address is on Tailscale, but tailscale.exe was not found, so the room stays on HTTP inside the tunnel.");
            return null;
        }

        try
        {
            var status = await _run(exe, "status --json", ct);
            if (status.ExitCode != 0) { _log.LogWarning($"[ShareScreen] tailscale status failed: {status.StdErr.Trim()}"); return null; }

            var (dnsName, addresses) = ParseStatus(status.StdOut);
            if (string.IsNullOrEmpty(dnsName)) { _log.LogWarning("[ShareScreen] Tailscale reported no MagicDNS name for this PC; HTTPS needs MagicDNS."); return null; }

            var host = viewerHost.Trim().TrimEnd('.');
            var isThisPc = string.Equals(host, dnsName, StringComparison.OrdinalIgnoreCase) || addresses.Contains(host, StringComparer.OrdinalIgnoreCase);
            if (!isThisPc) { _log.LogWarning($"[ShareScreen] The viewer address {host} is not this PC's Tailscale address."); return null; }

            Directory.CreateDirectory(_storeDirectory);
            var certPath = Path.Combine(_storeDirectory, "room.crt");
            var keyPath = Path.Combine(_storeDirectory, "room.key");
            var cert = await _run(exe, $"cert --cert-file \"{certPath}\" --key-file \"{keyPath}\" {dnsName}", ct);
            if (cert.ExitCode != 0)
            {
                _log.LogWarning($"[ShareScreen] Tailscale could not issue a certificate (is HTTPS switched on for the tailnet?): {cert.StdErr.Trim()}");
                return null;
            }

            // SChannel cannot serve with a PEM-loaded (ephemeral) key, so round-trip through PKCS#12.
            using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            var usable = new X509Certificate2(pem.Export(X509ContentType.Pkcs12));
            _log.LogInfo($"[ShareScreen] Serving the room over HTTPS as {dnsName} (certificate valid until {usable.NotAfter:yyyy-MM-dd}).");
            return new ShareTlsCertificate(usable, dnsName, certPath, keyPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogWarning($"[ShareScreen] Tailscale HTTPS unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>A Tailscale address (100.64.0.0/10 or fd7a:115c:a1e0::/48) or a <c>.ts.net</c> name.</summary>
    public static bool IsTailscaleHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var value = host.Trim().TrimEnd('.');
        if (value.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(value.Trim('[', ']'), out var ip)) return false;

        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 100 && (bytes[1] & 0xC0) == 64
            : bytes.Length == 16 && bytes[0] == 0xFD && bytes[1] == 0x7A && bytes[2] == 0x11 && bytes[3] == 0x5C && bytes[4] == 0xA1 && bytes[5] == 0xE0;
    }

    /// <summary>This PC's MagicDNS name (without the trailing dot) and Tailscale addresses, from <c>tailscale status --json</c>.</summary>
    internal static (string? DnsName, IReadOnlyList<string> Addresses) ParseStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("Self", out var self) || self.ValueKind != JsonValueKind.Object)
            return (null, Array.Empty<string>());

        var dns = self.TryGetProperty("DNSName", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()?.TrimEnd('.') : null;
        var ips = self.TryGetProperty("TailscaleIPs", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : new List<string>();
        return (string.IsNullOrEmpty(dns) ? null : dns, ips);
    }

    private static string? FindTailscale()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (File.Exists(installed)) return installed;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "tailscale.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* A malformed PATH entry is skipped. */ }
        }
        return null;
    }

    private static async Task<(int, string, string)> RunAsync(string exe, string arguments, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("tailscale.exe did not start.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60)); // the first issuance talks to Let's Encrypt
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* It exited as the timeout fired. */ }
            throw new IOException("tailscale.exe did not finish in time.");
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}
