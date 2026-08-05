namespace AxePower.Core.Models;

public class PowerStatusInfo
{
    public bool IsPluggedIn { get; set; }
    public int BatteryPercentage { get; set; }
    public bool HasBattery { get; set; }
    public bool IsCharging { get; set; }
}
