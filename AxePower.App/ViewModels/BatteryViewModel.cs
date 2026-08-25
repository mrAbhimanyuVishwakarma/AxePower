using System;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Services;
using AxePower.Core.Models;
using AxePower.App.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace AxePower.App.ViewModels;

public partial class BatteryViewModel : ObservableObject
{
    private readonly BatteryService _batteryService;
    private readonly SettingsService? _settingsService;
    private readonly NotificationService? _notificationService;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private string _batteryPercentage = "0%";

    [ObservableProperty]
    private bool _isPluggedIn;

    [ObservableProperty]
    private int _rawBatteryPercentage = 100;

    [ObservableProperty]
    private double _batteryFillWidth = 41;

    [ObservableProperty]
    private string _headerDisplayText = "100% | AC POWER";

    [ObservableProperty]
    private SolidColorBrush _headerAccentBrush = new(Color.FromRgb(0x10, 0xB9, 0x81));

    [ObservableProperty]
    private SolidColorBrush _headerPillBackgroundBrush = new(Color.FromArgb(0x22, 0x10, 0xB9, 0x81));

    [ObservableProperty]
    private SolidColorBrush _headerPillBorderBrush = new(Color.FromArgb(0x44, 0x10, 0xB9, 0x81));

    [ObservableProperty]
    private string _healthPercentage = "--";

    [ObservableProperty]
    private string _healthStatus = "--";

    [ObservableProperty]
    private string _chargingState = "Unknown";

    [ObservableProperty]
    private string _estimatedTimeRemaining = "Calculating...";

    [ObservableProperty]
    private string _powerSource = "Unknown";

    [ObservableProperty]
    private string _cycleCount = "--";

    [ObservableProperty]
    private string _temperature = "--";

    [ObservableProperty]
    private string _currentPowerUsage = "--";

    [ObservableProperty]
    private string _deviceChemistry = "Lithium-Ion";

    [ObservableProperty]
    private string _designCapacity = "--";

    [ObservableProperty]
    private string _fullChargeCapacity = "--";

    [ObservableProperty]
    private string _voltage = "--";

    [ObservableProperty]
    private string _manufacturer = "--";

    [ObservableProperty]
    private string _serialNumber = "--";

    [ObservableProperty]
    private string _manufactureDate = "--";

    [ObservableProperty]
    private bool _conservationMode;

    [ObservableProperty]
    private bool _rapidCharge;

    [ObservableProperty]
    private bool _overnightBatteryCharging;

    public ObservableCollection<double> BatteryUsagePoints { get; } = new();

    public ObservableCollection<double> BatteryTrendPoints { get; } = new();

    public BatteryViewModel(
        BatteryService batteryService,
        SettingsService? settingsService = null,
        NotificationService? notificationService = null)
    {
        _batteryService = batteryService;
        _settingsService = settingsService;
        _notificationService = notificationService;

        if (_settingsService != null)
        {
            var settings = _settingsService.GetSettings();
            _conservationMode = settings.ConservationModeEnabled;
            _rapidCharge = settings.RapidChargeEnabled;
            _overnightBatteryCharging = settings.OvernightBatteryChargingEnabled;
        }

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _timer.Tick += async (s, e) => await RefreshStatusAsync();
        _timer.Start();

        _ = RefreshStatusAsync();
    }

    partial void OnConservationModeChanged(bool value)
    {
        if (value && RapidCharge)
        {
            RapidCharge = false;
        }

        _batteryService.SetConservationMode(value);
        if (_settingsService != null)
        {
            var settings = _settingsService.GetSettings();
            settings.ConservationModeEnabled = value;
            settings.RapidChargeEnabled = RapidCharge;
            _settingsService.SaveSettings(settings);
        }

        _notificationService?.Show(
            value ? "Conservation mode active (capped at 80% to protect battery lifespan)." : "Conservation mode disabled.");
    }

