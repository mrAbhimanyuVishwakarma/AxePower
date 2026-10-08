using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.App.Views;
using AxePower.Core.Services;
using AxePower.Core.Services.Operations;
using AxePower.Tests.Shell;
using Xunit;

namespace AxePower.Tests.Trust;

/// <summary>
/// The Trust page builds and binds without errors under each base skin, every button has an
/// accessible name, and Tab reaches all of them. Set AXEPOWER_RENDER_DIR to save PNGs.
/// </summary>
[Collection(WpfCollection.Name)]
public class TrustViewRenderTests
{
    private static readonly string[] BaseSkins = { "dark", "light", "glass" };
    private readonly WpfApplicationFixture _wpf;

    public TrustViewRenderTests(WpfApplicationFixture wpf) => _wpf = wpf;

    private sealed class NeverConfirm : IConfirmationPrompts
    {
        public bool Confirm(ActionConfirmation confirmation) => false;
    }

    private sealed class Secrets : AxePower.Core.Services.Music.ISecretStore
    {
        private string? _value;
        public string? Get(string key) => _value;
        public void Set(string key, string? value) => _value = value;
    }

    [Fact]
    public Task ThePage_BindsWithoutErrors_AndEveryButtonIsNamedAndReachable() => _wpf.RunAsync(async () =>
    {
        ShellTestHost.UseAppResources();
        var dir = Path.Combine(Path.GetTempPath(), "AxePower_TrustRender_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var token = new PortalAccessToken(new Secrets());
        using var lanDrop = new LanDropService(new LogService(), token, downloadDirectory: dir) { DownloadDirectory = dir };
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.True(await lanDrop.StartServerAsync("127.0.0.1", port));
        using var vm = new TrustViewModel(new SettingsService(new LogService(), Path.Combine(dir, "settings")), new LogService(), new NeverConfirm(),
            lanDrop: lanDrop, startTimer: false);
        var errors = new System.Collections.Generic.List<string>();

        try
        {
            foreach (var skin in BaseSkins)
            {
                using var applied = ShellTestHost.ApplySkin(skin);
                using var bindings = new ShellTestHost.BindingErrors();
                var view = new TrustView { DataContext = vm };
                var window = new Window
                {
                    Content = view, Width = 1100, Height = 3400,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false,
                    Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush"),
                };
                window.Show();
                await Task.Delay(150);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var dirOut = Environment.GetEnvironmentVariable("AXEPOWER_RENDER_DIR");
                if (!string.IsNullOrEmpty(dirOut)) ShellTestHost.SavePng(view, 1100, 3400, Path.Combine(dirOut, $"trust-{skin}.png"));
                errors.AddRange(bindings.Errors.Select(e => $"[{skin}] {e}"));

                // Accessibility: every button says what it does, and Tab reaches each one.
                var buttons = ShellTestHost.Descendants(view).OfType<Button>().Where(b => b.IsVisible).ToList();
                Assert.NotEmpty(buttons);
                foreach (var button in buttons)
                {
                    var name = AutomationProperties.GetName(button);
                    if (string.IsNullOrWhiteSpace(name)) name = button.Content as string;
                    Assert.False(string.IsNullOrWhiteSpace(name), $"[{skin}] a button has no accessible name");
                    Assert.True(button.Focusable && KeyboardNavigation.GetIsTabStop(button), $"[{skin}] {name} is not a tab stop");
                }
                var stops = ShellTestHost.TabStops(view);
                Assert.All(buttons.Where(b => b.IsEnabled), b => Assert.Contains(b, stops));
                window.Close();
            }
        }
        finally
        {
            lanDrop.StopServer();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    });
}
