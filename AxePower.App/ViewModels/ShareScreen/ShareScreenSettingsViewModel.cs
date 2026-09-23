using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.ShareScreen;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels.ShareScreen;

/// <summary>
/// Every Share Screen preference, in one place.
///
/// These live on the Settings page rather than on the stage, so the sharing page itself stays
/// nothing but the picture and the controls. The stage's gear navigates here. A single instance
/// is shared by both surfaces, so a change made in Settings is what the next session uses without
/// any copying back and forth.
/// </summary>
public partial class ShareScreenSettingsViewModel : ObservableObject
{
    private readonly IShareDeviceProvider _deviceProvider;
    private readonly IShareToolProvider _toolProvider;
    private readonly SettingsService _settingsService;
    private readonly LogService _logService;

    private bool _suppressPersist;

    public ObservableCollection<CaptureDisplay> Displays { get; } = new();
    public ObservableCollection<QualityPreset> QualityPresets { get; } = new(QualityPreset.All);
    public ObservableCollection<ShareAudioDevice> SystemAudioDevices { get; } = new();
    public ObservableCollection<ShareAudioDevice> Microphones { get; } = new();

    [ObservableProperty]
    private CaptureDisplay? _selectedDisplay;

    [ObservableProperty]
    private QualityPreset _selectedQuality = QualityPreset.Balanced1080;

    [ObservableProperty]
    private ShareAudioMode _audioMode = ShareAudioMode.SystemAudio;

    [ObservableProperty]
    private ShareAudioDevice? _selectedSystemAudioDevice;

    [ObservableProperty]
    private ShareAudioDevice? _selectedMicrophone;

    [ObservableProperty]
    private bool _hideCursor;

    [ObservableProperty]
    private bool _adaptiveQuality = true;

    /// <summary>Hostname viewers should use, for a mesh VPN or a forwarded port. Empty = work it out.</summary>
    [ObservableProperty]
    private string _viewerHostOverride = string.Empty;

    [ObservableProperty]
    private string _toolStatus = string.Empty;

    /// <summary>
    /// Whether the stage offers remote control at all.
    ///
    /// Off by default. Turning it on only reveals the button: each session still has to be granted
    /// explicitly, and the viewer still needs a code the watch link does not carry.
    /// </summary>
    [ObservableProperty]
    private bool _allowRemoteControl;

    public bool ShowSystemAudioPicker => AudioMode == ShareAudioMode.SystemAudio;
    public bool ShowMicrophonePicker => AudioMode == ShareAudioMode.Microphone;

    /// <summary>Short line for the stage to show without repeating the whole settings card.</summary>
    public string SourceSummary
    {
        get
        {
            var display = SelectedDisplay?.FriendlyName ?? "Primary display";
            var audio = AudioMode switch
            {
                ShareAudioMode.SystemAudio => SelectedSystemAudioDevice?.Name ?? "System audio",
                ShareAudioMode.Microphone => SelectedMicrophone?.Name ?? "Microphone",
                _ => "No audio"
            };
            return $"{display}  ·  {SelectedQuality.ShortLabel}  ·  {audio}";
        }
    }

    public ShareScreenSettingsViewModel(
        IShareDeviceProvider deviceProvider,
        IShareToolProvider toolProvider,
        SettingsService settingsService,
        LogService logService)
    {
        _deviceProvider = deviceProvider;
        _toolProvider = toolProvider;
        _settingsService = settingsService;
        _logService = logService;

        LoadDevices();
        LoadSettings();
        _ = RefreshToolStatusAsync();
    }

    // -- Population ----------------------------------------------------------

    private void LoadDevices()
    {
        Displays.Clear();
        foreach (var display in _deviceProvider.GetDisplays()) Displays.Add(display);

        SystemAudioDevices.Clear();
        foreach (var device in _deviceProvider.GetRenderDevices()) SystemAudioDevices.Add(device);

        Microphones.Clear();
        foreach (var device in _deviceProvider.GetCaptureDevices()) Microphones.Add(device);
    }

