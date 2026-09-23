using System;
using System.IO;

namespace AxePower.Tests.Music;

/// <summary>
/// Generates audio fixtures in code (the repo holds no binary audio files) and gives each test
/// its own temporary folder, so nothing touches the real %LOCALAPPDATA%\AxePower.
/// </summary>
public sealed class TestAudio : IDisposable
{
    public TestAudio()
    {
        Folder = Path.Combine(Path.GetTempPath(), "AxePowerMusicTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    /// <summary>Writes a 440 Hz sine WAV (PCM, 16 or 24-bit) and returns its path.</summary>
    public string Wav(string name, int sampleRate = 44100, int channels = 2, int bits = 16, double seconds = 1.0, double amplitude = 0.5)
    {
        string path = Path.Combine(Folder, name);
        File.WriteAllBytes(path, SineWav(sampleRate, channels, bits, seconds, amplitude));
        return path;
    }

    public string WriteFile(string name, byte[] content)
    {
        string path = Path.Combine(Folder, name);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    public static byte[] SineWav(int sampleRate, int channels, int bits, double seconds, double amplitude)
    {
        int frames = (int)(sampleRate * seconds);
        int bytesPerSample = bits / 8;
        int dataLength = frames * channels * bytesPerSample;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataLength);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);                               // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * bytesPerSample);
        w.Write((short)(channels * bytesPerSample));
        w.Write((short)bits);
        w.Write("data"u8.ToArray());
        w.Write(dataLength);

        for (int f = 0; f < frames; f++)
        {
            double s = amplitude * Math.Sin(2 * Math.PI * 440 * f / sampleRate);
            for (int c = 0; c < channels; c++)
            {
                if (bits == 16)
                {
                    w.Write((short)Math.Round(s * short.MaxValue));
                }
                else
                {
                    int v = (int)Math.Round(s * 8388607);
                    w.Write((byte)v);
                    w.Write((byte)(v >> 8));
                    w.Write((byte)(v >> 16));
                }
            }
        }

        w.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch { }
    }
}
