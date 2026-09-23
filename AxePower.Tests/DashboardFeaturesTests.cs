using System;
using System.Collections.Generic;
using System.Linq;
using AxePower.App.ViewModels;

namespace AxePower.Tests;

/// <summary>
/// The "What's New" list on the Dashboard. Each entry navigates on click, so an entry that
/// names a view the app does not have would look fine and then do nothing.
/// </summary>
public class DashboardFeaturesTests
{
    /// <summary>
    /// The keys <c>MainViewModel.Navigate</c> understands. Kept here on purpose: this test is
    /// the thing that notices when an entry names a view that was renamed or removed.
    /// </summary>
    private static readonly HashSet<string> KnownViews = new(StringComparer.Ordinal)
    {
        "Dashboard", "AutoSwitch", "GameMode", "Battery", "Music", "Settings", "Logs", "About",
        "Uninstaller", "Optimizer", "Utilities", "Doctor", "Maintenance", "Terminal",
        "SoftwareHub", "UpdatePc", "ShareScreen", "PhoneControl", "PhoneHub", "SecondMonitor",
        "PhoneLink"
    };

    private static List<AppFeatureItem> Features() => DashboardViewModel.BuildNewFeatures().ToList();

    [Fact]
    public void EveryEntryNavigatesSomewhereThatExists()
    {
        foreach (var feature in Features())
        {
            // Targets can carry arguments, e.g. "UpdatePc:Apps:Gaming"; only the view matters.
            string view = feature.TargetNavigation.Split(':')[0];
            Assert.True(KnownViews.Contains(view),
                $"'{feature.Title}' points at '{view}', which is not a view the app can show.");
        }
    }

    [Fact]
    public void EveryEntryIsFilledIn()
    {
        var features = Features();
        Assert.NotEmpty(features);

        foreach (var feature in features)
        {
            Assert.False(string.IsNullOrWhiteSpace(feature.Title));
            Assert.False(string.IsNullOrWhiteSpace(feature.Description));
            Assert.False(string.IsNullOrWhiteSpace(feature.Badge));
            Assert.Matches("^#[0-9A-Fa-f]{6}$", feature.BadgeColorHex);
            Assert.False(string.IsNullOrWhiteSpace(feature.Glyph));
        }
    }

    [Fact]
    public void TheMusicWorkIsListed()
    {
        var titles = Features().Select(f => f.Title).ToList();

        Assert.Contains("Music Sources", titles);
        Assert.Contains("Discovery", titles);
        Assert.Contains("Media Shortcuts", titles);
        Assert.Contains("Secured Phone Portal", titles);
    }

    [Fact]
    public void TheShareScreenWorkIsListed()
    {
        var titles = Features().Select(f => f.Title).ToList();

        Assert.Contains("Share Screen", titles);
        Assert.Contains("Remote Control", titles);
    }

    [Fact]
    public void TheNewestEntriesComeFirst()
    {
        // The Dashboard selects the first entry on load, so ordering decides what is shown.
        var titles = Features().Select(f => f.Title).ToList();

        Assert.Equal("Share Screen", titles[0]);
        Assert.True(titles.IndexOf("Share Screen") < titles.IndexOf("Music Sources"));
        Assert.True(titles.IndexOf("Secured Phone Portal") < titles.IndexOf("DLSS 5 Swapper"));
    }

    [Fact]
    public void NoTwoEntriesShareATitle()
    {
        var titles = Features().Select(f => f.Title).ToList();

        Assert.Equal(titles.Count, titles.Distinct(StringComparer.Ordinal).Count());
    }
}
