using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AxePower.App;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.App.Views;
using AxePower.App.Views.Shell;
using AxePower.App.Views.SoftwareHub;
using AxePower.Core.Services;
using AxePower.Tests.SoftwareHub;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AxePower.Tests.Shell;

/// <summary>
/// The main window and the Software Hub page after their split into feature controls and resource
/// dictionaries (Prompt 20): they build, bind without errors in every state, reference only
/// resources that exist in every skin, and keep their keyboard and D-pad (arrow-key) navigation.
/// Windows are shown off-screen without activation, so nothing takes focus from the desktop.
/// </summary>
[Collection(WpfCollection.Name)]
public class ShellViewTests
{
    private static readonly string[] BaseSkins = { "dark", "light", "glass" };
    private readonly WpfApplicationFixture _wpf;

    public ShellViewTests(WpfApplicationFixture wpf) => _wpf = wpf;

    private sealed class Shell : IDisposable
    {
        public MainWindow Window { get; }
        public MainViewModel Vm { get; }
        private readonly ServiceProvider _provider;
        private readonly string _settings;

        public Shell()
        {
            ShellTestHost.UseAppResources();
            _settings = TestServiceContainer.NewSettingsDirectory();
            _provider = TestServiceContainer.Create(_settings).BuildServiceProvider();
            Vm = _provider.GetRequiredService<MainViewModel>();
            Window = new MainWindow(Vm, _provider.GetRequiredService<ThemeService>(), _provider.GetRequiredService<SettingsService>(), null)
            {
                Width = 1280, Height = 840,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
                ShowInTaskbar = false, ShowActivated = false
            };
            Window.Show();
            Vm.NavigateCommand.Execute("Tools");
        }

        public FrameworkElement Root => (FrameworkElement)Window.Content;

        public void Dispose()
        {
            (Window.FindName("AppTrayIcon") as IDisposable)?.Dispose();
            Window.AllowClose();
            Window.Close();
            _provider.GetService<SystemTelemetryService>()?.Dispose();
            try { Directory.Delete(_settings, recursive: true); } catch { }
        }
    }

    private static async Task Settle(UIElement element)
    {
        await Task.Delay(100);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
    }

