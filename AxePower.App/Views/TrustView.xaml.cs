using System.Windows.Controls;
using AxePower.App.ViewModels;

namespace AxePower.App.Views;

/// <summary>Trust & Permissions. Live refresh runs only while the page is on screen.</summary>
public partial class TrustView : UserControl
{
    public TrustView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as TrustViewModel)?.Activate();
        Unloaded += (_, _) => (DataContext as TrustViewModel)?.Deactivate();
    }
}
