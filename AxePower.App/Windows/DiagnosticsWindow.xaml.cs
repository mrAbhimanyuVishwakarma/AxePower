using System.Windows;

namespace AxePower.App.Windows;

/// <summary>Inspect and export diagnostics. All behaviour is in <see cref="ViewModels.DiagnosticsBundleViewModel"/>.</summary>
public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
