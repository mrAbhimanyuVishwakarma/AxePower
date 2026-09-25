using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AxePower.Core.Models;
using AxePower.Core.Services.Skins;

namespace AxePower.App.Services;

/// <summary>
/// Applies skins (skins\&lt;id&gt;\skin.json, see skins/README.md). A skin is compiled to the app's
/// theme resources and merged into the application as one dictionary, replacing the last one.
/// </summary>
public class ThemeService
{
    private readonly SkinCatalog _catalog;
    private ResourceDictionary? _appliedDictionary;

    public ThemeService(SkinCatalog catalog)
    {
        _catalog = catalog;
        ActiveSkin = catalog.Resolve(SkinCatalog.DefaultId);
    }

    public SkinCatalog Catalog => _catalog;

    /// <summary>The skin on screen now (dark until the first apply).</summary>
    public SkinEntry ActiveSkin { get; private set; }

    /// <summary>The active skin described the way older code asks: light, glass or dark.</summary>
    public AppTheme ActualTheme => ToAppTheme(ActiveSkin);

    public static AppTheme ToAppTheme(SkinEntry skin) =>
        skin.Compiled.IsLight ? AppTheme.Light : skin.Compiled.UsesAcrylic ? AppTheme.Glass : AppTheme.Dark;

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

    /// <summary>
    /// Applies <paramref name="skinId"/> when given, otherwise the skin <paramref name="theme"/>
    /// stands for (System follows Windows between light and dark).
    /// </summary>
    public void ApplyTheme(AppTheme theme, string? skinId = null)
    {
        var skin = skinId != null
            ? _catalog.Resolve(skinId)
            : _catalog.Resolve(ResolveActualTheme(theme) switch
            {
                AppTheme.Light => "light",
                AppTheme.Glass => "glass",
                _ => "dark"
            });

        var dictionary = SkinResourceFactory.Build(skin.Compiled);

        // Ensure popups, dropdowns, and flyouts remain opaque, high-contrast, and crisp across all skins (especially Glass)
        bool isLight = skin.Compiled.IsLight;
        bool isGlass = skin.Compiled.UsesAcrylic;
        var popupBg = isLight ? "#FFFFFFFF" : (isGlass ? "#FF161820" : "#FF1E2128");
        var popupBorder = isLight ? "#FFCBD5E1" : (isGlass ? "#33FFFFFF" : "#FF333842");
        var controlBacking = isLight ? "#FFFFFFFF" : (isGlass ? "#E614161C" : "#FF1E2128");

        var popupBgBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(popupBg));
        popupBgBrush.Freeze();
        dictionary["DropDownPopupBackgroundBrush"] = popupBgBrush;

        var popupBorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(popupBorder));
        popupBorderBrush.Freeze();
        dictionary["DropDownPopupBorderBrush"] = popupBorderBrush;

        var controlBackingBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(controlBacking));
        controlBackingBrush.Freeze();
        dictionary["DropDownControlBackingBrush"] = controlBackingBrush;

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_appliedDictionary != null) merged.Remove(_appliedDictionary);
        merged.Add(dictionary);
        _appliedDictionary = dictionary;
        ActiveSkin = skin;

        if (Application.Current.MainWindow != null)
        {
            ApplyWindowEffects(Application.Current.MainWindow, theme);
        }

        ThemeChanged?.Invoke(ActualTheme);
    }

    public void ApplyWindowEffects(Window window, AppTheme theme)
    {
        if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(window))
            return;

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero) return;

        var skin = ActiveSkin.Compiled;

        // 1. Dark mode titlebar matching the skin
        int useImmersiveDarkMode = skin.IsLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int));

        var hwndSource = HwndSource.FromHwnd(hwnd);

        if (skin.UsesAcrylic)
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
                // Fallback for Windows 10 and early Windows 11, tinted with the skin's window colour
                EnableAcrylicFallback(hwnd, skin.WindowArgb);
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
                var (_, r, g, b) = Hex.Parse(skin.WindowArgb);
                hwndSource.CompositionTarget.BackgroundColor = Color.FromRgb((byte)r, (byte)g, (byte)b);
            }

            // Restore window background brush from current theme
            if (Application.Current.TryFindResource("WindowBackgroundBrush") is Brush windowBrush)
            {
                window.Background = windowBrush;
            }
        }
    }

    private static void EnableAcrylicFallback(IntPtr hwnd, string windowArgb)
    {
        // AccentPolicy wants AABBGGRR
        var (a, r, g, b) = Hex.Parse(windowArgb);
        int tint = unchecked((a << 24) | (b << 16) | (g << 8) | r);

        try
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = tint
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
