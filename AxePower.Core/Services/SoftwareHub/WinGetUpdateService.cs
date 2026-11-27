using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Processes;
using Microsoft.Win32.SafeHandles;

namespace AxePower.Core.Services.SoftwareHub;

public sealed record WinGetUpdateCandidate(
    string Name, string Id, string InstalledVersion, string AvailableVersion, string Source, bool CanSelect);

public sealed record WinGetUpdateScanResult(
    IReadOnlyList<WinGetUpdateCandidate> Candidates, bool Succeeded, string? Detail,
    bool CanBulkUpgrade = false, bool IsPartial = false);

public enum WinGetUpdateOutcome
{
    Failed,
    Applied,
    Finished,
    NoApplicableUpdates,
    RestartRequired,
    Refused,
    Partial,
    Cancelled
}

public sealed record WinGetUpdateResult(
    bool Succeeded, bool RebootRequired, string Detail, WinGetUpdateOutcome Outcome = WinGetUpdateOutcome.Failed,
    int? ExitCode = null);

public interface IWinGetUpdateService
{
    Task<WinGetUpdateScanResult> ScanAsync(CancellationToken cancellationToken = default);
    Task<WinGetUpdateResult> UpgradeAsync(WinGetUpdateCandidate candidate, IProgress<string> progress, CancellationToken cancellationToken = default);
    Task<WinGetUpdateResult> UpgradeAllAsync(bool includeUnknown, IProgress<string> progress, CancellationToken cancellationToken = default);
}

