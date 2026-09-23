// Portions adapted from Bulk Crap Uninstaller (https://github.com/BCUninstaller/Bulk-Crap-Uninstaller)
// Copyright (c) 2017 Marcin Szeniak. Licensed under the Apache License, Version 2.0. Modified for AxePower.

using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller.Leftovers;

/// <summary>
/// The evidence records a leftover can carry (BCU's ConfidenceRecords, same points), plus
/// <see cref="TargetStillExists"/>: a shortcut or startup entry whose program is still on disk
/// points at something that wasn't removed.
/// </summary>
public static class ConfidenceRecords
{
    public static ConfidenceRecord IsUninstallerRegistryKey => new(nameof(IsUninstallerRegistryKey), 20, "An uninstall entry of the removed app whose uninstaller is gone");
    public static ConfidenceRecord ExplicitConnection => new(nameof(ExplicitConnection), 4, "Points into the removed app's folder");
    public static ConfidenceRecord CompanyNameMatch => new(nameof(CompanyNameMatch), 4, "Filed under the app's publisher");
    public static ConfidenceRecord AllSubdirsMatched => new(nameof(AllSubdirsMatched), 4, "Every sub-folder matches the app");
    public static ConfidenceRecord IsEmptyFolder => new(nameof(IsEmptyFolder), 4, "The folder is empty");
    public static ConfidenceRecord ProductNamePerfectMatch => new(nameof(ProductNamePerfectMatch), 2, "Named exactly after the app");
    public static ConfidenceRecord FolderHasNoSubdirectories => new(nameof(FolderHasNoSubdirectories), 2, "Has no sub-folders");
    public static ConfidenceRecord FilesArePresent => new(nameof(FilesArePresent), 0, "Contains files");
    public static ConfidenceRecord DirectlyInsideKnownFolder => new(nameof(DirectlyInsideKnownFolder), -1, "Sits directly in a common folder");
    public static ConfidenceRecord CompanyNameDidNotMatch => new(nameof(CompanyNameDidNotMatch), -2, "Filed under a different publisher");
    public static ConfidenceRecord ItemNameEqualsCompanyName => new(nameof(ItemNameEqualsCompanyName), -2, "Named after the publisher, not the app");
    public static ConfidenceRecord ProductNameDodgyMatch => new(nameof(ProductNameDodgyMatch), -2, "Only partly matches the app's name");
    public static ConfidenceRecord ManyFilesArePresent => new(nameof(ManyFilesArePresent), -2, "Contains many files");
    public static ConfidenceRecord UsedBySimilarNamedApp => new(nameof(UsedBySimilarNamedApp), -2, "Another installed app has a similar name");
    public static ConfidenceRecord QuestionableDirectoryName => new(nameof(QuestionableDirectoryName), -3, "Has a generic name");
    public static ConfidenceRecord ExecutablesArePresent => new(nameof(ExecutablesArePresent), -4, "Contains programs");
    public static ConfidenceRecord TargetStillExists => new(nameof(TargetStillExists), -4, "The program it points to is still there");
    public static ConfidenceRecord ProgramNameIsStillUsed => new(nameof(ProgramNameIsStillUsed), -4, "Another installed app uses this name");
    public static ConfidenceRecord PublisherIsStillUsed => new(nameof(PublisherIsStillUsed), -4, "Other installed apps come from this publisher");
    public static ConfidenceRecord DirectoryStillUsed => new(nameof(DirectoryStillUsed), -7, "Another installed app lives in this folder");
    public static ConfidenceRecord IsStoreApp => new(nameof(IsStoreApp), -10, "Store app data (Windows cleans this up itself)");

    /// <summary>Depth record: level 0 (directly in the scanned root) = +2, level 1 = 0, …</summary>
    public static ConfidenceRecord Depth(int level) =>
        new($"Depth{level}", 2 - 2 * level, level == 0 ? "Directly in the scanned folder" : $"{level} level(s) deep");
}

/// <summary>Turns evidence into a score and decides what is pre-ticked.</summary>
public static class LeftoverScoring
{
    /// <summary>Score = clamp(50 + 10 × Σpoints, 0, 100).</summary>
    public static int Score(IEnumerable<ConfidenceRecord> evidence) =>
        Math.Clamp(50 + 10 * evidence.Sum(e => e.Points), 0, 100);

    public static int Sum(LeftoverItem item) => item.Evidence.Sum(e => e.Points);

    /// <summary>
    /// Pre-tick only when the item is explicitly connected to the removed app, the evidence adds
    /// up to 4 or more, nothing says another app still uses it, it isn't blocked, and it can
    /// actually be deleted with the rights we have. Name-only matches are never pre-ticked.
    /// </summary>
    public static bool ShouldPreselect(LeftoverItem item, bool allowPreselection, bool isElevated)
    {
        if (!allowPreselection || item.IsInformational) return false;
        if (item.IsProtected || item.BlockedReason is not null) return false;
        if (item.RequiresElevation && !isElevated) return false;
        if (!item.HasEvidence(nameof(ConfidenceRecords.ExplicitConnection)) &&
            !item.HasEvidence(nameof(ConfidenceRecords.IsUninstallerRegistryKey)))
            return false;
        if (item.HasEvidence(nameof(ConfidenceRecords.DirectoryStillUsed)) ||
            item.HasEvidence(nameof(ConfidenceRecords.ProgramNameIsStillUsed)) ||
            item.HasEvidence(nameof(ConfidenceRecords.PublisherIsStillUsed)) ||
            item.HasEvidence(nameof(ConfidenceRecords.TargetStillExists)))
            return false;
        return Sum(item) >= 4;
    }

    /// <summary>"+4 Points into the removed app's folder; −2 Contains many files"</summary>
    public static string Describe(IEnumerable<ConfidenceRecord> evidence)
    {
        var parts = evidence.Where(e => e.Points != 0 || e.Name == nameof(ConfidenceRecords.FilesArePresent))
            .OrderByDescending(e => e.Points)
            .Select(e => e.ToString())
            .ToList();
        return parts.Count == 0 ? "No specific evidence" : string.Join("; ", parts);
    }
}
