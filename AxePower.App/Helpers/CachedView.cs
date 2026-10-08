using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace AxePower.App.Helpers;

/// <summary>
/// Shows one instance of <see cref="ViewType"/> per view model and reuses it on every visit
/// (Prompt 29). A page's DataTemplate otherwise builds the whole page again each time it is
/// shown: for Game Mode, ~1,000 lines of XAML and 134 ms on every switch. The reused page keeps
/// its scroll position and expanded sections between visits.
/// <code>&lt;helpers:CachedView ViewType="{x:Type views:GameModeView}"/&gt;</code>
/// </summary>
public sealed class CachedView : Decorator
{
    private static readonly ConditionalWeakTable<object, FrameworkElement> Views = new();

    public Type? ViewType { get; set; }

    public CachedView()
    {
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach(); // a host can be unloaded and loaded again while its page settles
        Unloaded += (_, _) => Child = null; // hand the page back, so the next host can take it
    }

    private void Attach()
    {
        if (DataContext is null || ViewType is null)
        {
            Child = null;
            return;
        }

        var view = Views.GetValue(DataContext, _ => (FrameworkElement)Activator.CreateInstance(ViewType)!);
        if (ReferenceEquals(Child, view)) return;
        // The previous host may not have unloaded yet: take the page from it
        if (view.Parent is Decorator previous) previous.Child = null;
        Child = view;
    }

    /// <summary>For tests: whether a page has been built for this view model.</summary>
    public static bool HasView(object viewModel) => Views.TryGetValue(viewModel, out _);
}