    [Fact]
    public Task MainWindow_BindsWithoutErrors_InEveryShellState_UnderEachBaseSkin() => _wpf.RunAsync(async () =>
    {
        using var shell = new Shell();
        var vm = shell.Vm;
        var states = new (string Name, Action Enter, Action Leave)[]
        {
            ("page", () => { }, () => { }),
            ("groups", () => { vm.IsMaintenanceExpanded = true; vm.IsUtilitiesExpanded = true; }, () => { vm.IsMaintenanceExpanded = false; vm.IsUtilitiesExpanded = false; }),
            ("rail", () => vm.OnWindowWidthChanged(900), () => vm.OnWindowWidthChanged(1280)),
            ("tool help", () => vm.OpenToolHelp(), () => vm.IsToolHelpModalOpen = false),
            ("update banner", () => vm.AboutVm.IsSteamUpdateBannerOpen = true, () => vm.AboutVm.IsSteamUpdateBannerOpen = false),
            ("patch notes", () => vm.AboutVm.IsSteamPatchNotesModalOpen = true, () => vm.AboutVm.IsSteamPatchNotesModalOpen = false),
        };

        var errors = new List<string>();
        foreach (var skin in BaseSkins)
        {
            using var applied = ShellTestHost.ApplySkin(skin);
            foreach (var (name, enter, leave) in states)
            {
                using var bindings = new ShellTestHost.BindingErrors();
                enter();
                await Settle(shell.Window);
                errors.AddRange(bindings.Errors.Select(e => $"[{skin}, {name}] {e}"));
                leave();
            }
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        // The feature controls are in place, each once.
        Assert.Single(ShellTestHost.Descendants(shell.Root).OfType<ShellSidebar>());
        Assert.Single(ShellTestHost.Descendants(shell.Root).OfType<ToolHelpOverlay>());
        Assert.Single(ShellTestHost.Descendants(shell.Root).OfType<UpdateBanner>());
        Assert.Single(ShellTestHost.Descendants(shell.Root).OfType<PatchNotesDialog>());
    });

    [Fact]
    public Task Overlays_ShowOnlyWhenOpened_AndTheGuideAndFeedStillScroll() => _wpf.RunAsync(async () =>
    {
        using var shell = new Shell();
        using var applied = ShellTestHost.ApplySkin("dark");
        var help = ShellTestHost.Descendants(shell.Root).OfType<ToolHelpOverlay>().Single();
        var notes = ShellTestHost.Descendants(shell.Root).OfType<PatchNotesDialog>().Single();
        await Settle(shell.Window);
        Assert.Empty(ShellTestHost.TabStops(help));
        Assert.Empty(ShellTestHost.TabStops(notes));

        shell.Vm.OpenToolHelp();
        await Settle(shell.Window);
        Assert.NotEmpty(ShellTestHost.TabStops(help));
        help.ScrollGuideToTop(); // what MainWindow calls when another tool is picked

        shell.Vm.AboutVm.IsSteamPatchNotesModalOpen = true;
        await Settle(shell.Window);
        var feed = (ScrollViewer)notes.FindName("PatchNotesScrollViewer");
        var down = ShellTestHost.Descendants(notes).OfType<Button>().Single(b => IsRailArrow(b, "PatchNotesScrollDown_Click"));
        down.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(shell.Window);
        Assert.True(feed.VerticalOffset > 0, "the rail's down arrow no longer scrolls the patch notes");
    });

    /// <summary>The rail arrows are the buttons whose Click is wired to the given handler.</summary>
    private static bool IsRailArrow(Button button, string handler)
    {
        var store = typeof(UIElement).GetProperty("EventHandlersStore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(button);
        if (store == null) return false;
        var handlers = (RoutedEventHandlerInfo[]?)store.GetType().GetMethod("GetRoutedEventHandlers")!.Invoke(store, new object[] { Button.ClickEvent });
        return handlers?.Any(h => h.Handler.Method.Name == handler) == true;
    }

    [Fact]
    public Task TrayMenu_KeepsItsFrameItemStyleSeparatorsAndHandlers() => _wpf.RunAsync(() =>
    {
        using var shell = new Shell();
        var tray = (FrameworkElement)shell.Window.FindName("AppTrayIcon");
        var menu = Assert.IsType<ContextMenu>(tray.ContextMenu);

        Assert.Same(shell.Window.FindResource("TrayMenuTemplate"), menu.Template);
        var itemStyle = Assert.IsType<Style>(menu.TryFindResource(typeof(MenuItem)));
        Assert.Contains(itemStyle.Setters.OfType<Setter>(), s => s.Property == FrameworkElement.HeightProperty && Equals(s.Value, 28.0));
        Assert.IsType<Style>(menu.TryFindResource(MenuItem.SeparatorStyleKey));

        var names = new[] { "TrayItem_Eco", "TrayItem_Smart", "TrayItem_Beast", "TrayItem_GameMode", "TrayItem_Dashboard", "TrayItem_Battery",
                            "TrayItem_PhoneControl", "TrayItem_SecondMonitor", "TrayItem_Maintenance", "TrayItem_Utilities", "TrayItem_UpdatePc",
                            "TrayItem_Settings", "TrayItem_Exit" };
        Assert.Equal(names, menu.Items.OfType<MenuItem>().Select(i => i.Name));
        // The implicit item style reaches only this menu.
        Assert.NotSame(itemStyle, shell.Root.TryFindResource(typeof(MenuItem)));
        return Task.CompletedTask;
    });

    [Fact]
    public Task SidebarAndTitleBar_ArrowKeysMoveThroughTheButtonsInOrder() => _wpf.RunAsync(async () =>
    {
        using var shell = new Shell();
        using var applied = ShellTestHost.ApplySkin("dark");
        shell.Vm.IsMaintenanceExpanded = true;
        shell.Vm.IsUtilitiesExpanded = true;
        await Settle(shell.Window);

        // Sidebar: Up and Down walk every entry in order and wrap, as the D-pad does.
        var sidebar = ShellTestHost.Descendants(shell.Root).OfType<ShellSidebar>().Single();
        var entries = ShellTestHost.TabStops(sidebar);
        Assert.Equal(19, entries.Count); // 18 + Trust & Permissions (Prompt 25)
        for (int i = 0; i < entries.Count; i++)
        {
            Assert.Same(entries[(i + 1) % entries.Count], entries[i].PredictFocus(FocusNavigationDirection.Down));
            Assert.Same(entries[(i - 1 + entries.Count) % entries.Count], entries[i].PredictFocus(FocusNavigationDirection.Up));
            Assert.NotNull(((Button)entries[i]).Command);
        }
        // The navigation entries keep their order (and so their tab order).
        var navigation = entries.OfType<Button>().Where(b => b.CommandParameter is string).Select(b => (string)b.CommandParameter).ToList();
        Assert.Equal(new[] { "Dashboard", "GameMode", "Battery", "Music", "Friends", "PhoneHub", "SecondMonitor", "UpdatePc",
                             "SystemCare", "Uninstaller", "Debloat", "Terminal", "Tools", "Trust", "Settings", "Profile" }, navigation);

        // Title bar: Left and Right step across the window buttons.
        var controls = (Panel)shell.Window.FindName("WindowControls");
        var buttons = ShellTestHost.TabStops(controls);
        Assert.True(buttons.Count >= 4);
        for (int i = 0; i + 1 < buttons.Count; i++)
        {
            Assert.Same(buttons[i + 1], buttons[i].PredictFocus(FocusNavigationDirection.Right));
            Assert.Same(buttons[i], buttons[i + 1].PredictFocus(FocusNavigationDirection.Left));
        }
    });

    // -- Software Hub ---------------------------------------------------------------------------

    [Fact]
    public Task SoftwareHub_BindsWithoutErrors_InEveryTabAndDialog_AndShowsOnlyTheActiveTab() => _wpf.RunAsync(async () =>
    {
        ShellTestHost.UseAppResources();
        using var rig = new HubRig();
        var log = new LogService();
        var vm = new SoftwareHubViewModel(rig.Hub, new WindowsUpdateShieldService(log), new DriverUpdateService(log),
            new FirmwareUpdateService(log, new FakeLauncher(), new FakeRunner()), new NotificationService(), log,
            new GraphicsDriverService(log, new FakeLauncher()), new TerminalService(), new RestorePointService(log, new FakeRunner()));
        var view = new SoftwareHubView { DataContext = vm };
        // Inside a window, as in the app (the category rail reads the window's sidebar state).
        var window = new Window
        {
            Content = view, Width = 1100, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
        };
        window.Show();
        try
        {
            var states = new (string Name, Action Enter, Action Leave)[]
            {
                ("updaters", () => vm.ActiveTab = "Updaters", () => { }),
                ("apps", () => vm.ActiveTab = "Apps", () => { }),
                ("apps busy", () => { vm.ActiveTab = "Apps"; vm.AllPackages[0].IsSelected = true; vm.AllPackages[1].IsBusy = true; vm.IsBatchInstalling = true; },
                              () => { vm.AllPackages[0].IsSelected = false; vm.AllPackages[1].IsBusy = false; vm.IsBatchInstalling = false; }),
                ("apps categories", () => { vm.ActiveTab = "Apps"; vm.IsCategoryListCollapsed = false; vm.SelectedCategory = "Gaming"; }, () => { vm.IsCategoryListCollapsed = true; vm.SelectedCategory = "All"; }),
                ("updates", () => vm.ActiveTab = "Updates", () => { }),
                ("console", () => vm.ActiveTab = "Console", () => { }),
                ("web link warning", () => vm.ShowWebLinkWarning("Open", "Message", "https://ninite.com/"), () => vm.CancelWebLinkWarning()),
                ("nvidia picker", () => vm.OpenNvidiaPicker(null), () => vm.CloseNvidiaPicker()),
            };

            var errors = new List<string>();
            foreach (var skin in BaseSkins)
            {
                using var applied = ShellTestHost.ApplySkin(skin);
                foreach (var (name, enter, leave) in states)
                {
                    using var bindings = new ShellTestHost.BindingErrors();
                    enter();
                    await Settle(window);
                    errors.AddRange(bindings.Errors.Select(e => $"[{skin}, {name}] {e}"));
                    leave();
                }
            }
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));

            // Exactly the active tab's control has anything a keyboard can reach.
            foreach (var (tab, type) in new[] { ("Updaters", typeof(UpdatersTab)), ("Apps", typeof(PackagesTab)), ("Updates", typeof(UpdateShieldTab)) })
            {
                vm.ActiveTab = tab;
                await Settle(window);
                foreach (var control in ShellTestHost.Descendants(view).OfType<UserControl>().Where(c => c is UpdatersTab or PackagesTab or UpdateShieldTab))
                    Assert.Equal(control.GetType() == type, ShellTestHost.TabStops(control).Count > 0);
            }
        }
        finally
        {
            window.Close();
        }
    });

