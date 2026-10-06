using System.Windows;

namespace AxePower.App.Windows;

/// <summary>The preview of a change to Windows. Closing it any way but Approve changes nothing.</summary>
public partial class OperationPreviewWindow : Window
{
    public OperationPreviewWindow()
    {
        InitializeComponent();
    }

    private void Approve_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
