using System.IO;
using System.Text.RegularExpressions;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>Discovery rules run against the 152 uninstall entries captured from the audit PC.</summary>
public class UninstallEntryClassifierTests
{
    private static InstalledApplication Classify(string displayName, string? keyName = null, FakeFileSystemProbe? fs = null, FakeMsiReader? msi = null) =>
        ArpFixtures.Classify(ArpFixtures.Find(displayName, keyName), fs ?? ArpFixtures.CompleteFileSystem(), msi ?? new FakeMsiReader())!;

    [Fact]
    public void FixtureIsPresent()
    {
        Assert.Equal(152, ArpFixtures.Entries.Count);
    }

    [Fact]
    public void D1_EveryUnquotedMsiExecEntry_IsMsi_NotBroken()
    {
        var fs = ArpFixtures.CompleteFileSystem();
        var msiEntries = ArpFixtures.Entries
            .Where(e => Regex.IsMatch(e.Values.UninstallString, @"^(?i)msiexec(\.exe)?\s"))
            .ToList();

        Assert.Equal(74, msiEntries.Count);
        Assert.All(msiEntries, e =>
        {
            var app = ArpFixtures.Classify(e, fs, new FakeMsiReader())!;
            Assert.Equal(UninstallerKind.Msi, app.UninstallerKind);
            Assert.Equal(ApplicationType.MSI, app.ApplicationType);
            Assert.True(MsiProductStateReader.IsProductCode(app.ProductCode));
        });
    }