    // -- Resources and skins --------------------------------------------------------------------

    /// <summary>The XAML files this split produced or changed.</summary>
    private static readonly string[] SplitFiles =
    {
        "AxePower.App/MainWindow.xaml",
        "AxePower.App/Views/Shell/ShellSidebar.xaml",
        "AxePower.App/Views/Shell/ToolHelpOverlay.xaml",
        "AxePower.App/Views/Shell/UpdateBanner.xaml",
        "AxePower.App/Views/Shell/PatchNotesDialog.xaml",
        "AxePower.App/Resources/ViewTemplates.xaml",
        "AxePower.App/Resources/ShellChromeStyles.xaml",
        "AxePower.App/Resources/TrayMenuStyles.xaml",
        "AxePower.App/Views/SoftwareHubView.xaml",
        "AxePower.App/Views/SoftwareHub/UpdatersTab.xaml",
        "AxePower.App/Views/SoftwareHub/PackagesTab.xaml",
        "AxePower.App/Views/SoftwareHub/UpdateShieldTab.xaml",
        "AxePower.App/Views/SoftwareHub/WebLinkWarningDialog.xaml",
        "AxePower.App/Views/SoftwareHub/NvidiaDriverPickerDialog.xaml",
        "AxePower.App/Resources/SoftwareHubStyles.xaml",
    };

