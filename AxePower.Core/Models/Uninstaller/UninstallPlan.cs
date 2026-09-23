namespace AxePower.Core.Models.Uninstaller;

/// <summary>
/// Exactly what will run to uninstall one application. It is built once by
/// <c>UninstallCommandBuilder</c> and used by the confirmation dialog, the dry run and the
/// executors, so there is one source of truth for "what will run".
/// </summary>
public sealed record UninstallPlan
{
    public UninstallerKind Kind { get; init; }

    /// <summary>Full path of the program to start (empty for broken entries).</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Arguments, exactly as passed to the program.</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>True when the command runs without the uninstaller's own UI.</summary>
    public bool IsQuiet { get; init; }

    /// <summary>Short human description, e.g. "Windows Installer (msiexec), interactive".</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Verbose MSI log written by this command, when there is one.</summary>
    public string LogPath { get; init; } = string.Empty;

    /// <summary>Why no command could be built; null when the plan can run.</summary>
    public string? Error { get; init; }

    public bool CanRun => Error is null && !string.IsNullOrEmpty(FileName);

    /// <summary>The command line as a user would type it, for dialogs and logs.</summary>
    public string CommandLine =>
        string.IsNullOrEmpty(FileName) ? string.Empty
        : string.IsNullOrEmpty(Arguments) ? $"\"{FileName}\""
        : $"\"{FileName}\" {Arguments}";
}
