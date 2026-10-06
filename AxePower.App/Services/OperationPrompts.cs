using System.Collections.Generic;
using System.Windows;
using AxePower.App.ViewModels;
using AxePower.App.Windows;
using AxePower.Core.Services.Operations;

namespace AxePower.App.Services;

/// <summary>The approval step before any change to Windows, behind an interface so tests can answer it.</summary>
public interface IOperationPrompts
{
    /// <summary>Shows the plans with every planned change; true only when the user approves.</summary>
    bool Approve(IReadOnlyList<OperationRecord> plans);
}

public sealed class OperationPrompts : IOperationPrompts
{
    public bool Approve(IReadOnlyList<OperationRecord> plans)
    {
        var preview = new OperationPreviewViewModel(plans);
        var window = new OperationPreviewWindow { DataContext = preview };
        if (Application.Current?.MainWindow is { IsVisible: true } owner) window.Owner = owner;
        return window.ShowDialog() == true && preview.HasChanges;
    }
}
