using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using AxePower.App.Services;
using AxePower.Core.Models.Music;
using AxePower.Core.Services.Music;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels.Music;

/// <summary>Presentation state for the expanded, cover-colour-driven theater player.</summary>
public partial class AmbientModeViewModel : ObservableObject, IDisposable
{
    private readonly NowPlayingViewModel _nowPlaying;
    private readonly IPaletteExtractor _paletteExtractor;
    private readonly MusicDispatcher _dispatcher;
    private CancellationTokenSource? _paletteCts;

    public AmbientModeViewModel(
        NowPlayingViewModel nowPlaying,
        LyricsViewModel lyrics,
        IPaletteExtractor paletteExtractor,
        MusicDispatcher dispatcher)
    {
        _nowPlaying = nowPlaying;
        Lyrics = lyrics;
        _paletteExtractor = paletteExtractor;
        _dispatcher = dispatcher;
        _nowPlaying.PropertyChanged += OnNowPlayingChanged;
        ApplyPalette(AlbumPalette.Default);
        BeginPaletteExtraction(_nowPlaying.CoverPath);
    }

    public NowPlayingViewModel NowPlaying => _nowPlaying;
    public LyricsViewModel Lyrics { get; }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private Color _primaryColor;

    [ObservableProperty]
    private Color _secondaryColor;

    [ObservableProperty]
    private Color _backgroundGlowColor;

    [RelayCommand]
    private void Toggle()
    {
        if (_nowPlaying.HasCurrent) IsOpen = !IsOpen;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    private void OnNowPlayingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.CoverPath))
            BeginPaletteExtraction(_nowPlaying.CoverPath);
        else if (e.PropertyName == nameof(NowPlayingViewModel.HasCurrent) && !_nowPlaying.HasCurrent)
            IsOpen = false;
    }

    private void BeginPaletteExtraction(string? path)
    {
        _paletteCts?.Cancel();
        _paletteCts?.Dispose();
        var cts = _paletteCts = new CancellationTokenSource();

        if (string.IsNullOrWhiteSpace(path))
        {
            ApplyPalette(AlbumPalette.Default);
            return;
        }

        _ = ExtractPaletteAsync(path, cts);
    }

    private async Task ExtractPaletteAsync(string path, CancellationTokenSource cts)
    {
        try
        {
            var palette = await _paletteExtractor.ExtractPaletteAsync(path, cts.Token);
            if (!cts.IsCancellationRequested) _dispatcher.Post(() => ApplyPalette(palette));
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (!cts.IsCancellationRequested) _dispatcher.Post(() => ApplyPalette(AlbumPalette.Default));
        }
    }

    private void ApplyPalette(AlbumPalette palette)
    {
        PrimaryColor = ToColor(palette.Primary);
        SecondaryColor = ToColor(palette.Secondary);
        BackgroundGlowColor = ToColor(palette.BackgroundGlow);
    }

    private static Color ToColor(RgbColor color) => Color.FromRgb(color.Red, color.Green, color.Blue);

    public void Dispose()
    {
        _paletteCts?.Cancel();
        _paletteCts?.Dispose();
        _nowPlaying.PropertyChanged -= OnNowPlayingChanged;
    }
}
