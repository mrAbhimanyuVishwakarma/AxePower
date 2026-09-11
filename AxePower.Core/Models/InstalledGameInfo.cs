namespace AxePower.Core.Models;

public class InstalledGameInfo
{
    public string Name { get; set; } = string.Empty;
    public string ExecutableName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string? SteamAppId { get; set; }
    public string? BannerUrl { get; set; }
}
