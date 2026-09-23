using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller.Leftovers;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Finds leftovers of a removed application by evidence rather than guessed names (BCU's junk
/// model). Finders return candidates with evidence records; the scanner merges duplicates,
/// applies the protection rules against a fresh list of the other installed apps, scores each
/// item and pre-ticks only items explicitly tied to the removed app.
///
/// IMPORTANT: This is an APPLICATION-SPECIFIC cleaner, not a generic registry cleaner.
/// </summary>
public sealed class LeftoverScannerService : ILeftoverScannerService
{
    private readonly IProtectionService _protection;
    private readonly IReadOnlyList<ILeftoverFinder> _finders;

    public LeftoverScannerService(IProtectionService protection, IFileSystemProbe fs, IMsiProductStateReader msi, ElevationService elevation)
        : this(protection, DefaultFinders(fs, msi, () => elevation.IsElevated))
    {
    }

    public LeftoverScannerService(IProtectionService protection)
        : this(protection, DefaultFinders(new FileSystemProbe(), new MsiProductStateReader(), ElevationService.CheckElevated))
    {
    }

    public LeftoverScannerService(IProtectionService protection, IReadOnlyList<ILeftoverFinder> finders)
    {
        _protection = protection;
        _finders = finders;
    }

    public static IReadOnlyList<ILeftoverFinder> DefaultFinders(IFileSystemProbe fs, IMsiProductStateReader msi, Func<bool> isElevated) =>
    [
        // Safe: explicit evidence
        new InstallLocationFinder(),
        new UninstallKeyFinder(fs, msi),
        new StartupFinder(fs),
        new ShortcutFinder(),
        // Moderate: name matches
        new CommonFolderFinder(),
        new SoftwareKeyFinder(),
        // Advanced
        new AppPathsFinder(),
        new AppCompatFinder(),
        new MuiCacheFinder(),
        new ComFinder(isElevated),
        new ScheduledTaskFinder(),
        new ServiceInfoFinder(fs),
        new FirewallRuleFinder(),
    ];

    public async Task<IReadOnlyList<LeftoverItem>> ScanAsync(
        InstalledApplication application,
        ScanMode scanMode,
        UninstallerSettings settings,
        LeftoverScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LeftoverScanOptions();

        var own = application.AllRegistryKeyPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool IsSelf(InstalledApplication a) =>
            ReferenceEquals(a, application) ||
            a.Id == application.Id ||
            a.AllRegistryKeyPaths.Any(own.Contains) ||
            a.PackageFullName.Length > 0 && a.PackageFullName == application.PackageFullName;

        var others = options.InstalledApplications.Where(a => !IsSelf(a)).ToList();
        var protectedLocations = others
            .Select(a => a.InstallLocation)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        if (options.ApplicationStillInstalled && !string.IsNullOrWhiteSpace(application.InstallLocation))
            protectedLocations.Add(application.InstallLocation);

        var context = new LeftoverScanContext(application, scanMode, others, protectedLocations);

        var found = await Task.Run(() =>
        {
            var items = new List<LeftoverItem>();
            foreach (var finder in _finders.Where(f => f.MinimumMode <= scanMode))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    items.AddRange(finder.Find(context, cancellationToken));
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // One failing finder doesn't lose the others' results
                }
            }
            return items;
        }, cancellationToken).ConfigureAwait(false);

        var results = new List<LeftoverItem>();
        foreach (var group in found.GroupBy(i => (i.Path.ToUpperInvariant(), i.ValueName?.ToUpperInvariant())))
        {
            var item = MergeEvidence(group.ToList());

            if (IsExcluded(item, settings)) continue;
            // Safe mode keeps only items with explicit evidence
            if (scanMode == ScanMode.Safe && !HasExplicitEvidence(item)) continue;

            ApplyProtection(item, application, protectedLocations, options);
            item.ConfidenceScore = LeftoverScoring.Score(item.Evidence);
            if (string.IsNullOrEmpty(item.ConfidenceReason) || !item.IsInformational)
                item.ConfidenceReason = LeftoverScoring.Describe(item.Evidence);
            item.IsSelected = LeftoverScoring.ShouldPreselect(item, options.AllowPreselection, options.IsElevated);
            results.Add(item);
        }

        return results
            .OrderByDescending(i => i.IsSelected)
            .ThenByDescending(i => i.ConfidenceScore)
            .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private void ApplyProtection(LeftoverItem item, InstalledApplication application,
        IReadOnlyCollection<string> protectedLocations, LeftoverScanOptions options)
    {
        var reason = _protection.GetBlockReason(item, protectedLocations);
        if (reason is not null && reason.StartsWith("Used by another installed app", StringComparison.Ordinal))
            item.Evidence.Add(ConfidenceRecords.DirectoryStillUsed);

        if (options.ApplicationStillInstalled && !string.IsNullOrWhiteSpace(application.InstallLocation) &&
            (ProtectedPaths.IsSameOrUnder(application.InstallLocation, item.Path) ||
             ProtectedPaths.IsSameOrUnder(item.Path, application.InstallLocation)))
            reason = $"{application.DisplayName} is still installed here.";

        if (item.ItemType == LeftoverItemType.RegistryKey &&
            item.Path.Contains(@"\Classes\CLSID\", StringComparison.OrdinalIgnoreCase) && !HasExplicitEvidence(item))
            reason ??= "COM registrations are only removed with explicit evidence.";

        item.BlockedReason ??= reason;
        item.IsProtected = item.BlockedReason is not null;
        item.IsSharedComponent = item.HasEvidence(nameof(ConfidenceRecords.DirectoryStillUsed)) ||
                                 item.HasEvidence(nameof(ConfidenceRecords.PublisherIsStillUsed));
        if (!item.IsProtected || item.IsInformational)
            item.RequiresElevation = item.RequiresElevation || _protection.RequiresElevation(item);
    }

    private static LeftoverItem MergeEvidence(List<LeftoverItem> duplicates)
    {
        var first = duplicates[0];
        foreach (var other in duplicates.Skip(1))
        {
            foreach (var record in other.Evidence)
                if (!first.Evidence.Any(e => e.Name == record.Name))
                    first.Evidence.Add(record);
            first.RequiresElevation |= other.RequiresElevation;
            first.BlockedReason ??= other.BlockedReason;
            if (first.SizeBytes == 0) first.SizeBytes = other.SizeBytes;
        }
        return first;
    }

    private static bool HasExplicitEvidence(LeftoverItem item) =>
        item.HasEvidence(nameof(ConfidenceRecords.ExplicitConnection)) ||
        item.HasEvidence(nameof(ConfidenceRecords.IsUninstallerRegistryKey));

    private static bool IsExcluded(LeftoverItem item, UninstallerSettings settings) =>
        settings.ExcludedRegistryPaths.Any(ex => !string.IsNullOrWhiteSpace(ex) && item.Path.StartsWith(ex, StringComparison.OrdinalIgnoreCase)) ||
        settings.ExcludedFilePaths.Any(ex => !string.IsNullOrWhiteSpace(ex) && item.Path.StartsWith(ex, StringComparison.OrdinalIgnoreCase));
}
