using System;
using AxePower.Core.Helpers;
using Xunit;
using ZXing;
using ZXing.Common;

namespace AxePower.Tests;

/// <summary>
/// Every QR code the app draws must scan. A real decoder (ZXing) reads each one back; checking the
/// image's shape is not enough (the hand-written encoder used until 2026-10-06 passed that and
/// produced codes no phone could read).
/// </summary>
public class QrCodeDecodeTests
{
    /// <summary>Decodes a BMP made by <see cref="QrCodeGenerator.GenerateBmpBytes"/>; null when it does not scan.</summary>
    internal static string? DecodeBmp(byte[] bmp)
    {
        int width = BitConverter.ToInt32(bmp, 18);
        int height = Math.Abs(BitConverter.ToInt32(bmp, 22));
        int offset = BitConverter.ToInt32(bmp, 10);
        int stride = ((width * 3) + 3) & ~3;
        var bgr = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(bmp, offset + y * stride, bgr, y * width * 3, width * 3);

        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = new[] { BarcodeFormat.QR_CODE }, TryHarder = true }
        };
        return reader.Decode(new RGBLuminanceSource(bgr, width, height, RGBLuminanceSource.BitmapFormat.BGR24))?.Text;
    }

    /// <summary>Decodes the module matrix drawn the way the SVG draws it (dark on light, 4-module quiet zone).</summary>
    private static string? DecodeMatrix(bool[,] matrix, int scale = 6, int quiet = 4)
    {
        int n = matrix.GetLength(0), size = (n + 2 * quiet) * scale;
        var gray = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int mx = x / scale - quiet, my = y / scale - quiet;
                bool dark = mx >= 0 && my >= 0 && mx < n && my < n && matrix[mx, my];
                gray[y * size + x] = dark ? (byte)0 : (byte)255;
            }
        var reader = new BarcodeReaderGeneric { Options = new DecodingOptions { PossibleFormats = new[] { BarcodeFormat.QR_CODE } } };
        return reader.Decode(new RGBLuminanceSource(gray, size, size, RGBLuminanceSource.BitmapFormat.Gray8))?.Text;
    }

    public static TheoryData<string> Contents => new()
    {
        "HELLO",
        // About page: the mobile app download.
        "https://github.com/mrAbhimanyuVishwakarma/Axepower-Mobile-Download/releases",
        // LAN Drop / Phone Hub: the portal link with its 43-character access token.
        "http://192.168.1.23:5050/?t=Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MEFCQ0RFRkdISUpLTE0",
        // Long enough for multi-block versions, which need codeword interleaving.
        "https://example.com/" + new string('a', 120),
        "https://example.com/" + new string('z', 300),
    };

    [Theory]
    [MemberData(nameof(Contents))]
    public void BmpQrCodes_ScanBackToTheirContent(string content)
    {
        // The colours and margins every caller in the app uses.
        Assert.Equal(content, DecodeBmp(QrCodeGenerator.GenerateBmpBytes(content, 8, 4, 0x00000000, 0x00FFFFFF)));
        Assert.Equal(content, DecodeBmp(QrCodeGenerator.GenerateBmpBytes(content)));
    }

    [Theory]
    [MemberData(nameof(Contents))]
    public void TheMatrixBehindTheSvg_ScansBackToItsContent(string content) =>
        Assert.Equal(content, DecodeMatrix(QrCodeGenerator.GenerateMatrix(content)));

    [Fact]
    public void Svg_IsDarkOnLight_WithAFullQuietZone()
    {
        var svg = QrCodeGenerator.GenerateSvg("http://192.168.1.23:5050/?t=x", 6, 4, "#0F172A", "#FFFFFF");
        Assert.StartsWith("<svg", svg);
        Assert.Contains("fill=\"#FFFFFF\"", svg);   // background
        Assert.Contains("fill=\"#0F172A\"", svg);   // modules
    }
}
