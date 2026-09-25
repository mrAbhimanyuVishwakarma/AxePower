using System;
using System.Diagnostics;
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
    private readonly PowerPlanService? _powerPlanService;
    private readonly SettingsService? _settingsService;
    private readonly NotificationService? _notificationService;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private string _currentMode = "Smart";

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

    // ─── Power & Sleep Settings (Screen, Sleep, Hibernate, Buttons & Lid) ─────
    private readonly TimeoutService? _timeoutService;
    private bool _isInitializingPowerSettings;

    public ObservableCollection<TimeoutOption> ScreenTimeoutOptions { get; } = new();
    public ObservableCollection<TimeoutOption> SleepTimeoutOptions { get; } = new();
    public ObservableCollection<TimeoutOption> HibernateTimeoutOptions { get; } = new();
    public ObservableCollection<PowerActionOption> PowerButtonOptions { get; } = new();
    public ObservableCollection<PowerActionOption> SleepButtonOptions { get; } = new();
    public ObservableCollection<PowerActionOption> LidActionOptions { get; } = new();

    [ObservableProperty]
    private TimeoutOption? _selectedScreenTimeoutAc;

    [ObservableProperty]
    private TimeoutOption? _selectedScreenTimeoutDc;

    [ObservableProperty]
    private TimeoutOption? _selectedSleepTimeoutAc;

    [ObservableProperty]
    private TimeoutOption? _selectedSleepTimeoutDc;

    [ObservableProperty]
    private TimeoutOption? _selectedHibernateTimeoutAc;

    [ObservableProperty]
    private TimeoutOption? _selectedHibernateTimeoutDc;

    [ObservableProperty]
    private PowerActionOption? _selectedPowerButtonActionAc;

    [ObservableProperty]
    private PowerActionOption? _selectedPowerButtonActionDc;

    [ObservableProperty]
    private PowerActionOption? _selectedSleepButtonActionAc;

    [ObservableProperty]
    private PowerActionOption? _selectedSleepButtonActionDc;

    [ObservableProperty]
    private PowerActionOption? _selectedLidActionAc;

    [ObservableProperty]
    private PowerActionOption? _selectedLidActionDc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScreenSleepTab))]
    [NotifyPropertyChangedFor(nameof(IsButtonsLidTab))]
    private string _selectedPowerSettingsTab = "ScreenSleep"; // "ScreenSleep" or "ButtonsLid"

    public bool IsScreenSleepTab => SelectedPowerSettingsTab == "ScreenSleep";
    public bool IsButtonsLidTab => SelectedPowerSettingsTab == "ButtonsLid";

    [RelayCommand]
    public void SelectPowerSettingsTab(string tab)
    {
        SelectedPowerSettingsTab = tab;
    }

    public BatteryViewModel(
        BatteryService batteryService,
        PowerPlanService? powerPlanService = null,
        SettingsService? settingsService = null,
        NotificationService? notificationService = null,
        TimeoutService? timeoutService = null)
    {
        _batteryService = batteryService;
        _powerPlanService = powerPlanService;
        _settingsService = settingsService;
        _notificationService = notificationService;
        _timeoutService = timeoutService;

        if (_settingsService != null)
        {
            var settings = _settingsService.GetSettings();
            _conservationMode = settings.ConservationModeEnabled;
            _rapidCharge = settings.RapidChargeEnabled;
            _overnightBatteryCharging = settings.OvernightBatteryChargingEnabled;
        }

        PopulatePowerOptions();
        _ = LoadPowerSettingsAsync();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _timer.Tick += async (s, e) => await RefreshStatusAsync();
        _timer.Start();

        _ = RefreshStatusAsync();
    }

    private void PopulatePowerOptions()
    {
        ScreenTimeoutOptions.Clear();
        int[] screenMinutes = [1, 2, 3, 5, 10, 15, 20, 25, 30, 45, 60, 120, 180, 240, 300, 0];
        foreach (var m in screenMinutes)
        {
            string label = m == 0 ? "Never" : (m >= 60 ? (m % 60 == 0 ? $"{m / 60} hour{(m >= 120 ? "s" : "")}" : $"{m} minutes") : $"{m} minute{(m > 1 ? "s" : "")}");
            ScreenTimeoutOptions.Add(new TimeoutOption { Minutes = m, DisplayName = label });
        }

        SleepTimeoutOptions.Clear();
        int[] sleepMinutes = [1, 2, 3, 5, 10, 15, 20, 25, 30, 45, 60, 120, 180, 240, 300, 0];
        foreach (var m in sleepMinutes)
        {
            string label = m == 0 ? "Never" : (m >= 60 ? (m % 60 == 0 ? $"{m / 60} hour{(m >= 120 ? "s" : "")}" : $"{m} minutes") : $"{m} minute{(m > 1 ? "s" : "")}");
            SleepTimeoutOptions.Add(new TimeoutOption { Minutes = m, DisplayName = label });
        }

        HibernateTimeoutOptions.Clear();
        int[] hibMinutes = [0, 15, 30, 45, 60, 120, 180, 240, 300, 360, 720, 1440];
        foreach (var m in hibMinutes)
        {
            string label = m == 0 ? "Never" : (m >= 60 ? $"{m / 60} hour{(m >= 120 ? "s" : "")}" : $"{m} minutes");
            HibernateTimeoutOptions.Add(new TimeoutOption { Minutes = m, DisplayName = label });
        }

        PowerButtonOptions.Clear();
        PowerButtonOptions.Add(new PowerActionOption { Index = 0, DisplayName = "Do nothing" });
        PowerButtonOptions.Add(new PowerActionOption { Index = 1, DisplayName = "Sleep" });
        PowerButtonOptions.Add(new PowerActionOption { Index = 2, DisplayName = "Hibernate" });
        PowerButtonOptions.Add(new PowerActionOption { Index = 3, DisplayName = "Shut down" });
        PowerButtonOptions.Add(new PowerActionOption { Index = 4, DisplayName = "Turn off the display" });

        SleepButtonOptions.Clear();
        SleepButtonOptions.Add(new PowerActionOption { Index = 0, DisplayName = "Do nothing" });
        SleepButtonOptions.Add(new PowerActionOption { Index = 1, DisplayName = "Sleep" });
        SleepButtonOptions.Add(new PowerActionOption { Index = 2, DisplayName = "Hibernate" });
        SleepButtonOptions.Add(new PowerActionOption { Index = 3, DisplayName = "Shut down" });
        SleepButtonOptions.Add(new PowerActionOption { Index = 4, DisplayName = "Turn off the display" });

        LidActionOptions.Clear();
        LidActionOptions.Add(new PowerActionOption { Index = 0, DisplayName = "Do nothing" });
        LidActionOptions.Add(new PowerActionOption { Index = 1, DisplayName = "Sleep" });
        LidActionOptions.Add(new PowerActionOption { Index = 2, DisplayName = "Hibernate" });
        LidActionOptions.Add(new PowerActionOption { Index = 3, DisplayName = "Shut down" });
    }

    private async Task LoadPowerSettingsAsync()
    {
        if (_timeoutService == null) return;
        _isInitializingPowerSettings = true;
        try
        {
            var (screenAc, screenDc) = await _timeoutService.GetMonitorTimeoutsAsync();
            SelectedScreenTimeoutAc = ScreenTimeoutOptions.FirstOrDefault(o => o.Minutes == screenAc) ?? ScreenTimeoutOptions.FirstOrDefault(o => o.Minutes == 0);
            SelectedScreenTimeoutDc = ScreenTimeoutOptions.FirstOrDefault(o => o.Minutes == screenDc) ?? ScreenTimeoutOptions.FirstOrDefault(o => o.Minutes == 3);

            var (sleepAc, sleepDc) = await _timeoutService.GetStandbyTimeoutsAsync();
            SelectedSleepTimeoutAc = SleepTimeoutOptions.FirstOrDefault(o => o.Minutes == sleepAc) ?? SleepTimeoutOptions.FirstOrDefault(o => o.Minutes == 0);
            SelectedSleepTimeoutDc = SleepTimeoutOptions.FirstOrDefault(o => o.Minutes == sleepDc) ?? SleepTimeoutOptions.FirstOrDefault(o => o.Minutes == 10);

            var (hibAc, hibDc) = await _timeoutService.GetHibernateTimeoutsAsync();
            SelectedHibernateTimeoutAc = HibernateTimeoutOptions.FirstOrDefault(o => o.Minutes == hibAc) ?? HibernateTimeoutOptions.FirstOrDefault(o => o.Minutes == 0);
            SelectedHibernateTimeoutDc = HibernateTimeoutOptions.FirstOrDefault(o => o.Minutes == hibDc) ?? HibernateTimeoutOptions.FirstOrDefault(o => o.Minutes == 180);

            var (pwrAc, pwrDc) = await _timeoutService.GetPowerButtonActionAsync();
            SelectedPowerButtonActionAc = PowerButtonOptions.FirstOrDefault(o => o.Index == pwrAc) ?? PowerButtonOptions.FirstOrDefault(o => o.Index == 1);
            SelectedPowerButtonActionDc = PowerButtonOptions.FirstOrDefault(o => o.Index == pwrDc) ?? PowerButtonOptions.FirstOrDefault(o => o.Index == 2);

            var (slpAc, slpDc) = await _timeoutService.GetSleepButtonActionAsync();
            SelectedSleepButtonActionAc = SleepButtonOptions.FirstOrDefault(o => o.Index == slpAc) ?? SleepButtonOptions.FirstOrDefault(o => o.Index == 1);
            SelectedSleepButtonActionDc = SleepButtonOptions.FirstOrDefault(o => o.Index == slpDc) ?? SleepButtonOptions.FirstOrDefault(o => o.Index == 1);

            var (lidAc, lidDc) = await _timeoutService.GetLidActionAsync();
            SelectedLidActionAc = LidActionOptions.FirstOrDefault(o => o.Index == lidAc) ?? LidActionOptions.FirstOrDefault(o => o.Index == 0);
            SelectedLidActionDc = LidActionOptions.FirstOrDefault(o => o.Index == lidDc) ?? LidActionOptions.FirstOrDefault(o => o.Index == 1);
        }
        catch { }
        finally
        {
            _isInitializingPowerSettings = false;
        }
    }

    partial void OnSelectedScreenTimeoutAcChanged(TimeoutOption? value)
    {
        if (_isInitializingPowerSettings || value == null || _timeoutService == null) return;
        _ = _timeoutService.SetMonitorTimeoutAsync(value.Minutes, true);
    }

    partial void OnSelectedScreenTimeoutDcChanged(TimeoutOption? value)
    {
        if (_isInitializingPowerSettings || value == null || _timeoutService == null) return;
        _ = _timeoutService.SetMonitorTimeoutAsync(value.Minutes, false);
    }

    partial void OnSelectedSleepTimeoutAcChanged(TimeoutOption? value)
    {
        if (_isInitializingPowerSettings || value == null || _timeoutService == null) return;
        _ = _timeoutService.SetStandbyTimeoutAsync(value.Minutes, true);
    }

    partial void OnSelectedSleepTimeoutDcChanged(TimeoutOption? value)
    {
        if (_isInitializingPowerSettings || value == null || _timeoutService == null) return;
        _ = _timeoutService.SetStandbyTimeoutAsync(value.Minutes, false);
    }

    partial void OnSelectedHibernateTimeoutAcChanged(TimeoutOption? value)
    {
        if (_isInitializingPowerSettings || value == null || _timeoutService == null) return;
        _ = _timeoutService.SetHibernateTimeoutAsync(value.Minutes, true);
    }

    partial void OnSelectedHibernateTimeoutDcChanged(TimeoutOption? value)
    {
        if (_isInitializingPowerSettings || value == null || _timeoutService == null) return;
        _ = _timeoutService.SetHibernateTimeoutAsync(value.Minutes, false);
    }

    partial void OnSelectedPowerButtonActionAcChanged(PowerActionOption? value)
    {
        if (_isInitializingPowerSettings || value == null || _timeoutService == null) return;
        _ = _timeoutService.SetPowerButtonActionAsync(value.Index, true);
    }

    partial void OnSelectedPowerButtonActionDcChanged(PowerActionOption? value)
    {
        if (_isInitializingPowerSettings || value == null || _timeoutService == null) return;
        _ = _timeoutService.SetPowerButtonActionAsync(value.Index, false);
    }

    partial void OnSelectedSleepButtonActionAcChanged(PowerActionOption? value)
    {
        if (_isInitializingPowerSettings || value == null || _timeoutService == null) return;
        _ = _timeoutService.SetSleepButtonActionAsync(value.Index, true);
    }

    partial void OnSelectedSleepButtonActionDcChanged(PowerActionOption? value)
    {
        if (_isInitializingPowerSettings || value == null || _timeoutService == null) return;
        _ = _timeoutService.SetSleepButtonActionAsync(value.Index, false);
    }

    partial void OnSelectedLidActionAcChanged(PowerActionOption? value)
    {
        if (_isInitializingPowerSettings || value == null || _timeoutService == null) return;
        _ = _timeoutService.SetLidActionAsync(value.Index, true);
    }

    partial void OnSelectedLidActionDcChanged(PowerActionOption? value)
    {
        if (_isInitializingPowerSettings || value == null || _timeoutService == null) return;
        _ = _timeoutService.SetLidActionAsync(value.Index, false);
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

        if (_powerPlanService != null)
        {
            try
            {
                var mode = await _powerPlanService.GetActivePowerModeAsync();
                CurrentMode = mode switch
                {
                    PowerMode.Eco => "Eco",
                    PowerMode.Smart => "Smart",
                    PowerMode.Beast => "Beast",
                    _ => mode.ToString()
                };
            }
            catch { }
        }
    }

    [RelayCommand]
    private void OpenPowerSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:batterysaver",
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:powersleep",
                    UseShellExecute = true
                });
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "ms-settings:",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }
    }

    [RelayCommand]
    private void OpenPowerSleepSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:powersleep",
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "control.exe",
                    Arguments = "/name Microsoft.PowerOptions",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}

public class TimeoutOption
{
    public int Minutes { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public override string ToString() => DisplayName;
}

public class PowerActionOption
{
    public int Index { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public override string ToString() => DisplayName;
}
