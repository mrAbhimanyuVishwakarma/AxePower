using System.Windows;
using System.Windows.Controls;

namespace AxePower.App.Views.Shell;

/// <summary>The Steam-style patch notes dialog: every release post with its reaction bar.</summary>
public partial class PatchNotesDialog : UserControl
{
    public PatchNotesDialog()
    {
        InitializeComponent();
    }

    // The rail's arrows scroll the feed a few lines at a time (view behaviour only).
    private void PatchNotesScrollUp_Click(object sender, RoutedEventArgs e)
    {
        PatchNotesScrollViewer?.LineUp();
        PatchNotesScrollViewer?.LineUp();
        PatchNotesScrollViewer?.LineUp();
        PatchNotesScrollViewer?.LineUp();
    }

    private void PatchNotesScrollDown_Click(object sender, RoutedEventArgs e)
    {
        PatchNotesScrollViewer?.LineDown();
        PatchNotesScrollViewer?.LineDown();
        PatchNotesScrollViewer?.LineDown();
        PatchNotesScrollViewer?.LineDown();
    }
}
