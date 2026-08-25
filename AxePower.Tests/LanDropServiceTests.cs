using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using AxePower.Core.Helpers;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.Tests;

public class LanDropServiceTests
{
    private readonly LogService _logService;
    private readonly LanDropService _lanDropService;

    public LanDropServiceTests()
    {
        _logService = new LogService();
        _lanDropService = new LanDropService(_logService);
    }

    [Fact]
    public void LanDropService_InitializesWithDefaultIpAndDirectory()
    {
        Assert.NotNull(_lanDropService.CurrentIpAddress);
        Assert.NotEmpty(_lanDropService.CurrentIpAddress);
        Assert.Equal(5050, _lanDropService.CurrentPort);
        Assert.False(string.IsNullOrEmpty(_lanDropService.DownloadDirectory));
        Assert.True(Directory.Exists(_lanDropService.DownloadDirectory));
    }

    [Fact]
    public void LanDropService_GetLocalIpAddresses_ReturnsAtLeastOneIp()
    {
        var ips = LanDropService.GetLocalIpAddresses();
        Assert.NotNull(ips);
        Assert.NotEmpty(ips);
    }

    [Fact]
    public void LanDropModels_FormatBytesCorrectly()
    {
        var item1 = new LanDropFileItem { SizeBytes = 500 };
        Assert.Equal("500 B", item1.FormattedSize);

        var item2 = new LanDropFileItem { SizeBytes = 2048 };
        Assert.Equal("2.0 KB", item2.FormattedSize);

        var item3 = new LanDropFileItem { SizeBytes = 1048576 * 5 };
        Assert.Equal("5.00 MB", item3.FormattedSize);

        var item4 = new LanDropFileItem { SizeBytes = 1073741824L * 3 };
        Assert.Equal("3.00 GB", item4.FormattedSize);
    }

    [Fact]
    public void QrCodeGenerator_GeneratesValidMatrixAndSvg()
    {
        string testUrl = "http://192.168.1.50:5050/";

        var matrix = QrCodeGenerator.GenerateMatrix(testUrl);
        Assert.NotNull(matrix);
        Assert.True(matrix.GetLength(0) >= 21);
        Assert.True(matrix.GetLength(1) >= 21);

        string svg = QrCodeGenerator.GenerateSvg(testUrl);
        Assert.NotNull(svg);
        Assert.StartsWith("<svg", svg);
        Assert.EndsWith("</svg>", svg);
        Assert.Contains("<rect", svg);

        byte[] bmp = QrCodeGenerator.GenerateBmpBytes(testUrl);
        Assert.NotNull(bmp);
        Assert.True(bmp.Length > 54);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
    }

    [Fact]
    public void LanDropService_AddAndRemoveSharedFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"axepower_test_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tempFile, "Hello LAN Drop Test");

            _lanDropService.AddSharedFile(tempFile);
            var shared = _lanDropService.GetSharedFiles();
            Assert.Single(shared);
            Assert.Equal(Path.GetFileName(tempFile), shared[0].FileName);
            Assert.False(shared[0].IsReceived);

            _lanDropService.RemoveSharedFile(shared[0].Id);
            Assert.Empty(_lanDropService.GetSharedFiles());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void LanDropService_TextMessagePosting()
    {
        bool eventFired = false;
        _lanDropService.OnTextReceived += (s, msg) =>
        {
            eventFired = true;
        };

        _lanDropService.PostTextMessageFromPc("Test OTP: 123456");

        var msgs = _lanDropService.GetClipboardMessages();
        Assert.NotEmpty(msgs);
        Assert.Equal("Test OTP: 123456", msgs[0].Content);
        Assert.True(msgs[0].IsFromPc);
        Assert.True(eventFired);
    }
}
