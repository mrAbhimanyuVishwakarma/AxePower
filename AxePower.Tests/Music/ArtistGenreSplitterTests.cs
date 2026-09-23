using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

public class ArtistGenreSplitterTests
{
    [Theory]
    [InlineData("A; B", new[] { "A", "B" })]
    [InlineData("A/B", new[] { "A", "B" })]
    [InlineData("A feat. B", new[] { "A", "B" })]
    [InlineData("A ft. B", new[] { "A", "B" })]
    [InlineData("A featuring B", new[] { "A", "B" })]
    [InlineData("A FEAT. B", new[] { "A", "B" })]
    [InlineData("Simon & Garfunkel", new[] { "Simon & Garfunkel" })]
    [InlineData("Crosby, Stills & Nash", new[] { "Crosby, Stills & Nash" })]
    [InlineData("  A ;a; B  ", new[] { "A", "B" })]
    public void Artists_DefaultRules(string value, string[] expected)
    {
        Assert.Equal(expected, ArtistGenreSplitter.SplitArtists(value, splitOnAmpersandAndComma: false));
    }

    [Theory]
    [InlineData("Simon & Garfunkel", new[] { "Simon", "Garfunkel" })]
    [InlineData("Crosby, Stills & Nash", new[] { "Crosby", "Stills", "Nash" })]
    public void Artists_AmpersandAndComma_WhenEnabled(string value, string[] expected)
    {
        Assert.Equal(expected, ArtistGenreSplitter.SplitArtists(value, splitOnAmpersandAndComma: true));
    }

    [Theory]
    [InlineData("Rock; Pop", new[] { "Rock", "Pop" })]
    [InlineData("Rock/Pop,Jazz", new[] { "Rock", "Pop", "Jazz" })]
    [InlineData("rock, Rock", new[] { "rock" })]
    public void Genres(string value, string[] expected)
    {
        Assert.Equal(expected, ArtistGenreSplitter.SplitGenres(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankValues_GiveNothing(string? value)
    {
        Assert.Empty(ArtistGenreSplitter.SplitArtists(value, false));
        Assert.Empty(ArtistGenreSplitter.SplitGenres(value));
    }
}
