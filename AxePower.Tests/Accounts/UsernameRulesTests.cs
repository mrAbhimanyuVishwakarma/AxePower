using AxePower.Core.Services.Accounts;

namespace AxePower.Tests.Accounts;

/// <summary>Must agree with <c>is_valid_username</c> in the A1 migration.</summary>
public class UsernameRulesTests
{
    [Theory]
    [InlineData("mrabhimanyu")]
    [InlineData("mr.abhi")]
    [InlineData("_x_")]
    [InlineData("abc")]
    [InlineData("a2345678901234567890")] // 20
    public void Valid(string name) => Assert.True(UsernameRules.IsValid(name));

    [Theory]
    [InlineData("ab")]                    // too short
    [InlineData("a23456789012345678901")] // 21
    [InlineData("MrAbhi")]                // capitals
    [InlineData(".abc")]
    [InlineData("abc.")]
    [InlineData("a..b")]
    [InlineData("mr abhi")]
    [InlineData("mr-abhi")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid(string? name) => Assert.False(UsernameRules.IsValid(name));

    [Theory]
    [InlineData("  @MrAbhimanyu ", "mrabhimanyu")]
    [InlineData("Mr.Abhi", "mr.abhi")]
    [InlineData(null, "")]
    public void Normalize_TrimsDropsAtAndLowercases(string? input, string expected) =>
        Assert.Equal(expected, UsernameRules.Normalize(input));

    [Theory]
    [InlineData("Abhimanyu", true)]
    [InlineData("", false)]
    [InlineData(" Abhi", false)]
    [InlineData("123456789012345678901234567890123", false)] // 33
    public void DisplayNames(string name, bool valid) => Assert.Equal(valid, UsernameRules.IsValidDisplayName(name));

    [Theory]
    [InlineData("Abhi\u202Eeyb", false)]  // right-to-left override: renders reversed
    [InlineData("Ab\u200Bhi", false)]     // zero-width space
    [InlineData("Ab\u0007hi", false)]     // control character
    [InlineData("Abhimanyu \u2728", true)] // emoji are fine
    public void DisplayNames_RejectHiddenCharacters(string name, bool valid) =>
        Assert.Equal(valid, UsernameRules.IsValidDisplayName(name));

    [Fact]
    public void StripHidden_RemovesThemFromProviderNames() =>
        Assert.Equal("Abhi", UsernameRules.StripHidden("A\u200Bb\u202Ehi"));

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/a/ACg8ocK=s96-c", true)]
    [InlineData("https://avatars.githubusercontent.com/u/12345?v=4", true)]
    [InlineData("https://cdn.discordapp.com/avatars/1/abc.png", true)]
    [InlineData("https://avatars.steamstatic.com/abc_full.jpg", true)]
    [InlineData("https://steamcdn-a.akamaihd.net/steamcommunity/public/images/avatars/ab/abc_full.jpg", true)]
    [InlineData("https://evil.example/track.png", false)]
    [InlineData("https://googleusercontent.com.evil.example/x.png", false)] // look-alike host
    [InlineData("http://lh3.googleusercontent.com/a/x", false)]            // not https
    [InlineData(null, false)]
    public void Avatars_OnlyFromProviderPictureServers(string? url, bool allowed) =>
        Assert.Equal(allowed, UsernameRules.IsAllowedAvatarUrl(url));
}
