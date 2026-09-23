using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.Services;

/// <summary>
/// Previous / Play-Pause / Next buttons in the taskbar thumbnail, plus playback progress on
/// the taskbar icon. They only show while the window is on the taskbar, not while it is hidden
/// in the tray — that is how Windows thumbnail toolbars work.
/// </summary>
public sealed class TaskbarMediaIntegration : IPlayerIntegration
{
    private const string GlyphPrevious = "";
    private const string GlyphPlay = "";
    private const string GlyphPause = "";
    private const string GlyphNext = "";

    private readonly IMusicPlayerService _player;
    private readonly MusicDispatcher _dispatcher;
    private readonly ThemeService _theme;
    private readonly SettingsService _settings;

    private TaskbarItemInfo? _taskbar;
    private ThumbButtonInfo? _previous, _playPause, _next;
    private PlayerState _state = PlayerState.Empty;
    private DateTime _lastProgressUtc;

    public TaskbarMediaIntegration(IMusicPlayerService player, MusicDispatcher dispatcher, ThemeService theme, SettingsService settings)
    {
        _player = player;
        _dispatcher = dispatcher;
        _theme = theme;
        _settings = settings;
    }

    public string Name => "Taskbar buttons";

    public Task InitializeAsync(IntPtr mainWindowHandle, CancellationToken ct)
    {
        _dispatcher.Post(() =>
        {
            var window = Application.Current?.MainWindow;
            if (window == null) return;

            _taskbar = window.TaskbarItemInfo ??= new TaskbarItemInfo();
            _previous = new ThumbButtonInfo { Description = "Previous", Command = new AsyncRelayCommand(_player.PreviousAsync), IsEnabled = false };
            _playPause = new ThumbButtonInfo { Description = "Play", Command = new AsyncRelayCommand(_player.TogglePlayPauseAsync), IsEnabled = false };
            _next = new ThumbButtonInfo { Description = "Next", Command = new AsyncRelayCommand(_player.NextAsync), IsEnabled = false };
            _taskbar.ThumbButtonInfos.Add(_previous);
            _taskbar.ThumbButtonInfos.Add(_playPause);
            _taskbar.ThumbButtonInfos.Add(_next);

            ApplyGlyphs();
            _theme.ThemeChanged += _ => _dispatcher.Post(ApplyGlyphs);
            Update();
        });
        return Task.CompletedTask;
    }

    public void OnStateChanged(PlayerState state)
    {
        _state = state;
        _dispatcher.Post(Update);
    }

    public void OnPositionChanged(TimeSpan position)
    {
        if (DateTime.UtcNow - _lastProgressUtc < TimeSpan.FromSeconds(1)) return;
        _lastProgressUtc = DateTime.UtcNow;
        _dispatcher.Post(() => UpdateProgress(position));
    }

    private void Update()
    {
        if (_taskbar == null || _previous == null || _playPause == null || _next == null) return;

        var state = _state;
        bool hasItem = state.Current != null;
        bool canWrap = state.Repeat == RepeatMode.All && state.Queue.Count > 1;

        _previous.IsEnabled = hasItem;
        _next.IsEnabled = hasItem && (state.Index < state.Queue.Count - 1 || canWrap);
        _playPause.IsEnabled = hasItem;
        _playPause.Description = state.Playing ? "Pause" : "Play";
        _playPause.ImageSource = Glyph(state.Playing ? GlyphPause : GlyphPlay);

        UpdateProgress(state.Position);
    }

    private void UpdateProgress(TimeSpan position)
    {
        if (_taskbar == null) return;

        var state = _state;
        if (state.Current == null || state.Duration <= TimeSpan.Zero)
        {
            _taskbar.ProgressState = TaskbarItemProgressState.None;
            return;
        }

        _taskbar.ProgressState = state.Playing ? TaskbarItemProgressState.Normal
            : position > TimeSpan.Zero ? TaskbarItemProgressState.Paused
            : TaskbarItemProgressState.None;
        _taskbar.ProgressValue = Math.Clamp(position.TotalSeconds / state.Duration.TotalSeconds, 0, 1);
    }

    private void ApplyGlyphs()
    {
        if (_previous == null || _next == null || _playPause == null) return;
        _previous.ImageSource = Glyph(GlyphPrevious);
        _next.ImageSource = Glyph(GlyphNext);
        _playPause.ImageSource = Glyph(_state.Playing ? GlyphPause : GlyphPlay);
    }

    /// <summary>A Segoe MDL2 glyph as an image, dark on a light taskbar and light on a dark one.</summary>
    private ImageSource Glyph(string glyph)
    {
        bool light = _theme.IsTaskbarLight(_settings.GetSettings().Theme);
        var brush = light ? Brushes.Black : Brushes.White;

        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe MDL2 Assets"), 16, brush, 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));

        var drawing = new DrawingGroup();
        // A transparent 16×16 frame keeps every glyph the same size and centred
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        drawing.Children.Add(new GeometryDrawing(brush, null, geometry));

        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        _dispatcher.Post(() =>
        {
            if (_taskbar == null) return;
            _taskbar.ThumbButtonInfos.Clear();
            _taskbar.ProgressState = TaskbarItemProgressState.None;
        });
    }
}