    private static string RepoRoot()
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "AxePower.sln"))) root = Path.GetDirectoryName(root);
        return root ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public Task SplitXaml_ReferencesOnlyResourcesThatExist_InEverySkin() => _wpf.RunAsync(() =>
    {
        var root = RepoRoot();
        var keyPattern = new Regex(@"\{(Dynamic|Static)Resource\s+([^{}\s]+)\}");
        var localKey = new Regex(@"x:Key=""([^""]+)""");
        var source = new Regex(@"Source=""/AxePower;component/([^""]+)""");

        var references = new List<(string File, string Kind, string Key, HashSet<string> Local)>();
        foreach (var file in SplitFiles)
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            var local = localKey.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
            foreach (Match merged in source.Matches(text))
                foreach (Match key in localKey.Matches(File.ReadAllText(Path.Combine(root, "AxePower.App", merged.Groups[1].Value))))
                    local.Add(key.Groups[1].Value);
            foreach (Match m in keyPattern.Matches(text))
                references.Add((file, m.Groups[1].Value, m.Groups[2].Value, local));
        }
        Assert.True(references.Count > 300, $"only {references.Count} resource references found; the scan is broken");

        var missing = new List<string>();
        foreach (var skin in ShellTestHost.AllSkinIds)
        {
            using var applied = ShellTestHost.ApplySkin(skin);
            foreach (var (file, kind, key, local) in references)
                if (!local.Contains(key) && !HasKey(Application.Current.Resources, key) && !ShellTestHost.SkinDeclares(skin, key))
                    missing.Add($"[{skin}] {file}: {kind}Resource {key}");
        }
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing.Distinct()));
        return Task.CompletedTask;
    });

    /// <summary>
    /// A missing DynamicResource fails silently (the property keeps its default), so scan every XAML
    /// file in the app, not only the split ones: each key must be a skin, app or file-local resource.
    /// </summary>
    [Fact]
    public Task AllAppXaml_DynamicResourcesExist_InEverySkin() => _wpf.RunAsync(() =>
    {
        var root = RepoRoot();
        var keyPattern = new Regex(@"\{DynamicResource\s+([^{}\s]+)\}");
        var localKey = new Regex(@"x:Key=""([^""]+)""");
        var appDir = Path.Combine(root, "AxePower.App");
        var files = Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToList();
        var allLocal = files.SelectMany(f => localKey.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet();
        var references = files.SelectMany(f => keyPattern.Matches(File.ReadAllText(f)).Select(m => (File: Path.GetRelativePath(root, f), Key: m.Groups[1].Value))).ToList();
        Assert.True(references.Count > 1000, $"only {references.Count} references found; the scan is broken");

        var missing = new List<string>();
        foreach (var skin in ShellTestHost.AllSkinIds)
        {
            using var applied = ShellTestHost.ApplySkin(skin);
            foreach (var (file, key) in references)
                if (!allLocal.Contains(key) && !HasKey(Application.Current.Resources, key) && !ShellTestHost.SkinDeclares(skin, key))
                    missing.Add($"[{skin}] {file}: {key}");
        }
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing.Select(m => m.Substring(m.IndexOf(']') + 2)).Distinct().Take(80)));
        return Task.CompletedTask;
    });

    /// <summary>True when the key is defined, even with a null value (skins without shadows define CardShadowEffect as null).</summary>
    private static bool HasKey(ResourceDictionary dictionary, object key) =>
        dictionary.Keys.Cast<object>().Contains(key) || dictionary.MergedDictionaries.Any(d => HasKey(d, key));

    [Fact]
    public Task EverySkin_CompilesAndBuildsItsResources() => _wpf.RunAsync(() =>
    {
        var catalog = new AxePower.Core.Services.Skins.SkinCatalog(new LogService());
        Assert.Empty(catalog.Errors);
        Assert.True(catalog.Skins.Count >= 3);
        foreach (var id in ShellTestHost.AllSkinIds)
        {
            using var applied = ShellTestHost.ApplySkin(id); // builds every brush, colour, shadow and image
            Assert.NotNull(Application.Current.TryFindResource("WindowBackgroundBrush"));
        }
        return Task.CompletedTask;
    });
}
