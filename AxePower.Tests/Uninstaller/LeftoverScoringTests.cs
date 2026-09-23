using System.IO;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller.Leftovers;

namespace AxePower.Tests.Uninstaller;

/// <summary>Name trimming, fuzzy matching and the evidence score / pre-tick rule (§6.9).</summary>
public class LeftoverScoringTests
{
    // ── Name normalisation ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Mozilla Firefox (x64 en-US)", "Mozilla Firefox")]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++")]
    [InlineData("7-Zip 26.03 (x64)", "7-Zip")]
    [InlineData("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.40664", "Microsoft Visual C++ 2013 Redistributable")]
    [InlineData("Python 3.13.15 (64-bit)", "Python")]
    [InlineData("HandBrake 1.11.2", "HandBrake")]
    [InlineData("AxePower version 1.8.0", "AxePower")]
    [InlineData("Audacity", "Audacity")]
    public void DisplayNames_AreTrimmed(string input, string expected)
    {
        Assert.Equal(expected, AppNameNormalizer.TrimDisplayName(input));
    }

    [Theory]
    [InlineData("Google LLC", "Google")]
    [InlineData("Microsoft Corporation", "Microsoft")]
    [InlineData("Black Tree Gaming Ltd.", "Black Tree Gaming")]
    [InlineData("Adobe Systems Incorporated", "Adobe Systems")]
    [InlineData("Intel(R) Corporation", "Intel")]
    [InlineData("The Git Development Community", "The Git Development Community")]
    public void Publishers_AreTrimmed(string input, string expected)
    {
        Assert.Equal(expected, AppNameNormalizer.TrimPublisher(input));
    }

    [Theory]
    [InlineData("Notepad++", "Notepad++", 0)]
    [InlineData("Notepad++", "notepad++", 0)]
    [InlineData("Beyond Compare", "Beyond_Compare", 0)]
    [InlineData("Mozilla Firefox", "Firefox", 2)]
    public void Match_FindsPerfectAndPartialNames(string product, string candidate, int expected)
    {
        Assert.Equal(expected, AppNameNormalizer.Match(product, candidate));
    }

    [Theory]
    [InlineData("AppX Extended", "AppX")]   // four characters or fewer never match
    [InlineData("GIMP", "GIMP")]
    [InlineData("Notepad++", "Blender Foundation")]
    public void Match_RejectsShortAndUnrelatedNames(string product, string candidate)
    {
        Assert.Equal(-1, AppNameNormalizer.Match(product, candidate));
    }

    [Fact]
    public void MatchAny_RetriesWithoutThePublisher()
    {
        Assert.True(AppNameNormalizer.MatchAny(["Google Chrome"], "Google", "Chrome") is >= 0 and <= 1);
    }

    [Fact]
    public void Sift4_MeasuresSimpleEdits()
    {
        Assert.Equal(0, Sift4.SimplestDistance("uninstaller", "uninstaller", 2));
        Assert.Equal(1, Sift4.SimplestDistance("uninstaller", "uninstaler", 2));
        Assert.Equal(5, Sift4.SimplestDistance("", "abcde", 2));
    }

    [Fact]
    public void CandidateNames_ComeFromNameFolderAndKey()
    {
        var app = new InstalledApplication
        {
            DisplayName = "Google Drive",
            InstallLocation = @"C:\Program Files\Google\Drive File Stream\131.0.2.0",
            RegistryKeyName = "{6BBAE539-2232-434A-A4E5-9A33560C6283}",
        };

        var names = AppNameNormalizer.GetCandidateNames(app);

        Assert.Contains("Google Drive", names);
        Assert.Contains("Drive File Stream", names);           // version folder skipped for its parent
        Assert.DoesNotContain(names, n => n.Contains('{'));    // GUID key names are not names
    }

    // ── Score and pre-tick rule ──────────────────────────────────────────────

    private static LeftoverItem Item(params ConfidenceRecord[] evidence)
    {
        var item = new LeftoverItem { Path = @"C:\Users\u\AppData\Roaming\App" };
        item.Evidence.AddRange(evidence);
        item.ConfidenceScore = LeftoverScoring.Score(item.Evidence);
        return item;
    }

