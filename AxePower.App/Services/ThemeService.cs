using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AxePower.Core.Models;

namespace AxePower.App.Services;

public class ThemeService
{
    // P/Invoke for Windows 11 Mica / Dark Mode
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_MAINWINDOW = 2; // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    public void ApplyTheme(AppTheme theme)
    {
        var actualTheme = theme;
        if (theme == AppTheme.System)
        {
            // Simple check for system theme in registry (defaulting to dark if it fails)
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null && key.GetValue("AppsUseLightTheme") is int lightThemeEnabled && lightThemeEnabled == 1)
                {
                    actualTheme = AppTheme.Light;
                }
                else
                {
                    actualTheme = AppTheme.Dark;
                }
            }
            catch
            {
                actualTheme = AppTheme.Dark;
            }
        }

        string themeUri = actualTheme switch
        {
            AppTheme.Light => "pack://application:,,,/AxePower.App;component/Resources/Themes/LightTheme.xaml",
            AppTheme.Glass => "pack://application:,,,/AxePower.App;component/Resources/Themes/GlassTheme.xaml",
            AppTheme.Transparent => "pack://application:,,,/AxePower.App;component/Resources/Themes/TransparentTheme.xaml",
            _ => "pack://application:,,,/AxePower.App;component/Resources/Themes/DarkTheme.xaml"
        };

        var dictionary = new ResourceDictionary { Source = new Uri(themeUri, UriKind.Absolute) };
        
        // Remove existing theme dictionary if present, we assume it's the first one, or we can clear and re-add all
        // To be safe, we'll find any dictionary in the Themes folder and replace it
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
            ApplyWindowEffects(Application.Current.MainWindow, actualTheme);
        }
    }

    public void ApplyWindowEffects(Window window, AppTheme theme)
    {
        // Don't run P/Invoke if in design mode
        if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(window))
            return;

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero) return;

        // 1. Dark mode titlebar
        int useImmersiveDarkMode = (theme == AppTheme.Light) ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int));

        // 2. Backdrop (Mica / Acrylic)
        int backdropType = DWMSBT_NONE;
        
        if (Environment.OSVersion.Version.Build >= 22000) // Windows 11
        {
            if (theme == AppTheme.Glass)
                backdropType = DWMSBT_MAINWINDOW; // Mica
            else if (theme == AppTheme.Transparent)
                backdropType = DWMSBT_TRANSIENTWINDOW; // Acrylic

            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
        }
        
        // 3. Fallback for older Windows versions or Transparent theme
        if (theme == AppTheme.Transparent)
        {
            // If they want full transparency, we can enable AllowsTransparency (requires WindowStyle=None)
            // But WindowChrome usually breaks with AllowsTransparency in standard ways, so we just rely on our Transparent brushes
            window.Background = System.Windows.Media.Brushes.Transparent;
        }
    }
}
