using System;

namespace AxePower.Core.Models;

public class BatteryHistoryPoint
{
    public DateTime Timestamp { get; set; }
    public int Percentage { get; set; }
    public bool IsPluggedIn { get; set; }
}
