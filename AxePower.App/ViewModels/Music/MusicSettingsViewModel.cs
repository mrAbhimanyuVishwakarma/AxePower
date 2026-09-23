using System;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxePower.App.ViewModels.Music;

/// <summary>
/// The Music group on the Settings page. Each change reads the latest settings, edits only the
/// music section and saves, so it never overwrites anything another screen changed.
/// </summary>
public partial class MusicSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private bool _loading;

    public MusicSettingsViewModel(SettingsService settings)
    {
        _settings = settings;
        Load(settings.GetSettings().Music);
        _settings.OnSettingsChanged += (_, s) => System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() => Load(s.Music));
    }

    [ObservableProperty] private bool _allowVolumeBoost;
    [ObservableProperty] private bool _resumePlaybackOnStartup;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayPlaylistFolder))]
    private string? _playlistFolder;

    public string DisplayPlaylistFolder =>
        string.IsNullOrWhiteSpace(PlaylistFolder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
            : PlaylistFolder;

    partial void OnAllowVolumeBoostChanged(bool value) => Save(m => m.AllowVolumeBoost = value);
    partial void OnResumePlaybackOnStartupChanged(bool value) => Save(m => m.ResumePlaybackOnStartup = value);
    partial void OnPlaylistFolderChanged(string? value) => Save(m => m.PlaylistFolder = value);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void BrowsePlaylistFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Music & Playlist Folder",
            InitialDirectory = DisplayPlaylistFolder
        };
        if (dlg.ShowDialog() == true)
        {
            PlaylistFolder = dlg.FolderName;
        }
    }

    private void Load(MusicSettings music)
    {
        _loading = true;
        try
        {
            AllowVolumeBoost = music.AllowVolumeBoost;
            ResumePlaybackOnStartup = music.ResumePlaybackOnStartup;
            PlaylistFolder = music.PlaylistFolder;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save(Action<MusicSettings> change)
    {
        if (_loading) return;

        var settings = _settings.GetSettings();
        change(settings.Music);
        _settings.SaveSettings(settings);
    }
}
