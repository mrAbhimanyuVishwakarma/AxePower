using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AxePower.Core.Helpers;

/// <summary>
/// A self-contained, pure C# QR Code generator implementing QR Code Model 2 (ISO/IEC 18004).
/// Generates boolean matrices, crisp SVGs, and BMP byte arrays without third-party libraries.
/// </summary>
public static class QrCodeGenerator
{
    public static bool[,] GenerateMatrix(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new bool[21, 21];
        }

        byte[] data = Encoding.UTF8.GetBytes(content);
        int version = GetBestVersion(data.Length);
        return EncodeQr(data, version);
    }

    public static string GenerateSvg(string content, int moduleSize = 8, int quietZone = 4, string darkColor = "#00F0FF", string lightColor = "#0E131F")
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

    public static byte[] GenerateBmpBytes(string content, int moduleSize = 8, int quietZone = 4, uint darkColorRgb = 0x00E0E0E0, uint lightColorRgb = 0x000F172A)
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

    private static int GetBestVersion(int dataLength)
    {
        // Version 1-6 capacity table for Byte mode with Medium error correction (Level M)
        int[] capacities = { 0, 14, 26, 42, 62, 84, 106, 122, 152, 180, 213 };
        for (int v = 1; v < capacities.Length; v++)
        {
            if (dataLength <= capacities[v]) return v;
        }
        return 6; // default fallback version
    }

    private static bool[,] EncodeQr(byte[] data, int version)
    {
        int size = 17 + (version * 4);
        bool[,] modules = new bool[size, size];
        bool[,] isFunction = new bool[size, size];

        // 1. Finder patterns
        PlaceFinderPattern(modules, isFunction, 0, 0, size);
        PlaceFinderPattern(modules, isFunction, size - 7, 0, size);
        PlaceFinderPattern(modules, isFunction, 0, size - 7, size);

        // 2. Alignment patterns (for version >= 2)
        if (version >= 2)
        {
            int alignPos = size - 7;
            PlaceAlignmentPattern(modules, isFunction, alignPos, alignPos);
        }

        // 3. Timing patterns
        for (int i = 8; i < size - 8; i++)
        {
            bool bit = (i % 2 == 0);
            if (!isFunction[6, i]) { modules[6, i] = bit; isFunction[6, i] = true; }
            if (!isFunction[i, 6]) { modules[i, 6] = bit; isFunction[i, 6] = true; }
        }

        // 4. Reserve format info areas
        ReserveFormatAreas(isFunction, size);

        // 5. Build bit stream with Byte mode (0100) + length + data + padding + ECC
        List<bool> bitStream = BuildBitStream(data, version);

        // 6. Place data bits using standard zigzag traversal
        PlaceDataBits(modules, isFunction, bitStream, size);

        // 7. Apply Mask Pattern 0 ( (x + y) % 2 == 0 )
        ApplyMask(modules, isFunction, size);

        // 8. Place Format Information for Mask 0 and Error Correction Level M (00)
        PlaceFormatInfo(modules, size);

        return modules;
    }

    private static void PlaceFinderPattern(bool[,] modules, bool[,] isFunction, int startX, int startY, int size)
    {
        for (int y = -1; y <= 7; y++)
        {
            for (int x = -1; x <= 7; x++)
            {
                int posX = startX + x;
                int posY = startY + y;
                if (posX >= 0 && posX < size && posY >= 0 && posY < size)
                {
                    bool isDark = (x >= 0 && x <= 6 && (y == 0 || y == 6)) ||
                                  (y >= 0 && y <= 6 && (x == 0 || x == 6)) ||
                                  (x >= 2 && x <= 4 && y >= 2 && y <= 4);
                    modules[posX, posY] = isDark;
                    isFunction[posX, posY] = true;
                }
            }
        }
    }

    private static void PlaceAlignmentPattern(bool[,] modules, bool[,] isFunction, int centerX, int centerY)
    {
        for (int y = -2; y <= 2; y++)
        {
            for (int x = -2; x <= 2; x++)
            {
                int posX = centerX + x;
                int posY = centerY + y;
                if (!isFunction[posX, posY])
                {
                    bool isDark = (Math.Abs(x) == 2 || Math.Abs(y) == 2 || (x == 0 && y == 0));
                    modules[posX, posY] = isDark;
                    isFunction[posX, posY] = true;
                }
            }
        }
    }

    private static void ReserveFormatAreas(bool[,] isFunction, int size)
    {
        for (int i = 0; i < 9; i++)
        {
            if (i < size) { isFunction[i, 8] = true; isFunction[8, i] = true; }
        }
        for (int i = 0; i < 8; i++)
        {
            isFunction[size - 1 - i, 8] = true;
            isFunction[8, size - 1 - i] = true;
        }
        isFunction[8, size - 8] = true;
    }

    private static List<bool> BuildBitStream(byte[] data, int version)
    {
        var bits = new List<bool>();

        // Mode indicator: 0100 (Byte mode)
        bits.Add(false); bits.Add(true); bits.Add(false); bits.Add(false);

        // Character count indicator (8 bits for versions 1-9)
        int length = data.Length;
        for (int i = 7; i >= 0; i--)
        {
            bits.Add(((length >> i) & 1) == 1);
        }

        // Data bytes
        foreach (byte b in data)
        {
            for (int i = 7; i >= 0; i--)
            {
                bits.Add(((b >> i) & 1) == 1);
            }
        }

        // Total data codewords available for version at Level M
        int[] totalCodewords = { 0, 16, 28, 44, 64, 86, 108, 124, 154, 182, 216 };
        int maxDataBytes = version < totalCodewords.Length ? totalCodewords[version] : 80;

        // Terminator bits (up to 4 zeroes)
        int term = Math.Min(4, (maxDataBytes * 8) - bits.Count);
        for (int i = 0; i < term; i++) bits.Add(false);

        // Pad to multiple of 8 bits
        while (bits.Count % 8 != 0) bits.Add(false);

        // Pad bytes (0xEC and 0x11)
        byte[] padBytes = { 0xEC, 0x11 };
        int padIndex = 0;
        while (bits.Count < maxDataBytes * 8)
        {
            byte pb = padBytes[padIndex % 2];
            for (int i = 7; i >= 0; i--)
            {
                bits.Add(((pb >> i) & 1) == 1);
            }
            padIndex++;
        }

        // Reed-Solomon Error Correction Codewords
        byte[] rawBytes = BitsToBytes(bits);
        int ecCodewordsCount = GetEccCount(version);
        byte[] ecBytes = CalculateReedSolomonEcc(rawBytes, ecCodewordsCount);

        var finalBits = new List<bool>(bits);
        foreach (byte eb in ecBytes)
        {
            for (int i = 7; i >= 0; i--)
            {
                finalBits.Add(((eb >> i) & 1) == 1);
            }
        }

        return finalBits;
    }

    private static int GetEccCount(int version)
    {
        int[] eccCounts = { 0, 10, 16, 26, 36, 48, 64, 72, 88, 110, 130 };
        return version < eccCounts.Length ? eccCounts[version] : 16;
    }

    private static byte[] BitsToBytes(List<bool> bits)
    {
        int count = bits.Count / 8;
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            byte val = 0;
            for (int b = 0; b < 8; b++)
            {
                if (bits[(i * 8) + b]) val |= (byte)(1 << (7 - b));
            }
            bytes[i] = val;
        }
        return bytes;
    }

    private static byte[] CalculateReedSolomonEcc(byte[] data, int eccCount)
    {
        // Galois Field GF(256) arithmetic with primitive polynomial 0x11D
        byte[] exp = new byte[512];
        byte[] log = new byte[256];
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            exp[i] = (byte)x;
            exp[i + 255] = (byte)x;
            log[x] = (byte)i;
            x = (x << 1);
            if ((x & 0x100) != 0) x ^= 0x11D;
        }

        // Generator polynomial for eccCount
        byte[] gen = new byte[eccCount + 1];
        gen[0] = 1;
        for (int i = 0; i < eccCount; i++)
        {
            for (int j = i + 1; j >= 1; j--)
            {
                gen[j] = (byte)(gen[j] ^ (gen[j - 1] == 0 ? 0 : exp[(log[gen[j - 1]] + i) % 255]));
            }
        }

        byte[] remainder = new byte[eccCount];
        for (int i = 0; i < data.Length; i++)
        {
            byte factor = (byte)(data[i] ^ remainder[0]);
            for (int j = 0; j < eccCount - 1; j++)
            {
                remainder[j] = remainder[j + 1];
            }
            remainder[eccCount - 1] = 0;

            if (factor != 0)
            {
                int factorLog = log[factor];
                for (int j = 0; j < eccCount; j++)
                {
                    if (gen[j + 1] != 0)
                    {
                        remainder[j] ^= exp[(factorLog + log[gen[j + 1]]) % 255];
                    }
                }
            }
        }

        return remainder;
    }

    private static void PlaceDataBits(bool[,] modules, bool[,] isFunction, List<bool> bitStream, int size)
    {
        int bitIdx = 0;
        int right = size - 1;

        while (right > 0)
        {
            if (right == 6) right--; // Skip vertical timing column

            for (int vert = 0; vert < size; vert++)
            {
                for (int col = 0; col < 2; col++)
                {
                    int x = right - col;
                    bool upwards = ((right + 1) / 2) % 2 == 1;
                    int y = upwards ? (size - 1 - vert) : vert;

                    if (!isFunction[x, y])
                    {
                        modules[x, y] = bitIdx < bitStream.Count && bitStream[bitIdx];
                        bitIdx++;
                    }
                }
            }
            right -= 2;
        }
    }

    private static void ApplyMask(bool[,] modules, bool[,] isFunction, int size)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!isFunction[x, y])
                {
                    // Mask Pattern 0: (row + col) % 2 == 0
                    if ((x + y) % 2 == 0)
                    {
                        modules[x, y] = !modules[x, y];
                    }
                }
            }
        }
    }

    private static void PlaceFormatInfo(bool[,] modules, int size)
    {
        // Format info for Level M + Mask 0 (with standard XOR mask 0x5412) -> 0x5412 ^ 0x0000 = 101010000010010 in binary (0x5412)
        int format = 0x5412;

        for (int i = 0; i < 6; i++)
        {
            bool bit = ((format >> (14 - i)) & 1) == 1;
            modules[8, i] = bit;
            modules[size - 1 - i, 8] = bit;
        }

        modules[8, 7] = ((format >> 8) & 1) == 1;
        modules[8, 8] = ((format >> 7) & 1) == 1;
        modules[7, 8] = ((format >> 6) & 1) == 1;

        modules[size - 7, 8] = ((format >> 8) & 1) == 1;
        modules[size - 8, 8] = ((format >> 7) & 1) == 1;

        for (int i = 0; i < 6; i++)
        {
            bool bit = ((format >> (5 - i)) & 1) == 1;
            modules[5 - i, 8] = bit;
            modules[8, size - 6 + i] = bit;
        }

        modules[8, size - 8] = true; // Dark module
    }
}