/// <summary>WinGet discovery and explicitly approved upgrades for the Update PC overview.</summary>
public sealed class WinGetUpdateService : IWinGetUpdateService
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(30);
    private const string WrongAccount = "WinGet needs the Windows account signed in to this desktop. Reopen AxePower normally and try again.";
    private static readonly string[] PreviewSources = { "winget", "msstore" };
    // The CLI passes versions through as strings. Restrict the display parser to unambiguous,
    // complete version tokens and leave unusual rows to WinGet's own review.
    private static readonly Regex VersionToken = new(@"\A[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}\z", RegexOptions.CultureInvariant);
    private static readonly Regex HeaderParts = new(@"\S(?:.*?\S)?(?= {2,}|$)", RegexOptions.CultureInvariant);
    private static readonly Regex HeaderWords = new(@"\S+", RegexOptions.CultureInvariant);

    private readonly IProcessRunner _runner;
    private readonly Func<bool> _inDesktopAccount;

    public WinGetUpdateService(IProcessRunner runner) : this(runner, IsInDesktopAccount) { }

    internal WinGetUpdateService(IProcessRunner runner, Func<bool> inDesktopAccount)
    {
        _runner = runner;
        _inDesktopAccount = inDesktopAccount;
    }

    public async Task<WinGetUpdateScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_inDesktopAccount())
            return new WinGetUpdateScanResult(Array.Empty<WinGetUpdateCandidate>(), false, WrongAccount);

        var candidates = new List<WinGetUpdateCandidate>();
        var notices = new List<string>();
        bool scanned = false;
        bool canBulk = false;
        foreach (string source in PreviewSources)
        {
            var preview = await ScanSourceAsync(source, null, cancellationToken).ConfigureAwait(false);
            scanned |= preview.Succeeded;
            canBulk |= preview.CanBulkUpgrade;
            candidates.AddRange(preview.Candidates);
            if (preview.Detail is not null) notices.Add($"{source}: {preview.Detail}");
            if (!preview.CanBulkUpgrade && preview.Detail == MissingWinGet)
                break;
        }

        return new WinGetUpdateScanResult(candidates, scanned, notices.Count == 0 ? null : string.Join(" ", notices),
            canBulk, IsPartial: scanned && notices.Count > 0);
    }

    public async Task<WinGetUpdateResult> UpgradeAsync(
        WinGetUpdateCandidate candidate, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Cancelled();
        if (!candidate.CanSelect || !PackageInput.IsValidWinGetId(candidate.Id) || !IsVersion(candidate.AvailableVersion) ||
            !PreviewSources.Contains(candidate.Source, StringComparer.Ordinal))
            return Refused("This package could not be matched to an exact WinGet source and version. Check for updates again.");
        if (!_inDesktopAccount())
            return Refused(WrongAccount);

        // Individual upgrade can honor fewer pin restrictions than --all. Recheck the list that
        // honors pins at apply time and accept only the exact reviewed package and versions.
        // https://learn.microsoft.com/en-us/windows/package-manager/winget/pinning
        WinGetUpdateScanResult preview;
        try
        {
            preview = await ScanSourceAsync(candidate.Source, candidate.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        if (!preview.Succeeded || preview.Detail is not null || preview.Candidates.Count != 1 ||
            !SameSelection(preview.Candidates[0], candidate))
            return Refused(preview.Detail ?? "The reviewed update could not be verified. Check for updates again before installing it.");
        if (cancellationToken.IsCancellationRequested)
            return Cancelled();
        if (!_inDesktopAccount())
            return Refused(WrongAccount);

        var command = InstallPlanner.WinGetUpgrade(candidate.Id, candidate.Source, candidate.AvailableVersion);
        return await RunUpgradeAsync(command, progress, bulk: false, cancellationToken).ConfigureAwait(false);
    }

    public Task<WinGetUpdateResult> UpgradeAllAsync(bool includeUnknown, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(Cancelled());
        if (!_inDesktopAccount())
            return Task.FromResult(Refused(WrongAccount));

        return RunUpgradeAsync(InstallPlanner.WinGetUpgradeAll(includeUnknown), progress, bulk: true, cancellationToken);
    }

    private const string MissingWinGet = "WinGet is unavailable. Check whether App Installer is installed and enabled for this user.";

    private async Task<WinGetUpdateScanResult> ScanSourceAsync(string source, string? id, CancellationToken cancellationToken)
    {
        // https://learn.microsoft.com/en-us/windows/package-manager/winget/list
        // Discovery leaves source agreements, unknown versions and pinned packages alone.
        var args = new List<string> { "list", "--upgrade-available", "--source", source, "--disable-interactivity" };
        if (id is not null) args.AddRange(new[] { "--id", id, "--exact" });

        var lines = new List<string>();
        int stderr = 0;
        var result = await _runner.RunAsync(new ProcessCommand(AllowedTool.WinGet, args, ScanTimeout), line =>
        {
            if (line.IsError)
            {
                if (!string.IsNullOrWhiteSpace(line.Text)) Interlocked.Increment(ref stderr);
            }
            else
            {
                lines.Add(line.Text);
            }
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (result.Outcome == ProcessRunOutcome.NotAllowed)
            return new WinGetUpdateScanResult(Array.Empty<WinGetUpdateCandidate>(), false, MissingWinGet);
        var code = Code(result);
        bool couldRunBulk = result.Outcome == ProcessRunOutcome.Exited &&
            code is (0 or 0x8A150014 or 0x8A15002B or 0x8A150046);
        if (result.Outcome != ProcessRunOutcome.Exited || code is not (0 or 0x8A150014 or 0x8A15002B) || stderr != 0)
        {
            return new WinGetUpdateScanResult(Array.Empty<WinGetUpdateCandidate>(), false,
                FailureDetail(result, "scan"), couldRunBulk);
        }
        if (code is 0x8A150014 or 0x8A15002B)
            return new WinGetUpdateScanResult(Array.Empty<WinGetUpdateCandidate>(), true, null, couldRunBulk);

        return ParsePreview(lines, source);
    }

    internal static WinGetUpdateScanResult ParsePreview(IEnumerable<string> output, string source)
    {
        // WinGet places its localized column labels above a line of dashes. Prefer the
        // infrastructure of the table to translated labels, and accept only its first,
        // unqualified table. Pinned/explicit-upgrade tables are preceded by a message.
        // https://github.com/microsoft/winget-cli/blob/master/src/AppInstallerCLICore/Workflows/WorkflowBase.cpp
        var lines = output.Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l) && !IsProgressLine(l)).ToArray();
        if (lines.Length == 1 && lines[0].Trim() is "No available upgrade found." or "No installed package found matching input criteria.")
            return new WinGetUpdateScanResult(Array.Empty<WinGetUpdateCandidate>(), true, null, true);

        if (lines.Length < 3 || lines[1].Length < 15 || !lines[1].All(c => c == '-'))
            return UnreadablePreview();

        var parts = HeaderParts.Matches(lines[0]);
        if (parts.Count is not (4 or 5))
            parts = HeaderWords.Matches(lines[0]);
        if (parts.Count is not (4 or 5) || parts[0].Index != 0)
            return UnreadablePreview();

        int[] starts = parts.Select(part => part.Index).ToArray();
        var candidates = new List<WinGetUpdateCandidate>();
        bool skipped = false;
        foreach (var row in lines.Skip(2))
        {
            // The total is printed directly after the first table; any later tables may be pinned.
            if (row.Length <= starts[3] &&
                row.TrimStart().Length > 0 && char.IsAsciiDigit(row.TrimStart()[0]))
                break;
            if (row.All(c => c == '-'))
            {
                skipped = true;
                break;
            }
            if (!TryReadRow(row, starts, source, out var candidate))
            {
                skipped = true;
                continue;
            }
            candidates.Add(candidate!);
        }

        var uniquelyIdentified = candidates.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1).Select(group => group.First()).ToArray();
        if (uniquelyIdentified.Length == 0) return UnreadablePreview();
        string? detail = skipped || uniquelyIdentified.Length != candidates.Count
            ? "Some entries could not safely be identified in the preview. Check WinGet for the complete list."
            : null;

        return new WinGetUpdateScanResult(uniquelyIdentified, true, detail, true, IsPartial: detail is not null);
    }

    private static bool IsProgressLine(string line)
    {
        string text = line.Trim();
        return text.Length == 1 && text[0] is '-' or '\\' or '|' or '/' ||
            text.Length > 0 && text[0] is '█' or '▒';
    }

    private static bool TryReadRow(string row, int[] starts, string source, out WinGetUpdateCandidate? candidate)
    {
        candidate = null;
        if (row.Length <= starts[3] ||
            starts.Skip(1).Take(3).Any(start => row[start - 1] != ' ' || char.IsWhiteSpace(row[start])))
            return false;
        if (row.Any(char.IsControl))
            return false;
        string name = Field(row, starts, 0);
        string id = Field(row, starts, 1);
        string installed = Field(row, starts, 2);
        string available = Field(row, starts, 3);
        string reportedSource = starts.Length == 5 ? Field(row, starts, 4) : string.Empty;
        if (name.Length == 0 || !PackageInput.IsValidWinGetId(id) || !IsVersion(installed) || !IsVersion(available) ||
            string.Equals(installed, available, StringComparison.Ordinal) ||
            reportedSource.Length != 0 && !string.Equals(reportedSource, source, StringComparison.OrdinalIgnoreCase))
            return false;

        candidate = new WinGetUpdateCandidate(name, id, installed, available, source, true);
        return true;
    }

    private static string Field(string row, int[] starts, int field) =>
        row[Math.Min(row.Length, starts[field])..(field + 1 < starts.Length ? Math.Min(row.Length, starts[field + 1]) : row.Length)].TrimEnd();

    private static bool IsVersion(string? value) =>
        value is not null && VersionToken.IsMatch(value) && value.Any(char.IsAsciiDigit) &&
        !value.Contains("..", StringComparison.Ordinal);

    private static bool SameSelection(WinGetUpdateCandidate current, WinGetUpdateCandidate approved) =>
        current.CanSelect && string.Equals(current.Id, approved.Id, StringComparison.OrdinalIgnoreCase) &&
        current.Source == approved.Source && current.InstalledVersion == approved.InstalledVersion &&
        current.AvailableVersion == approved.AvailableVersion;

    private static WinGetUpdateScanResult UnreadablePreview() =>
        new(Array.Empty<WinGetUpdateCandidate>(), false, "WinGet did not provide a verifiable list of upgrades.", true);

    private async Task<WinGetUpdateResult> RunUpgradeAsync(
        ProcessCommand command, IProgress<string> progress, bool bulk, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(command, line =>
        {
            if (!string.IsNullOrWhiteSpace(line.Text)) progress.Report(line.Text);
        }, cancellationToken).ConfigureAwait(false);
        var code = Code(result);

        // https://github.com/microsoft/winget-cli/blob/master/doc/windows/package-manager/winget/returnCodes.md
        return result.Outcome switch
        {
            ProcessRunOutcome.Cancelled => Cancelled(),
            ProcessRunOutcome.Exited => code switch
            {
                0 when bulk => new(true, false, "WinGet finished the bulk run. Check for updates again to see what remains.", WinGetUpdateOutcome.Finished, result.ExitCode),
                0 => new(true, false, "WinGet reported the upgrade completed.", WinGetUpdateOutcome.Applied, result.ExitCode),
                0x8A15002B => new(true, false, "WinGet found no applicable upgrade.", WinGetUpdateOutcome.NoApplicableUpdates, result.ExitCode),
                0x8A15002C => new(false, false, "WinGet reports one or more upgrades failed. Check again for remaining updates.", WinGetUpdateOutcome.Partial, result.ExitCode),
                0x8A150109 or 3010 => new(true, true, "A restart is required to finish the upgrade.", WinGetUpdateOutcome.RestartRequired, result.ExitCode),
                0x8A15010A => new(false, true, "Restart Windows before trying this upgrade again.", WinGetUpdateOutcome.RestartRequired, result.ExitCode),
                0x8A15010B or 1641 => new(true, true, "The installer reported that it started a restart.", WinGetUpdateOutcome.RestartRequired, result.ExitCode),
                0x8A15010C or 0x800704C7 or 1602 => Cancelled(result.ExitCode),
                _ => new(false, false, FailureDetail(result, "upgrade"), WinGetUpdateOutcome.Failed, result.ExitCode)
            },
            _ => new(false, false, FailureDetail(result, "upgrade"), WinGetUpdateOutcome.Failed, result.ExitCode)
        };
    }

    private static uint? Code(ProcessRunResult result) =>
        result.ExitCode is int exitCode ? unchecked((uint)exitCode) : null;

    private static WinGetUpdateResult Refused(string detail) => new(false, false, detail, WinGetUpdateOutcome.Refused);
    private static WinGetUpdateResult Cancelled(int? exitCode = null) =>
        new(false, false, "The upgrade was cancelled. Check again to see what remains.", WinGetUpdateOutcome.Cancelled, exitCode);

    private static string FailureDetail(ProcessRunResult result, string action) => result.Outcome switch
    {
        ProcessRunOutcome.TimedOut => $"WinGet timed out during the {action}. Check again before retrying.",
        ProcessRunOutcome.Cancelled => $"The WinGet {action} was cancelled.",
        ProcessRunOutcome.NotAllowed => MissingWinGet,
        ProcessRunOutcome.FailedToStart => $"WinGet could not start the {action}.",
        _ => Code(result) switch
        {
            0x8A15000F or 0x8A150012 or 0x8A150015 or 0x8A150045 or 0x8A15004B => "The WinGet source is unavailable.",
            0x8A150019 or 0x80070005 => "This WinGet action requires administrator permission.",
            0x8A150011 => "WinGet refused an installer whose checksum does not match.",
            0x8A15001B or 0x8A15001C or 0x8A15003A or 0x8A15010F => "An organization policy blocks this WinGet action.",
            0x8A150068 => "This package is pinned in WinGet.",
            0x8A150056 => "This installer requires a non-administrator session. Reopen AxePower normally and try again.",
            0x8A150101 or 0x8A150102 or 0x8A150103 => "An app, file, or installation is in use. Close it or wait, then try again.",
            0x8A150107 or 0x80072EE7 or 0x80072EFD => "WinGet could not connect. Check your network and try again.",
            0x8A150041 or 0x8A150042 => "WinGet could not obtain a required agreement or prompt response.",
            0x8A150046 => "This WinGet source requires its terms to be accepted before it can be checked.",
            uint code when code != 0 => $"WinGet could not complete the {action} (0x{code:X8}).",
            _ => $"WinGet did not return a reliable result for the {action}."
        }
    };

    private static bool IsInDesktopAccount()
    {
        // Compare the process user's SID to the interactive shell's SID. This blocks a runas
        // over-the-shoulder elevation that would otherwise use another account's inventory.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getshellwindow
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocesstoken
        const uint queryLimitedInformation = 0x1000;
        const uint tokenQuery = 0x0008;
        try
        {
            var window = GetShellWindow();
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var processId) == 0)
                return false;
            using var process = OpenProcess(queryLimitedInformation, false, processId);
            if (process.IsInvalid || !OpenProcessToken(process, tokenQuery, out var token))
                return false;
            using (token)
            using (var shell = new WindowsIdentity(token.DangerousGetHandle()))
            using (var current = WindowsIdentity.GetCurrent())
                return shell.User is { } shellUser && current.User is { } currentUser && shellUser.Equals(currentUser);
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);
}
