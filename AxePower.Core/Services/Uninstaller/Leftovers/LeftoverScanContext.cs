using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller.Leftovers;

/// <summary>Finds one kind of leftover. Each item it returns carries its evidence.</summary>
public interface ILeftoverFinder
{
    /// <summary>The lowest scan mode this finder runs in.</summary>
    ScanMode MinimumMode { get; }

    IEnumerable<LeftoverItem> Find(LeftoverScanContext context, CancellationToken cancellationToken);
}

/// <summary>Everything a finder needs to know about the removed app and what is still installed.</summary>
public sealed class LeftoverScanContext
{
    private const int FolderInspectionLimit = 5000;

    public LeftoverScanContext(
        InstalledApplication application,
        ScanMode mode,
        IReadOnlyList<InstalledApplication> otherApplications,
        IReadOnlyCollection<string> protectedInstallLocations)
    {
        Application = application;
        Mode = mode;
        OtherApplications = otherApplications;
        ProtectedInstallLocations = protectedInstallLocations;
        CandidateNames = AppNameNormalizer.GetCandidateNames(application);
        PublisherTrimmed = AppNameNormalizer.TrimPublisher(application.Publisher);
        AppFolders = BuildAppFolders(application);
        OtherPublishers = otherApplications
            .Select(a => AppNameNormalizer.TrimPublisher(a.Publisher))
            .Where(p => p.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        OtherTrimmedNames = otherApplications
            .Select(a => AppNameNormalizer.TrimDisplayName(a.DisplayName))
            .Where(n => n.Length > 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public InstalledApplication Application { get; }
    public ScanMode Mode { get; }

    /// <summary>Other installed apps (never the removed one).</summary>
    public IReadOnlyList<InstalledApplication> OtherApplications { get; }

    /// <summary>Install folders nothing may be offered from (other apps', and the app's own while still installed).</summary>
    public IReadOnlyCollection<string> ProtectedInstallLocations { get; }

    public IReadOnlyList<string> CandidateNames { get; }
    public string PublisherTrimmed { get; }

    /// <summary>
    /// Folders that belong to the removed app: its install folder and its uninstaller's folder.
    /// Anything pointing into one of these is explicitly connected.
    /// </summary>
    public IReadOnlyList<string> AppFolders { get; }

    private HashSet<string> OtherPublishers { get; }
    private IReadOnlyList<string> OtherTrimmedNames { get; }

    public bool PointsIntoAppFolders(string? path) =>
        !string.IsNullOrWhiteSpace(path) && AppFolders.Any(f => ProtectedPaths.IsSameOrUnder(path, f));

    public LeftoverItem NewItem(LeftoverItemType type, string path, string? valueName = null) => new()
    {
        ApplicationId = Application.Id,
        ApplicationName = Application.DisplayName,
        ItemType = type,
        Path = path,
        ValueName = valueName,
        ScanMode = Mode.ToString(),
    };

    /// <summary>Evidence from how well a folder or key name matches the app (and whether others use it).</summary>
    public void AddNameEvidence(LeftoverItem item, string name, int match, int level, bool underPublisher)
    {
        if (match is >= 0 and <= 1) item.Evidence.Add(ConfidenceRecords.ProductNamePerfectMatch);
        else if (match >= 2) item.Evidence.Add(ConfidenceRecords.ProductNameDodgyMatch);

        item.Evidence.Add(ConfidenceRecords.Depth(level));
        if (underPublisher) item.Evidence.Add(ConfidenceRecords.CompanyNameMatch);

        if (AppNameNormalizer.MatchesPublisher(PublisherTrimmed, name))
        {
            item.Evidence.Add(ConfidenceRecords.ItemNameEqualsCompanyName);
            if (OtherPublishers.Contains(PublisherTrimmed))
            {
                item.Evidence.Add(ConfidenceRecords.PublisherIsStillUsed);
                item.BlockedReason ??= $"Other installed apps from {PublisherTrimmed} use this.";
            }
        }

        if (AppNameNormalizer.GenericFolderNames.Contains(name))
            item.Evidence.Add(ConfidenceRecords.QuestionableDirectoryName);

        var stillUsed = false;
        var similar = false;
        foreach (var other in OtherTrimmedNames)
        {
            var m = AppNameNormalizer.Match(other, name);
            if (m is >= 0 and <= 1) stillUsed = true;
            else if (m >= 2) similar = true;
        }
        if (stillUsed) item.Evidence.Add(ConfidenceRecords.ProgramNameIsStillUsed);
        else if (similar) item.Evidence.Add(ConfidenceRecords.UsedBySimilarNamedApp);
    }

    /// <summary>Evidence from a folder's contents; also sets its size.</summary>
    public void AddFolderEvidence(LeftoverItem item, CancellationToken cancellationToken)
    {
        long size = 0;
        int files = 0;
        bool executables = false;
        bool hasSubdirectories = false;

        try
        {
            var info = new DirectoryInfo(item.Path);
            hasSubdirectories = info.EnumerateDirectories().Any();
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in info.EnumerateFiles("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files++;
                try { size += file.Length; } catch { }
                if (!executables && file.Extension.ToLowerInvariant() is ".exe" or ".dll" or ".sys") executables = true;
                if (files >= FolderInspectionLimit) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        item.SizeBytes = size;
        if (files == 0) item.Evidence.Add(ConfidenceRecords.IsEmptyFolder);
        else item.Evidence.Add(ConfidenceRecords.FilesArePresent);
        if (!hasSubdirectories) item.Evidence.Add(ConfidenceRecords.FolderHasNoSubdirectories);
        if (files > 100) item.Evidence.Add(ConfidenceRecords.ManyFilesArePresent);
        if (executables) item.Evidence.Add(ConfidenceRecords.ExecutablesArePresent);
    }

    /// <summary>ExplicitConnection, plus a penalty when the referenced program still exists.</summary>
    public void AddExplicitEvidence(LeftoverItem item, string? targetPath)
    {
        item.Evidence.Add(ConfidenceRecords.ExplicitConnection);
        if (!string.IsNullOrWhiteSpace(targetPath) && Path.IsPathFullyQualified(targetPath) &&
            (File.Exists(targetPath) || Directory.Exists(targetPath)) &&
            !AppFolders.Any(f => string.Equals(ProtectedPaths.Normalize(targetPath), f, StringComparison.OrdinalIgnoreCase)))
            item.Evidence.Add(ConfidenceRecords.TargetStillExists);
    }

    private static IReadOnlyList<string> BuildAppFolders(InstalledApplication app)
    {
        var folders = new List<string>();
        Add(app.InstallLocation);
        var uninstallerFolder = Path.GetDirectoryName(app.UninstallerPath);
        if (app.UninstallerKind is not (UninstallerKind.Msi or UninstallerKind.StoreApp) &&
            UninstallEntryClassifier.IsAcceptableDerivedFolder(uninstallerFolder ?? string.Empty) &&
            !app.UninstallerPath.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase))
            Add(uninstallerFolder);
        return folders;

        void Add(string? folder)
        {
            var normalized = ProtectedPaths.Normalize(folder);
            // Never treat a shared or system folder as the app's own
            if (normalized is null || ProtectedPaths.GetBlockReason(normalized) is not null) return;
            if (!folders.Contains(normalized, StringComparer.OrdinalIgnoreCase)) folders.Add(normalized);
        }
    }
}
