using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>What an uninstaller's exit code suggests. Verification always runs afterwards.</summary>
public enum ExecutionVerdict
{
    /// <summary>Finished normally; verification decides.</summary>
    Finished,

    /// <summary>Finished and asked for a restart (3010 / 1641).</summary>
    FinishedRebootRequired,

    /// <summary>msiexec 1605: the product isn't installed.</summary>
    UnknownProduct,

    /// <summary>The user cancelled, refused UAC, or closed the uninstaller.</summary>
    Cancelled,

    /// <summary>Another installation is in progress (msiexec 1618); try again later.</summary>
    Busy,

    /// <summary>A known failure code.</summary>
    Failed,

    /// <summary>A non-zero code we don't recognise; verification decides.</summary>
    UnknownError,

    /// <summary>We stopped waiting for the uninstaller.</summary>
    WaitAbandoned,

    /// <summary>The broken entry was backed up and deleted.</summary>
    EntryRemoved,

    /// <summary>The action needs administrator rights.</summary>
    NeedsAdmin,

    /// <summary>Dry run: nothing ran.</summary>
    DryRun,
}

/// <summary>The exit-code table from BCU and the Windows Installer documentation.</summary>
public static class ExitCodeInterpreter
{
    public const int Success = 0;
    public const int MsiUserExit = 1602;
    public const int MsiInstallFailure = 1603;
    public const int MsiUnknownProduct = 1605;
    public const int MsiAlreadyRunning = 1618;
    public const int NsisOk1627 = 1627;
    public const int RebootInitiated = 1641;
    public const int RebootRequired = 3010;
    public const int StatusControlCExit = -1073741510; // 0xC000013A: Ctrl+C / console window closed
    public const int ErrorCancelled = 1223;            // Win32: the user refused the UAC prompt

    public static (ExecutionVerdict Verdict, string Message) Interpret(UninstallerKind kind, int? exitCode)
    {
        if (exitCode is null)
            return (ExecutionVerdict.Finished, string.Empty);

        var code = exitCode.Value;
        if (code == Success)
            return (ExecutionVerdict.Finished, string.Empty);

        if (code is RebootRequired or RebootInitiated)
            return (ExecutionVerdict.FinishedRebootRequired, "The uninstaller asked for a restart.");

        if (code == StatusControlCExit)
            return (ExecutionVerdict.Cancelled, "The uninstaller was closed before it finished.");

        switch (kind)
        {
            case UninstallerKind.Msi:
            case UninstallerKind.BurnBundle:
                switch (code)
                {
                    case MsiUserExit:
                        return (ExecutionVerdict.Cancelled, "The uninstall was cancelled.");
                    case MsiUnknownProduct when kind == UninstallerKind.Msi:
                        return (ExecutionVerdict.UnknownProduct, "Windows Installer doesn't know this product (1605).");
                    case MsiAlreadyRunning:
                        return (ExecutionVerdict.Busy, "Another installation is in progress (1618).");
                    case MsiInstallFailure:
                        return (ExecutionVerdict.Failed, "Windows Installer reported a fatal error (1603). See the log.");
                }
                break;

            case UninstallerKind.Nsis:
                switch (code)
                {
                    case 1:
                        return (ExecutionVerdict.Cancelled, "The uninstall was cancelled.");
                    case 2:
                        return (ExecutionVerdict.Cancelled, "The uninstaller aborted.");
                    case NsisOk1627:
                        return (ExecutionVerdict.Finished, string.Empty);
                }
                break;
        }

        return (ExecutionVerdict.UnknownError, $"The uninstaller exited with code {code}.");
    }
}
