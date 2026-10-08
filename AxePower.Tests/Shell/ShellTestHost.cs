using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AxePower.App.Services;
using AxePower.Core.Services;
using AxePower.Core.Services.Skins;

namespace AxePower.Tests.Shell;

/// <summary>
/// Helpers for tests that lay real views out off-screen: apply a skin, collect binding errors,
/// render to PNG, and list the keyboard-reachable controls. Use from the WPF fixture's thread.
/// </summary>
internal static class ShellTestHost
{
    private static readonly SkinCatalog Catalog = new(new LogService());

    public static IReadOnlyList<string> AllSkinIds => Catalog.Skins.Select(s => s.Compiled.Id).ToList();

    /// <summary>
    /// Short pack URIs ("/Resources/Icons/x.png") resolve against the resource assembly, which in
    /// the test host would otherwise be the test runner. Point it at the app once.
    /// </summary>
    public static void UseAppResources()
    {
        var app = typeof(AxePower.App.App).Assembly;
        if (Application.ResourceAssembly == app) return;
        // The public setter refuses once WPF has defaulted it to the test host, so set the field.
        var field = typeof(Application).GetField("_resourceAssembly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("WPF's Application._resourceAssembly field was not found.");
        field.SetValue(null, app);
        // The pack-URI resolver caches a resource manager for the old assembly; drop it so it is rebuilt.
        typeof(Application).Assembly.GetType("MS.Internal.AppModel.ResourceContainer")?
            .GetField("_applicationResourceManagerWrapper", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?
            .SetValue(null, null);
    }

    /// <summary>
    /// Merges a skin's resources into the application, as ThemeService does, and returns a handle
    /// that removes them. Images are loaded with the full pack URI because the short form used by
    /// <see cref="SkinResourceFactory"/> does not resolve in the test host.
    /// </summary>
    public static IDisposable ApplySkin(string id)
    {
        var compiled = Catalog.Resolve(id).Compiled;
        var dictionary = SkinResourceFactory.Build(compiled with { Resources = compiled.Resources.Where(r => r is not ImageResource).ToList() });
        foreach (var image in compiled.Resources.OfType<ImageResource>())
        {
            var bitmap = new BitmapImage(new Uri($"pack://application:,,,/AxePower;component{image.UriSource}", UriKind.Absolute));
            bitmap.Freeze();
            dictionary[image.Key] = bitmap;
        }
        foreach (var key in new[] { "DropDownPopupBackgroundBrush", "DropDownPopupBorderBrush", "DropDownControlBackingBrush" })
            dictionary[key] = Brushes.Gray;

        var merged = Application.Current.Resources.MergedDictionaries;
        merged.Add(dictionary);
        return new Removal(() => merged.Remove(dictionary));
    }

    private sealed class Removal : IDisposable
    {
        private Action? _undo;
        public Removal(Action undo) => _undo = undo;
        public void Dispose() { _undo?.Invoke(); _undo = null; }
    }

    /// <summary>True when the skin declares the key, including keys it sets to nothing (CardShadowEffect without a shadow).</summary>
    public static bool SkinDeclares(string skinId, string key) => Catalog.Resolve(skinId).Compiled.Find(key) != null;

    /// <summary>Collects WPF binding errors while it is alive.</summary>
    public sealed class BindingErrors : TraceListener, IDisposable
    {
        public List<string> Errors { get; } = new();

        public BindingErrors()
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }

        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) Errors.Add(message); }

