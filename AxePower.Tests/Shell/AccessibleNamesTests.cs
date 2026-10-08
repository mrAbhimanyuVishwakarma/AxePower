using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AxePower.App;
using AxePower.App.Helpers;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AxePower.Tests.Shell;

/// <summary>
/// Screen-reader names (Prompt 28): every control a keyboard user can reach has a readable name,
/// either set in XAML or inferred by <see cref="AccessibleNames"/> from what a sighted user sees.
/// </summary>
[Collection(WpfCollection.Name)]
public class AccessibleNamesTests
{
    private const string Glyph = ""; // Segoe MDL2 "Play"

    /// <summary>Every page reachable from the sidebar.</summary>
    private static readonly string[] Pages =
    {
        "Dashboard", "GameMode", "Battery", "Music", "Friends", "PhoneHub", "SecondMonitor", "UpdatePc", "SoftwareHub",
        "SystemCare", "Doctor", "Uninstaller", "Debloat", "Terminal", "Tools", "Routines", "Trust", "Settings", "Profile", "Logs",
    };

    private readonly WpfApplicationFixture _wpf;

    public AccessibleNamesTests(WpfApplicationFixture wpf) => _wpf = wpf;

    private static string NameOf(Control control) => UIElementAutomationPeer.CreatePeerForElement(control)?.GetName() ?? "";

    private static async Task<Window> Show(UIElement content)
    {
        var window = new Window
        {
            Content = content, Width = 400, Height = 300, Left = -30000, Top = -30000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false,
        };
        AccessibleNames.SetEnabled(window, true);
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        return window;
    }

    [Theory]
    [InlineData(Glyph, true)]
    [InlineData(Glyph + " " + Glyph, true)]
    [InlineData("Play", false)]
    [InlineData(Glyph + " Play", false)]
    [InlineData("", false)]
    public void IsGlyphOnly_MeansIconFontCharactersOnly(string text, bool expected) =>
        Assert.Equal(expected, AccessibleNames.IsGlyphOnly(text));

    [Fact]
    public Task IconAndLabelButton_IsNamedByItsLabel_AndFollowsIt() => _wpf.RunAsync(async () =>
    {
        var label = new TextBlock { Text = "Launch" };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = Glyph });
        content.Children.Add(label);
        var button = new Button { Content = content };
        var window = await Show(button);

        Assert.Equal("Launch", NameOf(button));
        label.Text = "Stop";
        Assert.Equal("Stop", NameOf(button));
        window.Close();
    });

    [Fact]
    public Task ToggleBesideItsLabel_IsNamedByTheLabel() => _wpf.RunAsync(async () =>
    {
        // Settings' row shape: title and description on the left, switch on the right.
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "Start with Windows" });
        text.Children.Add(new TextBlock { Text = "Opens AxePower when you sign in." });
        row.Children.Add(text);
        var toggle = new CheckBox();
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        var window = await Show(row);

        Assert.Equal("Start with Windows", NameOf(toggle));
        window.Close();
    });

    [Fact]
    public Task IconOnlyButton_IsNamedByItsToolTip() => _wpf.RunAsync(async () =>
    {
        var button = new Button { Content = Glyph, ToolTip = "Refresh" };
        var window = await Show(button);

        Assert.Equal("Refresh", NameOf(button));
        window.Close();
    });

    [Fact]
    public Task NameSetInXaml_AlwaysWins() => _wpf.RunAsync(async () =>
    {
        var button = new Button { Content = new TextBlock { Text = "Go" }, ToolTip = "Tooltip" };
        AutomationProperties.SetName(button, "Run the scan");
        var window = await Show(button);

        Assert.Equal("Run the scan", NameOf(button));
        window.Close();
    });

    [Fact]
    public Task EveryFocusableControl_OnEveryPage_HasAReadableName() => _wpf.RunAsync(async () =>
    {
        ShellTestHost.UseAppResources();
        var settings = TestServiceContainer.NewSettingsDirectory();
        var provider = TestServiceContainer.Create(settings).BuildServiceProvider();
        var vm = provider.GetRequiredService<MainViewModel>();
        var window = new MainWindow(vm, provider.GetRequiredService<ThemeService>(), provider.GetRequiredService<SettingsService>(), null)
        {
            Width = 1280, Height = 840, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
            ShowInTaskbar = false, ShowActivated = false,
        };
        var unnamed = new List<string>();
        int checkedCount = 0;
        try
        {
            window.Show();
            vm.IsMaintenanceExpanded = true;
            vm.IsUtilitiesExpanded = true;
            foreach (var page in Pages)
            {
                vm.NavigateCommand.Execute(page);
                await Task.Delay(150);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                foreach (var control in ShellTestHost.Descendants((FrameworkElement)window.Content).OfType<Control>().Where(IsReachable))
                {
                    checkedCount++;
                    var name = NameOf(control);
                    if (!string.IsNullOrWhiteSpace(name) && !AccessibleNames.IsGlyphOnly(name)) continue;
                    if (HasAuthoredNameBinding(control)) continue; // a name from data the test container does not supply
                    unnamed.Add($"[{page}] {control.GetType().Name} {control.Name} (data: {control.DataContext?.GetType().Name})");
                }
            }
        }
        finally
        {
            (window.FindName("AppTrayIcon") as IDisposable)?.Dispose();
            window.AllowClose();
            window.Close();
            provider.GetService<SystemTelemetryService>()?.Dispose();
            // Not provider.Dispose(), as in ShellViewTests: the Dashboard's refresh timer keeps
            // ticking and would hit a disposed BatteryService, crashing the test host.
            try { Directory.Delete(settings, recursive: true); } catch { }
        }

        Assert.True(checkedCount > 500, $"Only {checkedCount} controls were checked; the pages did not load.");
        Assert.True(unnamed.Count == 0, "Controls without a screen-reader name:\n" + string.Join("\n", unnamed.Distinct()));
    });

    /// <summary>A control Tab can land on: visible, focusable, and not a container.</summary>
    private static bool IsReachable(Control c) =>
        c.IsVisible && c.Focusable
        && c is not (ScrollViewer or ListBoxItem or UserControl or Window)
        && c.GetType() != typeof(ContentControl)
        && (c is not ItemsControl || c is ComboBox || c is ListBox);

    /// <summary>
    /// A name bound in XAML to the control's own data (e.g. the power timer's button, whose view
    /// model comes from the running app). Inferred names bind to a TextBlock or tooltip source
    /// instead, so they are still checked.
    /// </summary>
    private static bool HasAuthoredNameBinding(Control control) =>
        BindingOperations.GetBinding(control, AutomationProperties.NameProperty) is { Source: null, RelativeSource: null, ElementName: null };
}
