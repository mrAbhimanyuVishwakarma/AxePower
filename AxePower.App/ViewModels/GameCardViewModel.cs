using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.App.Services;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.App.ViewModels;

public partial class GameCardViewModel : ObservableObject
{
    private static readonly GpuControlAppService _gpuService = new();
    private readonly Action<AppRule> _removeCallback;
    private readonly Action _saveCallback;

    public AppRule Rule { get; }

    public string? SteamAppId { get; set; }
    public string? ExecutablePath { get; set; }

    [ObservableProperty]
    private string _executableName = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private PowerMode _targetMode = PowerMode.Beast;

    [ObservableProperty]
    private ImageSource? _posterImage;

    [ObservableProperty]
    private bool _hasPoster;

    public Brush GpuBrandBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(_gpuService.GetBrandColorHex()));

    public string ModeDisplayName => TargetMode switch
    {
        PowerMode.Beast => "Beast Mode",
        PowerMode.Smart => "Balanced Mode",
        PowerMode.Eco => "Eco Mode",
        _ => TargetMode.ToString()
    };

    public Brush ModeBadgeBrush => TargetMode switch
    {
        PowerMode.Beast => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")),
        PowerMode.Smart => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6")),
        PowerMode.Eco => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")),
        _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7280"))
    };

    /// <summary>Shows a message to the user (set by the Game Mode page).</summary>
    public Action<string>? Notify { get; init; }

    public GameCardViewModel(AppRule rule, Action<AppRule> removeCallback, Action saveCallback, string? steamAppId = null, string? exePath = null, string? customTitle = null)
    {
        Rule = rule;
        _removeCallback = removeCallback;
        _saveCallback = saveCallback;

        SteamAppId = steamAppId;
        ExecutablePath = exePath ?? "";
        ExecutableName = rule.ExecutableName;
        DisplayName = !string.IsNullOrEmpty(customTitle) ? customTitle : GamePosterService.GetCleanTitle(rule.ExecutableName);
        TargetMode = rule.TargetMode;

        LoadPoster();
    }

    public void ReloadPoster() => LoadPoster();

    private void LoadPoster()
    {
        string? posterPath = GamePosterService.ResolvePosterPath(DisplayName, SteamAppId) 
                             ?? GamePosterService.ResolvePosterPath(ExecutableName, SteamAppId);
        if (!string.IsNullOrEmpty(posterPath))
        {
            ApplyPoster(posterPath);
        }
        else
        {
            // Try async download in background
            _ = Task.Run(async () =>
            {
                string? fetched = await GamePosterService.FetchCoverArtAsync(DisplayName, SteamAppId)
                                  ?? await GamePosterService.FetchCoverArtAsync(ExecutableName, SteamAppId);
                if (!string.IsNullOrEmpty(fetched))
                {
                    App.Current?.Dispatcher?.BeginInvoke(() => ApplyPoster(fetched));
                }
            });
        }
    }

    private void ApplyPoster(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.RelativeOrAbsolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            PosterImage = bitmap;
            HasPoster = true;
        }
        catch
        {
            HasPoster = false;
        }
    }

    [RelayCommand]
    public void PlayGame()
    {
        try
        {
            if (!string.IsNullOrEmpty(SteamAppId))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://rungameid/{SteamAppId}",
                    UseShellExecute = true
                });
                return;
            }

            if (!string.IsNullOrEmpty(ExecutablePath) && File.Exists(ExecutablePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ExecutablePath,
                    WorkingDirectory = Path.GetDirectoryName(ExecutablePath),
                    UseShellExecute = true
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = ExecutableName,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Notify?.Invoke($"Could not start {DisplayName}. Check that it is still installed.");
        }
    }

    [RelayCommand]
    public void OpenSettings()
    {
        _gpuService.OpenGraphicsSettings();
    }

    [RelayCommand]
    public void CycleMode()
    {
        TargetMode = TargetMode switch
        {
            PowerMode.Beast => PowerMode.Smart,
            PowerMode.Smart => PowerMode.Eco,
            PowerMode.Eco => PowerMode.Beast,
            _ => PowerMode.Beast
        };

        Rule.TargetMode = TargetMode;
        OnPropertyChanged(nameof(ModeDisplayName));
        OnPropertyChanged(nameof(ModeBadgeBrush));
        _saveCallback();
    }

    [RelayCommand]
    public void Remove()
    {
        _removeCallback(Rule);
    }
}