    [Fact]
    public void D2_GuidKeyWithExeUninstaller_IsNeverMsi()
    {
        var fs = ArpFixtures.CompleteFileSystem();
        var entries = ArpFixtures.Entries
            .Where(e => Regex.IsMatch(e.KeyName, @"^\{[0-9A-Fa-f-]{36}\}$") && e.Values.WindowsInstaller != 1 &&
                        !e.Values.UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(11, entries.Count);
        Assert.All(entries, e =>
        {
            var app = ArpFixtures.Classify(e, fs, new FakeMsiReader())!;
            Assert.NotEqual(UninstallerKind.Msi, app.UninstallerKind);
            Assert.Equal(string.Empty, app.ProductCode);
        });
    }

    [Fact]
    public void WithEveryUninstallerPresent_OnlyEntriesWithoutAResolvableCommandAreBroken()
    {
        var fs = ArpFixtures.CompleteFileSystem();
        foreach (var entry in ArpFixtures.Entries)
        {
            var app = ArpFixtures.Classify(entry, fs, new FakeMsiReader())!;
            if (app.UninstallerKind != UninstallerKind.BrokenEntry) continue;

            // Allowed: no command at all, or a bare program such as "winget" that this machine
            // doesn't have (the fake file system only resolves bare names in the Windows folder)
            var command = entry.Values.UninstallString.Trim();
            var bare = command.Length > 0 && ArpFixtures.ExePath(command) is null && !command.Split(' ')[0].Contains('\\');
            Assert.True(command.Length == 0 || bare, $"{entry.Values.DisplayName} was classified broken");
        }
    }

    [Theory]
    [InlineData("RustDesk", "RustDesk", "{CD24A421-5DFB-4EC7-9E1A-7930D58ADA49}")]
    [InlineData("RustDesk", "{CD24A421-5DFB-4EC7-9E1A-7930D58ADA49}", "{CD24A421-5DFB-4EC7-9E1A-7930D58ADA49}")]
    [InlineData("ONLYOFFICE 9.4.0 (x64)", "ONLYOFFICE Desktop Editors", "{2BC23EF7-7EF5-317D-9967-2AE583956171}")]
    public void MsiApps_AreMsiWithTheirProductCode(string name, string key, string productCode)
    {
        var app = Classify(name, key);
        Assert.Equal(UninstallerKind.Msi, app.UninstallerKind);
        Assert.Equal(productCode, app.ProductCode);
    }

    [Fact]
    public void GoogleDrive_IsAnExeUninstaller_WithItsFolderAsInstallLocation()
    {
        var app = Classify("Google Drive");
        Assert.Equal(UninstallerKind.Unknown, app.UninstallerKind);
        Assert.Equal(@"C:\Program Files\Google\Drive File Stream\131.0.2.0\uninstall.exe", app.UninstallerPath, ignoreCase: true);
        // InstallLocation names the exe; discovery keeps its folder
        Assert.Equal(@"C:\Program Files\Google\Drive File Stream\131.0.2.0", app.InstallLocation, ignoreCase: true);
    }

    [Fact]
    public void SamsungUsbDriver_IsAnExeUninstaller_WithDerivedLocation()
    {
        var app = Classify("Samsung USB Driver for Mobile Phones");
        Assert.Equal(UninstallerKind.Unknown, app.UninstallerKind);
        Assert.True(app.IsInstallLocationDerived);
        Assert.Equal(@"C:\Program Files\Samsung\USB Drivers", app.InstallLocation, ignoreCase: true);
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.40664")]
    [InlineData("Microsoft Windows Desktop Runtime - 9.0.20 (x64)", "{606b692c-f1ce-431c-a36b-8904b772ffc0}")]
    [InlineData("Python 3.13.15 (64-bit)")]
    public void PackageCacheBundles_AreBurnBundles(string name, string? key = null)
    {
        var app = Classify(name, key);
        Assert.Equal(UninstallerKind.BurnBundle, app.UninstallerKind);
        // A Package Cache folder is never taken as the install location
        Assert.False(app.IsInstallLocationDerived && app.InstallLocation.Contains("Package Cache", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Audacity 3.7.9", UninstallerKind.InnoSetup)]
    [InlineData("Discord", UninstallerKind.Squirrel)]
    [InlineData("GitHub Desktop", UninstallerKind.Squirrel)]
    [InlineData("NVIDIA App 11.0.9.251", UninstallerKind.Unknown)]
    [InlineData("Cyberpunk 2077", UninstallerKind.Unknown)]
    public void OtherKinds(string name, UninstallerKind expected)
    {
        Assert.Equal(expected, Classify(name).UninstallerKind);
    }

    [Fact]
    public void NsisUninstaller_IsDetectedByItsContent()
    {
        var fs = ArpFixtures.CompleteFileSystem();
        fs.NullsoftFiles.Add(@"C:\Program Files\Upscayl\Uninstall Upscayl.exe");
        var app = Classify("Upscayl 2.15.0", fs: fs);
        Assert.Equal(UninstallerKind.Nsis, app.UninstallerKind);
        Assert.Equal("/allusers", app.UninstallerArguments);
    }

    [Fact]
    public void MissingUninstaller_IsBroken()
    {
        var fs = new FakeFileSystemProbe(); // nothing exists
        var app = Classify("Samsung USB Driver for Mobile Phones", fs: fs);
        Assert.Equal(UninstallerKind.BrokenEntry, app.UninstallerKind);
        Assert.Equal(ApplicationType.Orphaned, app.ApplicationType);
    }

    [Fact]
    public void NvidiaEntryWithMissingDll_IsBroken()
    {
        var fs = new FakeFileSystemProbe();
        Assert.Equal(UninstallerKind.BrokenEntry, Classify("NVIDIA App 11.0.9.251", fs: fs).UninstallerKind);
    }

    [Fact]
    public void UnregisteredMsiProduct_IsBroken()
    {
        var msi = new FakeMsiReader();
        msi.States["{CD24A421-5DFB-4EC7-9E1A-7930D58ADA49}"] = MsiProductStateReader.InstallStateUnknown;
        Assert.Equal(UninstallerKind.BrokenEntry, Classify("RustDesk", "RustDesk", msi: msi).UninstallerKind);
    }

    [Fact]
    public void SystemComponent_IsFlaggedHidden()
    {
        var app = Classify("ONLYOFFICE", "{2BC23EF7-7EF5-317D-9967-2AE583956171}");
        Assert.True(app.IsSystemComponent);
    }

    [Theory]
    [InlineData("KB5000001", "", true)]
    [InlineData("", "Security Update", true)]
    [InlineData("", "Hotfix", true)]
    [InlineData("", "", false)]
    public void ParentKeyNameOrReleaseType_MarksUpdates(string parent, string releaseType, bool expected)
    {
        var values = new UninstallEntryValues
        {
            DisplayName = "Some Update",
            UninstallString = "MsiExec.exe /X{11111111-2222-3333-4444-555555555555}",
            ParentKeyName = parent,
            ReleaseType = releaseType,
        };
        var app = UninstallEntryClassifier.Classify(values, "Key", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Key",
            new FakeFileSystemProbe(), new FakeMsiReader())!;
        Assert.Equal(expected, app.IsUpdate);
    }

    [Fact]
    public void NoRemove_MakesItNotRemovable()
    {
        var values = new UninstallEntryValues { DisplayName = "Pinned", UninstallString = "MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", NoRemove = 1 };
        var app = UninstallEntryClassifier.Classify(values, "Key", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Key",
            new FakeFileSystemProbe(), new FakeMsiReader())!;
        Assert.False(app.CanUninstall);
    }

    [Fact]
    public void NoDisplayName_IsSkipped()
    {
        var values = new UninstallEntryValues { UninstallString = "MsiExec.exe /X{11111111-2222-3333-4444-555555555555}" };
        Assert.Null(UninstallEntryClassifier.Classify(values, "Key", @"HKLM\X", new FakeFileSystemProbe(), new FakeMsiReader()));
    }

    [Fact]
    public void RegistryPaths_Use64BitViewWithWow6432NodeSpelledOut()
    {
        var app = Classify("Steam");
        Assert.StartsWith(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\", app.RegistryKeyPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("x86", app.Architecture);
    }

    [Fact]
    public void DerivedLocation_NeverUsesSystemFolders()
    {
        Assert.False(UninstallEntryClassifier.IsAcceptableDerivedFolder(Environment.SystemDirectory));
        Assert.False(UninstallEntryClassifier.IsAcceptableDerivedFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.False(UninstallEntryClassifier.IsAcceptableDerivedFolder(@"C:\"));
        Assert.False(UninstallEntryClassifier.IsAcceptableDerivedFolder(@"C:\ProgramData\Package Cache\{0e3bb569-69d6-4c34-bff9-c2f81db5e5f0}"));
        Assert.True(UninstallEntryClassifier.IsAcceptableDerivedFolder(@"C:\Program Files\Notepad++"));
    }

    // ── Discovery merging (§6.5 rule 11) ─────────────────────────────────────

    [Fact]
    public void RustDeskDoubleRegistration_MergesIntoOneEntryWithBothKeys()
    {
        var fs = ArpFixtures.CompleteFileSystem();
        var list = new List<InstalledApplication>();
        foreach (var entry in ArpFixtures.Entries.Where(e => e.Values.DisplayName == "RustDesk"))
            ApplicationDiscoveryService.MergeOrAdd(list, ArpFixtures.Classify(entry, fs, new FakeMsiReader())!);

        var app = Assert.Single(list);
        Assert.Equal(UninstallerKind.Msi, app.UninstallerKind);
        Assert.Equal(2, app.AllRegistryKeyPaths.Count());
        Assert.Equal(@"C:\Program Files\RustDesk", app.InstallLocation, ignoreCase: true);
    }

    [Fact]
    public void BundleAndItsHiddenMsi_StaySeparate_AndOnlyTheBundleIsVisible()
    {
        // Same name, different versions (8.0.31.36421 vs 64.124.58455): not one registration
        var fs = ArpFixtures.CompleteFileSystem();
        var list = new List<InstalledApplication>();
        foreach (var entry in ArpFixtures.Entries.Where(e => e.Values.DisplayName == "Microsoft Windows Desktop Runtime - 8.0.31 (x64)"))
            ApplicationDiscoveryService.MergeOrAdd(list, ArpFixtures.Classify(entry, fs, new FakeMsiReader())!);

        Assert.Equal(2, list.Count);
        var visible = Assert.Single(list, a => !a.IsSystemComponent);
        Assert.Equal(UninstallerKind.BurnBundle, visible.UninstallerKind);
    }

    [Fact]
    public void SameNamePublisherAndVersion_MergeWithTheVisibleBundleAsPrimary()
    {
        var bundle = new InstalledApplication
        {
            DisplayName = "Runtime", Publisher = "Vendor", Version = "1.0", RegistryKeyPath = @"HKLM\X\{bundle}",
            UninstallerKind = UninstallerKind.BurnBundle, UninstallString = "\"C:\\Cache\\b.exe\" /uninstall",
        };
        var msi = new InstalledApplication
        {
            DisplayName = "Runtime", Publisher = "Vendor", Version = "1.0", RegistryKeyPath = @"HKLM\X\{msi}",
            UninstallerKind = UninstallerKind.Msi, ProductCode = "{11111111-2222-3333-4444-555555555555}",
            UninstallString = "MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", IsSystemComponent = true,
        };
        var list = new List<InstalledApplication>();

        ApplicationDiscoveryService.MergeOrAdd(list, msi);
        ApplicationDiscoveryService.MergeOrAdd(list, bundle);

        var app = Assert.Single(list);
        Assert.Equal(UninstallerKind.BurnBundle, app.UninstallerKind);
        Assert.Equal(@"HKLM\X\{bundle}", app.RegistryKeyPath);
        Assert.Equal([@"HKLM\X\{msi}"], app.RelatedKeyPaths);
        Assert.False(app.IsSystemComponent);
    }

    [Fact]
    public void WholeFixture_MergesIntoFewerEntries_AndNoMsiProductIsListedTwice()
    {
        var fs = ArpFixtures.CompleteFileSystem();
        var list = new List<InstalledApplication>();
        foreach (var entry in ArpFixtures.Entries)
            ApplicationDiscoveryService.MergeOrAdd(list, ArpFixtures.Classify(entry, fs, new FakeMsiReader())!);

        Assert.True(list.Count < ArpFixtures.Entries.Count);
        var productCodes = list.Where(a => a.ProductCode.Length > 0).Select(a => a.ProductCode).ToList();
        Assert.Equal(productCodes.Count, productCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void SharedUninstaller_ClearsDerivedLocations()
    {
        var a = new InstalledApplication { DisplayName = "Game A", UninstallerPath = @"C:\Steam\steam.exe", InstallLocation = @"C:\Steam", IsInstallLocationDerived = true };
        var b = new InstalledApplication { DisplayName = "Game B", UninstallerPath = @"C:\Steam\steam.exe", InstallLocation = @"C:\Steam", IsInstallLocationDerived = true };
        var list = new List<InstalledApplication> { a, b };

        ApplicationDiscoveryService.ClearSharedDerivedLocations(list);

        Assert.Equal(string.Empty, a.InstallLocation);
        Assert.Equal(string.Empty, b.InstallLocation);
    }
}
