using System;
using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxePower.App.ViewModels.Music;

/// <summary>
/// A library item (playlist, folder, or collection card) displayed in the library grid.
/// </summary>
public partial class LibraryItemViewModel : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string? Path { get; set; }
    public string IconGlyph { get; init; } = "\uE8D6";
    public Brush GradientBrush { get; init; } = Brushes.Transparent;

    [ObservableProperty]
    private ImageSource? _coverImage;

    public bool HasCover => CoverImage != null;

    partial void OnCoverImageChanged(ImageSource? value) => OnPropertyChanged(nameof(HasCover));

    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<SongItemViewModel> Songs { get; init; } = new();

    public int SongCount => Songs.Count;
}
