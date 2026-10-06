using System.Windows;

namespace AxePower.App.Windows;

/// <summary>The standard confirmation. Cancel has focus when it opens, so Enter or a stray click changes nothing.</summary>
public partial class ActionConfirmationWindow : Window
{
    public ActionConfirmationWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
