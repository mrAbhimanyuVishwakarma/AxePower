using System.Windows;
using AxePower.App.ViewModels;
using AxePower.App.Windows;
using AxePower.Core.Services.Operations;

namespace AxePower.App.Services;

/// <summary>The standard confirmation for destructive or system-wide actions (Prompt 27), behind an interface for tests.</summary>
public interface IConfirmationPrompts
{
    /// <summary>True only when the user pressed the named action button (and ticked the box for high risk).</summary>
    bool Confirm(ActionConfirmation confirmation);
}

public sealed class ConfirmationPrompts : IConfirmationPrompts
{
    public bool Confirm(ActionConfirmation confirmation)
    {
        var vm = new ActionConfirmationViewModel(confirmation);
        var window = new ActionConfirmationWindow { DataContext = vm };
        if (Application.Current?.MainWindow is { IsVisible: true } owner) window.Owner = owner;
        return window.ShowDialog() == true && vm.CanConfirm;
    }
}
