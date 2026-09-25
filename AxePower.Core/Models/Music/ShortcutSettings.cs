using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AxePower.Core.Models.Music;

/// <summary>
/// Something a shortcut can do. The names are stored in settings.json, so they are part of the
/// file format: renaming one silently drops the user's binding.
/// </summary>
public enum ShortcutAction
{
    PlayPause,
    NextTrack,
    PreviousTrack,
    SeekForward,
    SeekBackward,
    VolumeUp,
    VolumeDown,
    ToggleMute,
    ToggleShuffle,
    CycleRepeat,
    ToggleDiscovery,
    ToggleMiniPlayer
}

/// <summary>
/// The global shortcuts and how far the seek and volume keys move. A binding is stored as the
/// text the user sees, e.g. <c>Ctrl+Shift+M</c>; an empty or missing one means "not bound",
/// which is different from "use the default".
/// </summary>
public class ShortcutSettings
{
    /// <summary>
    /// Whether AxePower registers these shortcuts system-wide. Off by default: taking keys away
    /// from every other application is not something to do without being asked.
    /// </summary>
    [JsonPropertyName("globalHotkeysEnabled")]
    public bool GlobalHotkeysEnabled { get; set; } = false;

    /// <summary>Action name to key text. Actions not listed here fall back to the defaults.</summary>
    [JsonPropertyName("bindings")]
    public Dictionary<string, string> Bindings { get; set; } = new();

    /// <summary>How far the seek shortcuts move, in seconds.</summary>
    [JsonPropertyName("seekSeconds")]
    public int SeekSeconds { get; set; } = 5;

    /// <summary>How much the volume shortcuts move, in percent.</summary>
    [JsonPropertyName("volumeStepPercent")]
    public int VolumeStepPercent { get; set; } = 5;

    /// <summary>
    /// What each action is bound to out of the box. Media keys are deliberately left alone:
    /// Windows already routes them, and claiming them here would break every other player.
    /// </summary>
    public static IReadOnlyDictionary<ShortcutAction, string> Defaults { get; } =
        new Dictionary<ShortcutAction, string>
        {
            [ShortcutAction.PlayPause] = "Ctrl+Alt+Space",
            [ShortcutAction.NextTrack] = "Ctrl+Alt+Right",
            [ShortcutAction.PreviousTrack] = "Ctrl+Alt+Left",
            [ShortcutAction.SeekForward] = "Ctrl+Alt+Shift+Right",
            [ShortcutAction.SeekBackward] = "Ctrl+Alt+Shift+Left",
            [ShortcutAction.VolumeUp] = "Ctrl+Alt+Up",
            [ShortcutAction.VolumeDown] = "Ctrl+Alt+Down",
            [ShortcutAction.ToggleMute] = "Ctrl+Alt+M",
            [ShortcutAction.ToggleShuffle] = string.Empty,
            [ShortcutAction.CycleRepeat] = string.Empty,
            [ShortcutAction.ToggleDiscovery] = string.Empty,
            [ShortcutAction.ToggleMiniPlayer] = "Ctrl+Shift+M"
        };

    /// <summary>What a shortcut does, in the words used in Settings.</summary>
    public static string Describe(ShortcutAction action) => action switch
    {
        ShortcutAction.PlayPause => "Play / pause",
        ShortcutAction.NextTrack => "Next track",
        ShortcutAction.PreviousTrack => "Previous track",
        ShortcutAction.SeekForward => "Seek forward",
        ShortcutAction.SeekBackward => "Seek backward",
        ShortcutAction.VolumeUp => "Volume up",
        ShortcutAction.VolumeDown => "Volume down",
        ShortcutAction.ToggleMute => "Mute / unmute",
        ShortcutAction.ToggleShuffle => "Toggle shuffle",
        ShortcutAction.CycleRepeat => "Cycle repeat",
        ShortcutAction.ToggleDiscovery => "Toggle discovery",
        ShortcutAction.ToggleMiniPlayer => "Show / hide mini player",
        _ => action.ToString()
    };

    /// <summary>The binding in force for an action: the user's if they set one, else the default.</summary>
    public string Resolve(ShortcutAction action) =>
        Bindings.TryGetValue(action.ToString(), out var binding)
            ? binding ?? string.Empty
            : Defaults[action];

    public void Set(ShortcutAction action, string? binding) =>
        Bindings[action.ToString()] = binding?.Trim() ?? string.Empty;

    /// <summary>Forgets a custom binding so the action goes back to its default.</summary>
    public void Reset(ShortcutAction action) => Bindings.Remove(action.ToString());

    public void ResetAll() => Bindings.Clear();

    /// <summary>
    /// The action already using a binding, if any. Comparison ignores the order the modifiers
    /// were typed in, so Ctrl+Shift+M and Shift+Ctrl+M are the same shortcut.
    /// </summary>
    public ShortcutAction? FindConflict(ShortcutAction action, string binding)
    {
        if (string.IsNullOrWhiteSpace(binding)) return null;
        if (!Shortcut.TryParse(binding, out var wanted)) return null;

        foreach (ShortcutAction other in Enum.GetValues<ShortcutAction>())
        {
            if (other == action) continue;
            if (Shortcut.TryParse(Resolve(other), out var existing) && existing.Equals(wanted))
                return other;
        }
        return null;
    }

    public ShortcutSettings Clone() => new()
    {
        GlobalHotkeysEnabled = GlobalHotkeysEnabled,
        Bindings = Bindings is null
            ? new Dictionary<string, string>()
            : Bindings.ToDictionary(pair => pair.Key, pair => pair.Value),
        SeekSeconds = SeekSeconds,
        VolumeStepPercent = VolumeStepPercent
    };
}

/// <summary>Modifier keys, matching the Win32 values <c>RegisterHotKey</c> expects.</summary>
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

/// <summary>
/// A parsed key combination. Written and read as text ("Ctrl+Shift+M") so settings.json stays
/// readable and a binding survives a change in how keys are represented in code.
/// </summary>
public readonly record struct Shortcut(ShortcutModifiers Modifiers, string Key)
{
    private static readonly Dictionary<string, ShortcutModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = ShortcutModifiers.Control,
        ["control"] = ShortcutModifiers.Control,
        ["alt"] = ShortcutModifiers.Alt,
        ["shift"] = ShortcutModifiers.Shift,
        ["win"] = ShortcutModifiers.Windows,
        ["windows"] = ShortcutModifiers.Windows
    };

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    /// <summary>
    /// Reads "Ctrl+Shift+M". Returns false for empty text, for an unknown modifier, and for a
    /// combination with no actual key — all of which mean "not bound" rather than an error.
    /// </summary>
    public static bool TryParse(string? text, out Shortcut shortcut)
    {
        shortcut = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = ShortcutModifiers.None;
        string? key = null;

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ModifierNames.TryGetValue(raw, out var modifier)) modifiers |= modifier;
            else if (key is not null) return false;          // two non-modifier keys is not a shortcut
            else key = Canonical(raw);
        }

        if (key is null) return false;

        shortcut = new Shortcut(modifiers, key);
        return true;
    }

    /// <summary>The text form, with modifiers always in the same order so two equal shortcuts read alike.</summary>
    public override string ToString()
    {
        if (IsEmpty) return string.Empty;

        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ShortcutModifiers.Windows)) parts.Add("Win");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    private static string Canonical(string key) => key.Length == 1
        ? key.ToUpperInvariant()
        : char.ToUpperInvariant(key[0]) + key[1..].ToLowerInvariant();
}
