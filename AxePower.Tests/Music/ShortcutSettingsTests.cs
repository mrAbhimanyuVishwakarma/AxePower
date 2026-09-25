using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxePower.Core.Models.Music;

namespace AxePower.Tests.Music;

/// <summary>Parsing, conflict detection and persistence of keyboard shortcuts.</summary>
public class ShortcutSettingsTests
{
    private static readonly JsonSerializerOptions ReadOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData("Ctrl+Shift+M", "Ctrl+Shift+M")]
    [InlineData("shift+ctrl+m", "Ctrl+Shift+M")]
    [InlineData("  Ctrl  +  Alt + Space ", "Ctrl+Alt+Space")]
    [InlineData("Win+F1", "Win+F1")]
    [InlineData("Control+Right", "Ctrl+Right")]
    public void BindingsAreParsedAndWrittenBackInOneForm(string input, string expected)
    {
        Assert.True(Shortcut.TryParse(input, out var shortcut));
        Assert.Equal(expected, shortcut.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Ctrl+Alt")]
    [InlineData("A+B")]
    public void TextThatIsNotAShortcutIsRejectedRatherThanGuessedAt(string? input)
    {
        Assert.False(Shortcut.TryParse(input, out _));
    }

    [Fact]
    public void TheSameShortcutTypedInADifferentOrderIsTheSameShortcut()
    {
        Assert.True(Shortcut.TryParse("Ctrl+Shift+M", out var first));
        Assert.True(Shortcut.TryParse("Shift+Ctrl+m", out var second));

        Assert.Equal(first, second);
    }

    [Fact]
    public void AnActionWithNoStoredBindingUsesItsDefault()
    {
        var settings = new ShortcutSettings();

        Assert.Equal("Ctrl+Alt+Space", settings.Resolve(ShortcutAction.PlayPause));
        Assert.Equal("Ctrl+Shift+M", settings.Resolve(ShortcutAction.ToggleMiniPlayer));
    }

    [Fact]
    public void EveryActionHasADefaultEntry()
    {
        // A missing entry would throw the moment that action was resolved.
        foreach (ShortcutAction action in Enum.GetValues<ShortcutAction>())
        {
            Assert.True(ShortcutSettings.Defaults.ContainsKey(action), $"no default for {action}");
            Assert.False(string.IsNullOrWhiteSpace(ShortcutSettings.Describe(action)));
        }
    }

    [Fact]
    public void ClearingABindingIsRememberedAndIsNotTheSameAsResettingIt()
    {
        var settings = new ShortcutSettings();

        settings.Set(ShortcutAction.PlayPause, string.Empty);
        Assert.Equal(string.Empty, settings.Resolve(ShortcutAction.PlayPause));

        settings.Reset(ShortcutAction.PlayPause);
        Assert.Equal("Ctrl+Alt+Space", settings.Resolve(ShortcutAction.PlayPause));
    }

    [Fact]
    public void ABindingAlreadyInUseIsReportedWithTheActionThatHasIt()
    {
        var settings = new ShortcutSettings();

        var clash = settings.FindConflict(ShortcutAction.ToggleShuffle, "Ctrl+Alt+Space");

        Assert.Equal(ShortcutAction.PlayPause, clash);
    }

    [Fact]
    public void AnActionDoesNotConflictWithItself()
    {
        var settings = new ShortcutSettings();

        Assert.Null(settings.FindConflict(ShortcutAction.PlayPause, "Ctrl+Alt+Space"));
        Assert.Null(settings.FindConflict(ShortcutAction.ToggleShuffle, "Ctrl+Alt+F9"));
        Assert.Null(settings.FindConflict(ShortcutAction.ToggleShuffle, string.Empty));
    }

    [Fact]
    public void ConflictDetectionIgnoresTheOrderModifiersWereTypedIn()
    {
        var settings = new ShortcutSettings();
        settings.Set(ShortcutAction.ToggleShuffle, "Ctrl+Shift+P");

        Assert.Equal(ShortcutAction.ToggleShuffle, settings.FindConflict(ShortcutAction.CycleRepeat, "Shift+Ctrl+P"));
    }

    [Fact]
    public void ResetAllBringsBackEveryDefault()
    {
        var settings = new ShortcutSettings();
        settings.Set(ShortcutAction.PlayPause, "Ctrl+Alt+P");
        settings.Set(ShortcutAction.NextTrack, string.Empty);

        settings.ResetAll();

        Assert.All(Enum.GetValues<ShortcutAction>(),
            action => Assert.Equal(ShortcutSettings.Defaults[action], settings.Resolve(action)));
    }

    [Fact]
    public void ShortcutsSurviveACloneWithoutSharingTheirDictionary()
    {
        var original = new ShortcutSettings { GlobalHotkeysEnabled = true, SeekSeconds = 15 };
        original.Set(ShortcutAction.PlayPause, "Ctrl+Alt+P");

        var copy = original.Clone();
        copy.Set(ShortcutAction.NextTrack, "Ctrl+Alt+N");

        Assert.True(copy.GlobalHotkeysEnabled);
        Assert.Equal(15, copy.SeekSeconds);
        Assert.Equal("Ctrl+Alt+P", copy.Resolve(ShortcutAction.PlayPause));
        Assert.Equal(ShortcutSettings.Defaults[ShortcutAction.NextTrack], original.Resolve(ShortcutAction.NextTrack));
    }

    [Fact]
    public void ShortcutsRoundTripThroughJsonInsideMusicSettings()
    {
        var music = new MusicSettings();
        music.Shortcuts.GlobalHotkeysEnabled = true;
        music.Shortcuts.Set(ShortcutAction.ToggleMute, "Ctrl+Alt+0");
        music.Shortcuts.VolumeStepPercent = 10;

        string json = JsonSerializer.Serialize(music, WriteOptions);
        var loaded = JsonSerializer.Deserialize<MusicSettings>(json, ReadOptions)!;

        Assert.True(loaded.Shortcuts.GlobalHotkeysEnabled);
        Assert.Equal("Ctrl+Alt+0", loaded.Shortcuts.Resolve(ShortcutAction.ToggleMute));
        Assert.Equal(10, loaded.Shortcuts.VolumeStepPercent);
    }

    [Fact]
    public void SettingsWrittenBeforeShortcutsExistedLoadWithTheDefaults()
    {
        var loaded = JsonSerializer.Deserialize<MusicSettings>("{\"allowVolumeBoost\":true}", ReadOptions)!;

        Assert.NotNull(loaded.Shortcuts);
        Assert.False(loaded.Shortcuts.GlobalHotkeysEnabled);
        Assert.Equal("Ctrl+Shift+M", loaded.Shortcuts.Resolve(ShortcutAction.ToggleMiniPlayer));
    }

    [Fact]
    public void NoTwoActionsShipWithTheSameDefaultBinding()
    {
        var bound = ShortcutSettings.Defaults.Values
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => { Shortcut.TryParse(b, out var s); return s.ToString(); })
            .ToList();

        Assert.Equal(bound.Count, bound.Distinct(StringComparer.Ordinal).Count());
    }
}
