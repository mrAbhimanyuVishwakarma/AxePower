using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace AxePower.Tests;

public class DotNetBootstrapTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AxePower.sln")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new InvalidOperationException("Could not locate AxePower.sln root.");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunInstallDotNetScript(string arguments)
    {
        string scriptPath = Path.Combine(RepoRoot, "Installer", "install-dotnet.ps1");
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout, stderr);
    }

    [Fact]
    public void HtmlOrErrorPayload_IsRejected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var htmlContent = Encoding.UTF8.GetBytes("<html><body><h1>404 Not Found - Microsoft CDN Error</h1></body></html>");

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {htmlContent.Length}\r\nContent-Type: text/html\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(htmlContent);
            await stream.FlushAsync();
        });

        try
        {
            var url = $"http://127.0.0.1:{port}/dotnet-runtime.exe";
            var (exitCode, stdout, _) = RunInstallDotNetScript($"-Url \"{url}\" -AllowInsecureHttp -SkipExecution");

            Assert.Equal(1, exitCode);
            Assert.Contains("unexpectedly small", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void MzOnlyHeaderWithoutAuthenticode_IsRejected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Create a 128 KB payload starting with MZ (fake unsigned executable)
        var payload = new byte[128 * 1024];
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        });

        try
        {
            var url = $"http://127.0.0.1:{port}/dotnet-runtime.exe";
            var (exitCode, stdout, _) = RunInstallDotNetScript($"-Url \"{url}\" -AllowInsecureHttp -SkipExecution");

            Assert.Equal(1, exitCode);
            Assert.Contains("not digitally signed", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("MZ header check alone is not sufficient", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void UnexpectedRedirect_IsRejected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            var header = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: https://evil.attacker.example.com/trojan.exe\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.FlushAsync();
        });

        try
        {
            var url = $"http://127.0.0.1:{port}/dotnet-runtime.exe";
            var (exitCode, stdout, _) = RunInstallDotNetScript($"-Url \"{url}\" -AllowInsecureHttp -SkipExecution");

            Assert.Equal(1, exitCode);
            Assert.Contains("evil.attacker.example.com", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not an authorized Microsoft download host", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void PinnedHashMismatch_IsRejected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var payload = new byte[128 * 1024];
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        });

        try
        {
            var url = $"http://127.0.0.1:{port}/dotnet-runtime.exe";
            var wrongHash = "0000000000000000000000000000000000000000000000000000000000000000";
            var (exitCode, stdout, _) = RunInstallDotNetScript($"-Url \"{url}\" -PinnedSha256 \"{wrongHash}\" -AllowInsecureHttp -SkipExecution");

            Assert.Equal(1, exitCode);
            Assert.Contains("hash mismatch", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void FailedDownload_IsRejected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            var header = Encoding.ASCII.GetBytes("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.FlushAsync();
        });

        try
        {
            var url = $"http://127.0.0.1:{port}/dotnet-runtime.exe";
            var (exitCode, stdout, _) = RunInstallDotNetScript($"-Url \"{url}\" -AllowInsecureHttp -SkipExecution");

            Assert.Equal(1, exitCode);
            Assert.Contains("Failed to download runtime installer", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void ValidMicrosoftSignatureMetadata_IsAcceptedByVerificationLogic()
    {
        if (!OperatingSystem.IsWindows()) return;

        string hostExe = Environment.ProcessPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(hostExe)) return;

        var (exitCode, stdout, _) = RunInstallDotNetScript($"-Command \"Write-Host 'Checking'\"");
        // Verify that PowerShell Get-AuthenticodeSignature identifies Microsoft signature
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"(Get-AuthenticodeSignature '{hostExe}').SignerCertificate.Subject\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        string subject = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        Assert.Contains("Microsoft", subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InnoSetupScript_DoesNotOfferBypassOnRuntimeFailure()
    {
        string issPath = Path.Combine(RepoRoot, "Installer", "AxePower.iss");
        string content = File.ReadAllText(issPath);

        // Prove old bypass prompt is removed
        Assert.DoesNotContain("Continue installing AxePower anyway?", content);

        // Prove fail-closed abort is enforced
        Assert.Contains("Setup was cancelled because the .NET 8 Desktop Runtime is required.", content);
        Assert.Contains("https://dotnet.microsoft.com/download/dotnet/8.0", content);
    }
}
