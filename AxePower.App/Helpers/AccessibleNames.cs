using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace AxePower.App.Helpers;

/// <summary>
/// Gives every interactive control a screen-reader name (Prompt 28). WPF names a button only when
/// its content is a plain string, so the app's icon-and-label buttons, the toggle switches beside
/// their label and the unlabelled dropdowns were all read as just "button" or "check box".
/// <para>
/// When a control has no name of its own, it gets the one a sighted user sees, in this order:
/// its visible text (icon-font glyphs skipped); for a toggle, dropdown, text box or slider, the
/// label laid out before it in the same row; otherwise its tooltip. The name is a binding to that
/// text, so it stays right when the text changes. A name set in XAML always wins.
/// </para>
/// </summary>
public static class AccessibleNames
{
    /// <summary>Marks names this class inferred, so they are re-inferred when the control loads again.</summary>
    private static readonly DependencyProperty IsInferredProperty =
        DependencyProperty.RegisterAttached("IsInferred", typeof(bool), typeof(AccessibleNames), new PropertyMetadata(false));

    private static readonly DependencyProperty IsWatchedProperty =
        DependencyProperty.RegisterAttached("IsWatched", typeof(bool), typeof(AccessibleNames), new PropertyMetadata(false));

    /// <summary>
    /// Set True on a window (every AxePower window does). The value is inherited, so WPF calls
    /// <see cref="OnEnabledChanged"/> for each element as it joins the window's tree, including
    /// list items and template content. A class-wide Loaded handler is not enough: WPF only
    /// broadcasts Loaded into subtrees that already have Loaded handlers of their own.
    /// </summary>
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AccessibleNames),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || d is not Control control || !IsInteractive(control)) return;
        if ((bool)control.GetValue(IsWatchedProperty)) return;
        control.SetValue(IsWatchedProperty, true);
        // Instance handlers: these fire. Visibility matters because a collapsed control has no
        // template yet, so its text can only be found once it is shown.
        control.Loaded += (_, _) => Apply(control);
        control.IsVisibleChanged += (_, args) => { if (args.NewValue is true) Apply(control); };
        if (control.IsLoaded) Apply(control);
    }

    /// <summary>Names <paramref name="control"/> if it is interactive and has no name yet.</summary>
    public static void Apply(Control control)
    {
        if (!IsInteractive(control)) return;
        bool inferred = (bool)control.GetValue(IsInferredProperty);
        if (!inferred && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))) return;
        if (!inferred && control is ContentControl { Content: string text } && !IsGlyphOnly(text)) return; // WPF reads plain text itself

        var source = VisibleText(control) ?? (WantsLabel(control) ? LabelBefore(control) : null);
        if (source is not null)
        {
            BindingOperations.SetBinding(control, AutomationProperties.NameProperty,
                new Binding(nameof(TextBlock.Text)) { Source = source, Mode = BindingMode.OneWay });
        }
        else if (control.ToolTip is not null)
        {
            BindingOperations.SetBinding(control, AutomationProperties.NameProperty,
                new Binding(nameof(FrameworkElement.ToolTip)) { Source = control, Mode = BindingMode.OneWay, Converter = ToolTipText.Instance });
        }
        else
        {
            return;
        }
        control.SetValue(IsInferredProperty, true);
    }

    /// <summary>True for text made only of Segoe MDL2 / Fluent icon glyphs (Private Use Area) and spaces.</summary>
    public static bool IsGlyphOnly(string text) =>
        text.Length > 0 && text.All(c => (c >= '' && c <= '') || char.IsWhiteSpace(c));

    private static bool IsInteractive(Control control) =>
        control is ButtonBase or Selector or TextBoxBase or PasswordBox or RangeBase
        && control is not ScrollBar
        // Parts of another control's template (a ComboBox's drop-down button) are named by their owner.
        && control.TemplatedParent is not Control;

    /// <summary>Controls whose own content is a value, not a label: they are named by the text beside them.</summary>
    private static bool WantsLabel(Control control) =>
        control is ToggleButton or Selector or TextBoxBase or PasswordBox or RangeBase;

    /// <summary>The first readable text inside the control (not for value controls, whose text is the value).</summary>
    private static TextBlock? VisibleText(Control control)
    {
        if (control is Selector or TextBoxBase or PasswordBox or RangeBase) return null;
        return Descendants(control).OfType<TextBlock>().FirstOrDefault(IsReadable);
    }

    /// <summary>
    /// The label laid out before a toggle or input in the same row: the first readable text in the
    /// children of its nearest panels that come before it (up to three levels), e.g. the title in
    /// Settings' "title and description on the left, switch on the right" rows.
    /// </summary>
    private static TextBlock? LabelBefore(Control control)
    {
        DependencyObject current = control;
        for (int level = 0; level < 4; level++)
        {
            var parent = VisualTreeHelper.GetParent(current);
            if (parent is null) break;
            if (parent is Panel panel)
            {
                foreach (UIElement sibling in panel.Children)
                {
                    if (ReferenceEquals(sibling, current)) break;
                    if (sibling is TextBlock own && IsReadable(own)) return own;
                    if (sibling is Control) continue; // another control's text belongs to that control
                    var text = Descendants(sibling).OfType<TextBlock>().FirstOrDefault(IsReadable);
                    if (text is not null) return text;
                }
            }
            current = parent;
        }
        return null;
    }

    /// <summary>Visible text with at least one letter or digit: not an icon glyph, not a lone "✓" or "•".</summary>
    private static bool IsReadable(TextBlock text) =>
        text.Visibility == Visibility.Visible && text.Text.Any(char.IsLetterOrDigit) && !IsGlyphOnly(text.Text);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    /// <summary>
    /// A tooltip's readable text: the string itself, a ToolTip's string content, or the first
    /// readable text inside richer content (e.g. a credit card's title). Never null: a null name
    /// is not a valid value for <see cref="AutomationProperties.NameProperty"/>.
    /// </summary>
    private sealed class ToolTipText : IValueConverter
    {
        public static readonly ToolTipText Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
        {
            string s => s,
            ToolTip { Content: string s } => s,
            ToolTip { Content: DependencyObject content } => FirstText(content),
            DependencyObject element => FirstText(element),
            _ => string.Empty,
        };

        /// <summary>Tooltip content is not in the visual tree until shown, so the logical tree is searched.</summary>
        private static string FirstText(DependencyObject root)
        {
            if (root is TextBlock own && own.Text.Any(char.IsLetterOrDigit) && !IsGlyphOnly(own.Text)) return own.Text;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                var text = FirstText(child);
                if (text.Length > 0) return text;
            }
            return string.Empty;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