        public new void Dispose()
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose();
        }
    }

    public static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    public static void SavePng(Visual visual, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// One line per visible control a keyboard (or the controller's arrow and Enter keys) can reach,
    /// in visual-tree order (WPF's default tab order), and per element carrying accessibility
    /// metadata. Positions are rounded to whole pixels relative to <paramref name="root"/>.
    /// </summary>
    public static string Inventory(FrameworkElement root)
    {
        var sb = new StringBuilder();
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            if (!element.IsVisible) continue;
            var automationName = AutomationProperties.GetName(element);
            var automationId = AutomationProperties.GetAutomationId(element);
            var helpText = AutomationProperties.GetHelpText(element);
            bool tabStop = element is Control c && c.Focusable && c.IsTabStop && c.IsEnabled;
            bool hasAutomation = automationName.Length > 0 || automationId.Length > 0 || helpText.Length > 0;
            if (!tabStop && !hasAutomation && !HasKeyboardSetting(element)) continue;

            var bounds = Bounds(element, root);
            sb.Append(tabStop ? "TAB " : "    ").Append(element.GetType().Name);
            if (element is Control control && control.TabIndex != int.MaxValue) sb.Append(" tabindex=").Append(control.TabIndex);
            if (hasAutomation) sb.Append(" name='").Append(automationName).Append("' id='").Append(automationId).Append("' help='").Append(helpText).Append('\'');
            var nav = KeyboardNavigation.GetDirectionalNavigation(element);
            if (nav != KeyboardNavigationMode.Continue) sb.Append(" dirnav=").Append(nav);
            var tabNav = KeyboardNavigation.GetTabNavigation(element);
            if (tabNav != KeyboardNavigationMode.Continue) sb.Append(" tabnav=").Append(tabNav);
            if (element is ButtonBase button)
            {
                var command = BindingOperations.GetBindingExpression(button, ButtonBase.CommandProperty)?.ParentBinding.Path?.Path;
                if (command != null) sb.Append(" cmd=").Append(command);
                if (button.Content is string text) sb.Append(" text='").Append(text).Append('\'');
            }
            if (element.ToolTip is string tip) sb.Append(" tip='").Append(tip).Append('\'');
            sb.Append(" @").Append(bounds).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The visible, enabled keyboard tab stops under <paramref name="root"/>, in tab (visual-tree) order.</summary>
    public static List<Control> TabStops(FrameworkElement root) =>
        Descendants(root).OfType<Control>().Where(c => c.IsVisible && c.Focusable && c.IsTabStop && c.IsEnabled).ToList();

    /// <summary>
    /// Where each arrow key moves focus from every tab stop (WPF's directional navigation, which is
    /// what the controller's D-pad drives), as indexes into <see cref="TabStops"/>. Needs no active
    /// window: it uses <see cref="UIElement.PredictFocus"/>.
    /// </summary>
    public static string FocusMap(FrameworkElement root)
    {
        var stops = TabStops(root);
        var sb = new StringBuilder();
        for (int i = 0; i < stops.Count; i++)
        {
            sb.Append('#').Append(i).Append(' ').Append(stops[i].GetType().Name);
            foreach (var direction in new[] { FocusNavigationDirection.Up, FocusNavigationDirection.Down, FocusNavigationDirection.Left, FocusNavigationDirection.Right })
            {
                var target = stops[i].PredictFocus(direction);
                var index = target is Control c ? stops.IndexOf(c) : -1;
                sb.Append(' ').Append(direction.ToString()[0]).Append('=').Append(target is null ? "-" : index >= 0 ? "#" + index : target.GetType().Name);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static bool HasKeyboardSetting(DependencyObject element) =>
        KeyboardNavigation.GetDirectionalNavigation(element) != KeyboardNavigationMode.Continue ||
        KeyboardNavigation.GetTabNavigation(element) != KeyboardNavigationMode.Continue;

    private static string Bounds(FrameworkElement element, FrameworkElement root)
    {
        try
        {
            var p = element.TransformToAncestor(root).Transform(new Point(0, 0));
            return $"{Math.Round(p.X)},{Math.Round(p.Y)} {Math.Round(element.ActualWidth)}x{Math.Round(element.ActualHeight)}";
        }
        catch (InvalidOperationException)
        {
            return "detached";
        }
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            int count = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(current) : 0;
            for (int i = count - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(current, i));
        }
    }
}
