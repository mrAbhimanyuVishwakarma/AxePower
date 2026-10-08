using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using AxePower.App.Helpers;
using AxePower.App.ViewModels;
using AxePower.App.Windows;
using AxePower.Core.Services;
using Xunit;

namespace AxePower.Tests.Shell;

/// <summary>The first-run welcome (Prompt 31) builds without binding errors in each base skin, names every control, and starts on Continue.</summary>
[Collection(WpfCollection.Name)]
public class WelcomeWindowTests
{
    private readonly WpfApplicationFixture _wpf;

    public WelcomeWindowTests(WpfApplicationFixture wpf) => _wpf = wpf;

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    [InlineData("glass")]
    public Task Welcome_Renders_WithNamedControls(string skin) => _wpf.RunAsync(async () =>
    {
        ShellTestHost.UseAppResources();
        using var _ = ShellTestHost.ApplySkin(skin);
        var folder = Path.Combine(Path.GetTempPath(), "axepower-welcome-" + Guid.NewGuid().ToString("N"));
        var settings = new SettingsService(new LogService(), folder);
        settings.LoadSettings();
        using var bindings = new ShellTestHost.BindingErrors();
        var window = new WelcomeWindow(new WelcomeViewModel(settings))
        {
            Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false,
        };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            Assert.Empty(bindings.Errors);
            var controls = ShellTestHost.Descendants((FrameworkElement)window.Content).OfType<Control>()
                .Where(c => c.IsVisible && c.Focusable && c is CheckBox or Button).ToList();
            Assert.Equal(7, controls.Count); // five switches, Not now, Continue
            foreach (var control in controls)
            {
                var name = UIElementAutomationPeer.CreatePeerForElement(control)?.GetName() ?? "";
                Assert.False(string.IsNullOrWhiteSpace(name) || AccessibleNames.IsGlyphOnly(name), $"{control.GetType().Name} has no name");
            }
            Assert.True(window.ContinueButton.IsDefault);
        }
        finally
        {
            window.Close();
            try { Directory.Delete(folder, true); } catch { }
        }
    });
}
