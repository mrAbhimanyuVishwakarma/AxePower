using System;
using System.Windows.Controls;
using System.Windows.Threading;
using AxePower.App.ViewModels;

namespace AxePower.App.Views;

public partial class ToolsView : UserControl
{
    // Keeps Running / Installed current while the page is open, e.g. after the user closes an app
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(3) };

    public ToolsView()
    {
        InitializeComponent();
        _refresh.Tick += (_, _) => (DataContext as ToolsViewModel)?.RefreshRunning();
        Loaded += (_, _) =>
        {
            (DataContext as ToolsViewModel)?.Refresh();
            _refresh.Start();
        };
        Unloaded += (_, _) => _refresh.Stop();
    }
}
