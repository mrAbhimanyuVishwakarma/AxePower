using System;
using System.Windows;
using System.Windows.Threading;

namespace AxePower.App.Windows;

/// <summary>
/// A caution dialog that must be confirmed. With a countdown, doing nothing counts as Cancel
/// when it reaches zero, so a change nobody agreed to is never applied.
/// </summary>
public partial class CountdownConfirmWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _secondsLeft;

    public CountdownConfirmWindow(string title, string message, string confirmText, string cancelText, int countdownSeconds)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;

        _secondsLeft = countdownSeconds;
        if (countdownSeconds > 0)
        {
            UpdateCountdown();
            _timer.Tick += (_, _) =>
            {
                _secondsLeft--;
                if (_secondsLeft <= 0) Finish(false);
                else UpdateCountdown();
            };
            Loaded += (_, _) => _timer.Start();
        }
        else
        {
            CountdownText.Visibility = Visibility.Collapsed;
        }

        Closed += (_, _) => _timer.Stop();
    }

    private void UpdateCountdown() => CountdownText.Text = $"Cancels automatically in {_secondsLeft} s if you do nothing.";

    private void OnConfirm(object sender, RoutedEventArgs e) => Finish(true);

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(false);

    private void Finish(bool confirmed)
    {
        _timer.Stop();
        DialogResult = confirmed;
    }
}
