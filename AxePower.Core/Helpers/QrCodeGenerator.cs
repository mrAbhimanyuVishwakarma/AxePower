using System;
using System.Text;
using QRCoder;

namespace AxePower.Core.Helpers;

/// <summary>
/// QR codes for the LAN Drop portal, Phone Hub and the About page. The encoding comes from
/// QRCoder (MIT, ISO/IEC 18004); this class only draws the module matrix as SVG or BMP. The
/// hand-written encoder used before produced codes no scanner could read (2026-10-06).
/// Keep the dark colour darker than the light one: many phone scanners cannot read inverted codes.
/// </summary>
public static class QrCodeGenerator
{
    /// <summary>The module matrix, indexed [x, y], without a quiet zone (the renderers add it).</summary>
    public static bool[,] GenerateMatrix(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new bool[21, 21];
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix;
        // QRCoder includes a 4-module quiet zone around the symbol; strip it.
        const int border = 4;
        int size = rows.Count - (border * 2);
        var matrix = new bool[size, size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                matrix[x, y] = rows[y + border][x + border];
        return matrix;
    }

    public static string GenerateSvg(string content, int moduleSize = 8, int quietZone = 4, string darkColor = "#0F172A", string lightColor = "#FFFFFF")
    {
        bool[,] matrix = GenerateMatrix(content);
        int width = matrix.GetLength(0);
        int totalSize = (width + (quietZone * 2)) * moduleSize;

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {totalSize} {totalSize}\" width=\"{totalSize}\" height=\"{totalSize}\">");
        sb.Append($"<rect width=\"100%\" height=\"100%\" fill=\"{lightColor}\" rx=\"12\"/>");

        for (int y = 0; y < width; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (matrix[x, y])
                {
                    int posX = (x + quietZone) * moduleSize;
                    int posY = (y + quietZone) * moduleSize;
                    sb.Append($"<rect x=\"{posX}\" y=\"{posY}\" width=\"{moduleSize}\" height=\"{moduleSize}\" fill=\"{darkColor}\" rx=\"1.5\"/>");
                }
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    public static byte[] GenerateBmpBytes(string content, int moduleSize = 8, int quietZone = 4, uint darkColorRgb = 0x000F172A, uint lightColorRgb = 0x00FFFFFF)
    {
        bool[,] matrix = GenerateMatrix(content);
        int matrixDim = matrix.GetLength(0);
        int imgDim = (matrixDim + (quietZone * 2)) * moduleSize;

        int rowStride = ((imgDim * 3) + 3) & ~3; // 4-byte aligned
        int pixelDataSize = rowStride * imgDim;
        int fileSize = 54 + pixelDataSize;

        byte[] bmp = new byte[fileSize];

        // BMP Header
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        WriteInt32(bmp, 2, fileSize);
        WriteInt32(bmp, 10, 54); // Offset to pixel data

        // DIB Header (BITMAPINFOHEADER)
        WriteInt32(bmp, 14, 40); // Header size
        WriteInt32(bmp, 18, imgDim); // Width
        WriteInt32(bmp, 22, -imgDim); // Height (top-down)
        WriteInt16(bmp, 26, 1); // Color planes
        WriteInt16(bmp, 28, 24); // Bits per pixel (24-bit RGB)
        WriteInt32(bmp, 34, pixelDataSize);

        byte darkR = (byte)((darkColorRgb >> 16) & 0xFF);
        byte darkG = (byte)((darkColorRgb >> 8) & 0xFF);
        byte darkB = (byte)(darkColorRgb & 0xFF);

        byte lightR = (byte)((lightColorRgb >> 16) & 0xFF);
        byte lightG = (byte)((lightColorRgb >> 8) & 0xFF);
        byte lightB = (byte)(lightColorRgb & 0xFF);

        for (int y = 0; y < imgDim; y++)
        {
            int matrixY = (y / moduleSize) - quietZone;
            int rowOffset = 54 + (y * rowStride);

            for (int x = 0; x < imgDim; x++)
            {
                int matrixX = (x / moduleSize) - quietZone;
                bool isDark = false;

                if (matrixX >= 0 && matrixX < matrixDim && matrixY >= 0 && matrixY < matrixDim)
                {
                    isDark = matrix[matrixX, matrixY];
                }

                int pixelOffset = rowOffset + (x * 3);
                bmp[pixelOffset] = isDark ? darkB : lightB;
                bmp[pixelOffset + 1] = isDark ? darkG : lightG;
                bmp[pixelOffset + 2] = isDark ? darkR : lightR;
            }
        }

        return bmp;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
