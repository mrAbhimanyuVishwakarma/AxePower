using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AxePower.App.Views.Music;

public partial class HeaderPlayer : UserControl
{
    public HeaderPlayer()
    {
        InitializeComponent();
    }

    // The "More" menu opens on a normal click (and Enter/Space), not only on right-click
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is not { } menu) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
