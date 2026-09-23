using System.IO;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>Each row of the kind-detection table (§6.4), including the D2 regression.</summary>
public class UninstallerKindDetectorTests
{
    private const string Guid1 = "{CD24A421-5DFB-4EC7-9E1A-7930D58ADA49}";

    private static UninstallerKindDetector.Detection Detect(string uninstallString, IFileSystemProbe fs,
        string keyName = "SomeApp", int? windowsInstaller = null, FakeMsiReader? msi = null, string bundleCachePath = "")
    {
        var values = new UninstallEntryValues
        {
            DisplayName = "Some App",
            UninstallString = uninstallString,
            WindowsInstaller = windowsInstaller,
            BundleCachePath = bundleCachePath,
        };
        UninstallCommandParser.TryParse(uninstallString, fs, out var command);
        return UninstallerKindDetector.Detect(command, values, keyName, fs, msi ?? new FakeMsiReader());
    }

    [Fact]
    public void MsiExecCommand_IsMsi_WithProductCodeFromTheCommand()
    {
        var result = Detect($"MsiExec.exe /X{Guid1}", new FakeFileSystemProbe());
        Assert.Equal(UninstallerKind.Msi, result.Kind);
        Assert.Equal(Guid1, result.ProductCode);
    }

    [Fact]
    public void ModifyCommand_IsStillMsi()
    {
        var result = Detect($"MsiExec.exe /I{Guid1}", new FakeFileSystemProbe());
        Assert.Equal(UninstallerKind.Msi, result.Kind);
    }

    [Fact]
    public void GuidKeyName_WithExeUninstaller_IsNotMsi()
    {
        // D2: Google Drive's key name is a GUID but it uninstalls with its own exe
        var exe = @"C:\Program Files\Google\Drive File Stream\131.0.2.0\uninstall.exe";
        var result = Detect(exe, new FakeFileSystemProbe(exe), keyName: "{6BBAE539-2232-434A-A4E5-9A33560C6283}", windowsInstaller: 0);

        Assert.Equal(UninstallerKind.Unknown, result.Kind);
        Assert.Equal(string.Empty, result.ProductCode);
    }

    [Fact]
    public void WindowsInstallerFlag_WithGuidKeyName_IsMsi()
    {
        var result = Detect(string.Empty, new FakeFileSystemProbe(), keyName: Guid1, windowsInstaller: 1);
        Assert.Equal(UninstallerKind.Msi, result.Kind);
        Assert.Equal(Guid1, result.ProductCode);
    }

    [Fact]
    public void PackageCacheExe_IsBurnBundle()
    {
        var exe = @"C:\ProgramData\Package Cache\{0e3bb569-69d6-4c34-bff9-c2f81db5e5f0}\VC_redist.x64.exe";
        var result = Detect($"\"{exe}\"  /uninstall", new FakeFileSystemProbe(exe), keyName: "{0e3bb569-69d6-4c34-bff9-c2f81db5e5f0}");
        Assert.Equal(UninstallerKind.BurnBundle, result.Kind);
    }

    [Fact]
    public void BundleCachePathValue_IsBurnBundle()
    {
        var exe = @"C:\Setup\bundle.exe";
        var result = Detect($"\"{exe}\" /uninstall", new FakeFileSystemProbe(exe), bundleCachePath: exe);
        Assert.Equal(UninstallerKind.BurnBundle, result.Kind);
    }

    [Fact]
    public void InstallShieldCache_IsInstallShield()
    {
        var exe = @"C:\Program Files (x86)\InstallShield Installation Information\{11111111-2222-3333-4444-555555555555}\setup.exe";
        var result = Detect($"\"{exe}\" -runfromtemp -l0x0409 -removeonly", new FakeFileSystemProbe(exe));
        Assert.Equal(UninstallerKind.InstallShield, result.Kind);
    }

