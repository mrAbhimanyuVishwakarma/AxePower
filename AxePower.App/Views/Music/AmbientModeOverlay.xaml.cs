using System.Windows;
using System.Windows.Controls;
using AxePower.App.Helpers;

namespace AxePower.App.Views.Music;

public partial class AmbientModeOverlay : UserControl
{
    public AmbientModeOverlay() => InitializeComponent();

    private void AmbientLyricsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list)
            LyricsScrollHelper.SmoothCenter(list, list.SelectedItem);
    }
}

