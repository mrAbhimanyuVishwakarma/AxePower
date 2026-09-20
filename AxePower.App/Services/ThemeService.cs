using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AxePower.Core.Models;

namespace AxePower.App.Services;

public class ThemeService
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_MAINWINDOW = 2; // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic (Frosted Glass)
    private const int DWMSBT_TABBEDWINDOW = 4; // Mica Alt

    // Windows 10 / 11 Acrylic Fallback P/Invoke
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_INVALID_STATE = 5
    }

    public AppTheme ResolveActualTheme(AppTheme theme)
    {
        if (theme == AppTheme.System)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null && key.GetValue("AppsUseLightTheme") is int lightThemeEnabled && lightThemeEnabled == 1)
                {
                    return AppTheme.Light;
                }
            }
            catch { }
            return AppTheme.Dark;
        }

        return theme;
    }

    public event Action<AppTheme>? ThemeChanged;

    public bool IsTaskbarLight(AppTheme currentTheme)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null && key.GetValue("SystemUsesLightTheme") is int sys)
            {
                return sys == 1;
            }
        }
        catch { }

        var actual = ResolveActualTheme(currentTheme);
        return actual == AppTheme.Light;
    }

    public void ApplyTheme(AppTheme theme)
    {
        var actualTheme = ResolveActualTheme(theme);

        string themeUri = actualTheme switch
        {
            AppTheme.Light => "pack://application:,,,/Resources/Themes/LightTheme.xaml",
            AppTheme.Glass => "pack://application:,,,/Resources/Themes/GlassTheme.xaml",
            _ => "pack://application:,,,/Resources/Themes/DarkTheme.xaml"
        };

        var dictionary = new ResourceDictionary { Source = new Uri(themeUri, UriKind.Absolute) };
        
        var existingTheme = Application.Current.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("Themes/"));

        if (existingTheme != null)
        {
            Application.Current.Resources.MergedDictionaries.Remove(existingTheme);
        }
        Application.Current.Resources.MergedDictionaries.Add(dictionary);

        // Apply Window effects if main window is loaded
        if (Application.Current.MainWindow != null)
        {
            ApplyWindowEffects(Application.Current.MainWindow, theme);
        }

        ThemeChanged?.Invoke(actualTheme);
    }

    public void ApplyWindowEffects(Window window, AppTheme theme)
    {
        if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(window))
            return;

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero) return;

        var actualTheme = ResolveActualTheme(theme);

        // 1. Dark mode titlebar matching actual theme
        int useImmersiveDarkMode = (actualTheme == AppTheme.Light) ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int));

        var hwndSource = HwndSource.FromHwnd(hwnd);

        if (actualTheme == AppTheme.Glass)
        {
            // Set Window background to transparent
            window.Background = Brushes.Transparent;

            // Ensure WPF Composition Target doesn't paint an opaque backing
            if (hwndSource?.CompositionTarget != null)
            {
                hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            // Extend DWM glass frame into the client area
            MARGINS margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // Windows 11 Acrylic Backdrop (Build >= 22621 / 22000)
            if (Environment.OSVersion.Version.Build >= 22621)
            {
                int backdropType = DWMSBT_TRANSIENTWINDOW; // Acrylic frosted blur
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            }
            else
            {
                // Fallback for Windows 10 and early Windows 11
                EnableAcrylicFallback(hwnd);
            }
        }
        else
        {
            // Reset backdrop to NONE
            if (Environment.OSVersion.Version.Build >= 22621)
            {
                int backdropType = DWMSBT_NONE;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            }
            else
            {
                DisableAcrylicFallback(hwnd);
            }

            // Reset frame margins
            MARGINS margins = new MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 0, cyBottomHeight = 0 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // Restore solid composition backing
            if (hwndSource?.CompositionTarget != null)
            {
                hwndSource.CompositionTarget.BackgroundColor = (actualTheme == AppTheme.Light) ? Color.FromRgb(0xF3, 0xF4, 0xF6) : Color.FromRgb(0x09, 0x0A, 0x0D);
            }

            // Restore window background brush from current theme
            if (Application.Current.TryFindResource("WindowBackgroundBrush") is Brush windowBrush)
            {
                window.Background = windowBrush;
            }
        }
    }

    private static void EnableAcrylicFallback(IntPtr hwnd)
    {
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = 0x55111622 // Translucent dark glass tint
            };

            var accentSize = Marshal.SizeOf(accent);
            var accentPtr = Marshal.AllocHGlobal(accentSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = accentSize
            };

            SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(accentPtr);
        }
        catch { }
    }

    private static void DisableAcrylicFallback(IntPtr hwnd)
    {
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_DISABLED
            };

            var accentSize = Marshal.SizeOf(accent);
            var accentPtr = Marshal.AllocHGlobal(accentSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = accentSize
            };

            SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(accentPtr);
        }
        catch { }
    }
}
