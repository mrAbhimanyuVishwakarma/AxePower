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
}
