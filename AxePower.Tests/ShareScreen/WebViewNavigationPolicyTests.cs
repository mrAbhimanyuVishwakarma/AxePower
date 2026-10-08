using System.IO;
using System.Linq;
using AxePower.Core.Services.ShareScreen;
using Xunit;

namespace AxePower.Tests.ShareScreen;

/// <summary>
/// FINAL_SECURITY_REVIEW M2: Share Screen's embedded browser stays within the room host's origin,
/// and the installers start AxePower as the signed-in user, not elevated (L3).
/// </summary>
public class WebViewNavigationPolicyTests
{
    private const string Room = "https://192.168.1.20:8443/watch?room=abc";

    [Theory]
    [InlineData("https://192.168.1.20:8443/watch?room=abc", true)]
    [InlineData("https://192.168.1.20:8443/stream/whep", true)]
    [InlineData("about:blank", true)]
    [InlineData("https://192.168.1.20:9443/", false)]           // another port
    [InlineData("http://192.168.1.20:8443/", false)]            // downgraded scheme
    [InlineData("https://evil.example/login", false)]           // a lookalike page
    [InlineData("https://192.168.1.20.evil.example:8443/", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void OnlyTheRoomsOrigin_AndBlank_MayLoad(string target, bool allowed) =>
        Assert.Equal(allowed, WebViewNavigationPolicy.IsAllowed(target, Room));

    [Fact]
    public void NothingLoads_BeforeAxePowerOpenedARoom() =>
        Assert.False(WebViewNavigationPolicy.IsAllowed("https://192.168.1.20:8443/", ""));

    [Theory]
    [InlineData("AxePower.iss")]
    [InlineData("AxePowerSetup.iss")]
    public void TheInstaller_StartsAxePowerAsTheOriginalUser(string script)
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        var launch = File.ReadAllLines(Path.Combine(dir!.FullName, "Installer", script))
            .Where(l => l.TrimStart().StartsWith("Filename:") && l.Contains("postinstall"))
            .ToList();
        Assert.NotEmpty(launch);
        Assert.All(launch, l => Assert.Contains("runasoriginaluser", l));
    }
}
