using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AxePower.App.ViewModels.ShareScreen;
using Microsoft.Web.WebView2.Core;

namespace AxePower.App.Views.ShareScreen;

/// <summary>
/// Hosts the stage's embedded player.
///
/// The preview plays the published stream back over WebRTC rather than taking a second local
/// capture, so what is on the tile is the picture and the delay a viewer actually gets. WebView2
/// is an <c>HwndHost</c>, which means no WPF element can be drawn over it -- the name pill and the
/// rounded corners are therefore injected into the page rather than layered on top in XAML.
/// </summary>
public partial class ShareScreenView : UserControl
{
    private ShareScreenViewModel? _viewModel;
    private bool _browserReady;
    private bool _browserFailed;
    private string _shownUrl = string.Empty;
    private bool _isFullscreen;

    public ShareScreenView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    /// <summary>The "⋯" on a friend opens its Remove / Block menu on a left click too.</summary>
    private void OpenFriendMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.DataContext = button.DataContext;
            menu.IsOpen = true;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = e.NewValue as ShareScreenViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        UpdatePlaceholder();
        _ = SyncPreviewAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShareScreenViewModel.ViewerUrl)
            or nameof(ShareScreenViewModel.IsLive)
            or nameof(ShareScreenViewModel.IsPreviewEnabled)
            or nameof(ShareScreenViewModel.State)
            or nameof(ShareScreenViewModel.IsWatchingFriend)
            or nameof(ShareScreenViewModel.ActiveFriendWatchUrl)
            or nameof(ShareScreenViewModel.IsStageActive)
            or nameof(ShareScreenViewModel.IsJoinModalOpen))
        {
            UpdatePlaceholder();
            _ = SyncPreviewAsync();
        }
    }

    /// <summary>The tile's resting face: idle, preview switched off, or waiting to connect.</summary>
    private void UpdatePlaceholder()
    {
        var vm = _viewModel;
        if (vm == null) return;

        // WebView2 is an HWND and would otherwise render above the WPF join dialog, leaving the
        // user with a dimmed page and an apparently missing modal.
        if (vm.IsJoinModalOpen)
        {
            StopPreview();
            return;
        }

        if (vm.IsWatchingFriend)
        {
            TileHeadline.Text = $"Watching {vm.FriendName}'s screen...";
            TileSubtext.Visibility = Visibility.Collapsed;
        }
        else if (!vm.IsLive)
        {
            TileHeadline.Text = "Your screen isn't being shared";
            TileSubtext.Visibility = Visibility.Visible;
        }
        else if (!vm.IsPreviewEnabled)
        {
            TileHeadline.Text = "Preview hidden";
            TileSubtext.Visibility = Visibility.Collapsed;
        }
        else
        {
            TileHeadline.Text = "Connecting to the stream...";
            TileSubtext.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Brings the embedded player in line with whether there is a stream worth showing.</summary>
    private async Task SyncPreviewAsync()
    {
        var vm = _viewModel;
        if (vm == null) return;

        if (vm.IsJoinModalOpen)
        {
            StopPreview();
            return;
        }

        string targetUrl;
        if (vm.IsWatchingFriend && !string.IsNullOrWhiteSpace(vm.ActiveFriendWatchUrl))
        {
            targetUrl = vm.ActiveFriendWatchUrl;
        }
        else if (vm.IsLive && vm.IsPreviewEnabled && !string.IsNullOrWhiteSpace(vm.LocalPreviewUrl))
        {
            targetUrl = vm.LocalPreviewUrl;
        }
        else
        {
            StopPreview();
            return;
        }

        if (_browserFailed) return;
        if (!await EnsureBrowserAsync()) return;

        if (_shownUrl == targetUrl) return;
        _shownUrl = targetUrl;

        PreviewBrowser.CoreWebView2.Navigate(targetUrl);
    }

    private async Task<bool> EnsureBrowserAsync()
    {
        if (_browserReady) return true;

        try
        {
            // WebView2 cannot cache into the install directory; the per-user folder is the one
            // place we are always allowed to write.
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AxePower", "webview2");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: new CoreWebView2EnvironmentOptions
                {
                    // Without this the player sits on its first frame waiting for a click.
                    AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required"
                });

            await PreviewBrowser.EnsureCoreWebView2Async(environment);
            PreviewBrowser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 10, 11, 13);

            var core = PreviewBrowser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.NavigationCompleted += OnNavigationCompleted;

            _browserReady = true;
            return true;
        }
        catch (Exception ex)
        {
            // A missing WebView2 runtime must not take sharing down with it.
            _browserFailed = true;
            TileHeadline.Text = "Preview unavailable";
            TileSubtext.Text =
                "The in-app preview needs the Microsoft Edge WebView2 runtime. Sharing still works — " +
                "use the browser button to watch the stream. (" + ex.Message + ")";
            TileSubtext.Visibility = Visibility.Visible;
            return false;
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var vm = _viewModel;
        if (vm == null) return;

        if (!e.IsSuccess)
        {
            if (vm.IsWatchingFriend)
            {
                vm.JoinStatusMessage = "Could not reach friend's stream. Ensure the host is live and network is reachable.";
                vm.IsWatchingFriend = false;
            }
            else
            {
                TileHeadline.Text = "Could not reach the stream — it may still be starting.";
            }
            _shownUrl = string.Empty;   // let the next sync retry
            return;
        }

        if (vm.IsWatchingFriend)
        {
            try
            {
                await PreviewBrowser.CoreWebView2.ExecuteScriptAsync("""
                    (function () {
                      var v = document.querySelector('video');
                      if (v) {
                        v.muted = false;
                        v.volume = 1.0;
                        v.play().catch(function () {});
                      }
                      var snd = document.getElementById('snd');
                      if (snd) { snd.textContent = 'Mute'; }
                    })();
                    """);
                PreviewBrowser.Visibility = Visibility.Visible;
                TilePlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (Exception)
            {
                PreviewBrowser.Visibility = Visibility.Visible;
                TilePlaceholder.Visibility = Visibility.Collapsed;
            }
            return;
        }

        try
        {
            // JSON-encoding the name keeps a quote or a backslash in a Windows user name from
            // breaking the injected script.
            var name = JsonSerializer.Serialize(vm.TileName);

            await PreviewBrowser.CoreWebView2.ExecuteScriptAsync($$"""
                (function () {
                  var BG = '#0A0B0D';
                  document.documentElement.style.background = BG;
                  document.body.style.background = BG;
                  document.body.style.margin = '0';
                  document.body.style.overflow = 'hidden';

                  var style = function (v) {
                    // Muting matters: system audio is being captured, so playing it back here
                    // would feed straight into the stream as an echo.
                    v.muted = true;
                    v.volume = 0;
                    v.controls = false;
                    v.style.width = '100%';
                    v.style.height = '100%';
                    v.style.objectFit = 'contain';
                    v.style.borderRadius = '9px';
                    v.style.background = BG;
                    v.play().catch(function () {});
                  };

                  var pill = function () {
                    if (document.getElementById('axe-name')) return;
                    var d = document.createElement('div');
                    d.id = 'axe-name';
                    d.textContent = {{name}};
                    d.style.cssText = 'position:fixed;left:14px;bottom:14px;z-index:2147483647;' +
                      'background:rgba(0,0,0,.72);color:#F2F3F5;font:600 12px/1 "Segoe UI",sans-serif;' +
                      'padding:7px 11px;border-radius:8px;pointer-events:none;';
                    document.body.appendChild(d);
                  };

                  var apply = function () {
                    var v = document.querySelector('video');
                    if (!v) return false;
                    style(v);
                    pill();
                    return true;
                  };

                  if (!apply()) {
                    var tries = 0;
                    var timer = setInterval(function () {
                      if (apply() || ++tries > 40) clearInterval(timer);
                    }, 250);
                  }
                })();
                """);

            PreviewBrowser.Visibility = Visibility.Visible;
            TilePlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            // The player still works even if the tidy-up script did not run.
            PreviewBrowser.Visibility = Visibility.Visible;
            TilePlaceholder.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Navigates away so the player releases the connection and stops decoding.</summary>
    private void StopPreview()
    {
        _shownUrl = string.Empty;

        if (_browserReady)
        {
            try { PreviewBrowser.CoreWebView2.Navigate("about:blank"); }
            catch (Exception) { /* already torn down */ }
        }

        PreviewBrowser.Visibility = Visibility.Collapsed;
        TilePlaceholder.Visibility = Visibility.Visible;
    }

    /// <summary>Lets the tile fill the page, hiding the chrome around it.</summary>
    private void OnToggleFullscreen(object sender, RoutedEventArgs e)
    {
        _isFullscreen = !_isFullscreen;

        Tile.MaxWidth = _isFullscreen ? double.PositiveInfinity : 1180;
        Tile.MaxHeight = _isFullscreen ? double.PositiveInfinity : 720;
        Tile.Margin = _isFullscreen ? new Thickness(0) : new Thickness(0, 4, 0, 14);
        Tile.CornerRadius = new CornerRadius(_isFullscreen ? 0 : 10);

        FullscreenGlyph.Text = _isFullscreen ? "\uE73F" : "\uE740";
        FullscreenButton.ToolTip = _isFullscreen ? "Shrink the preview" : "Fill the window with the preview";
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Leaving the page should not keep a decoder running in the background.
        StopPreview();
    }
}
