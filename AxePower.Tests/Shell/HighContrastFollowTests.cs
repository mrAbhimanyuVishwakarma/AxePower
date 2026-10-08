using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AxePower.App.Services;
using AxePower.Core.Models;
using AxePower.Core.Services.Skins;
using Xunit;

namespace AxePower.Tests.Shell;

/// <summary>
/// Windows High Contrast (Prompt 28): while it is on, AxePower shows its High Contrast skin; the
/// user's chosen skin is only set aside, and comes back when High Contrast is turned off.
/// </summary>
[Collection(WpfCollection.Name)]
public class HighContrastFollowTests
{
    private readonly WpfApplicationFixture _wpf;

    public HighContrastFollowTests(WpfApplicationFixture wpf) => _wpf = wpf;

    private static SkinCatalog Catalog() => new(null, new[] { Path.Combine(AppContext.BaseDirectory, "skins") });

    /// <summary>Runs <paramref name="body"/> and takes back the theme dictionaries it merged into the app.</summary>
    private static Task WithAppResources(Action body)
    {
        ShellTestHost.UseAppResources();
        var merged = Application.Current.Resources.MergedDictionaries;
        var before = merged.ToList();
        try { body(); }
        finally
        {
            foreach (var added in merged.Except(before).ToList()) merged.Remove(added);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public void HighContrastSkinShips() => Assert.NotNull(Catalog().Find(ThemeService.HighContrastSkinId));

    [Fact]
    public Task WhileHighContrastIsOn_TheHighContrastSkinShows_InsteadOfTheChosenOne() => _wpf.RunAsync(() => WithAppResources(() =>
    {
        bool highContrast = true;
        var theme = new ThemeService(Catalog(), () => highContrast);

        theme.ApplyTheme(AppTheme.Dark, "neon-tokyo");
        Assert.Equal(ThemeService.HighContrastSkinId, theme.ActiveSkin.Id);
        Assert.True(theme.IsHighContrastActive);

        highContrast = false;
        theme.ApplyTheme(AppTheme.Dark, "neon-tokyo");
        Assert.Equal("neon-tokyo", theme.ActiveSkin.Id);
        Assert.False(theme.IsHighContrastActive);
    }));

    [Fact]
    public Task SystemTheme_IsAlsoOverridden_WhileHighContrastIsOn() => _wpf.RunAsync(() => WithAppResources(() =>
    {
        var theme = new ThemeService(Catalog(), () => true);
        theme.ApplyTheme(AppTheme.System);
        Assert.Equal(ThemeService.HighContrastSkinId, theme.ActiveSkin.Id);
    }));

    [Fact]
    public Task WithHighContrastOff_TheChosenSkinShows() => _wpf.RunAsync(() => WithAppResources(() =>
    {
        var theme = new ThemeService(Catalog(), () => false);
        theme.ApplyTheme(AppTheme.Light, "light");
        Assert.Equal("light", theme.ActiveSkin.Id);
        Assert.False(theme.IsHighContrastActive);
    }));
}
