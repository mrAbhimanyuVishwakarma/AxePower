using System.Windows.Controls;
using System.Windows.Input;
using AxePower.App.ViewModels;

namespace AxePower.App.Views;

public partial class TerminalView : UserControl
{
    private TerminalViewModel? _vm;

    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.OutputAppended -= ScrollToEnd;
        _vm = e.NewValue as TerminalViewModel;
        if (_vm != null) _vm.OutputAppended += ScrollToEnd;
    }

    private void ScrollToEnd()
    {
        // Already on the UI thread (VM marshals output there), so scroll directly.
        OutputScroller.ScrollToEnd();
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;

        switch (e.Key)
        {
            case Key.Enter:
                _vm.RunCommandCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                var prev = _vm.HistoryPrevious();
                if (prev != null)
                {
                    _vm.InputText = prev;
                    InputBox.CaretIndex = InputBox.Text.Length;
                }
                e.Handled = true;
                break;
            case Key.Down:
                var next = _vm.HistoryNext();
                if (next != null)
                {
                    _vm.InputText = next;
                    InputBox.CaretIndex = InputBox.Text.Length;
                }
                e.Handled = true;
                break;
        }
    }
}
