using System;
using System.Text;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

public class DecoderFactoryTests
{
    private static byte[] OggPage(string codecHeader)
    {
        // "OggS" page header (27 bytes) + one-entry segment table, then the codec's id packet
        var bytes = new byte[64];
        Encoding.ASCII.GetBytes("OggS").CopyTo(bytes, 0);
        bytes[26] = 1;
        Encoding.ASCII.GetBytes(codecHeader).CopyTo(bytes, 28);
        return bytes;
    }

    [Theory]
    [InlineData("a.wav", DecoderKind.Wave)]
    [InlineData("a.WAV", DecoderKind.Wave)]
    [InlineData("a.aif", DecoderKind.Aiff)]
    [InlineData("a.aiff", DecoderKind.Aiff)]
    [InlineData("a.mp3", DecoderKind.MediaFoundation)]
    [InlineData("a.m4a", DecoderKind.MediaFoundation)]
    [InlineData("a.mp4", DecoderKind.MediaFoundation)]
    [InlineData("a.aac", DecoderKind.MediaFoundation)]
    [InlineData("a.flac", DecoderKind.MediaFoundation)]
    [InlineData("a.wma", DecoderKind.MediaFoundation)]
    [InlineData("a.opus", DecoderKind.Opus)]
    public void Extension_MapsToDecoder(string file, DecoderKind expected)
    {
        Assert.Equal(expected, DecoderFactory.Classify(file, () => throw new InvalidOperationException("header not needed")));
    }

    [Theory]
    [InlineData("a.ogg")]
    [InlineData("a.oga")]
    public void Ogg_WithOpusHead_IsOpus(string file)
    {
        Assert.Equal(DecoderKind.Opus, DecoderFactory.Classify(file, () => OggPage("OpusHead")));
    }

    [Fact]
    public void Ogg_WithVorbisHeader_IsVorbis()
    {
        Assert.Equal(DecoderKind.Vorbis, DecoderFactory.Classify("a.ogg", () => OggPage("\u0001vorbis")));
    }

    [Theory]
    [InlineData(".ape")]
    [InlineData(".dsf")]
    [InlineData(".wv")]
    public void Unsupported_Throws_WithTheExtension(string ext)
    {
        var ex = Assert.Throws<UnsupportedFormatException>(() => DecoderFactory.Classify("song" + ext, () => Array.Empty<byte>()));
        Assert.Equal(ext, ex.Extension);
        Assert.Equal($"Unsupported format ({ext})", ex.Message);
        Assert.False(DecoderFactory.IsSupported("song" + ext));
    }

    [Fact]
    public void ShortOrEmptyHeader_IsNotOpus()
    {
        Assert.False(DecoderFactory.ContainsOpusHead(Array.Empty<byte>()));
        Assert.False(DecoderFactory.ContainsOpusHead(Encoding.ASCII.GetBytes("OpusHead")));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void GeneratedWav_OpensWithCorrectDurationAndSampleCount(int bits)
    {
        using var audio = new TestAudio();
        string path = audio.Wav($"tone{bits}.wav", 44100, 2, bits, seconds: 1.0);

        using var decoder = DecoderFactory.Open(path);
        Assert.Equal(44100, decoder.WaveFormat.SampleRate);
        Assert.Equal(2, decoder.WaveFormat.Channels);
        Assert.InRange(decoder.Duration.TotalMilliseconds, 999, 1001);

        var buffer = new float[4096];
        long total = 0;
        int n;
        while ((n = decoder.Read(buffer, 0, buffer.Length)) > 0) total += n;
        Assert.Equal(44100 * 2, total);
    }

    [Fact]
    public void MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<System.IO.FileNotFoundException>(() => DecoderFactory.Open(@"C:\definitely\missing\song.flac"));
    }
}
