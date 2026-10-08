using System.Windows;
using System.Windows.Controls;

namespace AxePower.App.Views.Shell;

/// <summary>The tool instructions and help window: tool list, step-by-step guide, tips and footer actions.</summary>
public partial class ToolHelpOverlay : UserControl
{
    public ToolHelpOverlay()
    {
        InitializeComponent();
    }

    /// <summary>Starts the open guide from the top (MainWindow calls this when another tool is picked).</summary>
    public void ScrollGuideToTop() => ToolHelpScroller.ScrollToTop();
}
