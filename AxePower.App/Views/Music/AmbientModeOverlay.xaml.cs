using System.Windows;
using System.Windows.Controls;
using AxePower.App.Helpers;

namespace AxePower.App.Views.Music;

public partial class AmbientModeOverlay : UserControl
{
    public AmbientModeOverlay()
    {
        InitializeComponent();

        // The window opens mid-song and is then maximized: centre the line being sung straight
        // away rather than waiting for the next one to start.
        Loaded += (_, _) => CenterActiveLine();
        AmbientLyricsList.SizeChanged += (_, _) => CenterActiveLine();
        AmbientLyricsList.IsVisibleChanged += (_, _) => CenterActiveLine();
    }

    private void CenterActiveLine()
    {
        if (AmbientLyricsList.IsVisible && AmbientLyricsList.SelectedItem is { } line)
            LyricsScrollHelper.SmoothCenter(AmbientLyricsList, line);
    }

    private void AmbientLyricsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list)
            LyricsScrollHelper.SmoothCenter(list, list.SelectedItem);
    }
}

