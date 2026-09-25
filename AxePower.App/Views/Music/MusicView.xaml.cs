using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AxePower.App.Helpers;
using AxePower.App.Services;
using AxePower.App.ViewModels.Music;
using Microsoft.Extensions.DependencyInjection;

namespace AxePower.App.Views.Music;

/// <summary>
/// View-only wiring: keyboard shortcuts, queue gestures, and file drops. Drops arrive through
/// <see cref="FileDropHelper"/> (WM_DROPFILES) so they also work while AxePower runs elevated.
/// </summary>
public partial class MusicView : UserControl
{
    private FileDropHelper? _drops;
    private int _dragFrom = -1;
    private Point _dragStart;
    private bool _dragging;
    private bool _keepRecentSearchesClosed;

    public MusicView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private MusicViewModel? ViewModel => DataContext as MusicViewModel;

    private Window? _hostWindow;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel?.Activate();

        // The recent-searches list stays open until something here closes it (see below)
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow != null)
        {
            _hostWindow.PreviewMouseDown += HostWindow_PreviewMouseDown;
            _hostWindow.Deactivated += HostWindow_Deactivated;
            _hostWindow.LocationChanged += HostWindow_Deactivated;
        }

        _drops = (Application.Current as App)?.Services?.GetService<FileDropHelper>();
        if (_drops == null) return;
        _drops.FilesDropped += OnFilesDropped;
        _drops.Enable();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel?.Deactivate();
        CloseRecentSearches();

        if (_hostWindow != null)
        {
            _hostWindow.PreviewMouseDown -= HostWindow_PreviewMouseDown;
            _hostWindow.Deactivated -= HostWindow_Deactivated;
            _hostWindow.LocationChanged -= HostWindow_Deactivated;
            _hostWindow = null;
        }

        if (_drops == null) return;
        _drops.FilesDropped -= OnFilesDropped;
        _drops.Disable();
        _drops = null;
    }

    private void OnFilesDropped(string[] paths, Point windowPoint)
    {
        var window = Window.GetWindow(this);
        if (window == null || ViewModel == null) return;

        var inView = window.TranslatePoint(windowPoint, this);
        if (inView.X < 0 || inView.Y < 0 || inView.X > ActualWidth || inView.Y > ActualHeight) return;

        bool onQueue = false;
        if (QueuePanel.IsVisible)
        {
            var inQueue = window.TranslatePoint(windowPoint, QueuePanel);
            onQueue = inQueue.X >= 0 && inQueue.Y >= 0 && inQueue.X <= QueuePanel.ActualWidth && inQueue.Y <= QueuePanel.ActualHeight;
        }

        _ = ViewModel.HandleDropAsync(paths, onQueue);
    }

    // ── Keyboard ────────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // While a shortcut is being recorded every key belongs to it, including the ones this
        // page uses for playback, or a shortcut could never be bound to them.
        if (ViewModel?.Shortcuts is { Recording: not null } shortcuts)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            shortcuts.RecordKey(key, Keyboard.Modifiers);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            ViewModel?.Ambient?.ToggleCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel?.Ambient?.IsOpen == true)
        {
            ViewModel.Ambient.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        var np = ViewModel?.NowPlaying;
        if (np == null || Keyboard.FocusedElement is TextBoxBase) return;

        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;

        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not ButtonBase)
        {
            np.PlayPauseCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Right)
        {
            np.NextCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Left)
        {
            np.PreviousCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Up)
        {
            np.ChangeVolumeCommand.Execute("5");
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Down)
        {
            np.ChangeVolumeCommand.Execute("-5");
            e.Handled = true;
        }
    }

    // ── Queue gestures ──────────────────────────────────────────────────────

    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e.OriginalSource as DependencyObject) is { } entry)
            ViewModel?.NowPlaying.JumpCommand.Execute(entry);
    }

    private void SongsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SongItemAt(e.OriginalSource as DependencyObject) is { } song)
        {
            ViewModel?.PlaySongCommand.Execute(song);
            e.Handled = true;
        }
    }

    // ── Recent searches dropdown ────────────────────────────────────────────
    //
    // The popup is StaysOpen="True" and closed here instead of by WPF: a self-closing popup eats
    // the click that dismisses it, so clicking the search box while the list was open used to
    // close it rather than keep it. It opens on every click in the box, filters as you type, and
    // closes on a click elsewhere, Tab away, Esc, choosing a search, or leaving the window.

    private void YouTubeSearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        vm.IsSearchBoxFocused = true;
        if (_keepRecentSearchesClosed)
        {
            _keepRecentSearchesClosed = false;
            return;
        }
        vm.ShowRecentSearches();
    }

    private void YouTubeSearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        vm.IsSearchBoxFocused = false;

        // Moving into the list (Down arrow) keeps it open; going anywhere else closes it
        if (e.NewFocus is DependencyObject target && IsInsideRecentSearches(target)) return;
        vm.IsRecentSearchesOpen = false;
    }

    /// <summary>Every click in the box shows the full history, whether or not it had the cursor.</summary>
    private void YouTubeSearchBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        ViewModel?.ShowRecentSearches();

    private void YouTubeSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;

        if (e.Key == Key.Escape && vm.IsRecentSearchesOpen)
        {
            vm.IsRecentSearchesOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            vm.IsRecentSearchesOpen = false;
        }
        else if (e.Key == Key.Down && vm.HasRecentSearches)
        {
            // Keep whatever filter the typing applied; only open it if it was shut
            if (!vm.IsRecentSearchesOpen) vm.ShowRecentSearches();
            // The popup's rows exist only once it has opened and laid out
            Dispatcher.BeginInvoke(() =>
                RecentSearchesList.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
                System.Windows.Threading.DispatcherPriority.Loaded);
            e.Handled = true;
        }
    }

    /// <summary>Esc inside the list closes it and puts the cursor back in the search box.</summary>
    private void RecentSearchesPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ViewModel == null) return;
        ViewModel.IsRecentSearchesOpen = false;
        _keepRecentSearchesClosed = true;
        YouTubeSearchBox.Focus();
        e.Handled = true;
    }

    /// <summary>A click anywhere but the search box or the list closes the list.</summary>
    private void HostWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { IsRecentSearchesOpen: true }) return;
        if (e.OriginalSource is DependencyObject source && (IsInside(source, YouTubeSearchBox) || IsInsideRecentSearches(source))) return;
        CloseRecentSearches();
    }

    /// <summary>A StaysOpen popup would otherwise sit over other apps, or stay put while the window moves.</summary>
    private void HostWindow_Deactivated(object? sender, EventArgs e) => CloseRecentSearches();

    private void CloseRecentSearches()
    {
        if (ViewModel is { IsRecentSearchesOpen: true } vm) vm.IsRecentSearchesOpen = false;
    }

    private bool IsInsideRecentSearches(DependencyObject element) =>
        RecentSearchesPopup.Child is { } child && IsInside(element, child);

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var node = element; node != null; node = ParentOf(node))
            if (ReferenceEquals(node, container)) return true;
        return false;
    }

    // Text runs are not visuals, so they only have a logical parent
    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    private void QueueList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var np = ViewModel?.NowPlaying;
        if (np == null || QueueList.SelectedItem is not QueueEntryViewModel entry) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Enter)
        {
            np.JumpCommand.Execute(entry);
            e.Handled = true;
        }
        else if (key == Key.Delete)
        {
            int index = entry.Index;
            np.RemoveCommand.Execute(entry);
            RestoreSelection(Math.Max(0, index - (index >= np.Queue.Count - 1 ? 1 : 0)));
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && (key == Key.Up || key == Key.Down))
        {
            int target = entry.Index + (key == Key.Up ? -1 : 1);
            if (key == Key.Up) np.MoveUpCommand.Execute(entry);
            else np.MoveDownCommand.Execute(entry);
            RestoreSelection(target);
            e.Handled = true;
        }
    }

    /// <summary>The queue list is rebuilt after an edit; keep keyboard focus on the moved row.</summary>
    private void RestoreSelection(int index)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (index < 0 || index >= QueueList.Items.Count) return;
            QueueList.SelectedIndex = index;
            QueueList.ScrollIntoView(QueueList.SelectedItem);
            (QueueList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // Drag to reorder, done with mouse capture rather than OLE drag-and-drop
    private void QueueList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = ItemAt(e.OriginalSource as DependencyObject)?.Index ?? -1;
        _dragStart = e.GetPosition(QueueList);
        _dragging = false;
    }

    private void QueueList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom < 0 || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(QueueList) - _dragStart;
        if (!_dragging && (Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            _dragging = true;
            QueueList.CaptureMouse();
            QueueList.Cursor = Cursors.SizeNS;
        }
    }

    private void QueueList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            QueueList.ReleaseMouseCapture();
            QueueList.Cursor = null;

            var hit = QueueList.InputHitTest(e.GetPosition(QueueList)) as DependencyObject;
            int to = ItemAt(hit)?.Index ?? (QueueList.Items.Count - 1);
            if (to != _dragFrom && _dragFrom >= 0) ViewModel?.NowPlaying.MoveEntry(_dragFrom, to);
            e.Handled = true;
        }

        _dragFrom = -1;
        _dragging = false;
    }

    private static QueueEntryViewModel? ItemAt(DependencyObject? element)
    {
        while (element != null && element is not ListBoxItem)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

        return (element as ListBoxItem)?.DataContext as QueueEntryViewModel;
    }

    private static SongItemViewModel? SongItemAt(DependencyObject? element)
    {
        while (element != null && element is not ListBoxItem)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

        return (element as ListBoxItem)?.DataContext as SongItemViewModel;
    }
}
