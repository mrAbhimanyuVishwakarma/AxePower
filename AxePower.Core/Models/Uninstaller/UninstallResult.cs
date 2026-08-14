namespace AxePower.Core.Models.Uninstaller;

/// <summary>Result of a single application uninstall operation.</summary>
public sealed class UninstallResult
{
    public string ApplicationId { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public bool RequiresReboot { get; set; }
    public bool WasSkipped { get; set; }
    public bool WasDryRun { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public ApplicationType ApplicationType { get; set; }
    public string UninstallMethod { get; set; } = string.Empty;
    public string CommandExecuted { get; set; } = string.Empty;
}
