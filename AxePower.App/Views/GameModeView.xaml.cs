using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AxePower.App.Views;

public partial class GameModeView : UserControl
{
    public GameModeView()
    {
        InitializeComponent();
    }

    private void LibraryScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer scv)
        {
            if ((e.Delta < 0 && scv.VerticalOffset >= scv.ScrollableHeight) ||
                (e.Delta > 0 && scv.VerticalOffset <= 0) ||
                scv.ScrollableHeight <= 0)
            {
                e.Handled = true;
                var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = sender
                };
                var parent = (UIElement?)scv.Parent;
                parent?.RaiseEvent(eventArg);
            }
        }
    }
}
