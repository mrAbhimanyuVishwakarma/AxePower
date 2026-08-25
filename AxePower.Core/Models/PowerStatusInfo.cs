namespace AxePower.Core.Models;

public class PowerStatusInfo
{
    public bool IsPluggedIn { get; set; }
    public int BatteryPercentage { get; set; }
    public bool HasBattery { get; set; }
    public bool IsCharging { get; set; }
    public string HealthStatus { get; set; } = "Good";
    public string HealthPercentage { get; set; } = "100%";
    public string CycleCount { get; set; } = "Not reported";
    public string Chemistry { get; set; } = "Lithium-Ion";
    public string Temperature { get; set; } = "Normal (32°C)";
    public string CurrentPowerUsage { get; set; } = "0.0 W";
    public string DesignCapacity { get; set; } = "Not reported";
    public string FullChargeCapacity { get; set; } = "Not reported";
    public string Voltage { get; set; } = "Not reported";
    public string Manufacturer { get; set; } = "Not reported";
    public string SerialNumber { get; set; } = "Not reported";
    public string ManufactureDate { get; set; } = "Not reported";
    public string EstimatedTimeRemaining { get; set; } = "Calculating...";
}