    [Fact]
    public void Score_Is50Plus10TimesTheSum_Clamped()
    {
        Assert.Equal(50, LeftoverScoring.Score([]));
        Assert.Equal(90, LeftoverScoring.Score([ConfidenceRecords.ExplicitConnection]));
        Assert.Equal(100, LeftoverScoring.Score([ConfidenceRecords.IsUninstallerRegistryKey]));
        Assert.Equal(0, LeftoverScoring.Score([ConfidenceRecords.IsStoreApp]));
    }

    [Fact]
    public void ExplicitlyConnectedItem_IsPreselected()
    {
        var item = Item(ConfidenceRecords.ExplicitConnection, ConfidenceRecords.FolderHasNoSubdirectories);
        Assert.True(LeftoverScoring.ShouldPreselect(item, allowPreselection: true, isElevated: false));
    }

    [Fact]
    public void NameOnlyMatch_IsNeverPreselected_HoweverStrong()
    {
        var item = Item(ConfidenceRecords.ProductNamePerfectMatch, ConfidenceRecords.Depth(0),
            ConfidenceRecords.CompanyNameMatch, ConfidenceRecords.IsEmptyFolder);
        Assert.True(LeftoverScoring.Sum(item) >= 4);
        Assert.False(LeftoverScoring.ShouldPreselect(item, true, true));
    }

    [Fact]
    public void DirectoryStillUsed_BlocksPreselection()
    {
        var item = Item(ConfidenceRecords.ExplicitConnection, ConfidenceRecords.IsEmptyFolder, ConfidenceRecords.DirectoryStillUsed);
        Assert.False(LeftoverScoring.ShouldPreselect(item, true, true));
    }

    [Theory]
    [InlineData(nameof(ConfidenceRecords.ProgramNameIsStillUsed))]
    [InlineData(nameof(ConfidenceRecords.PublisherIsStillUsed))]
    [InlineData(nameof(ConfidenceRecords.TargetStillExists))]
    public void StillUsedEvidence_BlocksPreselection(string recordName)
    {
        var item = Item(ConfidenceRecords.ExplicitConnection, ConfidenceRecords.IsEmptyFolder, ConfidenceRecords.IsEmptyFolder);
        item.Evidence.Add(new ConfidenceRecord(recordName, 0, recordName));
        Assert.False(LeftoverScoring.ShouldPreselect(item, true, true));
    }

    [Fact]
    public void AdminOnlyItem_IsOnlyPreselectedWhenElevated()
    {
        var item = Item(ConfidenceRecords.ExplicitConnection, ConfidenceRecords.FolderHasNoSubdirectories);
        item.RequiresElevation = true;
        Assert.False(LeftoverScoring.ShouldPreselect(item, true, isElevated: false));
        Assert.True(LeftoverScoring.ShouldPreselect(item, true, isElevated: true));
    }

    [Fact]
    public void BlockedOrDisallowed_IsNeverPreselected()
    {
        var blocked = Item(ConfidenceRecords.IsUninstallerRegistryKey);
        blocked.BlockedReason = "test";
        Assert.False(LeftoverScoring.ShouldPreselect(blocked, true, true));

        var ok = Item(ConfidenceRecords.IsUninstallerRegistryKey);
        Assert.False(LeftoverScoring.ShouldPreselect(ok, allowPreselection: false, isElevated: true));
    }

    [Fact]
    public void WeakExplicitEvidence_IsNotPreselected()
    {
        // +4 explicit −4 executables = 0: a folder full of programs is never pre-ticked
        var item = Item(ConfidenceRecords.ExplicitConnection, ConfidenceRecords.ExecutablesArePresent, ConfidenceRecords.FilesArePresent);
        Assert.False(LeftoverScoring.ShouldPreselect(item, true, true));
    }

    [Theory]
    [InlineData(95, "Very High")]
    [InlineData(85, "High")]
    [InlineData(70, "Medium")]
    [InlineData(45, "Low")]
    [InlineData(20, "Very Low")]
    public void ConfidenceLabel_MatchesExpected(int score, string expected)
    {
        Assert.Equal(expected, new LeftoverItem { ConfidenceScore = score }.ConfidenceLabel);
    }
}