    private void LoadSettings()
    {
        var settings = _settingsService.GetSettings().ShareScreen ?? new ShareScreenSettings();

        _suppressPersist = true;
        try
        {
            SelectedDisplay = Displays.FirstOrDefault(d => d.OutputIndex == settings.DisplayIndex)
                              ?? Displays.FirstOrDefault(d => d.IsPrimary)
                              ?? Displays.FirstOrDefault();
            SelectedQuality = QualityPreset.For(settings.Quality);
            AudioMode = settings.AudioMode;
            HideCursor = settings.HideCursor;
            AdaptiveQuality = settings.AdaptiveQuality;
            AllowRemoteControl = settings.AllowRemoteControl;
            ViewerHostOverride = settings.ViewerHostOverride ?? string.Empty;

            SelectedSystemAudioDevice =
                SystemAudioDevices.FirstOrDefault(d => d.Id == settings.SystemAudioDeviceId)
                ?? SystemAudioDevices.FirstOrDefault(d => d.IsDefault)
                ?? SystemAudioDevices.FirstOrDefault();

            SelectedMicrophone =
                Microphones.FirstOrDefault(d => d.Id == settings.MicrophoneDeviceId)
                ?? Microphones.FirstOrDefault(d => d.IsDefault)
                ?? Microphones.FirstOrDefault();
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    /// <summary>Says up front whether the tools are there, rather than failing at Start.</summary>
    public async Task RefreshToolStatusAsync()
    {
        var ffmpeg = await Task.Run(() => _toolProvider.FindFfmpeg());
        var mediaMtx = await Task.Run(() => _toolProvider.FindMediaMtx());

        ToolStatus = (ffmpeg.IsAvailable, mediaMtx.IsAvailable) switch
        {
            (true, true) => $"FFmpeg {ffmpeg.Version} and the media server are ready.",
            (true, false) => "The media server will be downloaded the first time you share.",
            (false, true) => "FFmpeg will be downloaded the first time you share.",
            _ => "FFmpeg and the media server will be downloaded the first time you share (about 100 MB)."
        };
    }

    // -- Persistence ---------------------------------------------------------

    /// <summary>
    /// Writes the pickers back to settings.json. Guarded so that loading the page never rewrites
    /// the file and wakes every other settings listener.
    /// </summary>
    private void Persist()
    {
        if (_suppressPersist) return;

        try
        {
            var settings = _settingsService.GetSettings();
            var share = settings.ShareScreen ?? new ShareScreenSettings();

            share.DisplayIndex = SelectedDisplay?.OutputIndex ?? 0;
            share.Quality = SelectedQuality.Quality;
            share.AudioMode = AudioMode;
            share.SystemAudioDeviceId = SelectedSystemAudioDevice?.Id;
            share.MicrophoneDeviceId = SelectedMicrophone?.Id;
            share.HideCursor = HideCursor;
            share.AdaptiveQuality = AdaptiveQuality;
            share.AllowRemoteControl = AllowRemoteControl;
            share.ViewerHostOverride = string.IsNullOrWhiteSpace(ViewerHostOverride) ? null : ViewerHostOverride.Trim();

            settings.ShareScreen = share;
            _settingsService.SaveSettings(settings, notifyListeners: false);
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not save the sharing preferences", ex);
        }

        OnPropertyChanged(nameof(SourceSummary));
    }

    /// <summary>The settings a session should start with, taken from the current pickers.</summary>
    public ShareScreenSettings BuildSessionSettings()
    {
        var settings = (_settingsService.GetSettings().ShareScreen ?? new ShareScreenSettings()).Clone();

        settings.DisplayIndex = SelectedDisplay?.OutputIndex ?? 0;
        settings.Quality = SelectedQuality.Quality;
        settings.AudioMode = AudioMode;
        settings.SystemAudioDeviceId = SelectedSystemAudioDevice?.Id;
        settings.MicrophoneDeviceId = SelectedMicrophone?.Id;
        settings.HideCursor = HideCursor;
        settings.AdaptiveQuality = AdaptiveQuality;
        settings.AllowRemoteControl = AllowRemoteControl;
        settings.ViewerHostOverride = string.IsNullOrWhiteSpace(ViewerHostOverride) ? null : ViewerHostOverride.Trim();

        return settings;
    }

    partial void OnSelectedDisplayChanged(CaptureDisplay? value) => Persist();
    partial void OnSelectedQualityChanged(QualityPreset value) => Persist();
    partial void OnSelectedSystemAudioDeviceChanged(ShareAudioDevice? value) => Persist();
    partial void OnSelectedMicrophoneChanged(ShareAudioDevice? value) => Persist();
    partial void OnHideCursorChanged(bool value) => Persist();
    partial void OnAdaptiveQualityChanged(bool value) => Persist();
    partial void OnAllowRemoteControlChanged(bool value) => Persist();
    partial void OnViewerHostOverrideChanged(string value) => Persist();

    partial void OnAudioModeChanged(ShareAudioMode value)
    {
        OnPropertyChanged(nameof(ShowSystemAudioPicker));
        OnPropertyChanged(nameof(ShowMicrophonePicker));
        Persist();
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        LoadDevices();
        LoadSettings();
        _ = RefreshToolStatusAsync();
    }
}