    [Fact]
    public void PowerShellScript_IsPowerShell()
    {
        var result = Detect(@"powershell.exe -File C:\Tools\remove.ps1", new FakeFileSystemProbe());
        Assert.Equal(UninstallerKind.PowerShell, result.Kind);
    }

    [Fact]
    public void SquirrelUpdateExe_IsSquirrel()
    {
        var exe = @"C:\Users\u\AppData\Local\Discord\Update.exe";
        var result = Detect($"{exe} --uninstall", new FakeFileSystemProbe(exe));
        Assert.Equal(UninstallerKind.Squirrel, result.Kind);
    }

    [Fact]
    public void InnoUninstaller_WithDat_IsInnoSetup()
    {
        var exe = @"C:\Program Files\Audacity\unins000.exe";
        var result = Detect($"\"{exe}\"", new FakeFileSystemProbe(exe, @"C:\Program Files\Audacity\unins000.dat"));
        Assert.Equal(UninstallerKind.InnoSetup, result.Kind);
    }

    [Fact]
    public void InnoNamedExe_WithoutDat_IsNotInnoSetup()
    {
        var exe = @"C:\Program Files\Audacity\unins000.exe";
        var result = Detect($"\"{exe}\"", new FakeFileSystemProbe(exe));
        Assert.Equal(UninstallerKind.Unknown, result.Kind);
    }

    [Fact]
    public void ExeContainingNullsoft_IsNsis()
    {
        var exe = @"C:\Program Files\Notepad++\uninstall.exe";
        var fs = new FakeFileSystemProbe(exe);
        fs.NullsoftFiles.Add(exe);
        Assert.Equal(UninstallerKind.Nsis, Detect($"\"{exe}\"", fs).Kind);
    }

    [Fact]
    public void RealFileContainingNullsoft_IsNsis()
    {
        using var temp = new TempFolder();
        var exe = temp.CreateFile("uninst.exe", "MZ....padding....Nullsoft Install System v3.10");
        Assert.Equal(UninstallerKind.Nsis, Detect($"\"{exe}\"", new FileSystemProbe()).Kind);
    }

    [Fact]
    public void MissingExe_IsBrokenEntry()
    {
        var result = Detect("\"C:\\DoesNotExist\\uninst.exe\"", new FakeFileSystemProbe());
        Assert.Equal(UninstallerKind.BrokenEntry, result.Kind);
    }

    [Fact]
    public void NoUninstallString_NotMsi_IsBrokenEntry()
    {
        Assert.Equal(UninstallerKind.BrokenEntry, Detect(string.Empty, new FakeFileSystemProbe()).Kind);
    }

    [Fact]
    public void UnregisteredMsiProduct_IsBrokenEntry()
    {
        var msi = new FakeMsiReader();
        msi.States[Guid1] = MsiProductStateReader.InstallStateUnknown;
        Assert.Equal(UninstallerKind.BrokenEntry, Detect($"MsiExec.exe /X{Guid1}", new FakeFileSystemProbe(), msi: msi).Kind);
    }

    [Fact]
    public void MsiInstalledForAnotherUser_IsStillMsi()
    {
        var msi = new FakeMsiReader();
        msi.States[Guid1] = MsiProductStateReader.InstallStateAbsent;
        Assert.Equal(UninstallerKind.Msi, Detect($"MsiExec.exe /X{Guid1}", new FakeFileSystemProbe(), msi: msi).Kind);
    }

    [Fact]
    public void UnregisteredMsiFlag_WithWorkingExe_FallsBackToTheExe()
    {
        var exe = @"C:\Program Files\App\uninstall.exe";
        var msi = new FakeMsiReader();
        msi.States[Guid1] = MsiProductStateReader.InstallStateUnknown;
        var result = Detect($"\"{exe}\"", new FakeFileSystemProbe(exe), keyName: Guid1, windowsInstaller: 1, msi: msi);
        Assert.Equal(UninstallerKind.Unknown, result.Kind);
    }
}
