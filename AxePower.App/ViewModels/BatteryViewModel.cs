using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using AxePower.Core.Services;
using AxePower.Core.Models;
using System.Collections.ObjectModel;
using System.Linq;

namespace AxePower.App.ViewModels;

public partial class BatteryViewModel : ObservableObject
{
    private readonly BatteryService _batteryService;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private string _batteryPercentage = "0%";

    [ObservableProperty]
    private string _chargingState = "Unknown";

    [ObservableProperty]
    private string _powerSource = "Unknown";

    [ObservableProperty]
    private string _estimatedTimeRemaining = "Calculating...";

    [ObservableProperty]
    private string _healthStatus = "Not reported by this device";

    [ObservableProperty]
    private string _cycleCount = "Not reported by this device";

    [ObservableProperty]
    private string _chemistry = "Not reported by this device";

    [ObservableProperty]
    private string _temperature = "Not reported";

    [ObservableProperty]
    private string _currentPowerUsage = "Not reported";

    [ObservableProperty]
    private string _designCapacity = "Not reported";

    [ObservableProperty]
    private string _fullChargeCapacity = "Not reported";

    [ObservableProperty]
    private string _voltage = "Not reported";

    [ObservableProperty]
    private string _manufacturer = "Not reported";

    [ObservableProperty]
    private string _serialNumber = "Not reported";

    [ObservableProperty]
    private string _manufactureDate = "Not reported";

    public ObservableCollection<double> BatteryUsagePoints { get; } = new();

    public ObservableCollection<double> BatteryTrendPoints { get; } = new();

    public BatteryViewModel(BatteryService batteryService)
    {
        _batteryService = batteryService;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _timer.Tick += async (s, e) => await RefreshStatusAsync();
        _timer.Start();

        _ = RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        var status = _batteryService.GetCurrentStatus();
        BatteryPercentage = status.HasBattery ? $"{status.BatteryPercentage}%" : "100%";
        PowerSource = status.IsPluggedIn ? "AC Power" : "Battery";
        ChargingState = status.HasBattery ? (status.IsPluggedIn ? "Charging" : "Discharging") : "Not Applicable";
        
        var history = await _batteryService.GetHistoryAsync();
        BatteryTrendPoints.Clear();
        foreach (var point in history)
        {
            BatteryTrendPoints.Add(point.Percentage);
        }
    }
}
