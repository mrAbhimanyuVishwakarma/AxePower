using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;

namespace AxePower.App.Views.SoftwareHub;

/// <summary>Windows, app, and device update review and results.</summary>
public partial class UpdatersTab : UserControl
{
    public UpdatersTab()
    {
        InitializeComponent();
    }

    private void AnnounceStatus(object sender, DataTransferEventArgs e)
    {
        if (sender is TextBlock { IsVisible: true } status && !string.IsNullOrEmpty(status.Text)
            && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            UIElementAutomationPeer.CreatePeerForElement(status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
