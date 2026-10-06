using System;
using System.Threading;

namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// Represents an actively connected or authenticated client device in the LAN Drop session.
/// </summary>
public sealed class LanDropClientSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ClientIp { get; init; } = string.Empty;
    public string DeviceName { get; set; } = "Mobile Client";
    public string UserAgent { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; init; } = DateTime.Now;
    public DateTime LastActivity { get; set; } = DateTime.Now;

    public string FormattedConnectedTime => ConnectedAt.ToString("HH:mm:ss");
    public string FormattedLastActivity => LastActivity.ToString("HH:mm:ss");

    internal CancellationTokenSource SessionCts { get; } = new();
}
