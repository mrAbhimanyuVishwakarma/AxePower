using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller.Leftovers;

namespace AxePower.App.ViewModels;

/// <summary>A leftover in the review list: selection state, badges and evidence.</summary>
public sealed partial class LeftoverItemViewModel : ObservableObject
{
    public LeftoverItem Source { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Set once cleanup has processed the item.</summary>
    [ObservableProperty]
    private string _resultLabel = string.Empty;

    public LeftoverItemViewModel(LeftoverItem source, bool isElevated = false)
    {
        Source = source;
        IsElevated = isElevated;
        _isSelected = source.IsSelected && CanSelect;
    }

    public bool IsElevated { get; }

    public string ApplicationName => Source.ApplicationName;
    public string Path          => Source.DisplayPath;
    public string TypeLabel     => Source.ItemType switch
    {
        LeftoverItemType.RegistryKey => "Registry key",
        LeftoverItemType.RegistryValue => "Registry value",
        LeftoverItemType.StartupEntry => "Startup entry",
        LeftoverItemType.ScheduledTask => "Scheduled task",
        LeftoverItemType.FirewallRule => "Firewall rule",
        _ => Source.ItemType.ToString(),
    };

    /// <summary>Segoe MDL2 glyph for the item type.</summary>
    public string TypeGlyph => Source.ItemType switch
    {
        LeftoverItemType.Folder => "",
        LeftoverItemType.File => "",
        LeftoverItemType.Shortcut => "",
        LeftoverItemType.ScheduledTask => "",
        LeftoverItemType.Service => "",
        LeftoverItemType.FirewallRule => "",
        LeftoverItemType.StartupEntry => "",
        _ => "",
    };

    public int ConfidenceScore  => Source.ConfidenceScore;
    public string ConfidenceLabel => Source.ConfidenceLabel;
    public string Reason        => Source.ConfidenceReason;
    public string DisplaySize   => Source.DisplaySize;

    /// <summary>One line per evidence record, for the tooltip.</summary>
    public string EvidenceText => Source.Evidence.Count == 0
        ? Source.ConfidenceReason
        : string.Join(Environment.NewLine, Source.Evidence.OrderByDescending(e => e.Points).Select(e => e.ToString())) +
          $"{Environment.NewLine}Score {Source.ConfidenceScore}/100 (Σ {LeftoverScoring.Sum(Source)})";

    public bool IsProtected     => Source.IsProtected;
    public bool IsNotProtected  => !Source.IsProtected;
    public bool IsShared        => Source.IsSharedComponent;
    public bool IsBlocked       => Source.BlockedReason is not null || Source.IsInformational;
    public string BlockedReason => Source.BlockedReason ?? (Source.IsInformational ? "Shown for information only" : string.Empty);
    public bool NeedsAdmin      => Source.RequiresElevation && !IsBlocked;
    public bool IsLocked        => NeedsAdmin && !IsElevated;

    /// <summary>Blocked, informational and (while not elevated) admin-only items can't be ticked.</summary>
    public bool CanSelect => !IsBlocked && !IsLocked;
}

/// <summary>A past session in the History panel.</summary>
public sealed partial class UninstallSessionViewModel : ObservableObject
{
    public UninstallSession Session { get; }

    [ObservableProperty]
    private bool _canRestore;

    [ObservableProperty]
    private bool _hasBackupFolder;

    public UninstallSessionViewModel(UninstallSession session, string backupFolder)
    {
        Session = session;
        BackupFolder = backupFolder;
        _hasBackupFolder = Directory.Exists(backupFolder);
    }

    public string BackupFolder { get; }

    public string Title => Session.IsStandaloneScan
        ? $"Leftover scan: {string.Join(", ", Session.ApplicationNames.Take(3))}"
        : string.Join(", ", Session.ApplicationNames.Take(3)) + (Session.ApplicationNames.Count > 3 ? $" +{Session.ApplicationNames.Count - 3}" : string.Empty);

    public string When => Session.StartTime.ToString("MMM d, yyyy  HH:mm");

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Session.Results.Count > 0) parts.Add(Session.OutcomeSummary);
            if (Session.WasDryRun) parts.Add("dry run");
            if (Session.CleanupResult is { } cleanup) parts.Add($"cleanup: {cleanup.Summary}");
            else if (Session.LeftoverItems.Count > 0) parts.Add($"{Session.LeftoverItems.Count} leftovers not reviewed");
            return string.Join(" · ", parts);
        }
    }

    public IReadOnlyList<UninstallResult> Results => Session.Results;
    public bool CanResumeReview => Session.HasPendingLeftoverReview;
}