    partial void OnRapidChargeChanged(bool value)
    {
        if (value && ConservationMode)
        {
            ConservationMode = false;
        }

        _batteryService.SetRapidCharge(value);
        if (_settingsService != null)
        {
            var settings = _settingsService.GetSettings();
            settings.RapidChargeEnabled = value;
            settings.ConservationModeEnabled = ConservationMode;
            _settingsService.SaveSettings(settings);
        }

        _notificationService?.Show(
            value ? "Rapid charge enabled for accelerated charging." : "Rapid charge disabled.");
    }

    partial void OnOvernightBatteryChargingChanged(bool value)
    {
        _batteryService.SetOvernightCharging(value);
        if (_settingsService != null)
        {
            var settings = _settingsService.GetSettings();
            settings.OvernightBatteryChargingEnabled = value;
            _settingsService.SaveSettings(settings);
        }

        _notificationService?.Show(
            value ? "Overnight adaptive charging enabled." : "Overnight charging disabled.");
    }

    private async Task RefreshStatusAsync()
    {
        var status = _batteryService.GetCurrentStatus();
        RawBatteryPercentage = status.HasBattery ? status.BatteryPercentage : 100;
        BatteryPercentage = $"{RawBatteryPercentage}%";
        IsPluggedIn = status.IsPluggedIn;
        PowerSource = status.IsPluggedIn ? "AC Power" : "Battery";
        ChargingState = status.HasBattery ? (status.IsPluggedIn ? "Charging" : "Discharging") : "Not Applicable";
        
        // Populate rich hardware telemetry
        HealthStatus = status.HealthStatus;
        HealthPercentage = status.HealthPercentage;
        CycleCount = status.CycleCount;
        DeviceChemistry = status.Chemistry;
        Temperature = status.Temperature;
        CurrentPowerUsage = status.CurrentPowerUsage;
        DesignCapacity = status.DesignCapacity;
        FullChargeCapacity = status.FullChargeCapacity;
        Voltage = status.Voltage;
        Manufacturer = status.Manufacturer;
        SerialNumber = status.SerialNumber;
        ManufactureDate = status.ManufactureDate;
        EstimatedTimeRemaining = status.EstimatedTimeRemaining;

        // Calculate inner fill width (0 to 55 px for scaled battery shell)
        BatteryFillWidth = Math.Max(RawBatteryPercentage > 0 ? 5 : 0, Math.Min(55, 55.0 * RawBatteryPercentage / 100.0));

        if (IsPluggedIn)
        {
            HeaderDisplayText = $"{RawBatteryPercentage}% | AC POWER";
            var accent = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // Emerald green #10B981
            accent.Freeze();
            var bg = new SolidColorBrush(Color.FromArgb(0x22, 0x10, 0xB9, 0x81));
            bg.Freeze();
            var border = new SolidColorBrush(Color.FromArgb(0x44, 0x10, 0xB9, 0x81));
            border.Freeze();

            HeaderAccentBrush = accent;
            HeaderPillBackgroundBrush = bg;
            HeaderPillBorderBrush = border;
        }
        else
        {
            HeaderDisplayText = $"{RawBatteryPercentage}% | ON BATTERY";
            var accent = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // Amber gold #F59E0B
            accent.Freeze();
            var bg = new SolidColorBrush(Color.FromArgb(0x22, 0xF5, 0x9E, 0x0B));
            bg.Freeze();
            var border = new SolidColorBrush(Color.FromArgb(0x44, 0xF5, 0x9E, 0x0B));
            border.Freeze();

            HeaderAccentBrush = accent;
            HeaderPillBackgroundBrush = bg;
            HeaderPillBorderBrush = border;
        }

        var history = await _batteryService.GetHistoryAsync();
        BatteryTrendPoints.Clear();
        foreach (var point in history)
        {
            BatteryTrendPoints.Add(point.Percentage);
        }
    }
}
