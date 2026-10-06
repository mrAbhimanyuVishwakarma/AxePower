using System.Windows;
using AxePower.App.ViewModels;
using AxePower.App.Windows;
using AxePower.Core.Services.Diagnostics;

namespace AxePower.App.Services;

/// <summary>The questions and windows of Settings › Diagnostics, behind an interface so tests can answer them.</summary>
public interface IDiagnosticsPrompts
{
    /// <summary>True when the user confirms deleting every stored diagnostics file.</summary>
    bool ConfirmDeleteAll(DiagnosticsSummary summary);

    /// <summary>Where to save a bundle, or null when the user cancels. Nothing is saved without this.</summary>
    string? ChooseBundlePath(string suggestedName);

    /// <summary>Shows the inspect-and-export window until the user closes it.</summary>
    void ShowBundle(DiagnosticsBundleViewModel bundle);
}

public sealed class DiagnosticsPrompts : IDiagnosticsPrompts
{
    private const string Title = "AxePower diagnostics";

    public bool ConfirmDeleteAll(DiagnosticsSummary summary) =>
        MessageBox.Show(
            $"Delete all {summary.Files} diagnostics file(s) ({DiagnosticsViewModel.FormatBytes(summary.Bytes)}) from this PC?\n\n" +
            "This cannot be undone. AxePower starts recording again with the next event.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public string? ChooseBundlePath(string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save diagnostic bundle",
            FileName = suggestedName,
            DefaultExt = ".zip",
            Filter = "Zip archive (*.zip)|*.zip",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public void ShowBundle(DiagnosticsBundleViewModel bundle)
    {
        var window = new DiagnosticsWindow { DataContext = bundle };
        if (Application.Current?.MainWindow is { IsVisible: true } owner) window.Owner = owner;
        window.ShowDialog();
    }
}
