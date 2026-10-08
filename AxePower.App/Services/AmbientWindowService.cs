using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AxePower.App.ViewModels.Music;
using AxePower.App.Views.Music;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.App.Services;

/// <summary>
/// Shows Ambient Mode as its own borderless window covering the whole monitor AxePower is on,
/// so it works from any page, and opens it by itself after the PC sits idle while music plays
/// when Music Settings asks for that.
/// </summary>
public sealed class AmbientWindowService : IDisposable
{
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>How far the pointer must travel before a screensaver-style Ambient closes.</summary>
    private const double MouseWakeDistance = 8;

    private readonly AmbientModeViewModel _ambient;
    private readonly NowPlayingViewModel _nowPlaying;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _idleTimer;
    private Window? _window;
    private Point? _mouseOrigin;
    private bool _autoEnter;
    private int _idleMinutes;
    private bool _started;

    public AmbientWindowService(AmbientModeViewModel ambient, NowPlayingViewModel nowPlaying, SettingsService settings)
    {
        _ambient = ambient;
        _nowPlaying = nowPlaying;
        _settings = settings;
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = IdleCheckInterval };
        _idleTimer.Tick += (_, _) => CheckIdle();
    }

    /// <summary>Call once on the UI thread, after the main window exists.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        ApplySettings(_settings.GetSettings());
        _settings.OnSettingsChanged += OnSettingsChanged;
        _ambient.PropertyChanged += OnAmbientChanged;
        _idleTimer.Start();
        if (_ambient.IsOpen) Show();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        Application.Current?.Dispatcher.BeginInvoke(() => ApplySettings(settings));

    private void ApplySettings(AppSettings settings)
    {
        _autoEnter = settings.Music.AmbientAutoEnter;
        _idleMinutes = Math.Clamp(settings.Music.AmbientIdleMinutes, 1, 60);
    }

    private void OnAmbientChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AmbientModeViewModel.IsOpen)) return;
        if (_ambient.IsOpen) Show();
        else Hide();
    }

    // ── Screensaver ─────────────────────────────────────────────────────────

    private void CheckIdle()
    {
        if (!_autoEnter || _ambient.IsOpen || !_nowPlaying.IsPlaying) return;
        if (IdleTime() < TimeSpan.FromMinutes(_idleMinutes)) return;

        // A game, a full-screen video or a presentation owns the screen: never cover it
        if (SomethingOwnsTheScreen()) return;

        _ambient.OpenAsScreensaver();
    }

    private static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    private static bool SomethingOwnsTheScreen()
    {
        if (SHQueryUserNotificationState(out var state) != 0) return false;
        return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
    }

    // ── Window ──────────────────────────────────────────────────────────────

    private void Show()
    {
        if (_window != null)
        {
            _window.Activate();
            return;
        }

        var owner = Application.Current?.MainWindow;
        var window = new Window
        {
            Title = "Ambient Mode",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Background = Brushes.Black,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new AmbientModeOverlay { DataContext = _ambient }
        };
        AxePower.App.Helpers.AccessibleNames.SetEnabled(window, true);

        // Start on the monitor AxePower is on; maximizing a borderless window then covers all
        // of that monitor, taskbar included. A minimized main window has no useful position.
        if (owner is { WindowState: not WindowState.Minimized, IsVisible: true })
        {
            window.Left = owner.Left + 40;
            window.Top = owner.Top + 40;
        }
        window.Width = 800;
        window.Height = 600;
        window.SourceInitialized += (_, _) => window.WindowState = WindowState.Maximized;

        window.PreviewKeyDown += OnWindowKeyDown;
        window.PreviewMouseDown += OnWindowMouseDown;
        window.PreviewMouseMove += OnWindowMouseMove;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_window, window)) return;
            _window = null;
            _ambient.IsOpen = false;
        };

        _mouseOrigin = null;
        _window = window;
        window.Show();
        window.Activate();
    }

    private void Hide()
    {
        var window = _window;
        _window = null;
        window?.Close();

        // Opened by hand from AxePower: go back to it. As a screensaver it may have covered
        // another application, which should keep the focus.
        bool wasScreensaver = _ambient.IsAutoOpened;
        _ambient.IsAutoOpened = false;
        if (window != null && !wasScreensaver) Application.Current?.MainWindow?.Activate();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_ambient.IsAutoOpened || e.Key is Key.Escape or Key.F11)
        {
            _ambient.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.ButtonBase)
        {
            _nowPlaying.PlayPauseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_ambient.IsAutoOpened) return;
        _ambient.CloseCommand.Execute(null);
        e.Handled = true;
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!_ambient.IsAutoOpened || sender is not Window window) return;

        // WPF reports a move when the window appears under a still pointer; only real travel counts
        var point = e.GetPosition(window);
        if (_mouseOrigin is not { } origin)
        {
            _mouseOrigin = point;
            return;
        }

        if ((point - origin).Length >= MouseWakeDistance)
            _ambient.CloseCommand.Execute(null);
    }

    public void Dispose()
    {
        _idleTimer.Stop();
        _settings.OnSettingsChanged -= OnSettingsChanged;
        _ambient.PropertyChanged -= OnAmbientChanged;
        _window?.Close();
        _window = null;
    }

    // ── Native ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);
}
