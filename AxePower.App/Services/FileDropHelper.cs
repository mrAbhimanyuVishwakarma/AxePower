using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace AxePower.App.Services;

/// <summary>
/// Accepts files dragged from Explorer even while AxePower runs elevated.
///
/// Windows (UIPI) blocks OLE drag-and-drop from a standard-user Explorer into an elevated
/// window, so WPF's AllowDrop never fires. The shell's older WM_DROPFILES path still works once
/// the window opts in with DragAcceptFiles and lets WM_DROPFILES / WM_COPYDATA /
/// WM_COPYGLOBALDATA through the message filter. The OLE drop target is revoked so the shell
/// uses that path. Enabled only while a view that handles drops is on screen.
/// </summary>
public sealed class FileDropHelper
{
    private const int WM_DROPFILES = 0x0233;
    private const int WM_COPYDATA = 0x004A;
    private const int WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;

    private HwndSource? _source;
    private int _enabledCount;

    /// <summary>Dropped paths and the drop point in window coordinates (device-independent).</summary>
    public event Action<string[], Point>? FilesDropped;

    public void Enable()
    {
        if (!EnsureHook()) return;
        if (_enabledCount++ > 0) return;

        var hwnd = _source!.Handle;
        RevokeDragDrop(hwnd);
        ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
        DragAcceptFiles(hwnd, true);
    }

    public void Disable()
    {
        if (_source == null || _enabledCount == 0) return;
        if (--_enabledCount > 0) return;
        DragAcceptFiles(_source.Handle, false);
    }

    private bool EnsureHook()
    {
        if (_source != null) return true;

        var window = Application.Current?.MainWindow;
        if (window == null) return false;

        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
        _source?.AddHook(WndProc);
        return _source != null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DROPFILES) return IntPtr.Zero;

        handled = true;
        var drop = wParam;
        try
        {
            uint count = DragQueryFile(drop, 0xFFFFFFFF, null, 0);
            var paths = new string[count];
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFile(drop, i, null, 0);
                var buffer = new StringBuilder((int)length + 1);
                DragQueryFile(drop, i, buffer, (uint)buffer.Capacity);
                paths[i] = buffer.ToString();
            }

            DragQueryPoint(drop, out var pt);
            var point = new Point(pt.X, pt.Y);
            if (_source?.CompositionTarget != null)
                point = _source.CompositionTarget.TransformFromDevice.Transform(point);

            if (paths.Length > 0) FilesDropped?.Invoke(paths, point);
        }
        finally
        {
            DragFinish(drop);
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("shell32.dll")]
    private static extern bool DragQueryPoint(IntPtr hDrop, out POINT lppt);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr hwnd);
}
