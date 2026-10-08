using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using AxePower.Core.Models;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;

namespace AxePower.App.Services;

/// <summary>An action that could not be bound, and the honest reason why.</summary>
public sealed record HotkeyConflict(ShortcutAction Action, string Binding, string Reason);

/// <summary>
/// Registers AxePower's playback shortcuts with Windows so they work while another application
/// has focus.
///
/// Windows grants a hotkey to one process at a time, and refuses silently apart from a failure
/// code. Rather than pretend, every refusal is collected in <see cref="Conflicts"/> so Settings
/// can say which shortcut another application already owns.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;

    /// <summary>Keeps Windows from repeating a held-down shortcut.</summary>
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly Dictionary<int, ShortcutAction> _registered = new();
    private readonly List<HotkeyConflict> _conflicts = new();

    private HwndSource? _source;
    private IntPtr _handle;
    private int _nextId = 0xA1E0;
    private bool _disposed;

    public HotkeyService(SettingsService settings, LogService log)
    {
        _settings = settings;
        _log = log;
    }

    /// <summary>Raised on the UI thread when a registered shortcut is pressed.</summary>
    public event EventHandler<ShortcutAction>? Pressed;

    /// <summary>Raised after every re-registration, so Settings can refresh what it shows.</summary>
    public event EventHandler? ConflictsChanged;

    /// <summary>Shortcuts Windows would not grant, most likely taken by another application.</summary>
    public IReadOnlyList<HotkeyConflict> Conflicts => _conflicts;

    public bool IsAttached => _source is not null;

    /// <summary>
    /// Starts listening, using the main window's message loop. Called once the window has a
    /// handle; before that there is nothing to register against.
    /// </summary>
    public void Attach(IntPtr windowHandle)
    {
        if (_disposed || _source is not null || windowHandle == IntPtr.Zero) return;

        _handle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        if (_source is null)
        {
            _log.LogWarning("[Music] Shortcuts could not attach to the main window.");
            return;
        }

        _source.AddHook(OnMessage);
        _settings.OnSettingsChanged += OnSettingsChanged;
        Apply();
    }

    /// <summary>Drops every registration and puts back the ones the settings ask for.</summary>
    public void Apply()
    {
        if (_source is null) return;

        UnregisterAll();
        _conflicts.Clear();

        var shortcuts = _settings.GetSettings().Music.Shortcuts ?? new ShortcutSettings();
        if (!shortcuts.GlobalHotkeysEnabled)
        {
            ConflictsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        foreach (ShortcutAction action in Enum.GetValues<ShortcutAction>())
        {
            string binding = shortcuts.Resolve(action);
            if (!Shortcut.TryParse(binding, out var shortcut)) continue;

            if (!TryKeyCode(shortcut.Key, out uint virtualKey))
            {
                _conflicts.Add(new HotkeyConflict(action, binding, $"'{shortcut.Key}' is not a key AxePower can register."));
                continue;
            }

            int id = _nextId++;
            if (RegisterHotKey(_handle, id, (uint)shortcut.Modifiers | ModNoRepeat, virtualKey))
            {
                _registered[id] = action;
            }
            else
            {
                _conflicts.Add(new HotkeyConflict(action, binding, "Another application is already using this shortcut."));
            }
        }

        if (_conflicts.Count > 0)
            _log.LogWarning($"[Music] {_conflicts.Count} shortcut(s) could not be registered; see Settings for which.");

        ConflictsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => Apply();

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || !_registered.TryGetValue(wParam.ToInt32(), out var action)) return IntPtr.Zero;

        handled = true;
        try
        {
            Pressed?.Invoke(this, action);
        }
        catch (Exception ex)
        {
            // A throw here would travel into the window procedure, so it stops at this boundary.
            _log.LogWarning($"[Music] Shortcut '{action}' failed: {ex.Message}");
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The Win32 virtual-key code for a key name. Built from WPF's own <see cref="Key"/>
    /// parsing so the names accepted here are the names WPF prints when recording a shortcut.
    /// </summary>
    internal static bool TryKeyCode(string keyName, out uint virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(keyName)) return false;

        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out var key) || key == Key.None) return false;

        int code = KeyInterop.VirtualKeyFromKey(key);
        if (code <= 0) return false;

        virtualKey = (uint)code;
        return true;
    }

    private void UnregisterAll()
    {
        foreach (int id in _registered.Keys.ToList())
        {
            try { UnregisterHotKey(_handle, id); } catch { /* Best-effort: the hotkey is released anyway when the window closes. */ }
        }
        _registered.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _settings.OnSettingsChanged -= OnSettingsChanged;
        UnregisterAll();
        _source?.RemoveHook(OnMessage);
        _source = null;
    }
}
