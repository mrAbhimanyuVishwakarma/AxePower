using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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

    public MusicView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private MusicViewModel? ViewModel => DataContext as MusicViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _drops = (Application.Current as App)?.Services?.GetService<FileDropHelper>();
        if (_drops == null) return;
        _drops.FilesDropped += OnFilesDropped;
        _drops.Enable();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
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

    private void SongsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SongItemAt(e.OriginalSource as DependencyObject) is { } song)
            ViewModel?.PlaySongCommand.Execute(song);
    }

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
